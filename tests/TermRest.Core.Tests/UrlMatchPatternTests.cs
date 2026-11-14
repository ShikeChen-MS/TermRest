using TermRest.Core.Certificates;
using Xunit;

namespace TermRest.Core.Tests;

public class UrlMatchPatternTests
{
    [Theory]
    [InlineData("https://api.contoso.com/*", "https://api.contoso.com/v1/x", true)]
    [InlineData("https://api.contoso.com/*", "http://api.contoso.com/v1/x", false)]
    [InlineData("https://api.contoso.com/*", "https://api.contoso.com:8443/x", true)]
    [InlineData("https://api.contoso.com:8443/*", "https://api.contoso.com/x", false)]
    [InlineData("https://api.contoso.com:8443/*", "https://api.contoso.com:8443/x", true)]
    [InlineData("https://*.contoso.com/*", "https://deep.api.contoso.com/x", true)]
    [InlineData("https://*.contoso.com/*", "https://contoso.com/x", true)]
    [InlineData("https://*.contoso.com/*", "https://evilcontoso.com/x", false)]
    [InlineData("*://localhost/*", "http://localhost:5000/", true)]
    [InlineData("https://api.contoso.com/v1/*", "https://api.contoso.com/v2/x", false)]
    [InlineData("<all_urls>", "https://anything.example/", true)]
    [InlineData("api.contoso.com", "https://API.CONTOSO.COM/", true)]
    public void Matches_like_postman(string pattern, string url, bool expected)
    {
        Assert.True(UrlMatchPattern.TryParse(pattern, out var parsed));
        Assert.Equal(expected, parsed!.IsMatch(new Uri(url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://api.*.com/*")]
    [InlineData("https://host:notaport/*")]
    [InlineData("https://bü..example/*")]
    public void Rejects_invalid_patterns(string pattern)
    {
        Assert.False(UrlMatchPattern.TryParse(pattern, out _));
    }

    [Theory]
    [InlineData("https://bücher.example/*", "https://bücher.example/api")]
    [InlineData("https://BÜCHER.example/*", "https://xn--bcher-kva.example/api")]
    [InlineData("https://*.bücher.example/*", "https://shop.bücher.example/")]
    [InlineData("https://xn--bcher-kva.example/*", "https://bücher.example/api")]
    public void A_host_matches_whether_written_in_unicode_or_in_ascii(string pattern, string url)
    {
        Assert.True(UrlMatchPattern.TryParse(pattern, out var parsed));
        Assert.True(parsed!.IsMatch(new Uri(url)));
    }

    [Theory]
    [InlineData("https://[::1]:8443/*", "https://[::1]:8443/api", true)]
    [InlineData("https://[::1]/*", "https://[::1]:8443/api", true)]
    [InlineData("https://[0:0::1]:*/*", "https://[::1]:8443/api", true)]
    [InlineData("https://[FE80::A]/*", "https://[fe80:0::a]/", true)]
    [InlineData("https://[::2]/*", "https://[::1]/", false)]
    public void An_ipv6_host_matches_in_any_of_its_forms(string pattern, string url, bool expected)
    {
        Assert.True(UrlMatchPattern.TryParse(pattern, out var parsed));
        Assert.Equal(expected, parsed!.IsMatch(new Uri(url)));
    }

    [Theory]
    [InlineData("https://[::1/*")]
    [InlineData("https://[1.2.3.4]/*")]
    [InlineData("https://[nothex::1]:8443/*")]
    public void Rejects_invalid_ipv6_hosts(string pattern)
    {
        Assert.False(UrlMatchPattern.TryParse(pattern, out _));
    }

    [Fact]
    public void Registry_prefers_the_most_specific_match()
    {
        var registry = new CertificateRegistry(Path.GetTempPath())
        {
            Entries =
            [
                new CertificateEntry { Name = "any", Matches = ["<all_urls>"] },
                new CertificateEntry { Name = "wildcard", Matches = ["https://*.contoso.com/*"] },
                new CertificateEntry { Name = "exact", Matches = ["https://api.contoso.com/*"] },
                new CertificateEntry { Name = "exact-port", Matches = ["https://api.contoso.com:8443/*"] },
                new CertificateEntry { Name = "disabled", Matches = ["https://api.contoso.com:8443/admin/*"], Disabled = true },
            ],
        };

        Assert.Equal("exact", registry.FindBestMatch(new Uri("https://api.contoso.com/x"))!.Value.Entry.Name);
        Assert.Equal("exact-port", registry.FindBestMatch(new Uri("https://api.contoso.com:8443/admin/x"))!.Value.Entry.Name);
        Assert.Equal("wildcard", registry.FindBestMatch(new Uri("https://web.contoso.com/"))!.Value.Entry.Name);
        Assert.Equal("any", registry.FindBestMatch(new Uri("https://other.example/"))!.Value.Entry.Name);
    }
}
