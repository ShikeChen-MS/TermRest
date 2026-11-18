using System.Net;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class ProxyTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static PreparedRequest Prepare(string url, ExecutionDefaults defaults) =>
        new RequestPreparer(VariableResolver.Empty, null, defaults).Prepare(new HttpRequestSpec { Url = url }, null, AppContext.BaseDirectory);

    [Fact]
    public async Task A_proxy_that_asks_for_basic_sign_in_gets_the_user_name_and_password_given_for_it()
    {
        await using var proxy = new SignInProxy("CONTOSO\\ada", "p@ss:wörd");
        var executor = new HttpExecutor();

        // Given for it (kept in the user's settings, or --proxy-user): a user name with a domain goes as typed in Basic.
        var given = await executor.SendAsync(Prepare("http://api.example.test/hello", new ExecutionDefaults
        {
            Proxy = proxy.Url,
            ProxyCredential = ProxyCredentialStore.Credential("CONTOSO\\ada", "p@ss:wörd"),
        }), _ct);

        // In the proxy's URL, escaped as URLs have it.
        var inUrl = await executor.SendAsync(Prepare("http://api.example.test/hello", new ExecutionDefaults
        {
            Proxy = proxy.Url.Replace("http://", "http://CONTOSO%5Cada:p%40ss%3Aw%C3%B6rd@", StringComparison.Ordinal),
        }), _ct);

        // None: the user's own sign-in, which a Basic proxy never gets; the proxy's 407 comes back, with what to do.
        var none = await executor.SendAsync(Prepare("http://api.example.test/hello", new ExecutionDefaults { Proxy = proxy.Url }), _ct);

        Assert.Equal((200, "through the proxy"), (given.Response?.StatusCode, System.Text.Encoding.UTF8.GetString(given.Response!.Body)));
        Assert.Equal(200, inUrl.Response?.StatusCode);
        Assert.Equal(407, none.Response?.StatusCode);
        Assert.Contains(none.Request.Warnings, w => w.StartsWith("The proxy asks for a user name and password (407", StringComparison.Ordinal));
        Assert.DoesNotContain(given.Request.Warnings, w => w.Contains("407", StringComparison.Ordinal));

        // Each signed in after the proxy's challenge, and never sent the password where the request is shown or recorded.
        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CONTOSO\\ada:p@ss:wörd"));
        Assert.Equal(
            ["GET http://api.example.test/hello HTTP/1.1 | -", $"GET http://api.example.test/hello HTTP/1.1 | {expected}",
             "GET http://api.example.test/hello HTTP/1.1 | -", $"GET http://api.example.test/hello HTTP/1.1 | {expected}",
             "GET http://api.example.test/hello HTTP/1.1 | -"],
            proxy.Requests);
        Assert.DoesNotContain(given.Request.Headers, h => h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_tunnel_the_proxy_refuses_until_it_gets_a_sign_in_says_how_to_give_one()
    {
        await using var proxy = new SignInProxy("ada", "s3cret");

        var exchange = await new HttpExecutor().SendAsync(Prepare("https://api.example.test/x", new ExecutionDefaults { Proxy = proxy.Url }), _ct);

        Assert.Null(exchange.Response);
        Assert.Contains("407", exchange.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("give them in Tools › Settings (Proxy sign-in), with --proxy-user", exchange.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxy_credentials_are_kept_per_proxy_in_a_private_file_with_the_password_encrypted_on_windows()
    {
        var directory = TestFiles.NewTempDirectory();
        var store = ProxyCredentialStore.Load(directory);
        Assert.True(store.Set("http://ada:old@Proxy.Contoso.com:3128/path", "CONTOSO\\ada", "s3cret-pw"));
        Assert.True(store.Set(null, "bob", "hunter2-pw")); // the system's proxy
        Assert.False(store.Set("none", "carol", "x")); // no proxy: nothing to sign in to
        store.Save(directory);

        var file = Path.Combine(directory, ProxyCredentialStore.FileName);
        var text = File.ReadAllText(file);
        Assert.Contains("\"http://proxy.contoso.com:3128\"", text, StringComparison.Ordinal);
        Assert.Contains("\"system\"", text, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("s3cret-pw", text, StringComparison.Ordinal);
            Assert.Contains("\"password\": \"dpapi:", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        var read = ProxyCredentialStore.Load(directory);
        var contoso = read.Find("http://proxy.contoso.com:3128");
        Assert.Equal(("ada", "s3cret-pw", "CONTOSO"), (contoso?.UserName, contoso?.Password, contoso?.Domain));
        Assert.Equal(("bob", "hunter2-pw"), (read.Find("system")?.UserName, read.Find("")?.Password));
        Assert.Equal(("CONTOSO\\ada", true), read.Describe("HTTP://proxy.contoso.com:3128"));
        Assert.Null(read.Find("http://other:3128"));

        // A password left as it was (null) stays; an empty user forgets them; with nothing kept, the file goes.
        Assert.True(read.Set("http://proxy.contoso.com:3128", "CONTOSO\\ada2", null));
        Assert.Equal("s3cret-pw", read.Find("http://proxy.contoso.com:3128")?.Password);
        Assert.True(read.Set("http://proxy.contoso.com:3128", "", null));
        Assert.True(read.Set("system", " ", null));
        read.Save(directory);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Requests_use_the_proxy_and_sign_in_given_on_the_command_line_over_those_kept()
    {
        var directory = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(directory);
        workspace.Settings.Proxy = "http://proxy.contoso.com:3128"; // not allowed for this folder: not used
        using var runner = new RequestRunner(workspace);
        runner.ConfigureProxy(directory);
        runner.ProxyCredentials.Set("system", "kept", "kept-pw");

        var kept = runner.CreateDefaults();
        runner.ProxyOverride = "http://cli-proxy:8080";
        runner.ProxyCredentials.Set("http://cli-proxy:8080", "kept2", "kept2-pw");
        var overridden = runner.CreateDefaults();
        runner.ProxyCredentialOverride = new NetworkCredential("cli", "cli-pw");
        var given = runner.CreateDefaults();

        Assert.Equal((null, "kept"), (kept.Proxy, kept.ProxyCredential?.UserName));
        Assert.Null(runner.BlockedProxy); // replaced: nothing to warn about
        Assert.Equal(("http://cli-proxy:8080", "kept2"), (overridden.Proxy, overridden.ProxyCredential?.UserName));
        Assert.Equal("cli", given.ProxyCredential?.UserName);
    }
}
