using System.Text;
using TermRest.Core.Scripting;
using TermRest.Tui.Syntax;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>
/// Syntax highlighting of scripts and bodies. Expected lines mark each colored part as «kind text»: C comment, K keyword,
/// S string, N number or constant, V variable, F function, T type, P parameter.
/// </summary>
public sealed class SyntaxTests
{
    [Theory]
    [InlineData("@tr.test('status')", "«F @tr.test»(«S 'status'»)")]
    [InlineData("def check(r):", "«K def» «F check»(r):")]
    [InlineData("    assert r.status == 200 and r.ok is not None  # fine", "    «K assert» r.status == «N 200» «K and» r.ok «K is» «K not» «N None»  «C # fine»")]
    [InlineData("x = f\"user {data[\"name\"]!r:>10} {{literal}}\"", "x = «S f\"user {»data[«S \"name\"»]!r:>«N 10»«S } {{literal}}\"»")]
    [InlineData("s = rb'\\x00' + u\"é\"", "s = «S rb'\\x00'» + «S u\"é\"»")]
    [InlineData("print(len(s), 0x1F, 1_000, 3.5e-2, 2j)", "«F print»(«F len»(s), «N 0x1F», «N 1_000», «N 3.5e-2», «N 2j»)")]
    [InlineData("match command.split():", "«K match» command.«F split»():")]
    [InlineData("    case [action]:", "    «K case» [action]:")]
    [InlineData("match = re.match(r'\\d+', text)", "match = re.«F match»(«S r'\\d+'», text)")]
    [InlineData("tr.environment.set('a', True)", "tr.environment.«F set»(«S 'a'», «N True»)")]
    public void Python(string line, string expected) => Assert.Equal(expected, Colored(ScriptLanguage.Python, line));

    [Fact]
    public void Python_strings_and_f_string_fields_go_on_over_lines()
    {
        AssertColored(ScriptLanguage.Python,
            """"
            class Greeter(Base):
                """Doc
                string"""
                s = 'a\
            b' + 1
                t = 'open
                u = f"""a {
                  x + 1
                } b"""
            """",
            """"
            «K class» «T Greeter»(Base):
                «S """Doc»
            «S     string"""»
                s = «S 'a\»
            «S b'» + «N 1»
                t = «S 'open»
                u = «S f"""a {»
                  x + «N 1»
                «S } b"""»
            """");
    }

    [Theory]
    [InlineData("pm.test(\"Status is 200\", function () {", "pm.«F test»(«S \"Status is 200\"», «K function» () {")]
    [InlineData("    pm.response.to.have.status(200);", "    pm.response.to.have.«F status»(«N 200»);")]
    [InlineData("const re = /a\\/b[/]c/gi, half = total / 2; // done", "«K const» re = «S /a\\/b[/]c/gi», half = total / «N 2»; «C // done»")]
    [InlineData("const r = (a + b) / 2 / c;", "«K const» r = (a + b) / «N 2» / c;")]
    [InlineData("return /^\\d+$/.test(s);", "«K return» «S /^\\d+$/».«F test»(s);")]
    [InlineData("class Client extends Base { static items = new Map(); }", "«K class» «T Client» «K extends» «T Base» { «K static» items = «K new» «T Map»(); }")]
    [InlineData("if (typeof x === 'string' || x.default.delete(1) === undefined) {}", "«K if» («K typeof» x === «S 'string'» || x.default.«F delete»(«N 1») === «N undefined») {}")]
    [InlineData("const s = `a${`b${1}`}`;", "«K const» s = «S `a${`b${»«N 1»«S }`}`»;")]
    public void JavaScript(string line, string expected) => Assert.Equal(expected, Colored(ScriptLanguage.JavaScript, line));

    [Fact]
    public void JavaScript_template_literals_and_comments_go_on_over_lines()
    {
        AssertColored(ScriptLanguage.JavaScript,
            """
            const msg = `Hello ${user.name}, ${
              items.length > 0 ? 'many' : 'none'
            } done`;
            let a = 1; /* start
            still */ let b = true;
            const s = "a\
            b";
            """,
            """
            «K const» msg = «S `Hello ${»user.name«S }, ${»
              items.length > «N 0» ? «S 'many'» : «S 'none'»
            «S } done`»;
            «K let» a = «N 1»; «C /* start»
            «C still */» «K let» b = «N true»;
            «K const» s = «S "a\»
            «S b"»;
            """);
    }

    [Theory]
    [InlineData("#:package Humanizer@2.14.1", "«K #:package» «S Humanizer@2.14.1»")]
    [InlineData("  #if DEBUG // note", "  «K #if» DEBUG «C // note»")]
    [InlineData("#!/usr/bin/env dotnet", "«C #!/usr/bin/env dotnet»")]
    [InlineData("var client = new HttpClient();", "«K var» client = «K new» «T HttpClient»();")]
    [InlineData("tr.Test(\"status\", () => Assert(tr.Response.Status == 200));", "tr.«F Test»(«S \"status\"», () => «F Assert»(tr.Response.Status == «N 200»));")]
    [InlineData("Console.WriteLine($\"logged in as {body[\"json\"][\"user\"]}\");", "Console.«F WriteLine»(«S $\"logged in as {»body[«S \"json\"»][«S \"user\"»]«S }\"»);")]
    [InlineData("char q = '\\'', c = 'x'; var @class = default(int);", "«K char» q = «S '\\''», c = «S 'x'»; «K var» @class = «K default»(«K int»);")]
    [InlineData("var s = $\"{{x}} = {x,5:N2}\";", "«K var» s = «S $\"{{x}} = {»x,«N 5»:N2«S }\"»;")]
    [InlineData("record struct Point(int X, int Y);", "«K record» «K struct» «T Point»(«K int» X, «K int» Y);")]
    [InlineData("if (data.string is null) return;", "«K if» (data.string «K is» «N null») «K return»;")]
    public void CSharp(string line, string expected) => Assert.Equal(expected, Colored(ScriptLanguage.CSharp, line));

    [Fact]
    public void CSharp_verbatim_and_raw_strings_and_comments_go_on_over_lines()
    {
        AssertColored(ScriptLanguage.CSharp,
            """"
            var path = @"C:\temp\
            ""quoted"" end";
            var json = """
                { "a": $"{1}" }
                """;
            /* a
            b */ return null;
            """",
            """"
            «K var» path = «S @"C:\temp\»
            «S ""quoted"" end"»;
            «K var» json = «S """»
            «S     { "a": $"{1}" }»
            «S     """»;
            «C /* a»
            «C b */» «K return» «N null»;
            """");
    }

    [Theory]
    [InlineData("$expected = 200 # status", "«V $expected» = «N 200» «C # status»")]
    [InlineData("if ($tr.Response.Status -eq $expected) { Write-Host \"ok: $($tr.Response.Status) for $env:USERNAME\" -ForegroundColor Green }",
        "«K if» («V $tr».Response.Status «K -eq» «V $expected») { «F Write-Host» «S \"ok: $(»«V $tr».Response.Status«S ) for »«V $env:USERNAME»«S \"» «P -ForegroundColor» Green }")]
    [InlineData("[int]$count = [Math]::Round(3.7) + $null.Count", "«T [int]»«V $count» = «T [Math]»::«F Round»(«N 3.7») + «N $null».Count")]
    [InlineData("Get-ChildItem -Path . -Recurse | Where-Object { $_.Length -gt 1kb } | ForEach-Object Name",
        "«F Get-ChildItem» «P -Path» . «P -Recurse» | «F Where-Object» { «V $_».Length «K -gt» «N 1kb» } | «F ForEach-Object» Name")]
    [InlineData("Function Get-Token { Param([string]$Name) Return $Name }", "«K Function» «F Get-Token» { «K Param»(«T [string]»«V $Name») «K Return» «V $Name» }")]
    [InlineData("$headers = @{ Accept = 'application/json'; Authorization = \"Bearer $token\" }",
        "«V $headers» = @{ Accept = «S 'application/json'»; Authorization = «S \"Bearer »«V $token»«S \"» }")]
    [InlineData("git status; ls", "«F git» status; «F ls»")]
    [InlineData("if ($a -cne 'x' -and -not $b) { Invoke-Thing @params }", "«K if» («V $a» «K -cne» «S 'x'» «K -and» «K -not» «V $b») { «F Invoke-Thing» «V @params» }")]
    [InlineData("Write-Output a#b # real", "«F Write-Output» a#b «C # real»")]
    [InlineData("foreach ($i in 1..10) { $list[$i] }", "«K foreach» («V $i» «K in» «N 1»..«N 10») { «V $list»[«V $i»] }")]
    public void PowerShell(string line, string expected) => Assert.Equal(expected, Colored(ScriptLanguage.PowerShell, line));

    [Fact]
    public void PowerShell_here_strings_strings_and_comments_go_on_over_lines()
    {
        AssertColored(ScriptLanguage.PowerShell,
            """
            $body = @"
            { "user": "$user", "id": $($ids[0]) }
            "@
            Invoke-RestMethod -Uri $url -Body $body
            <# Help
               text #> $a = 'it''s
            fine'
            """,
            """
            «V $body» = «S @"»
            «S { "user": "»«V $user»«S ", "id": $(»«V $ids»[«N 0»]«S ) }»
            «S "@»
            «F Invoke-RestMethod» «P -Uri» «V $url» «P -Body» «V $body»
            «C <# Help»
            «C    text #>» «V $a» = «S 'it''s»
            «S fine'»
            """);
    }

    [Theory]
    [InlineData("{\"name\": \"Zoë\", \"tags\": [\"a\"], \"id\": -12.5e3, \"ok\": true, \"none\": null}",
        "{«V \"name\"»: «S \"Zoë\"», «V \"tags\"»: [«S \"a\"»], «V \"id\"»: «N -12.5e3», «V \"ok\"»: «K true», «V \"none\"»: «K null»}")]
    [InlineData("  \"say \\\"hi\\\":\" : \"a:b\",", "  «V \"say \\\"hi\\\":\"» : «S \"a:b\"»,")]
    [InlineData("[1, 2-3, x1, truex] // note", "[«N 1», «N 2»-«N 3», x1, truex] «C // note»")]
    public void Json(string line, string expected) => Assert.Equal(expected, Colored("json", line));

    [Fact]
    public void Json_comments_go_on_over_lines() =>
        AssertColored("json", "{ /* a\nb */ \"k\": 1 }", "{ «C /* a»\n«C b */» «V \"k\"»: «N 1» }");

    [Theory]
    [InlineData("xml", "<?xml version=\"1.0\"?>", "«T <?xml» «V version»=«S \"1.0\"»?>")]
    [InlineData("xml", "<a:item id='1' xmlns:a=\"urn:x\">Tom &amp; Jerry &#x26;</a:item>",
        "<«K a:item» «V id»=«S '1'» «V xmlns:a»=«S \"urn:x\"»>Tom «N &amp;» Jerry «N &#x26;»</«K a:item»>")]
    [InlineData("xml", "<!-- note --><b/> 1 < 2 & 3", "«C <!-- note -->»<«K b»/> 1 < 2 & 3")]
    [InlineData("xml", "<![CDATA[a < b]]>", "«T <![CDATA[»«S a < b»«T ]]>»")]
    [InlineData("html", "<!DOCTYPE html><input disabled value=on>", "«T <!DOCTYPE» «V html»><«K input» «V disabled» «V value»=«S on»>")]
    public void Markup(string language, string line, string expected) => Assert.Equal(expected, Colored(language, line));

    [Fact]
    public void Markup_tags_values_comments_and_script_content_go_on_over_lines()
    {
        AssertColored("html",
            """
            <a href="x
            y" title=t
              class='c'>text <!-- a
            b --> <script type="module">
            if (a < b) {}
            </script><p>&lt;</p>
            """,
            """
            <«K a» «V href»=«S "x»
            «S y"» «V title»=«S t»
              «V class»=«S 'c'»>text «C <!-- a»
            «C b -->» <«K script» «V type»=«S "module"»>
            if (a < b) {}
            </«K script»><«K p»>«N &lt;»</«K p»>
            """);
    }

    [Theory]
    [InlineData(ScriptLanguage.Python, "f\"{")]
    [InlineData(ScriptLanguage.CSharp, "$\"{")]
    [InlineData(ScriptLanguage.PowerShell, "\"$(")]
    public void Interpolations_nested_on_a_long_line_do_not_run_out_of_stack(string language, string opening)
    {
        var line = string.Concat(Enumerable.Repeat(opening, 30_000));
        var kinds = new SyntaxKind[line.Length];

        var state = SyntaxLexer.For(language)!.Lex(line, kinds, LexState.Code);

        Assert.Equal((LexMode.String, SyntaxKind.String), (state.Mode, kinds[0]));
    }

    [Fact]
    public void Regular_expressions_that_never_close_on_a_long_line_stay_quick()
    {
        var line = string.Concat(Enumerable.Repeat("(/[", 30_000)) + "x = /ab/";
        var kinds = new SyntaxKind[line.Length];
        var clock = System.Diagnostics.Stopwatch.StartNew();

        SyntaxLexer.For(ScriptLanguage.JavaScript)!.Lex(line, kinds, LexState.Code);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData(ScriptLanguage.PowerShell, "[a ", 60_000)] // [types] that never close (6.5 s before)
    [InlineData(ScriptLanguage.PowerShell, "${", 400_000)] // ${names} that never close
    [InlineData(ScriptLanguage.CSharp, "$", 90_000)] // $ and @ that lead to no string (7 s before)
    [InlineData(ScriptLanguage.CSharp, "@", 90_000)]
    [InlineData(ScriptLanguage.CSharp, "'\\", 90_000)] // 'characters' that never close
    [InlineData(ScriptLanguage.Python, "'''", 60_000)] // closing quotes counted to the end of the line
    [InlineData(ScriptLanguage.Python, "\"", 180_000)]
    [InlineData("json", "\"a\" ", 120_000)] // a key is looked for after each string
    [InlineData("html", "<a b=\"", 90_000)] // values that never close
    [InlineData("xml", "<!--", 120_000)]
    public void Scans_that_could_go_to_the_end_of_a_long_line_each_time_stay_quick(string language, string piece, int times)
    {
        var line = string.Concat(Enumerable.Repeat(piece, times));
        var kinds = new SyntaxKind[line.Length];
        var clock = System.Diagnostics.Stopwatch.StartNew();

        SyntaxLexer.For(language)!.Lex(line, kinds, LexState.Code);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}"); // a few ms
    }

    private static void AssertColored(string language, string script, string expected) =>
        Assert.Equal(expected.ReplaceLineEndings("\n"), Colored(language, script.ReplaceLineEndings("\n")));

    /// <summary>The script with its colored parts marked, lexed line by line as the editor does.</summary>
    private static string Colored(string language, string script)
    {
        var lexer = SyntaxLexer.For(language)!;
        var state = LexState.Code;
        var colored = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            var kinds = new SyntaxKind[line.Length];
            state = lexer.Lex(line, kinds, state);
            for (var i = 0; i < line.Length;)
            {
                var end = i + 1;
                while (end < line.Length && kinds[end] == kinds[i])
                {
                    end++;
                }

                colored.Append(kinds[i] == SyntaxKind.Plain ? line[i..end] : $"«{Code(kinds[i])} {line[i..end]}»");
                i = end;
            }

            colored.Append('\n');
        }

        return colored.ToString(0, colored.Length - 1);
    }

    private static char Code(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Comment => 'C',
        SyntaxKind.Keyword => 'K',
        SyntaxKind.String => 'S',
        SyntaxKind.Number => 'N',
        SyntaxKind.Variable => 'V',
        SyntaxKind.Function => 'F',
        SyntaxKind.Type => 'T',
        _ => 'P',
    };
}
