using System.Text.Json;
using TermRest.Core.Certificates;
using TermRest.Core.IO;
using TermRest.Core.Postman;
using TermRest.Core.Postman.V3;

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

    /// <summary>
    /// Opens a workspace folder, the folder containing a collection file, or (for a v3 collection folder) the
    /// workspace that holds it. Postman v3 projects are found under <c>postman/collections</c>,
    /// <c>postman/environments</c> and <c>postman/globals</c>.
    /// </summary>
    public static ApiWorkspace Open(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root;
        string? explicitFile = null;
        string? explicitFolder = null;
        if (File.Exists(fullPath))
        {
            root = Path.GetDirectoryName(fullPath)!;
            explicitFile = fullPath;
        }
        else if (Directory.Exists(fullPath))
        {
            root = fullPath;
            if (!Directory.Exists(Path.Combine(fullPath, PostmanFolder)) && V3Names.IsCollectionDirectory(fullPath))
            {
                // A v3 collection folder: open the workspace around it (its settings, environments, certificates).
                explicitFolder = fullPath;
                root = V3Names.WorkspaceRootFor(fullPath);
            }
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

        var collectionsFolder = Path.Combine(root, PostmanFolder, "collections");
        var collectionFiles = Directory.EnumerateFiles(root, "*" + PostmanCollection.FileSuffix)
            .Concat(EnumerateFiles(collectionsFolder, "*" + PostmanCollection.FileSuffix))
            .ToList();
        if (explicitFile is not null && !collectionFiles.Contains(explicitFile, StringComparer.OrdinalIgnoreCase)
            && !explicitFile.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
            && !PostmanEnvironment.IsYamlPath(explicitFile))
        {
            collectionFiles.Insert(0, explicitFile);
        }

        // v3 collection folders: postman/collections/*, plus collection folders kept directly in the workspace.
        var collectionFolders = EnumerateDirectories(collectionsFolder).Where(V3Names.IsCollectionDirectory)
            .Concat(EnumerateDirectories(root).Where(d => !Path.GetFileName(d).Equals(PostmanFolder, StringComparison.OrdinalIgnoreCase)
                                                         && File.Exists(V3Names.DefinitionPath(d))))
            .Concat(explicitFolder is null ? [] : [explicitFolder])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var source in collectionFiles.Concat(collectionFolders).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var collection = PostmanCollection.Load(source);
                workspace.Collections.Add(collection);
                workspace.LoadErrors.AddRange(collection.LoadWarnings.Select(w => $"{collection.Name}: {w}"));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        var environmentsFolder = Path.Combine(root, PostmanFolder, "environments");
        var environmentFiles = Directory.EnumerateFiles(root, "*" + PostmanEnvironment.EnvironmentSuffix)
            .Concat(EnumerateFiles(environmentsFolder, "*" + PostmanEnvironment.EnvironmentSuffix))
            .Concat(EnumerateFiles(environmentsFolder, "*.y*ml").Where(PostmanEnvironment.IsYamlPath));
        foreach (var file in environmentFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
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

        var globalsFolder = Path.Combine(root, PostmanFolder, "globals");
        var globalsFile = Directory.EnumerateFiles(root, "*" + PostmanEnvironment.GlobalsSuffix)
            .Concat(EnumerateFiles(globalsFolder, "*.y*ml").Where(PostmanEnvironment.IsYamlPath))
            .Concat(EnumerateFiles(globalsFolder, "*" + PostmanEnvironment.GlobalsSuffix))
            .FirstOrDefault();
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

        workspace.ApplyYamlLineWidth();
        return workspace;
    }

    /// <summary>
    /// Passes <see cref="WorkspaceSettings.YamlLineWidth"/> to every loaded collection and environment (done when
    /// loading and by <see cref="SaveSettings"/>; call it after changing the setting without saving).
    /// </summary>
    public void ApplyYamlLineWidth()
    {
        foreach (var collection in Collections)
        {
            collection.YamlLineWidth = Settings.YamlLineWidth;
        }

        foreach (var environment in Environments)
        {
            environment.YamlLineWidth = Settings.YamlLineWidth;
        }

        if (Globals is not null)
        {
            Globals.YamlLineWidth = Settings.YamlLineWidth;
        }
    }

    /// <summary>The settings in <c>.termrest/settings.json</c> under <paramref name="workspaceRoot"/>; defaults when missing or invalid.</summary>
    public static WorkspaceSettings ReadSettings(string workspaceRoot) =>
        LoadSettings(Path.Combine(workspaceRoot, ConfigDirectoryName, "settings.json"), []);

    public const string PostmanFolder = "postman";

    /// <summary>
    /// Whether new collections and environments are created in the Postman v3 layout (<c>postman/…</c> YAML folders).
    /// Follows the <see cref="WorkspaceSettings.CollectionFormat"/> setting, or the workspace's existing layout.
    /// </summary>
    public bool UsesV3Layout => Settings.CollectionFormat?.ToLowerInvariant() switch
    {
        "v3" => true,
        "v2.1" => false,
        _ => Directory.Exists(Path.Combine(RootPath, PostmanFolder, "collections"))
             || Directory.Exists(Path.Combine(RootPath, PostmanFolder, "environments"))
             || File.Exists(Path.Combine(RootPath, ".postman", "resources.yaml")),
    };

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];

    private static IEnumerable<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Where(d => !Path.GetFileName(d).StartsWith('.')) : [];

    /// <summary>Creates a collection: a v2.1 JSON file, or a v3 folder under <c>postman/collections</c> (see <see cref="UsesV3Layout"/>).</summary>
    public PostmanCollection CreateCollection(string name, bool? v3 = null)
    {
        if (v3 ?? UsesV3Layout)
        {
            var parent = Path.Combine(RootPath, PostmanFolder, "collections");
            var directory = UniqueFolder(parent, V3Names.Safe(name));
            var created = V3CollectionStorage.Create(directory, name, Settings.YamlLineWidth);
            Collections.Add(created);
            return created;
        }

        var collection = PostmanCollection.Create(name, UniquePath(name, PostmanCollection.FileSuffix));
        collection.Save();
        Collections.Add(collection);
        return collection;
    }

    public PostmanEnvironment CreateEnvironment(string name)
    {
        var environment = UsesV3Layout
            ? PostmanEnvironment.CreateYaml(name, UniqueFile(Path.Combine(RootPath, PostmanFolder, "environments"), V3Names.Safe(name), V3Names.EnvironmentSuffix))
            : PostmanEnvironment.Create(name, UniquePath(name, PostmanEnvironment.EnvironmentSuffix));
        environment.YamlLineWidth = Settings.YamlLineWidth;
        environment.Save();
        Environments.Add(environment);
        return environment;
    }

    private static string UniqueFolder(string parent, string safe)
    {
        var taken = new HashSet<string>(EnumerateDirectories(parent).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return Path.Combine(parent, V3Names.Unique(safe, taken));
    }

    private static string UniqueFile(string folder, string safe, string suffix)
    {
        var taken = new HashSet<string>(EnumerateFiles(folder, "*" + suffix).Select(f => Path.GetFileName(f)[..^suffix.Length]), StringComparer.OrdinalIgnoreCase);
        return Path.Combine(folder, V3Names.Unique(safe, taken) + suffix);
    }

    /// <summary>Copies an exported Postman collection into the workspace and loads it.</summary>
    /// <remarks>
    /// A workspace that creates collections in the v3 layout gets a YAML folder under <c>postman/collections</c>, as
    /// Postman's Git-connected workspaces do. A file that is already loaded is reloaded from disk; that is refused
    /// while it has unsaved changes.
    /// </remarks>
    public PostmanCollection ImportCollection(string sourcePath)
    {
        if (FindCollection(sourcePath) is { } loaded)
        {
            ThrowIfDirty(loaded.IsDirty, loaded.Name);
            return ReloadCollection(loaded);
        }

        var collection = PostmanCollection.Load(sourcePath);
        if (!IsInside(sourcePath))
        {
            if (UsesV3Layout && !collection.IsV3)
            {
                collection = collection.ConvertToV3(UniqueFolder(Path.Combine(RootPath, PostmanFolder, "collections"), V3Names.Safe(collection.Name)), Settings.YamlLineWidth);
            }
            else
            {
                var target = UniquePath(collection.Name, PostmanCollection.FileSuffix);
                File.Copy(sourcePath, target);
                collection = PostmanCollection.Load(target);
            }
        }

        collection.YamlLineWidth = Settings.YamlLineWidth;
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
        reloaded.YamlLineWidth = Settings.YamlLineWidth;
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
            var reloaded = PostmanEnvironment.Load(fullSource);
            reloaded.YamlLineWidth = Settings.YamlLineWidth;
            return Environments[loadedIndex] = reloaded;
        }

        var environment = PostmanEnvironment.Load(sourcePath);
        var target = IsInside(sourcePath) ? fullSource : UniquePath(environment.Name, PostmanEnvironment.EnvironmentSuffix);
        if (!string.Equals(fullSource, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, target);
            environment = PostmanEnvironment.Load(target);
        }

        environment.YamlLineWidth = Settings.YamlLineWidth;
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
        ApplyYamlLineWidth();
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
