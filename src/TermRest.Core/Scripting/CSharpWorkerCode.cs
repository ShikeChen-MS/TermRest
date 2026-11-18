using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TermRest.Core.IO;

namespace TermRest.Core.Scripting;

/// <summary>
/// The C# side of <see cref="ScriptWorker"/>. C# is compiled, so TermRest writes the C# scripts of a collection into one
/// small program, each script the body of a method, which the user's .NET SDK builds as a file-based app
/// (<see cref="CSharpPrograms"/>). The program reads one JSON message per line, runs the scripts named in it with the
/// <c>tr</c> object, and writes one line back. <c>#line</c> directives keep the scripts' own line numbers in compiler
/// errors and stack traces.
/// </summary>
internal static partial class CSharpWorkerCode
{
    /// <summary>Starts each answer: lines without it come from programs that scripts started.</summary>
    public const string ReplyMarker = "\u001eTR ";

    /// <summary>Makes the program print the folder it runs from, and exit (after a build).</summary>
    public const string WhereArgument = "--termrest-where";

    public const string AssemblyName = "termrest-scripts";

    /// <summary>The file name a script's lines have in errors and stack traces, followed by its id.</summary>
    private const string ScriptFile = "termrest-script-";

    /// <summary>
    /// Properties of every program, unless a script sets them: reflection works (JsonSerializer…), no .exe (it runs
    /// with dotnet), no background garbage-collector thread (less memory for a small program), no nullable warnings, and
    /// line numbers in the program itself (it runs from a copy: a separate .pdb would be looked for where it was built).
    /// </summary>
    private static readonly (string Name, string Value)[] Properties =
    [
        ("PublishAot", "false"),
        ("UseAppHost", "false"),
        ("ConcurrentGarbageCollection", "false"),
        ("TieredPGO", "false"),
        ("Nullable", "disable"),
        ("ImplicitUsings", "enable"),
        ("SatelliteResourceLanguages", "en"),
        ("DebugType", "embedded"),
    ];

    public static string Normalize(string code) => code.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>A script's id: the same code is built once, whichever requests share it.</summary>
    public static string Id(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))))[..16];

    /// <summary>The id of the script whose lines a compiler error or stack frame is in; null for the program's own lines.</summary>
    public static string? ScriptIdOf(string? file)
    {
        var at = file?.LastIndexOf(ScriptFile, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : file![(at + ScriptFile.Length)..];
    }

    /// <summary>
    /// The program for <paramref name="scripts"/> (id → code). A script may start with <c>using</c> directives and
    /// file-based app directives (<c>#:package Name@1.2.3</c>…): they are moved out of the method, and the lines they
    /// leave are kept empty, so line numbers do not change. Relative <c>#:project</c> and <c>#:include</c> paths are
    /// relative to the workspace.
    /// </summary>
    public static string Generate(IReadOnlyDictionary<string, string> scripts, string workspaceRoot)
    {
        var parts = scripts.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (Id: s.Key, Script: Split(s.Value, workspaceRoot))).ToList();
        var directives = parts.SelectMany(p => p.Script.Directives).Distinct(StringComparer.Ordinal).ToList();
        var set = directives.Select(d => PropertyDirective().Match(d)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder("// TermRest writes this program from C# scripts for each build: changes to it are lost.\n");
        foreach (var (name, value) in Properties.Where(p => !set.Contains(p.Name)))
        {
            text.Append("#:property ").Append(name).Append('=').Append(value).Append('\n');
        }

        foreach (var directive in directives)
        {
            text.Append(directive).Append('\n');
        }

        // Global usings apply to the whole program, as they do in a file-based app (where they also reach #:include files):
        // they come before the namespaces, once each.
        var globalUsings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, script) in parts)
        {
            foreach (var (line, directive) in script.GlobalUsings.Where(u => globalUsings.Add(u.Text)))
            {
                text.Append("\n#line ").Append(line).Append(" \"").Append(ScriptFile).Append(id).Append("\"\n").Append(directive);
            }
        }

        if (globalUsings.Count > 0)
        {
            text.Append("\n#line default\n");
        }

        foreach (var (id, script) in parts)
        {
            text.Append("\nnamespace TermRestScripts.S").Append(id).Append("\n{\n")
                .Append("    using System.Text;\n    using System.Text.Json.Nodes;\n    using System.Text.RegularExpressions;\n");
            foreach (var (line, directive) in script.Usings)
            {
                text.Append("#line ").Append(line).Append(" \"").Append(ScriptFile).Append(id).Append("\"\n    ").Append(directive).Append('\n');
            }

            text.Append("#line default\n")
                .Append("    internal static class Script\n    {\n")
                .Append("        internal static async global::System.Threading.Tasks.Task Run(global::TermRest.Tr tr)\n        {\n")
                .Append("#line 1 \"").Append(ScriptFile).Append(id).Append("\"\n")
                .Append(script.Body).Append('\n')
                .Append("#line default\n#nullable restore\n        }\n    }\n}\n");
        }

        text.Append("\n#nullable disable\n#pragma warning disable\nnamespace TermRest\n{\n    internal static partial class Host\n    {\n")
            .Append("        private static readonly global::System.Collections.Generic.Dictionary<string, global::System.Func<Tr, global::System.Threading.Tasks.Task>> Scripts = new()\n        {\n");
        foreach (var (id, _) in parts)
        {
            text.Append("            [\"").Append(id).Append("\"] = global::TermRestScripts.S").Append(id).Append(".Script.Run,\n");
        }

        return text.Append("        };\n    }\n}\n\n").Append(Runtime).ToString();
    }

    /// <summary>
    /// The directives, <c>using</c> directives and <c>global using</c> directives at the start of a script, and the rest (the
    /// method's body).
    /// </summary>
    private static (List<string> Directives, List<(int Line, string Text)> Usings, List<(int Line, string Text)> GlobalUsings, string Body) Split(string code, string workspaceRoot)
    {
        var lines = Normalize(code).Split('\n');
        var directives = new List<string>();
        var usings = new List<(int, string)>();
        var globalUsings = new List<(int, string)>();
        var comment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (comment || line.StartsWith("/*", StringComparison.Ordinal))
            {
                comment = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("#:", StringComparison.Ordinal) || (i == 0 && line.StartsWith("#!", StringComparison.Ordinal)))
            {
                if (line.StartsWith("#:", StringComparison.Ordinal))
                {
                    directives.Add(WithFullPath(line, workspaceRoot));
                }

                lines[i] = "";
                continue;
            }

            if (!UsingDirective().IsMatch(line))
            {
                break;
            }

            // C# allows a global using only before the namespaces (see Generate).
            (line.StartsWith("global", StringComparison.Ordinal) ? globalUsings : usings).Add((i + 1, line));
            lines[i] = "";
        }

        return (directives, usings, globalUsings, string.Join('\n', lines));
    }

    private static string WithFullPath(string directive, string workspaceRoot)
    {
        var match = PathDirective().Match(directive);
        if (!match.Success)
        {
            return directive;
        }

        var path = match.Groups[2].Value.Trim();
        if (path.StartsWith('$'))
        {
            return directive;
        }

        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workspaceRoot, path));

        if (match.Groups[1].Value == "project")
        {
            full = ProjectPath(full);
        }

        return full == path ? directive : $"#:{match.Groups[1].Value} {full}";
    }

    /// <summary>
    /// A project's path as the build should see it: on Linux and macOS without symbolic links (<see cref="RealPath.Of"/>),
    /// as NuGet leaves out the projects that a project references when the path to it goes through a link (on macOS, /var
    /// and /tmp are links), and the program then builds without them.
    /// </summary>
    internal static string ProjectPath(string path) => OperatingSystem.IsWindows() ? path : RealPath.Of(path) ?? path;


    /// <summary>The #:include and #:project directives of a program from <see cref="Generate"/>: which one, and its path.</summary>
    public static IEnumerable<(string Directive, string Path)> PathsOf(string program) =>
        program[..Math.Max(0, program.IndexOf("\n\n", StringComparison.Ordinal))].Split('\n')
            .Select(line => PathDirective().Match(line))
            .Where(match => match.Success)
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value.Trim()));

    // using X.Y;  using static X.Y;  using A = X.Y<Z>;  (not using statements: using var x = …, using (…), using T x = …)
    [GeneratedRegex(@"^(?:global\s+)?using\s+(?:static\s+[\w.@<>,\s]+|[A-Za-z_@]\w*\s*=\s*[^;]+|[A-Za-z_@][\w.@]*)\s*;\s*(?://.*)?$")]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"^#:(project|include)\s+(.+)$")]
    private static partial Regex PathDirective();

    [GeneratedRegex(@"^#:property\s+([^=\s]+)\s*=")]
    private static partial Regex PropertyDirective();

    /// <summary>The tr object and the protocol, compiled into every program.</summary>
    private const string Runtime = """
        namespace TermRest
        {
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.Globalization;
            using System.IO;
            using System.Linq;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Text;
            using System.Text.Encodings.Web;
            using System.Text.Json;
            using System.Text.Json.Nodes;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;
            using Microsoft.Win32.SafeHandles;

            /// <summary>What scripts get as tr: the request, the response, variables, tests.</summary>
            public sealed class Tr
            {
                private readonly JsonArray _tests;

                internal Tr(Scope environment, Scope collectionVariables, Scope globals, Variables variables, Scope iterationData, Request request, Response response, Info info, JsonArray tests)
                {
                    Environment = environment;
                    CollectionVariables = collectionVariables;
                    Globals = globals;
                    Variables = variables;
                    IterationData = iterationData;
                    Request = request;
                    Response = response;
                    Info = info;
                    _tests = tests;
                }

                /// <summary>The selected environment's variables.</summary>
                public Scope Environment { get; }

                public Scope CollectionVariables { get; }

                public Scope Globals { get; }

                /// <summary>Reads every scope (this request, the run's iteration data, environment, collection, globals); Set is for this request only.</summary>
                public Variables Variables { get; }

                /// <summary>The data of the run's current iteration, a row of its data file (termrest run --iteration-data); empty without one.</summary>
                public Scope IterationData { get; }

                /// <summary>The request: change it in pre-request scripts ({{variables}} are not replaced yet).</summary>
                public Request Request { get; }

                /// <summary>The response; null in pre-request scripts.</summary>
                public Response Response { get; }

                public Info Info { get; }

                /// <summary>Records a test, which fails when it throws (tr.Assert, tr.AssertEqual…).</summary>
                public void Test(string name, Action test) => Record(name, () =>
                {
                    test();
                    return Task.CompletedTask;
                });

                public void Test(string name, Func<Task> test) => Record(name, test);

                /// <summary>
                /// Sends a request through TermRest, with its certificates, proxy and settings, and returns its response (as
                /// tr.Response has it); throws HttpRequestException when it can't be sent. The request is a URL, or as Postman's
                /// pm.sendRequest takes it: new { url, method, header, body, auth }, where header may be a dictionary of names to
                /// values, and body text (raw), JSON (any object without a mode) or Postman's { mode, raw … }. Its {{variables}}
                /// are sent as written: tr.Variables.ReplaceIn replaces them.
                /// </summary>
                public Response SendRequest(object request) => Host.Send(request switch
                {
                    string url => JsonValue.Create(url),
                    JsonNode node => node.DeepClone(),
                    null => throw new ArgumentNullException(nameof(request)),
                    _ => JsonSerializer.SerializeToNode(request, request.GetType()),
                });

                public Task<Response> SendRequestAsync(object request) => Task.Run(() => SendRequest(request));

                public void Assert(bool condition, string message = null, [CallerArgumentExpression(nameof(condition))] string expression = null)
                {
                    if (!condition)
                    {
                        throw new AssertionException(message ?? "assertion failed: " + expression);
                    }
                }

                /// <summary>Compares numbers as numbers, JSON as JSON (json["name"] equals "Ada"), and lists item by item.</summary>
                public void AssertEqual(object expected, object actual, string message = null, [CallerArgumentExpression(nameof(actual))] string expression = null)
                {
                    if (!Host.Same(expected, actual))
                    {
                        throw new AssertionException(message ?? $"{expression}: expected {Host.Show(expected)}, got {Host.Show(actual)}");
                    }
                }

                private void Record(string name, Func<Task> test)
                {
                    var result = new JsonObject { ["name"] = name, ["passed"] = true, ["script"] = Info.Script };
                    try
                    {
                        test().GetAwaiter().GetResult();
                    }
                    catch (Exception error)
                    {
                        result["passed"] = false;
                        result["error"] = Host.Failure(error, false);
                    }

                    _tests.Add(result);
                }
            }

            /// <summary>The variables of one scope: text values by name.</summary>
            public class Scope : IEnumerable<KeyValuePair<string, string>>
            {
                private protected readonly Dictionary<string, string> Values;
                private readonly JsonObject _changes;

                internal Scope(Dictionary<string, string> values, JsonObject changes)
                {
                    Values = values;
                    _changes = changes;
                }

                /// <summary>The value, or null; setting null unsets it.</summary>
                public string this[string name]
                {
                    get => Get(name);
                    set
                    {
                        if (value is null)
                        {
                            Unset(name);
                        }
                        else
                        {
                            Set(name, value);
                        }
                    }
                }

                public virtual string Get(string name, string fallback = null) => Values.TryGetValue(name, out var value) ? value : fallback;

                /// <summary>Sets a value until TermRest closes: text as it is, anything else as JSON (true, 42, {"a":1}).</summary>
                public void Set(string name, object value)
                {
                    var text = Host.ToText(value);
                    Values[name] = text;
                    _changes[name] = text;
                }

                public void Unset(string name)
                {
                    Values.Remove(name);
                    _changes[name] = null;
                }

                public virtual bool Has(string name) => Values.ContainsKey(name);

                public void Clear()
                {
                    foreach (var name in Values.Keys.ToList())
                    {
                        Unset(name);
                    }
                }

                public virtual Dictionary<string, string> ToDictionary() => new(Values);

                public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ToDictionary().GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                public override string ToString() => Host.Show(ToDictionary());
            }

            /// <summary>tr.Variables: this request's variables, then the environment's, the collection's and the globals.</summary>
            public sealed class Variables : Scope
            {
                private readonly Dictionary<string, string>[] _scopes;
                private readonly JsonObject _samples;

                internal Variables(Dictionary<string, string> local, JsonObject changes, Dictionary<string, string>[] scopes, JsonObject samples)
                    : base(local, changes)
                {
                    _scopes = scopes;
                    _samples = samples;
                }

                public override string Get(string name, string fallback = null)
                {
                    foreach (var scope in _scopes)
                    {
                        if (scope.TryGetValue(name, out var value))
                        {
                            return value;
                        }
                    }

                    return fallback;
                }

                public override bool Has(string name) => _scopes.Any(s => s.ContainsKey(name));

                public override Dictionary<string, string> ToDictionary()
                {
                    var merged = new Dictionary<string, string>();
                    for (var i = _scopes.Length - 1; i >= 0; i--)
                    {
                        foreach (var pair in _scopes[i])
                        {
                            merged[pair.Key] = pair.Value;
                        }
                    }

                    return merged;
                }

                /// <summary>Replaces {{name}} with the variables' values, and Postman's dynamic variables ({{$guid}}…); unknown names stay as they are.</summary>
                public string ReplaceIn(string text)
                {
                    for (var i = 0; i < 10 && text is not null; i++)
                    {
                        var replaced = Host.Reference.Replace(text, m =>
                        {
                            var name = m.Groups[1].Value.Trim();
                            return Get(name) ?? Host.Dynamic(name, _samples) ?? m.Value;
                        });
                        if (replaced == text)
                        {
                            break;
                        }

                        text = replaced;
                    }

                    return text;
                }
            }

            /// <summary>Headers in order: names are case-insensitive and may repeat.</summary>
            public sealed class Headers : IEnumerable<KeyValuePair<string, string>>
            {
                private readonly List<KeyValuePair<string, string>> _pairs = new();
                private readonly bool _writable;

                internal Headers(JsonNode pairs, bool writable)
                {
                    foreach (var pair in (pairs as JsonArray ?? new JsonArray()).OfType<JsonArray>().Where(p => p.Count >= 2))
                    {
                        _pairs.Add(new KeyValuePair<string, string>(Host.AsText(pair[0]) ?? "", Host.AsText(pair[1]) ?? ""));
                    }

                    _writable = writable;
                }

                /// <summary>The first value, or null; setting null removes the header.</summary>
                public string this[string name]
                {
                    get => Get(name);
                    set
                    {
                        if (value is null)
                        {
                            Remove(name);
                        }
                        else
                        {
                            Set(name, value);
                        }
                    }
                }

                public int Count => _pairs.Count;

                public string Get(string name, string fallback = null) => _pairs.Where(p => Same(p.Key, name)).Select(p => p.Value).DefaultIfEmpty(fallback).First();

                public IReadOnlyList<string> GetAll(string name) => _pairs.Where(p => Same(p.Key, name)).Select(p => p.Value).ToList();

                public bool Has(string name) => _pairs.Any(p => Same(p.Key, name));

                /// <summary>Gives the header this one value, where it was (new ones go last).</summary>
                public void Set(string name, object value)
                {
                    Check();
                    var index = _pairs.FindIndex(p => Same(p.Key, name));
                    _pairs.RemoveAll(p => Same(p.Key, name));
                    _pairs.Insert(index < 0 ? _pairs.Count : Math.Min(index, _pairs.Count), new KeyValuePair<string, string>(name, Host.ToText(value)));
                }

                public void Add(string name, object value)
                {
                    Check();
                    _pairs.Add(new KeyValuePair<string, string>(name, Host.ToText(value)));
                }

                public void Remove(string name)
                {
                    Check();
                    _pairs.RemoveAll(p => Same(p.Key, name));
                }

                public Dictionary<string, string> ToDictionary()
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in _pairs)
                    {
                        headers[pair.Key] = pair.Value;
                    }

                    return headers;
                }

                public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _pairs.ToList().GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                public override string ToString() => string.Join(", ", _pairs.Select(p => p.Key + ": " + p.Value));

                internal JsonArray ToJson() => new(_pairs.Select(p => (JsonNode)new JsonArray(p.Key, p.Value)).ToArray());

                private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

                private void Check()
                {
                    if (!_writable)
                    {
                        throw new InvalidOperationException("These headers cannot be changed: change the request in a pre-request script.");
                    }
                }
            }

            /// <summary>tr.Request: as written (pre-request scripts can change it) or as sent (test scripts).</summary>
            public sealed class Request
            {
                private readonly bool _writable;
                private string _method;
                private string _url;
                private string _body;

                internal Request(JsonObject data, bool writable)
                {
                    _writable = writable;
                    Name = Host.AsText(data?["name"]);
                    _method = Host.AsText(data?["method"]) ?? "GET";
                    _url = Host.AsText(data?["url"]) ?? "";
                    _body = Host.AsText(data?["body"]);
                    Headers = new Headers(data?["headers"], writable);
                }

                public string Name { get; }

                public string Method
                {
                    get => _method;
                    set
                    {
                        Check();
                        _method = value;
                    }
                }

                public string Url
                {
                    get => _url;
                    set
                    {
                        Check();
                        _url = value;
                    }
                }

                public Headers Headers { get; }

                /// <summary>The raw body; null when the body is not raw text (form fields, a file…).</summary>
                public string Body
                {
                    get => _body;
                    set
                    {
                        Check();
                        _body = value;
                    }
                }

                public override string ToString() => Method + " " + Url;

                internal JsonObject ToJson() => new() { ["method"] = _method, ["url"] = _url, ["headers"] = Headers.ToJson(), ["body"] = _body };

                private void Check()
                {
                    if (!_writable)
                    {
                        throw new InvalidOperationException("The request was already sent: change it in a pre-request script.");
                    }
                }
            }

            /// <summary>tr.Response: what arrived, in post-response scripts.</summary>
            public sealed class Response
            {
                private readonly string _text;
                private readonly string _base64;

                internal Response(JsonObject data)
                {
                    Status = (int?)data["status"] ?? 0;
                    Reason = Host.AsText(data["reason"]) ?? "";
                    Headers = new Headers(data["headers"], false);
                    Time = (double?)data["time"] ?? 0;
                    Size = (long?)data["size"] ?? 0;
                    _text = Host.AsText(data["text"]);
                    _base64 = Host.AsText(data["base64"]);
                }

                public int Status { get; }

                public int Code => Status;

                public string Reason { get; }

                public Headers Headers { get; }

                /// <summary>How long the response took, in milliseconds.</summary>
                public double Time { get; }

                /// <summary>The size of the body, in bytes.</summary>
                public long Size { get; }

                public byte[] Content => _base64 is not null ? Convert.FromBase64String(_base64) : Encoding.UTF8.GetBytes(_text ?? "");

                public string Text() => _text ?? Encoding.UTF8.GetString(Content);

                /// <summary>The body as JSON: json["name"], (int)json["id"], json["items"].AsArray()…; throws when it is not JSON.</summary>
                public JsonNode Json() => JsonNode.Parse(Text());

                public override string ToString() => Status + " " + Reason;
            }

            public sealed class Info
            {
                internal Info(string phase, JsonObject data)
                {
                    Phase = phase;
                    RequestName = Host.AsText(data?["request_name"]);
                    Collection = Host.AsText(data?["collection"]);
                    Environment = Host.AsText(data?["environment"]);
                    Iteration = data?["iteration"] is JsonValue iteration && iteration.TryGetValue<int>(out var number) ? number : 0;
                    IterationCount = data?["iteration_count"] is JsonValue count && count.TryGetValue<int>(out var total) ? total : 1;
                }

                /// <summary>"prerequest" or "test".</summary>
                public string Phase { get; }

                /// <summary>The script running now, such as "request 'Users/Create'".</summary>
                public string Script { get; internal set; }

                public string RequestName { get; }

                public string Collection { get; }

                public string Environment { get; }

                /// <summary>The run's current iteration, from 0.</summary>
                public int Iteration { get; }

                /// <summary>The number of iterations of the run.</summary>
                public int IterationCount { get; }
            }

            /// <summary>A failed tr.Assert or tr.AssertEqual: a test shows its message alone.</summary>
            public sealed class AssertionException : Exception
            {
                public AssertionException(string message)
                    : base(message)
                {
                }
            }

            internal static partial class Host
            {
                private const string Marker = "\u001eTR ";
                private const int MaxLines = 1000;
                internal static readonly Regex Reference = new(@"\{\{([^{}]+)\}\}");
                private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                private static readonly UTF8Encoding Utf8 = new(false);
                private static readonly Capture Output = new();
                private static readonly object SendLock = new();
                private static FileStream _nothing;
                private static TextReader _input;
                private static TextWriter _output;
                private static bool _sending;
                private static int _sent;

                private static int Main(string[] args)
                {
                    if (args.Length == 1 && args[0] == "--termrest-where")
                    {
                        using var stdout = Console.OpenStandardOutput();
                        stdout.Write(Utf8.GetBytes(Marker + "where " + AppContext.BaseDirectory + "\n"));
                        return 0;
                    }

                    var (input, output) = Channel();
                    (_input, _output) = (input, output);
                    Console.SetIn(TextReader.Null);
                    Console.SetOut(Output);
                    Console.SetError(Output);
                    string line;
                    while ((line = input.ReadLine()) is not null)
                    {
                        JsonObject reply;
                        lock (SendLock)
                        {
                            _sending = true;
                        }

                        try
                        {
                            reply = Run(JsonNode.Parse(line).AsObject());
                        }
                        catch (Exception error)
                        {
                            reply = new JsonObject { ["fatal"] = error.ToString() };
                        }

                        // After a request a task still waits for: its answer comes before the next message.
                        lock (SendLock)
                        {
                            _sending = false;
                        }

                        output.Write(Marker + reply.ToJsonString(Options) + "\n");
                        output.Flush();
                    }

                    return 0;
                }

                /// <summary>
                /// tr.SendRequest: asks TermRest to send the request, with a line of output, and reads its answer, a line of
                /// input; one at a time, as only the thread that asked may read it.
                /// </summary>
                internal static Response Send(JsonNode request)
                {
                    string line;
                    lock (SendLock)
                    {
                        if (!_sending)
                        {
                            throw new InvalidOperationException("The scripts already finished: TermRest sends no more requests for them.");
                        }

                        var send = new JsonObject { ["send"] = new JsonObject { ["id"] = ++_sent, ["request"] = request } };
                        _output.Write(Marker + send.ToJsonString(Options) + "\n");
                        _output.Flush();
                        line = _input.ReadLine();
                    }

                    var answer = (line is null ? null : JsonNode.Parse(line)?["sent"] as JsonObject) ?? new JsonObject { ["error"] = "TermRest did not answer." };
                    if (AsText(answer["log"]) is { } log)
                    {
                        Output.Write(log + "\n");
                    }

                    if (answer["error"] is not null)
                    {
                        throw new System.Net.Http.HttpRequestException(AsText(answer["error"]));
                    }

                    return new Response(answer["response"] as JsonObject ?? new JsonObject());
                }

                /// <summary>
                /// Requests come on a copy of stdin and answers go to a copy of stdout. Stdin itself then reads nothing and
                /// stdout goes to stderr, so programs that scripts start can neither take a request nor mix into an answer.
                /// </summary>
                private static (TextReader Input, TextWriter Output) Channel()
                {
                    Stream input = null, output = null;
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            input = Console.OpenStandardInput();
                            output = Console.OpenStandardOutput();
                            _nothing = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Inheritable);
                            SetStdHandle(-10, _nothing.SafeFileHandle.DangerousGetHandle());
                            SetStdHandle(-11, GetStdHandle(-12));
                        }
                        else
                        {
                            var self = NativeLibrary.GetMainProgramHandle();

                            // A session of its own, without TermRest's terminal: programs that scripts start (ssh, sudo, gpg)
                            // cannot open /dev/tty to ask over TermRest's screen, and fail at once, as they do without a terminal.
                            if (NativeLibrary.TryGetExport(self, "setsid", out var setsid))
                            {
                                Marshal.GetDelegateForFunctionPointer<Setsid>(setsid)();
                            }

                            var dup = Marshal.GetDelegateForFunctionPointer<Dup>(NativeLibrary.GetExport(self, "dup"));
                            var dup2 = Marshal.GetDelegateForFunctionPointer<Dup2>(NativeLibrary.GetExport(self, "dup2"));
                            int inputCopy = dup(0), outputCopy = dup(1);
                            if (inputCopy >= 0 && outputCopy >= 0)
                            {
                                input = new FileStream(new SafeFileHandle(inputCopy, true), FileAccess.Read, 1);
                                output = new FileStream(new SafeFileHandle(outputCopy, true), FileAccess.Write, 1);
                                _nothing = new FileStream("/dev/null", FileMode.Open, FileAccess.Read);
                                dup2((int)_nothing.SafeFileHandle.DangerousGetHandle(), 0);
                                dup2(2, 1);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // the protection is a nicety: the protocol works without it
                    }

                    input ??= Console.OpenStandardInput();
                    output ??= Console.OpenStandardOutput();
                    return (new StreamReader(input, Utf8), new StreamWriter(output, Utf8));
                }

                [DllImport("kernel32.dll")]
                private static extern bool SetStdHandle(int handle, IntPtr value);

                [DllImport("kernel32.dll")]
                private static extern IntPtr GetStdHandle(int handle);

                [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
                private delegate int Dup(int descriptor);

                [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
                private delegate int Dup2(int descriptor, int target);

                [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
                private delegate int Setsid();

                private static JsonObject Run(JsonObject message)
                {
                    var phase = AsText(message["phase"]) ?? "";
                    var variables = message["variables"] as JsonObject;
                    var changes = new JsonObject { ["environment"] = new JsonObject(), ["collection"] = new JsonObject(), ["globals"] = new JsonObject(), ["local"] = new JsonObject() };
                    Dictionary<string, string> Values(string scope)
                    {
                        var values = new Dictionary<string, string>();
                        foreach (var pair in variables?[scope] as JsonObject ?? new JsonObject())
                        {
                            values[pair.Key] = AsText(pair.Value) ?? "";
                        }

                        return values;
                    }

                    Dictionary<string, string> environment = Values("environment"), collection = Values("collection"), globals = Values("globals"), local = Values("local"), data = Values("data");
                    var info = new Info(phase, message["info"] as JsonObject);
                    var tests = new JsonArray();
                    var logs = new JsonArray();
                    var request = new Request(message["request"] as JsonObject, phase == "prerequest");
                    var tr = new Tr(
                        new Scope(environment, changes["environment"].AsObject()),
                        new Scope(collection, changes["collection"].AsObject()),
                        new Scope(globals, changes["globals"].AsObject()),
                        new Variables(local, changes["local"].AsObject(), new[] { local, data, environment, collection, globals }, message["dynamic"] as JsonObject),
                        new Scope(data, new JsonObject()),
                        request,
                        message["response"] is JsonObject response ? new Response(response) : null,
                        info,
                        tests);
                    JsonObject error = null;
                    var dropped = 0;
                    foreach (var script in (message["scripts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var name = AsText(script["name"]) ?? "";
                        info.Script = name;
                        if (script["error"] is JsonObject build)
                        {
                            error = new JsonObject { ["script"] = name, ["message"] = AsText(build["message"]), ["traceback"] = AsText(build["traceback"]) };
                            break;
                        }

                        var id = AsText(script["id"]) ?? "";
                        Output.Begin();
                        try
                        {
                            if (!Scripts.TryGetValue(id, out var run))
                            {
                                throw new InvalidOperationException("This script was not built: send the request again.");
                            }

                            run(tr).GetAwaiter().GetResult();
                        }
                        catch (Exception failure)
                        {
                            error = new JsonObject { ["script"] = name, ["message"] = Failure(failure, true), ["traceback"] = Traceback(failure, id, name, AsText(script["code"]) ?? "") };
                        }

                        var text = Output.End();
                        if (text.EndsWith('\n'))
                        {
                            text = text[..^1];
                        }

                        foreach (var printed in text.Length == 0 ? Array.Empty<string>() : text.Split('\n'))
                        {
                            var clean = printed.TrimEnd('\r');
                            if (logs.Count < MaxLines)
                            {
                                // Lines are cut at 4,000 characters, not inside a surrogate pair (an emoji).
                                logs.Add(new JsonObject { ["script"] = name, ["text"] = clean.Length > 4000 ? clean[..(char.IsHighSurrogate(clean[3999]) ? 3999 : 4000)] : clean });
                            }
                            else
                            {
                                dropped++;
                            }
                        }

                        if (error is not null)
                        {
                            break;
                        }
                    }

                    var result = new JsonObject { ["changes"] = changes, ["tests"] = tests, ["logs"] = logs, ["dropped"] = dropped, ["error"] = error };
                    if (phase == "prerequest")
                    {
                        result["request"] = request.ToJson();
                    }

                    return result;
                }

                internal static string Failure(Exception error, bool withType)
                {
                    while (error is AggregateException { InnerExceptions.Count: 1 } aggregate)
                    {
                        error = aggregate.InnerExceptions[0];
                    }

                    if (error is TargetInvocationException { InnerException: { } inner })
                    {
                        error = inner;
                    }

                    return error is AssertionException && !withType ? error.Message : error.GetType().Name + ": " + error.Message;
                }

                /// <summary>The script's lines in the stack, outermost first, as Python shows them.</summary>
                private static string Traceback(Exception error, string id, string name, string code)
                {
                    var lines = code.Replace("\r\n", "\n").Split('\n');
                    var text = new StringBuilder();
                    var frames = new StackTrace(error, true).GetFrames();
                    for (var i = frames.Length - 1; i >= 0; i--)
                    {
                        var file = frames[i].GetFileName();
                        if (file is null || !file.EndsWith("termrest-script-" + id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var number = frames[i].GetFileLineNumber();
                        text.Append("  File \"").Append(name).Append("\", line ").Append(number).Append('\n');
                        if (number >= 1 && number <= lines.Length && lines[number - 1].Trim().Length > 0)
                        {
                            text.Append("    ").Append(lines[number - 1].Trim()).Append('\n');
                        }
                    }

                    return text.Append(Failure(error, true)).ToString();
                }

                internal static string AsText(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString();

                /// <summary>
                /// Variables and headers are text: strings stay as they are, numbers, dates, GUIDs and the like become their
                /// text, anything else becomes JSON; one whose JSON is a string is that string, without the quotes.
                /// </summary>
                internal static string ToText(object value) => value switch
                {
                    null => "",
                    string text => text,
                    bool flag => flag ? "true" : "false",
                    JsonValue node when node.GetValueKind() == JsonValueKind.String => StringOf(node),
                    JsonNode node => node.ToJsonString(Options),
                    DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
                    DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
                    DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
                    TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                    Uri uri => uri.OriginalString, // as written: its ToString() unescapes it (a %20 becomes a space)
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => Unquoted(JsonSerializer.Serialize(value, value.GetType(), Options)),
                };

                /// <summary>A JSON string as the string it holds; other JSON as it is.</summary>
                private static string Unquoted(string json) => json.StartsWith('"') ? JsonSerializer.Deserialize<string>(json, Options) ?? "" : json;

                /// <summary>
                /// The text of a JSON string value: also of one made from a GUID, a date or a char (new JsonObject { ["id"] =
                /// Guid.NewGuid() }), which GetValue&lt;string&gt;() refuses.
                /// </summary>
                private static string StringOf(JsonValue node) => node.TryGetValue<string>(out var text) ? text : Unquoted(node.ToJsonString(Options));

                internal static bool Same(object expected, object actual)
                {
                    expected = Plain(expected);
                    actual = Plain(actual);
                    if (Equals(expected, actual))
                    {
                        return true;
                    }

                    if (expected is null || actual is null)
                    {
                        return false;
                    }

                    if (Number(expected) is { } x && Number(actual) is { } y)
                    {
                        return x == y;
                    }

                    if (expected is JsonNode || actual is JsonNode)
                    {
                        return JsonNode.DeepEquals(Node(expected), Node(actual));
                    }

                    if (expected is IEnumerable left && actual is IEnumerable right && expected is not string && actual is not string)
                    {
                        var a = left.Cast<object>().ToList();
                        var b = right.Cast<object>().ToList();
                        return a.Count == b.Count && a.Zip(b).All(p => Same(p.First, p.Second));
                    }

                    return false;
                }

                internal static string Show(object value)
                {
                    try
                    {
                        return Plain(value) switch
                        {
                            null => "null",
                            string text => JsonSerializer.Serialize(text, Options),
                            bool flag => flag ? "true" : "false",
                            JsonNode node => node.ToJsonString(Options),
                            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                            var other => JsonSerializer.Serialize(other, other.GetType(), Options),
                        };
                    }
                    catch (Exception)
                    {
                        return value?.ToString() ?? "null";
                    }
                }

                private static object Plain(object value) => value is JsonValue node ? node.GetValueKind() switch
                {
                    JsonValueKind.String => StringOf(node),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => node,
                } : value;

                private static double? Number(object value) => value switch
                {
                    byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                    JsonValue node when node.GetValueKind() == JsonValueKind.Number => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture),
                    _ => null,
                };

                private static JsonNode Node(object value) => value as JsonNode ?? JsonSerializer.SerializeToNode(value, value.GetType(), Options);

                /// <summary>
                /// A value of one of Postman's dynamic variables, as requests resolve them, or null for another name; the
                /// names and words they pick from come with the message.
                /// </summary>
                internal static string Dynamic(string name, JsonObject samples)
                {
                    const string Alphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789";
                    string Pick(string key)
                    {
                        var values = (samples?[key] as JsonArray)?.Select(AsText).Where(v => v is not null).ToArray() ?? Array.Empty<string>();
                        return values.Length == 0 ? "" : values[global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(values.Length)];
                    }

                    static int Next(int limit) => global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(limit);
                    static string RandomText(int length, string alphabet)
                    {
                        var letters = new char[length];
                        for (var i = 0; i < length; i++)
                        {
                            letters[i] = alphabet[Next(alphabet.Length)];
                        }

                        return new string(letters);
                    }

                    return name switch
                    {
                        "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
                        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                        "$isoTimestamp" => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                        "$randomInt" => Next(1001).ToString(CultureInfo.InvariantCulture),
                        "$randomBoolean" => Next(2) == 1 ? "true" : "false",
                        "$randomAlphaNumeric" => RandomText(1, Alphanumeric),
                        "$randomHexadecimal" => RandomText(1, "0123456789abcdef"),
                        "$randomFirstName" => Pick("firstNames"),
                        "$randomLastName" => Pick("lastNames"),
                        "$randomFullName" => Pick("firstNames") + " " + Pick("lastNames"),
                        "$randomUserName" => Pick("firstNames").ToLowerInvariant() + "." + Pick("lastNames").ToLowerInvariant() + Next(100).ToString(CultureInfo.InvariantCulture),
                        "$randomEmail" => Pick("firstNames").ToLowerInvariant() + "." + Pick("lastNames").ToLowerInvariant() + "@example.com",
                        "$randomWord" => Pick("words"),
                        "$randomPassword" => RandomText(15, Alphanumeric),
                        _ => null,
                    };
                }

                /// <summary>What the running script writes to the console (Console.WriteLine…): its output.</summary>
                private sealed class Capture : TextWriter
                {
                    private StringBuilder _text;

                    public override Encoding Encoding => Encoding.UTF8;

                    public void Begin()
                    {
                        lock (this)
                        {
                            _text = new StringBuilder();
                        }
                    }

                    public string End()
                    {
                        lock (this)
                        {
                            var text = _text?.ToString() ?? "";
                            _text = null;
                            return text;
                        }
                    }

                    public override void Write(char value)
                    {
                        lock (this)
                        {
                            if (_text is { Length: < 4_000_000 })
                            {
                                _text.Append(value);
                            }
                        }
                    }

                    public override void Write(string value)
                    {
                        lock (this)
                        {
                            if (_text is { Length: < 4_000_000 })
                            {
                                _text.Append(value);
                            }
                        }
                    }

                    public override void Write(char[] buffer, int index, int count)
                    {
                        lock (this)
                        {
                            if (_text is { Length: < 4_000_000 })
                            {
                                _text.Append(buffer, index, count);
                            }
                        }
                    }
                }
            }
        }
        """;
}
