using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Certificates;
using TermRest.Core.IO;
using TermRest.Core.Json;
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

    /// <summary>The settings that name the active environment (see <see cref="ActiveEnvironment"/>).</summary>
    private static readonly string[] ActiveEnvironmentKeys = ["activeEnvironment", "activeEnvironmentFile"];

    /// <summary><see cref="Settings"/> as last loaded or saved, to save only what changed since.</summary>
    private JsonObject _savedSettings;

    private ApiWorkspace(string rootPath, WorkspaceSettings settings, CertificateRegistry certificates)
    {
        RootPath = rootPath;
        Settings = settings;
        Certificates = certificates;
        _savedSettings = Serialize(settings);
    }

    public string RootPath { get; }

    public List<PostmanCollection> Collections { get; } = [];

    public List<PostmanEnvironment> Environments { get; } = [];

    public PostmanEnvironment? Globals { get; private set; }

    public WorkspaceSettings Settings { get; private set; }

    public CertificateRegistry Certificates { get; private set; }

    /// <summary>Files that could not be loaded (shown to the user instead of failing the whole workspace).</summary>
    public List<string> LoadErrors { get; } = [];

    /// <summary>
    /// Why <see cref="SettingsPath"/> could not be read, if it could not (also in <see cref="LoadErrors"/>): the settings
    /// are the defaults then, and <see cref="SaveSettings"/> does not save over the file.
    /// </summary>
    public string? SettingsError { get; private init; }

    /// <summary>
    /// Why <see cref="CertificatesPath"/> could not be read, if it could not (also in <see cref="LoadErrors"/>): the
    /// registry is empty then, and <see cref="SaveCertificates"/> does not save over the file.
    /// </summary>
    public string? CertificatesError { get; private init; }

    public string ConfigDirectory => Path.Combine(RootPath, ConfigDirectoryName);

    public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public string CertificatesPath => Path.Combine(ConfigDirectory, "certificates.json");

    /// <summary>
    /// The selected environment: the one loaded from <see cref="WorkspaceSettings.ActiveEnvironmentFile"/>, which tells
    /// environments of the same name apart; else (settings of earlier versions, a file renamed or deleted meanwhile) the
    /// first one named <see cref="WorkspaceSettings.ActiveEnvironment"/>.
    /// </summary>
    public PostmanEnvironment? ActiveEnvironment
    {
        get
        {
            if (Settings.ActiveEnvironmentFile is { } file)
            {
                var path = file.Replace(Path.DirectorySeparatorChar, '/');
                if (Environments.FirstOrDefault(e => string.Equals(SettingsFileOf(e), path, StringComparison.OrdinalIgnoreCase)) is { } selected)
                {
                    return selected;
                }
            }

            return Settings.ActiveEnvironment is { } name
                ? Environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        set
        {
            Settings.ActiveEnvironment = value?.Name;
            Settings.ActiveEnvironmentFile = value is null ? null : SettingsFileOf(value);
        }
    }

    /// <summary>An environment's file as <see cref="WorkspaceSettings.ActiveEnvironmentFile"/> names it.</summary>
    private string? SettingsFileOf(PostmanEnvironment environment) =>
        environment.FilePath is { } file ? Path.GetRelativePath(RootPath, file).Replace(Path.DirectorySeparatorChar, '/') : null;

    /// <summary>
    /// How to name <paramref name="environment"/> to the user: its name, and its file too where another environment of the
    /// workspace has the same name (see <see cref="ActiveEnvironment"/>), so that the user can tell which one it is; the
    /// file's path in the workspace where their files have the same name too (in the workspace folder and in
    /// <c>postman/environments</c>).
    /// </summary>
    public string DisplayName(PostmanEnvironment environment)
    {
        var file = Path.GetFileName(environment.FilePath);
        var namesakes = Environments.Where(e => e != environment && string.Equals(e.Name, environment.Name, StringComparison.OrdinalIgnoreCase));
        if (file is null || !namesakes.Any())
        {
            return environment.Name;
        }

        return namesakes.Any(e => string.Equals(Path.GetFileName(e.FilePath), file, StringComparison.OrdinalIgnoreCase))
            ? $"{environment.Name} ({SettingsFileOf(environment)})"
            : $"{environment.Name} ({file})";
    }

    /// <summary>
    /// A collection's file (a v3 collection's folder) as the history names it: relative to the workspace, with "/", as
    /// <see cref="WorkspaceSettings.ActiveEnvironmentFile"/> names an environment's. Unlike its name, it tells apart
    /// collections of the same name (an imported update of a collection, a converted copy).
    /// </summary>
    public string? FileOf(PostmanCollection collection) =>
        collection.FilePath is { } file ? Path.GetRelativePath(RootPath, file).Replace(Path.DirectorySeparatorChar, '/') : null;

    /// <summary>
    /// How to name <paramref name="collection"/> to the user, as <see cref="DisplayName(PostmanEnvironment)"/> names an
    /// environment: its name, and its file (a v3 collection's folder) too where another collection of the workspace has
    /// the same name; the file's path in the workspace where their files have the same name too.
    /// </summary>
    public string DisplayName(PostmanCollection collection)
    {
        var namesakes = Collections.Where(c => c != collection && c.Name == collection.Name);
        if (collection.FilePath is not { } path || !namesakes.Any())
        {
            return collection.Name;
        }

        var file = Path.GetFileName(path);
        return namesakes.Any(c => string.Equals(Path.GetFileName(c.FilePath), file, StringComparison.OrdinalIgnoreCase))
            ? $"{collection.Name} ({FileOf(collection)})"
            : $"{collection.Name} ({file})";
    }

    public bool HasUnsavedChanges =>
        Collections.Any(c => c.IsDirty) || Environments.Any(e => e.IsDirty) || Globals?.IsDirty == true;

    /// <summary>
    /// Opens a workspace folder, or the workspace that holds a collection file or v3 collection folder: the folder above
    /// <c>postman</c> for one in <c>postman/collections</c>, else the folder it is in (for a file, see
    /// <see cref="RootForFile"/>). Postman v3 projects are found under <c>postman/collections</c>,
    /// <c>postman/environments</c> and <c>postman/globals</c>.
    /// </summary>
    /// <param name="path">The workspace folder, or a collection file or v3 collection folder.</param>
    /// <param name="collections">Which collections to load; the settings, certificates and environments are always loaded.</param>
    public static ApiWorkspace Open(string path, CollectionLoading collections = CollectionLoading.All)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root;
        string? explicitFile = null;
        string? explicitFolder = null;
        if (File.Exists(fullPath))
        {
            root = RootForFile(fullPath);
            explicitFile = fullPath;
        }
        else if (Directory.Exists(fullPath))
        {
            root = fullPath;
            if (IsCollectionFolder(fullPath))
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

        var settings = LoadSettings(Path.Combine(root, ConfigDirectoryName, "settings.json"), out var settingsError);
        CertificateRegistry certificates;
        string? certificatesError = null;
        var certificatesPath = Path.Combine(root, ConfigDirectoryName, "certificates.json");
        try
        {
            certificates = CertificateRegistry.LoadOrEmpty(certificatesPath, root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            certificatesError = $"{certificatesPath}: {ex.Message}";
            certificates = new CertificateRegistry(root); // no file to save to: only SaveCertificates could, and it refuses
        }

        var workspace = new ApiWorkspace(root, settings, certificates) { SettingsError = settingsError, CertificatesError = certificatesError };
        workspace.LoadErrors.AddRange(new[] { settingsError, certificatesError }.OfType<string>());

        // The collection that was opened: a file (not an environment or globals) or a v3 collection folder.
        var opened = explicitFolder ?? (explicitFile is not null && !explicitFile.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
                                        && !explicitFile.EndsWith(PostmanEnvironment.GlobalsSuffix, StringComparison.OrdinalIgnoreCase)
                                        && !PostmanEnvironment.IsYamlPath(explicitFile) ? explicitFile : null);
        IEnumerable<string> collectionSources = collections switch
        {
            CollectionLoading.None => [],
            CollectionLoading.Requested when opened is not null => [opened],
            _ => EnumerateCollections(root, opened),
        };

        foreach (var source in collectionSources.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
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
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var globalsFile = FindGlobalsFile(root);
        if (globalsFile is not null)
        {
            try
            {
                workspace.Globals = PostmanEnvironment.Load(globalsFile);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                workspace.LoadErrors.Add($"{Path.GetFileName(globalsFile)}: {ex.Message}");
            }
        }

        workspace.ApplyYamlLineWidth();
        return workspace;
    }

    /// <summary>
    /// Passes <see cref="WorkspaceSettings.YamlLineWidth"/> to every loaded collection and environment (done when
    /// loading and by <see cref="SaveSettings"/>, even when saving fails; call it after changing the setting without
    /// saving).
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
        LoadSettings(Path.Combine(workspaceRoot, ConfigDirectoryName, "settings.json"), out _);

    /// <summary>
    /// The workspace folder of a file given on its own (see <see cref="Open"/>): the folder that holds <c>postman</c> for
    /// a file in <c>postman/collections</c>, <c>postman/environments</c> or <c>postman/globals</c>, where the workspace keeps
    /// such files; else the folder the file is in.
    /// </summary>
    public static string RootForFile(string file) => V3Names.WorkspaceRootForFile(file);

    /// <summary>
    /// The workspace folder for a path given on its own, as <see cref="Open"/> finds it: a file's (see
    /// <see cref="RootForFile"/>), the workspace that holds a v3 collection folder, else the folder itself.
    /// </summary>
    public static string RootFor(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return File.Exists(full) ? RootForFile(full)
            : Directory.Exists(full) && IsCollectionFolder(full) ? V3Names.WorkspaceRootFor(full)
            : full;
    }

    /// <summary>
    /// Whether a folder given on its own is a v3 collection, opened in the workspace around it: a folder with a
    /// <c>postman</c> folder is a workspace, unless it is in a <c>postman/collections</c> folder (where a collection may have a
    /// folder of that name).
    /// </summary>
    private static bool IsCollectionFolder(string fullPath) =>
        (V3Names.IsInCollectionsFolder(fullPath) || !Directory.Exists(Path.Combine(fullPath, PostmanFolder))) && V3Names.IsCollectionDirectory(fullPath);

    /// <summary>The globals file of the workspace in <paramref name="root"/>, if it has one: the first one found.</summary>
    private static string? FindGlobalsFile(string root)
    {
        var globalsFolder = Path.Combine(root, PostmanFolder, "globals");
        return Directory.EnumerateFiles(root, "*" + PostmanEnvironment.GlobalsSuffix)
            .Concat(EnumerateFiles(globalsFolder, "*.y*ml").Where(PostmanEnvironment.IsYamlPath))
            .Concat(EnumerateFiles(globalsFolder, "*" + PostmanEnvironment.GlobalsSuffix))
            .FirstOrDefault();
    }

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

    /// <summary>The collection files and v3 collection folders of the workspace in <paramref name="root"/>, and the one that was opened.</summary>
    private static IEnumerable<string> EnumerateCollections(string root, string? opened)
    {
        var collectionsFolder = Path.Combine(root, PostmanFolder, "collections");
        var collectionFiles = Directory.EnumerateFiles(root, "*" + PostmanCollection.FileSuffix)
            .Concat(EnumerateFiles(collectionsFolder, "*" + PostmanCollection.FileSuffix));

        // v3 collection folders: postman/collections/*, plus collection folders kept directly in the workspace.
        var collectionFolders = EnumerateDirectories(collectionsFolder).Where(V3Names.IsCollectionDirectory)
            .Concat(EnumerateDirectories(root).Where(d => !Path.GetFileName(d).Equals(PostmanFolder, StringComparison.OrdinalIgnoreCase)
                                                         && File.Exists(V3Names.DefinitionPath(d))));
        return collectionFiles.Concat(collectionFolders).Concat(opened is null ? [] : [opened]).Distinct(StringComparer.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// The file or folder name for a collection or environment called <paramref name="name"/>, as the workspace names those
    /// it creates in the v3 layout: characters invalid on any OS, and leading dots (which would hide it), become "-".
    /// </summary>
    public static string SafeFileName(string name) => V3Names.Safe(name);

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
    /// A collection where the workspace keeps collections (where <see cref="Open"/> loads them) is loaded in place; any
    /// other is copied in, or, in a workspace that creates collections in the v3 layout, converted into a YAML folder
    /// under <c>postman/collections</c>, as Postman's Git-connected workspaces do. A file that is already loaded is
    /// reloaded from disk; that is refused while it has unsaved changes.
    /// </remarks>
    public PostmanCollection ImportCollection(string sourcePath) => ImportCollection(sourcePath, out _);

    /// <inheritdoc cref="ImportCollection(string)"/>
    /// <param name="sourcePath">The exported collection.</param>
    /// <param name="secretValuesLeftOut">
    /// The secret variables whose values were left out, as the collection was converted into the v3 layout (see
    /// <see cref="PostmanCollection.SecretValuesLeftOutOfV3"/>).
    /// </param>
    public PostmanCollection ImportCollection(string sourcePath, out List<string> secretValuesLeftOut)
    {
        secretValuesLeftOut = [];
        if (FindCollection(sourcePath) is { } loaded)
        {
            ThrowIfDirty(loaded.IsDirty, loaded.Name);
            return ReloadCollection(loaded);
        }

        var collection = PostmanCollection.Load(sourcePath);
        if (!IsCollectionLocation(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath))))
        {
            if (UsesV3Layout && !collection.IsV3)
            {
                secretValuesLeftOut = collection.SecretValuesLeftOutOfV3();
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

    /// <summary>Whether <see cref="Open"/> loads the file or v3 collection folder at <paramref name="fullPath"/> as a collection.</summary>
    private bool IsCollectionLocation(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        var inRoot = string.Equals(folder, RootPath, StringComparison.OrdinalIgnoreCase);
        var inCollections = string.Equals(folder, Path.Combine(RootPath, PostmanFolder, "collections"), StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(fullPath))
        {
            return (inRoot || inCollections) && fullPath.EndsWith(PostmanCollection.FileSuffix, StringComparison.OrdinalIgnoreCase);
        }

        var name = Path.GetFileName(fullPath);
        return !name.StartsWith('.') && (inCollections
            ? V3Names.IsCollectionDirectory(fullPath)
            : inRoot && !name.Equals(PostmanFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(V3Names.DefinitionPath(fullPath)));
    }

    /// <summary>The loaded collection stored in <paramref name="path"/>, if any.</summary>
    public PostmanCollection? FindCollection(string path)
    {
        // A folder may be given with a trailing separator (as shells complete it); v3 collections are stored without one.
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Collections.FirstOrDefault(c => c.FilePath is { } file
                                               && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(file)), fullPath, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Adds an environment file to the workspace and loads it.</summary>
    /// <remarks>
    /// A file where the workspace keeps environments is loaded in place; any other is saved as a new environment of the
    /// workspace, in its format (YAML under <c>postman/environments</c> in the v3 layout, else a JSON file), converted
    /// when needed. A file that is already loaded is reloaded from disk; that is refused while it has unsaved changes.
    /// A globals file (a Postman globals export) becomes the workspace's <see cref="Globals"/> the same way, saved where
    /// the workspace keeps globals (<c>postman/globals/workspace.globals.yaml</c> in the v3 layout, else
    /// <c>workspace.postman_globals.json</c>); that is refused while the workspace has a globals file of its own.
    /// </remarks>
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

        if (Globals?.FilePath is { } globalsFile && string.Equals(Path.GetFullPath(globalsFile), fullSource, StringComparison.OrdinalIgnoreCase))
        {
            ThrowIfDirty(Globals.IsDirty, Globals.Name);
            var reloaded = PostmanEnvironment.Load(fullSource);
            reloaded.YamlLineWidth = Settings.YamlLineWidth;
            return Globals = reloaded;
        }

        var environment = PostmanEnvironment.Load(fullSource);
        environment.YamlLineWidth = Settings.YamlLineWidth;
        if (environment.IsGlobals)
        {
            // The workspace uses one globals file, the first one Open finds: also one it could not read, or one added since.
            var existing = Globals?.FilePath ?? FindGlobalsFile(RootPath);
            if (existing is not null && !string.Equals(Path.GetFullPath(existing), fullSource, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(fullSource)}' holds globals, and the workspace has globals of its own already ({Path.GetRelativePath(RootPath, existing)}). " +
                    "A workspace has one set of globals: edit those, or replace that file with this one.");
            }

            if (existing is null)
            {
                environment.Save(UsesV3Layout
                    ? Path.Combine(RootPath, PostmanFolder, "globals", "workspace.globals.yaml")
                    : Path.Combine(RootPath, "workspace" + PostmanEnvironment.GlobalsSuffix));
            }

            return Globals = environment;
        }

        if (!IsEnvironmentLocation(fullSource))
        {
            environment.Save(UsesV3Layout
                ? UniqueFile(Path.Combine(RootPath, PostmanFolder, "environments"), V3Names.Safe(environment.Name), V3Names.EnvironmentSuffix)
                : UniquePath(environment.Name, PostmanEnvironment.EnvironmentSuffix));
        }

        Environments.Add(environment);
        return environment;
    }

    /// <summary>Whether <see cref="Open"/> loads the file at <paramref name="fullPath"/> as an environment.</summary>
    private bool IsEnvironmentLocation(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        var environments = Path.Combine(RootPath, PostmanFolder, "environments");
        return fullPath.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
            ? string.Equals(folder, RootPath, StringComparison.OrdinalIgnoreCase) || string.Equals(folder, environments, StringComparison.OrdinalIgnoreCase)
            : PostmanEnvironment.IsYamlPath(fullPath) && string.Equals(folder, environments, StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfDirty(bool isDirty, string name)
    {
        if (isDirty)
        {
            throw new InvalidOperationException($"'{name}' is already open and has unsaved changes. Save them first, or reload it to discard them.");
        }
    }

    /// <summary>
    /// Saves what changed in <see cref="Settings"/> since they were loaded or last saved into <see cref="SettingsPath"/> as
    /// it is now: everything else in the file is kept, including settings another program (a git pull) changed meanwhile
    /// and ones this version does not know. Writes nothing when nothing changed; the active environment's name and file
    /// are written together. Saved or not, the settings apply for this session: <see cref="WorkspaceSettings.YamlLineWidth"/>
    /// is passed to the loaded collections and environments first.
    /// </summary>
    /// <remarks>
    /// Refused with an <see cref="IOException"/> while the file holds settings that could not be read when loading
    /// (<see cref="SettingsError"/>) or cannot be read now.
    /// </remarks>
    public void SaveSettings()
    {
        ApplyYamlLineWidth();
        var settings = Serialize(Settings);
        var changed = settings.Select(p => p.Key).Union(_savedSettings.Select(p => p.Key))
            .Where(key => !JsonNode.DeepEquals(settings[key], _savedSettings[key]))
            .ToList();
        if (changed.Intersect(ActiveEnvironmentKeys).Any())
        {
            // The name and the file are one choice: written apart, the file could be paired with a name a git pull wrote,
            // and the name decides once that file is gone.
            changed = [.. changed.Union(ActiveEnvironmentKeys)];
        }

        if (changed.Count > 0)
        {
            ThrowIfUnreadable(SettingsPath, SettingsError, "then open the workspace again");
            var file = new JsonObject();
            if (File.Exists(SettingsPath))
            {
                var (text, _) = TextFile.Read(SettingsPath);
                try
                {
                    file = PostmanJson.Parse(text) as JsonObject ?? throw new JsonException("The settings are not a JSON object.");
                }
                catch (JsonException ex)
                {
                    throw Unreadable($"{SettingsPath}: {ex.Message}", "then save again", ex); // changed since loading, e.g. a merge conflict
                }
            }

            foreach (var key in changed)
            {
                if (settings[key] is { } value)
                {
                    file[key] = value.DeepClone();
                }
                else
                {
                    file.Remove(key); // not written when null
                }
            }

            Directory.CreateDirectory(ConfigDirectory);
            var json = file.ToJsonString(CoreJsonContext.Default.Options);
            TextFile.WriteAtomic(SettingsPath, json.Replace("\r\n", "\n", StringComparison.Ordinal), new TextFileInfo(false, true, false));
            _savedSettings = settings;
        }
    }

    private static JsonObject Serialize(WorkspaceSettings settings) =>
        (JsonObject)JsonSerializer.SerializeToNode(settings, CoreJsonContext.Default.WorkspaceSettings)!;

    /// <inheritdoc cref="SaveCertificates(bool)"/>
    public void SaveCertificates() => SaveCertificates(overwriteChanged: false);

    /// <summary>
    /// Saves <see cref="Certificates"/> into <see cref="CertificatesPath"/>; not over a file another program changed since
    /// it was read (a git pull, another TermRest) unless <paramref name="overwriteChanged"/>: a
    /// <see cref="FileChangedException"/> says so then (see <see cref="ReloadCertificates"/>).
    /// </summary>
    /// <remarks>Refused with an <see cref="IOException"/> while the file holds a registry that could not be read (<see cref="CertificatesError"/>).</remarks>
    public void SaveCertificates(bool overwriteChanged)
    {
        ThrowIfUnreadable(CertificatesPath, CertificatesError, "then open the workspace again");
        Directory.CreateDirectory(ConfigDirectory);
        Certificates.Save(CertificatesPath, overwriteChanged);
    }

    /// <summary>Reads <see cref="CertificatesPath"/> again in place of <see cref="Certificates"/> (empty when the file is gone).</summary>
    public void ReloadCertificates() => Certificates = CertificateRegistry.LoadOrEmpty(CertificatesPath, RootPath);

    /// <summary>Refuses to save over a file that could not be read when it was loaded: saving would lose what it holds.</summary>
    internal static void ThrowIfUnreadable(string path, string? loadError, string then)
    {
        if (loadError is not null && File.Exists(path))
        {
            throw Unreadable(loadError, then);
        }
    }

    private static IOException Unreadable(string error, string then, Exception? inner = null) =>
        new($"{error}\nTermRest could not read this file, so it did not save over it: that would lose what it holds. Fix or delete it, {then}.", inner);

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

    /// <summary>
    /// A new file in the workspace folder for <paramref name="name"/>, named as <see cref="SafeFileName"/> says, so it is
    /// valid on every OS (a workspace is shared through Git), and unique ignoring case ("Name (2)"), as Windows and macOS
    /// tell names apart.
    /// </summary>
    private string UniquePath(string name, string suffix)
    {
        var safe = V3Names.Safe(name);
        var taken = new HashSet<string>(
            Directory.Exists(RootPath) ? Directory.EnumerateFileSystemEntries(RootPath).Select(Path.GetFileName).OfType<string>() : [],
            StringComparer.OrdinalIgnoreCase);
        var candidate = safe + suffix;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{safe} ({i}){suffix}";
        }

        return Path.Combine(RootPath, candidate);
    }

    private static WorkspaceSettings LoadSettings(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return new WorkspaceSettings();
        }

        try
        {
            var (text, _) = TextFile.Read(path);
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.WorkspaceSettings) ?? new WorkspaceSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            return new WorkspaceSettings();
        }
    }
}

/// <summary>Which collections <see cref="ApiWorkspace.Open"/> loads (parsing them is most of the work).</summary>
public enum CollectionLoading
{
    /// <summary>Every collection of the workspace (and the one opened, wherever it is).</summary>
    All,

    /// <summary>Only the collection file or v3 collection folder that was opened; every collection when a workspace folder was.</summary>
    Requested,

    /// <summary>None, for what needs only the workspace folder, its settings, certificates and environments.</summary>
    None,
}
