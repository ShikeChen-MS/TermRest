using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermRest.Core.IO;

namespace TermRest.Core.Workspace;

/// <summary>
/// The user names and passwords the user gave for proxies (Tools › Settings › Proxy sign-in): kept in
/// <c>proxy-credentials.json</c> in the user's configuration folder (see <see cref="UserSettings.DefaultDirectory"/>), never
/// in a workspace, whose settings come with its repository. Only the user can read the file (on Unix it is written with
/// mode 600), and on Windows the passwords in it are encrypted for the user's account (DPAPI) too.
/// </summary>
public sealed class ProxyCredentialStore
{
    public const string FileName = "proxy-credentials.json";

    /// <summary>The key of the credentials for the system's proxy (the OS's settings, <c>HTTPS_PROXY</c>).</summary>
    public const string SystemProxy = "system";

    private const string DpapiPrefix = "dpapi:";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly byte[] Entropy = "TermRest proxy credentials"u8.ToArray();

    private readonly Dictionary<string, ProxyCredentialEntry> _entries;

    private ProxyCredentialStore(Dictionary<string, ProxyCredentialEntry> entries, string? loadError)
    {
        _entries = entries;
        LoadError = loadError;
    }

    /// <summary>Why the file could not be read (<c>path: problem</c>), if it could not: <see cref="Save"/> does not save over it then.</summary>
    public string? LoadError { get; }

    public static ProxyCredentialStore Empty() => new(new Dictionary<string, ProxyCredentialEntry>(StringComparer.Ordinal), null);

    /// <summary>Reads the file from <paramref name="directory"/> (default: <see cref="UserSettings.DefaultDirectory"/>); empty when there is none.</summary>
    public static ProxyCredentialStore Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? UserSettings.DefaultDirectory(), FileName);
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            var (text, _) = TextFile.Read(path);
            var file = JsonSerializer.Deserialize(text, CoreJsonContext.Default.ProxyCredentialFile);
            return new ProxyCredentialStore(new Dictionary<string, ProxyCredentialEntry>(file?.Proxies ?? new(), StringComparer.Ordinal), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ProxyCredentialStore(new Dictionary<string, ProxyCredentialEntry>(StringComparer.Ordinal), $"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The key credentials are kept under for a proxy setting (see <see cref="WorkspaceSettings.Proxy"/>): <c>system</c> for
    /// the system's proxy, <c>scheme://host:port</c> for a proxy URL (its user, password and path left out), null for none.
    /// </summary>
    public static string? KeyOf(string? proxy)
    {
        var value = proxy?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return SystemProxy;
        }

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant()
            : value.ToLowerInvariant();
    }

    /// <summary>
    /// The user name and password kept for <paramref name="proxy"/>, if any. A password that can't be decrypted (encrypted by
    /// another account or computer) is none: the user name goes without one.
    /// </summary>
    public NetworkCredential? Find(string? proxy) =>
        KeyOf(proxy) is { } key && _entries.TryGetValue(key, out var entry) && entry.User.Length > 0 ? Credential(entry.User, Reveal(entry.Password)) : null;

    /// <summary>The user name kept for <paramref name="proxy"/> (empty when none), and whether a password is kept with it.</summary>
    public (string User, bool HasPassword) Describe(string? proxy) =>
        KeyOf(proxy) is { } key && _entries.TryGetValue(key, out var entry) ? (entry.User, !string.IsNullOrEmpty(entry.Password)) : ("", false);

    /// <summary>
    /// Keeps <paramref name="user"/> and <paramref name="password"/> for <paramref name="proxy"/>; an empty user forgets
    /// them, and a null password keeps the one kept for <paramref name="keptFrom"/>: <paramref name="proxy"/>'s own by
    /// default, or another's when the sign-in goes with a proxy setting that changed (the dialog showed that one's, masked).
    /// Returns false when nothing changed (there is nothing to save then).
    /// </summary>
    /// <exception cref="CryptographicException">The password could not be encrypted for the user (Windows).</exception>
    public bool Set(string? proxy, string? user, string? password, string? keptFrom = null)
    {
        if (KeyOf(proxy) is not { } key)
        {
            return false;
        }

        user = user?.Trim() ?? "";
        _entries.TryGetValue(key, out var current);
        if (user.Length == 0)
        {
            return _entries.Remove(key);
        }

        // As stored (encrypted for the user on Windows, with the same entropy for every proxy): copied as it is.
        var kept = keptFrom is null || KeyOf(keptFrom) is not { } from || from == key ? current?.Password
            : _entries.TryGetValue(from, out var source) ? source.Password : null;
        if (current is not null && current.User == user && (password is null ? current.Password == kept : Reveal(current.Password) == password))
        {
            return false;
        }

        _entries[key] = new ProxyCredentialEntry { User = user, Password = password is null ? kept : Conceal(password) };
        return true;
    }

    /// <summary>
    /// Writes the file to <paramref name="directory"/> (default: <see cref="UserSettings.DefaultDirectory"/>), readable by the
    /// user only; deletes it when nothing is kept.
    /// </summary>
    /// <remarks>Refused with an <see cref="IOException"/> while the file holds what could not be read (<see cref="LoadError"/>).</remarks>
    public void Save(string? directory = null)
    {
        directory ??= UserSettings.DefaultDirectory();
        var path = Path.Combine(directory, FileName);
        ApiWorkspace.ThrowIfUnreadable(path, LoadError, "then try again");
        if (_entries.Count == 0)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(directory);
        var file = new ProxyCredentialFile { Proxies = new Dictionary<string, ProxyCredentialEntry>(_entries, StringComparer.Ordinal) };
        var json = JsonSerializer.Serialize(file, CoreJsonContext.Default.ProxyCredentialFile).Replace("\r\n", "\n", StringComparison.Ordinal);
        TextFile.WriteAtomic(path, json, new TextFileInfo(false, true, false), PrivateFile);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile); // a file that existed keeps its mode otherwise
        }
    }

    /// <summary>A credential for <paramref name="user"/>, which may name a domain: <c>CONTOSO\ada</c> (or <c>ada@contoso.com</c>, as it is).</summary>
    public static NetworkCredential Credential(string user, string? password)
    {
        var slash = user.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? new NetworkCredential(user[(slash + 1)..], password ?? "", user[..slash]) : new NetworkCredential(user, password ?? "");
    }

    // An empty password is kept as it is: there is nothing to encrypt.
    private static string Conceal(string password) =>
        OperatingSystem.IsWindows() && password.Length > 0 ? DpapiPrefix + Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(password), Entropy)) : password;

    private static string? Reveal(string? stored)
    {
        if (stored is null || !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), Entropy));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>The content of <see cref="ProxyCredentialStore.FileName"/>.</summary>
internal sealed class ProxyCredentialFile
{
    public Dictionary<string, ProxyCredentialEntry> Proxies { get; set; } = new();
}

internal sealed class ProxyCredentialEntry
{
    public string User { get; set; } = "";

    /// <summary>The password: <c>dpapi:</c> and base64 when encrypted for the user (Windows), else as typed.</summary>
    public string? Password { get; set; }
}
