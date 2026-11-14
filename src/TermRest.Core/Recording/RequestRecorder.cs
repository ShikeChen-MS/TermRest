using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermRest.Core.Http;
using TermRest.Core.Json;

namespace TermRest.Core.Recording;

/// <summary>Options for <see cref="RequestRecorder.Start"/>.</summary>
public sealed class RecordingOptions
{
    /// <summary>Name of the workspace, written to the recording.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The workspace folder (kept with the temporary files, to tell unsaved recordings apart).</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>Extra header, parameter and field names to redact (the workspace setting <c>recordingRedact</c>).</summary>
    public IReadOnlyCollection<string> RedactNames { get; init; } = [];

    /// <summary>Folder for the temporary files; default <see cref="RequestRecorder.DefaultTempRoot"/>.</summary>
    public string? TempRoot { get; init; }
}

/// <summary>A saved recording.</summary>
public sealed record RecordingResult(string Path, int Requests, long Bytes, int Redactions);

/// <summary>
/// A recording whose temporary files were left behind: TermRest was closed or crashed before it was saved. Its requests
/// are counted when it is opened (<see cref="RequestRecorder.Recover"/>).
/// </summary>
public sealed record UnsavedRecording(string TempDirectory, DateTimeOffset StartedAt, string? WorkspaceName, string? WorkspaceRoot, long Bytes);

/// <summary>Where an entry is in the temporary file.</summary>
internal readonly record struct SpoolEntry(long Offset, long Length, DateTimeOffset? StartedAt);

/// <summary>
/// Records the requests sent while it runs and saves them as one HAR 1.2 file (HTTP Archive, JSON) that browser dev
/// tools and HAR viewers open.
/// <list type="bullet">
/// <item>Memory: each exchange is written to a private temporary file when it completes, on a background thread, so a
/// recording never grows in memory (when several are still waiting to be written, recording the next one waits).
/// Bodies are written in pieces; saving streams the temporary file into the destination with a small buffer.</item>
/// <item>Secrets are redacted before anything is written (see <see cref="Redactor"/>).</item>
/// <item>Temporary files (see <see cref="DefaultTempRoot"/>) are private to the user and deleted when the recording is
/// saved or discarded. If TermRest ends before that, <see cref="FindUnsaved"/> finds them and the recording can
/// still be saved (<see cref="Recover"/>).</item>
/// </list>
/// </summary>
public sealed class RequestRecorder : IDisposable
{
    /// <summary>Overrides the folder for temporary recording files.</summary>
    public const string TempDirectoryVariable = "TERMREST_TEMP_DIR";

    internal const string DirectoryPrefix = "termrest-recording-";
    internal const string SpoolFileName = "entries.spool";
    internal const string InfoFileName = "recording.json";

    /// <summary>Large temporary files on Linux when /tmp is in memory (tmpfs).</summary>
    internal const string LargeTempDirectory = "/var/tmp";

    /// <summary>
    /// Recorded requests that may wait to be written; <see cref="Record"/> waits beyond that, so a recording does not grow
    /// in memory when requests are sent faster than they are written.
    /// </summary>
    internal const int MaxPendingWrites = 4;

    private const string DeletedMarker = "saved";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupOrOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private const int BufferSize = 64 * 1024;
    private const string EntriesMarker = "@@termrest-entries@@";

    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, NewLine = "\n", Encoder = MinimalJsonEncoder.Instance };

    private readonly Lock _gate = new();
    private readonly FileStream _spool;
    private readonly List<SpoolEntry> _entries;
    private readonly Redactor _redactor;
    private RecordingInfo _info;
    private Task _tail = Task.CompletedTask;
    private int _pending;
    private int _redactions;
    private int _withFiles;
    private long _bytes;
    private bool _accepting;
    private bool _finished;

    private RequestRecorder(string directory, FileStream spool, RecordingInfo info, List<SpoolEntry> entries, bool recovered)
    {
        TempDirectory = directory;
        _spool = spool;
        _info = info;
        _entries = entries;
        _redactor = new Redactor(info.RedactNames);
        _bytes = spool.Length;
        _accepting = !recovered;
        IsRecovered = recovered;
    }

    /// <summary>
    /// Raised when a request is recorded (on the thread that called <see cref="Record"/>) and when it has been written
    /// or could not be (on a thread-pool thread).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The private folder with the temporary files.</summary>
    public string TempDirectory { get; }

    public DateTimeOffset StartedAt => _info.StartedAt;

    public string? WorkspaceName => _info.WorkspaceName;

    /// <summary>A recording found after TermRest ended without saving it (it does not record new requests).</summary>
    public bool IsRecovered { get; }

    /// <summary>Whether sent requests are recorded (until the recording is saved, discarded or disposed).</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    /// <summary>Requests recorded, including any still being written.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count + _pending;
            }
        }
    }

    /// <summary>Requests recorded that are still being written.</summary>
    internal int PendingWrites
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Size of the temporary file.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Values redacted so far (not known for recovered recordings).</summary>
    public int Redactions
    {
        get
        {
            lock (_gate)
            {
                return _redactions;
            }
        }
    }

    /// <summary>
    /// Requests recorded that sent a file (the body, or a form-data field): the recording has the file's path, not its
    /// content. Not known for recovered recordings.
    /// </summary>
    public int RequestsWithFiles
    {
        get
        {
            lock (_gate)
            {
                return _withFiles;
            }
        }
    }

    /// <summary>The last problem writing a request, if any (the recording goes on without it).</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The extra names it redacts (normalized; see <see cref="RecordingOptions.RedactNames"/>): those it started with and
    /// those added since (<see cref="AddRedactNames"/>), which it keeps until it stops.
    /// </summary>
    public IReadOnlySet<string> ExtraNames => _redactor.ExtraNames;

    /// <summary>
    /// The folder for temporary recording files: <c>$TERMREST_TEMP_DIR</c>, else the user's temporary folder on
    /// Windows (<c>%TEMP%</c>) and macOS (<c>$TMPDIR</c>, private to the user), else <c>$TMPDIR</c> when set, else
    /// <c>/tmp</c> — or <c>/var/tmp</c> when <c>/tmp</c> is a RAM file system (tmpfs), where a large recording would
    /// take memory after all. Recordings get their own folder in it, readable only by the user.
    /// </summary>
    public static string DefaultTempRoot() => ChooseTempRoot(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
        Environment.GetEnvironmentVariable(TempDirectoryVariable),
        Environment.GetEnvironmentVariable("TMPDIR"),
        Path.GetTempPath(),
        IsInMemoryFileSystem,
        Directory.Exists);

    internal static string ChooseTempRoot(bool privateSystemTemp, string? configured, string? tmpDir, string systemTemp, Func<string, bool> isInMemory, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (privateSystemTemp || !string.IsNullOrWhiteSpace(tmpDir))
        {
            return systemTemp;
        }

        return isInMemory(systemTemp) && exists(LargeTempDirectory) ? LargeTempDirectory : systemTemp;
    }

    /// <summary>Starts a recording: creates its private temporary folder and file.</summary>
    public static RequestRecorder Start(RecordingOptions? options = null)
    {
        options ??= new RecordingOptions();
        var root = options.TempRoot ?? DefaultTempRoot();
        string directory;
        try
        {
            directory = CreatePrivateDirectory(root);
        }
        catch (Exception ex) when (options.TempRoot is null && ex is IOException or UnauthorizedAccessException
                                   && !string.Equals(Path.GetFullPath(root), Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal))
        {
            directory = CreatePrivateDirectory(Path.GetTempPath()); // e.g. /var/tmp is not writable here
        }

        try
        {
            var info = new RecordingInfo(DateTimeOffset.Now, options.WorkspaceName, options.WorkspaceRoot, [.. options.RedactNames], Environment.ProcessId);
            using (var file = new FileStream(Path.Combine(directory, InfoFileName), FileOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                file.Write(info.ToJson());
            }

            var spool = new FileStream(Path.Combine(directory, SpoolFileName), FileOptions(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            return new RequestRecorder(directory, spool, info, [], recovered: false);
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }
    }

    /// <summary>
    /// Recordings whose temporary files are left over in <paramref name="tempRoot"/> (default: <see cref="DefaultTempRoot"/>,
    /// plus the system's temporary folder unless <c>$TERMREST_TEMP_DIR</c> is set), oldest first. Recordings that
    /// another TermRest is still making are skipped; left-over files without a complete request are deleted. Only
    /// folders private to the user are looked at: in a shared folder such as /tmp, others may be another user's.
    /// </summary>
    public static IReadOnlyList<UnsavedRecording> FindUnsaved(string? tempRoot = null)
    {
        IEnumerable<string> roots = tempRoot is not null ? [tempRoot]
            : string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TempDirectoryVariable)) ? [DefaultTempRoot(), Path.GetTempPath()]
            : [DefaultTempRoot()];
        var found = new List<UnsavedRecording>();
        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            List<string> directories;
            try
            {
                directories = Directory.Exists(root) ? [.. Directory.EnumerateDirectories(root, DirectoryPrefix + "*")] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (IsPrivate(directory) && Inspect(directory) is { } unsaved)
                {
                    found.Add(unsaved);
                }
            }
        }

        return [.. found.OrderBy(u => u.StartedAt)];
    }

    /// <summary>Opens an unsaved recording to save or discard it. Its last request is dropped if it was cut off while being written.</summary>
    public static RequestRecorder Recover(UnsavedRecording unsaved)
    {
        var spool = new FileStream(Path.Combine(unsaved.TempDirectory, SpoolFileName), FileOptions(FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        try
        {
            var entries = Scan(spool);
            var info = RecordingInfo.TryRead(Path.Combine(unsaved.TempDirectory, InfoFileName))
                       ?? new RecordingInfo(unsaved.StartedAt, unsaved.WorkspaceName, unsaved.WorkspaceRoot, [], 0);
            return new RequestRecorder(unsaved.TempDirectory, spool, info, entries, recovered: true);
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records an exchange (in the background; this waits only while several earlier requests are still being written).
    /// Requests that were never sent, such as an invalid URL, are skipped.
    /// </summary>
    public void Record(HttpExchange exchange, string? environment = null)
    {
        var request = exchange.Request;
        if (exchange.Response is null && (request.Errors.Count > 0 || request.Uri is null))
        {
            return;
        }

        var recorded = RecordedExchange.Capture(exchange, environment);
        Task? backlog = null;
        lock (_gate)
        {
            if (!_accepting)
            {
                return;
            }

            if (_pending >= MaxPendingWrites)
            {
                backlog = _tail;
            }

            _pending++;
            if (request.Body.HasFiles)
            {
                _withFiles++;
            }

            _tail = _tail.ContinueWith(_ => Write(recorded), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        backlog?.Wait();
    }

    /// <summary>Completes when every recorded request has been written to the temporary file.</summary>
    public Task WhenWritten()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    /// <summary>
    /// Adds names to redact (see <see cref="RecordingOptions.RedactNames"/>), as when the workspace settings change while
    /// recording: the requests written from now on are redacted with them too, and the recording lists them; requests
    /// written already stay as they are. Names are never removed. Returns the names new to the recording (none once it
    /// no longer records).
    /// </summary>
    public IReadOnlyList<string> AddRedactNames(IEnumerable<string> names)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return [];
            }

            var known = SecretNames.NormalizeAll(_info.RedactNames);
            List<string> added = [.. names.Where(name => SecretNames.Normalize(name) is { Length: > 0 } normalized && known.Add(normalized))];
            if (added.Count > 0)
            {
                _redactor.AddExtraNames(added);
                _info = _info with { RedactNames = [.. _info.RedactNames, .. added] };
                try
                {
                    WriteInfo(_info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Read only for a recording recovered after TermRest ended: its HAR file would not list these names,
                    // though its requests are redacted with them all the same.
                }
            }

            return added;
        }
    }

    /// <summary>
    /// Stops recording and writes the HAR file to <paramref name="path"/> (replacing it; missing folders are created),
    /// then deletes the temporary files. If saving fails, nothing is lost: the recording can be saved again.
    /// </summary>
    public RecordingResult Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        bool wasRecording;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            wasRecording = _accepting;
            _accepting = false;
        }

        List<SpoolEntry> entries;
        try
        {
            WhenWritten().Wait();
            lock (_gate)
            {
                // Sorted by start time, as HAR prefers; requests run one at a time are already in that order.
                entries = [.. _entries.OrderBy(e => e.StartedAt ?? DateTimeOffset.MinValue)];
            }

            WriteHar(path, entries, DateTimeOffset.Now);
        }
        catch
        {
            lock (_gate)
            {
                _accepting = wasRecording;
            }

            throw;
        }

        var result = new RecordingResult(path, entries.Count, new FileInfo(path).Length, Redactions);
        Finish(deleteFiles: true);
        return result;
    }

    /// <summary>Stops recording and deletes the temporary files.</summary>
    public void Discard()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _accepting = false;
        }

        WhenWritten().Wait();
        Finish(deleteFiles: true);
    }

    /// <summary>
    /// Stops recording and closes the temporary files without deleting them, so a recording that was not saved can
    /// be recovered later (<see cref="FindUnsaved"/>). Empty recordings are deleted.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _accepting = false;
        }

        WhenWritten().Wait();
        Finish(deleteFiles: _entries.Count == 0);
    }

    /// <summary>
    /// Complete entries in a temporary file (each ends with a <c>}</c> line), the first <paramref name="max"/> of them; a
    /// cut-off last entry is left out. Stops reading once it has found them.
    /// </summary>
    internal static List<SpoolEntry> Scan(Stream stream, int max = int.MaxValue)
    {
        var entries = new List<SpoolEntry>();
        var buffer = new byte[BufferSize];
        stream.Position = 0;
        long bufferStart = 0; // where buffer[0] is in the file
        long start = 0;
        var kept = 0;
        int read;
        while (entries.Count < max && (read = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
        {
            var data = buffer.AsSpan(0, kept + read);

            // Strings never contain raw line breaks, so "\n}\n" only ends a whole (indented) entry.
            for (var from = 0; entries.Count < max;)
            {
                var index = data[from..].IndexOf("\n}\n"u8);
                if (index < 0)
                {
                    break;
                }

                var end = bufferStart + from + index + 2; // the line break after the "}"
                entries.Add(new SpoolEntry(start, end - start, null));
                start = end + 1;
                from += index + 2;
            }

            // The last two bytes may start a "\n}\n" that the next read completes.
            kept = Math.Min(2, data.Length);
            data[^kept..].CopyTo(buffer);
            bufferStart += data.Length - kept;
        }

        return entries;
    }

    /// <summary>Whether a temporary file ends with a complete entry, as it does unless TermRest ended while writing one.</summary>
    private static bool EndsWithEntry(Stream stream)
    {
        if (stream.Length < 3)
        {
            return false;
        }

        Span<byte> end = stackalloc byte[3];
        stream.Position = stream.Length - 3;
        stream.ReadExactly(end);
        return end.SequenceEqual("\n}\n"u8);
    }

    private void Write(RecordedExchange recorded)
    {
        long start = 0;
        try
        {
            start = _spool.Length;
            _spool.Position = start;
            var redactor = _redactor.For(recorded.Exchange.Request);
            using (var writer = new Utf8JsonWriter(_spool, JsonOptions))
            {
                HarEntryWriter.Write(writer, recorded, redactor);
            }

            var length = _spool.Position - start;
            _spool.Write("\n"u8);
            _spool.Flush();
            lock (_gate)
            {
                _pending--;
                _entries.Add(new SpoolEntry(start, length, recorded.Exchange.StartedAt));
                _bytes = start + length + 1;
                _redactions += redactor.Count;
            }
        }
#pragma warning disable CA1031 // A request that cannot be recorded (disk full …) must not end the recording or the application.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            try
            {
                _spool.SetLength(start);
                _spool.Position = start;
            }
            catch (IOException)
            {
                // The next entry starts where the file ends; a cut-off entry is skipped when the file is read.
            }

            lock (_gate)
            {
                _pending--;
                LastError = $"A request could not be recorded: {ex.Message}";
            }
        }

        // Not on this thread: a listener that waits for the UI thread (Terminal.Gui's Invoke does) must not hold up
        // this write, which Save may be waiting for on the UI thread.
        if (Changed is { } changed)
        {
            ThreadPool.QueueUserWorkItem(_ => changed(this, EventArgs.Empty));
        }
    }

    /// <summary>Replaces the recording's <c>recording.json</c>: written next to it, then moved over it.</summary>
    private void WriteInfo(RecordingInfo info)
    {
        var path = Path.Combine(TempDirectory, InfoFileName);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileOptions(FileMode.Create, FileAccess.Write, FileShare.None)))
        {
            file.Write(info.ToJson());
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Writes the HAR file next to <paramref name="path"/>, then moves it into place (an existing file is replaced only when complete).</summary>
    private void WriteHar(string path, List<SpoolEntry> entries, DateTimeOffset stoppedAt)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp");
        try
        {
            using (var output = new FileStream(temp, FileOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                var (prefix, indent, suffix) = Skeleton(entries.Count, stoppedAt);
                output.Write(prefix);
                var buffer = new byte[BufferSize];
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Write(",\n"u8);
                    }

                    CopyIndented(entries[i], output, indent, buffer);
                }

                if (entries.Count > 0)
                {
                    output.Write("\n"u8);
                }

                output.Write(suffix);
                output.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    /// <summary>
    /// The HAR document around the entries: written with an indented JSON writer and a marker in place of the entries,
    /// then split at the marker's line. The marker's indentation is the indentation of every entry line.
    /// </summary>
    private (byte[] Prefix, byte[] Indent, byte[] Suffix) Skeleton(int count, DateTimeOffset stoppedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("log");
            writer.WriteString("version", "1.2");
            writer.WriteStartObject("creator");
            writer.WriteString("name", "TermRest");
            writer.WriteString("version", typeof(RequestRecorder).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
            writer.WriteEndObject();
            writer.WriteStartArray("pages");
            writer.WriteEndArray();
            writer.WriteString("comment", string.Create(CultureInfo.InvariantCulture,
                $"Recorded with TermRest from {StartedAt:yyyy-MM-dd HH:mm:ss} to {stoppedAt:yyyy-MM-dd HH:mm:ss}. Credentials, cookies, tokens and secret variables are replaced with {Redactor.Placeholder}; each entry lists what was redacted in _termrest.redacted. Files sent with a request (a body file or a form-data file) are not included: their _file entry gives the path, size and modification time."));
            writer.WriteStartObject("_termrest");
            writer.WriteString("startedAt", HarEntryWriter.FormatTime(StartedAt));
            writer.WriteString("stoppedAt", HarEntryWriter.FormatTime(stoppedAt));
            if (WorkspaceName is not null)
            {
                writer.WriteString("workspace", WorkspaceName);
            }

            writer.WriteNumber("requests", count);
            if (IsRecovered)
            {
                writer.WriteBoolean("recovered", true);
            }

            writer.WriteStartObject("redaction");
            writer.WriteString("placeholder", Redactor.Placeholder);
            if (!IsRecovered)
            {
                writer.WriteNumber("count", Redactions);
            }

            writer.WriteStartArray("rules");
            writer.WriteStringValue("Authorization and Proxy-Authorization credentials (the scheme, such as Bearer, is kept)");
            writer.WriteStringValue("Cookie values in Cookie and Set-Cookie headers (cookie names and attributes are kept)");
            writer.WriteStringValue("Headers, URL parameters, and JSON and form fields named like credentials: password, secret, token, API key, signature, session id …");
            writer.WriteStringValue("Values of auth credentials, secret variables and variables named like credentials, wherever they appear");
            writer.WriteStringValue("Values redacted by name, wherever they appear later in the recording");
            writer.WriteEndArray();
            if (_info.RedactNames.Length > 0)
            {
                writer.WriteStartArray("extraNames");
                foreach (var name in _info.RedactNames)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteStartObject("system");
            writer.WriteString("os", RuntimeInformation.OSDescription);
            writer.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
            writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("entries");
            writer.WriteStringValue(EntriesMarker);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        var marker = Encoding.UTF8.GetBytes($"\"{EntriesMarker}\"");
        var index = bytes.AsSpan().IndexOf(marker);
        var lineStart = bytes.AsSpan(0, index).LastIndexOf((byte)'\n') + 1;
        var lineEnd = index + marker.Length; // the line break after the marker
        return (bytes[..lineStart], bytes[lineStart..index], [.. bytes[(lineEnd + 1)..], (byte)'\n']);
    }

    /// <summary>Copies one entry from the temporary file, indenting each of its lines to its place in the document.</summary>
    private void CopyIndented(SpoolEntry entry, Stream output, byte[] indent, byte[] buffer)
    {
        output.Write(indent);
        _spool.Position = entry.Offset;
        var remaining = entry.Length;
        while (remaining > 0)
        {
            var read = _spool.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new IOException("The temporary recording file ended unexpectedly.");
            }

            remaining -= read;
            var chunk = buffer.AsSpan(0, read);
            int newline;
            while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
            {
                output.Write(chunk[..(newline + 1)]);
                output.Write(indent);
                chunk = chunk[(newline + 1)..];
            }

            output.Write(chunk);
        }
    }

    private void Finish(bool deleteFiles)
    {
        lock (_gate)
        {
            _finished = true;
            _accepting = false;
        }

        _spool.Dispose();
        if (deleteFiles)
        {
            DeleteDirectory(TempDirectory);
        }
    }

    private static UnsavedRecording? Inspect(string directory)
    {
        var spoolPath = Path.Combine(directory, SpoolFileName);
        try
        {
            if (File.Exists(Path.Combine(directory, DeletedMarker))
                || (!File.Exists(spoolPath) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddMinutes(-5)))
            {
                DeleteDirectory(directory); // saved or discarded, but the files could not be deleted then
                return null;
            }

            if (!File.Exists(spoolPath))
            {
                return null; // being created right now
            }

            bool complete;
            long length;
            // Exclusive: fails while a running TermRest records into it. One complete request is enough to offer it.
            using (var stream = new FileStream(spoolPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                complete = EndsWithEntry(stream) || Scan(stream, max: 1).Count > 0;
                length = stream.Length;
            }

            if (!complete)
            {
                DeleteDirectory(directory);
                return null;
            }

            var info = RecordingInfo.TryRead(Path.Combine(directory, InfoFileName));
            return new UnsavedRecording(directory, info?.StartedAt ?? new DateTimeOffset(Directory.GetCreationTime(directory)), info?.WorkspaceName, info?.WorkspaceRoot, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // in use, or another user's
        }
    }

    /// <summary>
    /// Whether a folder can be one of this user's recordings: TermRest makes them private (0700 on Unix), never links.
    /// Others, in a shared folder such as /tmp, may be another user's and are not opened.
    /// </summary>
    private static bool IsPrivate(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).LinkTarget is null
                   && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(directory) & GroupOrOthers) == 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CreatePrivateDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            CreateDirectory(root);
        }

        // Unguessable, so nobody can prepare it in a shared folder such as /tmp.
        var name = $"{DirectoryPrefix}{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";
        var path = Path.Combine(root, name);
        if (Directory.Exists(path))
        {
            throw new IOException($"'{path}' already exists.");
        }

        CreateDirectory(path);
        return path;
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path); // inside the user's profile: private already
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectory);
        }
    }

    private static FileStreamOptions FileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share, BufferSize = BufferSize };
        if (!OperatingSystem.IsWindows() && mode is FileMode.CreateNew or FileMode.Create or FileMode.OpenOrCreate)
        {
            options.UnixCreateMode = PrivateFile;
        }

        return options;
    }

    private static bool IsInMemoryFileSystem(string path)
    {
        try
        {
            var format = new DriveInfo(Path.TrimEndingDirectorySeparator(path)).DriveFormat;
            return format.Equals("tmpfs", StringComparison.OrdinalIgnoreCase) || format.Equals("ramfs", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mark it: it is deleted later instead of being offered as an unsaved recording.
            try
            {
                File.WriteAllText(Path.Combine(directory, DeletedMarker), "");
                TryDeleteFile(Path.Combine(directory, SpoolFileName));
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do; the folder is private to the user.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>What is known about a recording besides its entries (<c>recording.json</c> next to them).</summary>
    private sealed record RecordingInfo(DateTimeOffset StartedAt, string? WorkspaceName, string? WorkspaceRoot, string[] RedactNames, int ProcessId)
    {
        public byte[] ToJson()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, JsonOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("format", "termrest-recording");
                writer.WriteNumber("version", 1);
                writer.WriteString("startedAt", StartedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("workspace", WorkspaceName);
                writer.WriteString("workspaceRoot", WorkspaceRoot);
                writer.WriteStartArray("redact");
                foreach (var name in RedactNames)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
                writer.WriteNumber("processId", ProcessId);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        public static RecordingInfo? TryRead(string path)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("startedAt", out var started) || started.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(started.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt))
                {
                    return null;
                }

                string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var redact = root.TryGetProperty("redact", out var names) && names.ValueKind == JsonValueKind.Array
                    ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToArray()
                    : [];
                var processId = root.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id) ? id : 0;
                return new RecordingInfo(startedAt, Text("workspace"), Text("workspaceRoot"), redact, processId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return null; // missing or damaged: the recording is still offered, without these details
            }
        }
    }
}
