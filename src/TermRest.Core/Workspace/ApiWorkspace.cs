using System.Text.Json;
using TermRest.Core.Certificates;
using TermRest.Core.IO;
using TermRest.Core.Postman;

namespace TermRest.Core.Workspace;

/// <summary>
/// A folder of Postman files: <c>*.postman_collection.json</c>, <c>*.postman_environment.json</c>,
/// an optional <c>*.postman_globals.json</c>, plus TermRest settings and certificates in <c>.termrest/</c>.
/// </summary>
public sealed class ApiWorkspace
{
    public const string ConfigDirectoryName = ".termrest";

    private ApiWorkspace(string rootPath, WorkspaceSettings settings, CertificateRegistry certificates)
    {
        RootPath = rootPath;
        Settings = settings;
        Certificates = certificates;
    }

    public string RootPath { get; }

    public List<PostmanCollection> Collections { get; } = [];

    public List<PostmanEnvironment> Environments { get; } = [];

    public PostmanEnvironment? Globals { get; private set; }

    public WorkspaceSettings Settings { get; private set; }

    public CertificateRegistry Certificates { get; private set; }

    /// <summary>Files that could not be loaded (shown to the user instead of failing the whole workspace).</summary>
    public List<string> LoadErrors { get; } = [];

    public string ConfigDirectory => Path.Combine(RootPath, ConfigDirectoryName);

    public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public string CertificatesPath => Path.Combine(ConfigDirectory, "certificates.json");

    public PostmanEnvironment? ActiveEnvironment
    {
        get => Settings.ActiveEnvironment is { } name
            ? Environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        set => Settings.ActiveEnvironment = value?.Name;
    }

    public bool HasUnsavedChanges =>
        Collections.Any(c => c.IsDirty) || Environments.Any(e => e.IsDirty) || Globals?.IsDirty == true;

    /// <summary>Opens a workspace folder, or the folder containing a collection file.</summary>
    public static ApiWorkspace Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        string root;
        string? explicitFile = null;
        if (File.Exists(fullPath))
        {
            root = Path.GetDirectoryName(fullPath)!;
            explicitFile = fullPath;
        }
        else if (Directory.Exists(fullPath))
        {
            root = fullPath;
        }
        else
        {
            throw new DirectoryNotFoundException($"'{path}' does not exist.");
        }

        var errors = new List<string>();
        var settings = LoadSettings(Path.Combine(root, ConfigDirectoryName, "settings.json"), errors);
        CertificateRegistry certificates;
        var certificatesPath = Path.Combine(root, ConfigDirectoryName, "certificates.json");
        try
        {
            certificates = CertificateRegistry.LoadOrEmpty(certificatesPath, root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            errors.Add($"{certificatesPath}: {ex.Message}");
            certificates = new CertificateRegistry(root, certificatesPath);
        }

        var workspace = new ApiWorkspace(root, settings, certificates);
        workspace.LoadErrors.AddRange(errors);

        var collectionFiles = Directory.EnumerateFiles(root, "*" + PostmanCollection.FileSuffix).ToList();
        if (explicitFile is not null && !collectionFiles.Contains(explicitFile, StringComparer.OrdinalIgnoreCase)
            && !explicitFile.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase))
        {
            collectionFiles.Insert(0, explicitFile);
        }

        foreach (var file in collectionFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                workspace.Collections.Add(PostmanCollection.Load(file));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*" + PostmanEnvironment.EnvironmentSuffix).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                workspace.Environments.Add(PostmanEnvironment.Load(file));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var globalsFile = Directory.EnumerateFiles(root, "*" + PostmanEnvironment.GlobalsSuffix).FirstOrDefault();
        if (globalsFile is not null)
        {
            try
            {
                workspace.Globals = PostmanEnvironment.Load(globalsFile);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(globalsFile)}: {ex.Message}");
            }
        }

        return workspace;
    }

    public PostmanCollection CreateCollection(string name)
    {
        var collection = PostmanCollection.Create(name, UniquePath(name, PostmanCollection.FileSuffix));
        collection.Save();
        Collections.Add(collection);
        return collection;
    }

    public PostmanEnvironment CreateEnvironment(string name)
    {
        var environment = PostmanEnvironment.Create(name, UniquePath(name, PostmanEnvironment.EnvironmentSuffix));
        environment.Save();
        Environments.Add(environment);
        return environment;
    }

    /// <summary>Copies an exported Postman collection into the workspace and loads it.</summary>
    /// <remarks>A file that is already loaded is reloaded from disk; that is refused while it has unsaved changes.</remarks>
    public PostmanCollection ImportCollection(string sourcePath)
    {
        if (FindCollection(sourcePath) is { } loaded)
        {
            ThrowIfDirty(loaded.IsDirty, loaded.Name);
            return ReloadCollection(loaded);
        }

        var collection = PostmanCollection.Load(sourcePath);
        var target = IsInside(sourcePath) ? Path.GetFullPath(sourcePath) : UniquePath(collection.Name, PostmanCollection.FileSuffix);
        if (!string.Equals(Path.GetFullPath(sourcePath), target, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, target);
            collection = PostmanCollection.Load(target);
        }

        Collections.Add(collection);
        return collection;
    }

    /// <summary>The loaded collection stored in <paramref name="path"/>, if any.</summary>
    public PostmanCollection? FindCollection(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Collections.FirstOrDefault(c => c.FilePath is { } file && string.Equals(Path.GetFullPath(file), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Re-reads a collection from disk in place of the loaded copy, discarding its unsaved changes.</summary>
    public PostmanCollection ReloadCollection(PostmanCollection collection)
    {
        var index = Collections.IndexOf(collection);
        if (index < 0 || collection.FilePath is null)
        {
            throw new ArgumentException($"'{collection.Name}' is not a saved collection of this workspace.", nameof(collection));
        }

        var reloaded = PostmanCollection.Load(collection.FilePath);
        Collections[index] = reloaded;
        return reloaded;
    }

    /// <remarks>A file that is already loaded is reloaded from disk; that is refused while it has unsaved changes.</remarks>
    public PostmanEnvironment ImportEnvironment(string sourcePath)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var loadedIndex = Environments.FindIndex(e => e.FilePath is { } file && string.Equals(Path.GetFullPath(file), fullSource, StringComparison.OrdinalIgnoreCase));
        if (loadedIndex >= 0)
        {
            ThrowIfDirty(Environments[loadedIndex].IsDirty, Environments[loadedIndex].Name);
            return Environments[loadedIndex] = PostmanEnvironment.Load(fullSource);
        }

        var environment = PostmanEnvironment.Load(sourcePath);
        var target = IsInside(sourcePath) ? fullSource : UniquePath(environment.Name, PostmanEnvironment.EnvironmentSuffix);
        if (!string.Equals(fullSource, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, target);
            environment = PostmanEnvironment.Load(target);
        }

        Environments.Add(environment);
        return environment;
    }

    private static void ThrowIfDirty(bool isDirty, string name)
    {
        if (isDirty)
        {
            throw new InvalidOperationException($"'{name}' is already open and has unsaved changes. Save them first, or reload it to discard them.");
        }
    }

    public void SaveSettings()
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(Settings, CoreJsonContext.Default.WorkspaceSettings);
        TextFile.WriteAtomic(SettingsPath, json.Replace("\r\n", "\n", StringComparison.Ordinal), new TextFileInfo(false, true, false));
    }

    public void SaveCertificates()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Certificates.Save(CertificatesPath);
    }

    public void SaveAll()
    {
        foreach (var collection in Collections.Where(c => c.IsDirty))
        {
            collection.Save();
        }

        foreach (var environment in Environments.Where(e => e.IsDirty))
        {
            environment.Save();
        }

        if (Globals?.IsDirty == true)
        {
            Globals.Save();
        }
    }

    public string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(RootPath, path));

    private bool IsInside(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), RootPath, StringComparison.OrdinalIgnoreCase);

    private string UniquePath(string name, string suffix)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)).Trim();
        if (safe.Length == 0)
        {
            safe = "untitled";
        }

        var candidate = Path.Combine(RootPath, safe + suffix);
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(RootPath, $"{safe} ({i}){suffix}");
        }

        return candidate;
    }

    private static WorkspaceSettings LoadSettings(string path, List<string> errors)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceSettings();
        }

        try
        {
            var (text, _) = TextFile.Read(path);
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.WorkspaceSettings) ?? new WorkspaceSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            errors.Add($"{path}: {ex.Message}");
            return new WorkspaceSettings();
        }
    }
}
