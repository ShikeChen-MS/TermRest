using System.Security.AccessControl;
using TermRest.Core.IO;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Saving must replace a file's content without changing its identity (links, permissions).</summary>
public class TextFileTests
{
    [Fact]
    public void Saving_keeps_unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        var path = Path.Combine(TestFiles.NewTempDirectory(), "secrets.postman_environment.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        TextFile.WriteAtomic(path, "{\"changed\": true}");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("{\"changed\": true}", File.ReadAllText(path));
    }

    [Fact]
    public void Saving_keeps_windows_access_rules()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs only.");
            return;
        }

        var path = Path.Combine(TestFiles.NewTempDirectory(), "secrets.postman_environment.json");
        File.WriteAllText(path, "{}");
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        file.SetAccessControl(security);

        TextFile.WriteAtomic(path, "{\"changed\": true}");

        Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected, "the explicit (non-inherited) ACL was replaced");
        Assert.Equal("{\"changed\": true}", File.ReadAllText(path));
    }

    [Fact]
    public void Saving_through_a_symbolic_link_updates_the_target_and_keeps_the_link()
    {
        var directory = TestFiles.NewTempDirectory();
        var target = Path.Combine(directory, "shared.postman_collection.json");
        var link = Path.Combine(TestFiles.NewTempDirectory(), "linked.postman_collection.json");
        File.WriteAllText(target, "{}");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Cannot create symbolic links here (Windows needs Developer Mode or elevation): {e.Message}");
        }

        TextFile.WriteAtomic(link, "{\"changed\": true}");

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("{\"changed\": true}", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Saving_a_new_file_creates_it()
    {
        var path = Path.Combine(TestFiles.NewTempDirectory(), "sub", "new.postman_collection.json");

        TextFile.WriteAtomic(path, "{}", new TextFileInfo(HasBom: true, HasTrailingNewline: true, UsesCrLf: true));

        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}', (byte)'\r', (byte)'\n'], File.ReadAllBytes(path));
    }

    [Fact]
    public void A_failed_save_keeps_the_original_and_leaves_no_temp_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows file locks only.");
            return;
        }

        var directory = TestFiles.NewTempDirectory();
        var path = Path.Combine(directory, "locked.postman_environment.json");
        File.WriteAllText(path, "{}");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => TextFile.WriteAtomic(path, "{\"changed\": true}"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    [Fact]
    public void Files_that_are_not_utf8_are_reported_instead_of_being_garbled()
    {
        var directory = TestFiles.NewTempDirectory();
        var path = Path.Combine(directory, "dev.postman_environment.json");
        File.WriteAllBytes(path, [.. "{\"name\": \"dev\",\n\"values\": [{\"key\": \"city\", \"value\": \"Z"u8, 0xFC, .. "rich\"}]}"u8]);

        var error = Assert.Throws<IOException>(() => TextFile.Read(path));
        Assert.Contains("not UTF-8", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);

        var workspace = ApiWorkspace.Open(directory);
        Assert.Empty(workspace.Environments);
        Assert.Contains(workspace.LoadErrors, e => e.Contains("not UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_regular_files_of_a_sane_size_are_read()
    {
        // A symbolic link in a cloned repository can point at a device or a pipe: reading must neither exhaust memory nor block.
        var device = OperatingSystem.IsWindows() ? "NUL" : "/dev/zero";
        Assert.Contains("not a regular file", Assert.Throws<IOException>(() => TextFile.Read(device)).Message, StringComparison.Ordinal);

        var path = Path.Combine(TestFiles.NewTempDirectory(), "huge.postman_collection.json");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.SetLength(TextFile.MaxSize + 1);
            }

            Assert.Contains("too large", Assert.Throws<IOException>(() => TextFile.Read(path)).Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
