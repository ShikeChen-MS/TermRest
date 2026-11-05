using System.Security.AccessControl;
using TermRest.Core.IO;
using TermRest.Core.Tests.Infrastructure;
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
}
