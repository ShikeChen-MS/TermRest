using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// In-process Kestrel server for integration tests. <c>/echo</c> reflects the request (method, path, query, headers,
/// body, client certificate and the CA certificates sent with it) as JSON; <c>/mirror</c> answers with the request's body
/// and Content-Type as received; <c>/bytes/{hex}?type=…&amp;repeat=…&amp;encoding=…</c> answers with those bytes (labelled
/// with that Content-Encoding, as they are); other endpoints cover redirects, auth challenges, cookies, status codes and
/// slow responses.
/// </summary>
public sealed class EchoServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EchoServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public string Url(string path) => new Uri(BaseUri, path).ToString();

    /// <param name="askForClientCertificate">Request a client certificate without requiring one.</param>
    /// <param name="beforeEcho">Runs when a request to <c>/echo</c> has come in, before its body is read (with Expect: 100-continue, before it is sent).</param>
    /// <param name="delayClientCertificate">Ask for a client certificate only on <c>/renegotiate</c>, after the request came in (TLS 1.2).</param>
    public static async Task<EchoServer> StartAsync(X509Certificate2? serverCertificate = null, bool requireClientCertificate = false, bool askForClientCertificate = false, Action? beforeEcho = null, bool delayClientCertificate = false)
    {
        // The CA certificates each client certificate came with, by its thumbprint (/echo reports them).
        var clientChains = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                if (serverCertificate is not null)
                {
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = serverCertificate;
                        if (requireClientCertificate || askForClientCertificate || delayClientCertificate)
                        {
                            https.ClientCertificateMode = requireClientCertificate ? ClientCertificateMode.RequireCertificate
                                : delayClientCertificate ? ClientCertificateMode.DelayCertificate : ClientCertificateMode.AllowCertificate;
                            if (delayClientCertificate)
                            {
                                https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12; // TLS 1.3 has no renegotiation
                            }

                            https.ClientCertificateValidation = (certificate, chain, _) =>
                            {
                                clientChains[certificate.Thumbprint] = chain is null ? []
                                    : [.. chain.ChainPolicy.ExtraStore.Select(c => c.Thumbprint).Where(t => t != certificate.Thumbprint)];
                                return true;
                            };
                        }
                    });
                }
            });
        });

        var app = builder.Build();
        Task Echo(HttpContext ctx)
        {
            beforeEcho?.Invoke();
            return EchoAsync(ctx, clientChains);
        }

        app.Map("/echo/{**rest}", Echo);
        app.Map("/echo", Echo);

        // As openssl s_server -www, Python's http.server and HTTP/1.0 servers do: the connection is closed after the response.
        app.Map("/close", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Connection = "close";
            return EchoAsync(ctx, clientChains);
        });

        // As IIS without "Negotiate Client Certificate" and servers that want one for some paths only: the certificate is
        // asked for once the request came in. macOS can't renegotiate: there the request is echoed without one.
        app.Map("/renegotiate", async (HttpContext ctx) =>
        {
            try
            {
                await ctx.Connection.GetClientCertificateAsync(ctx.RequestAborted);
            }
            catch (PlatformNotSupportedException)
            {
            }

            await EchoAsync(ctx, clientChains);
        });
        app.Map("/mirror", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = ctx.Request.ContentType;
            await ctx.Request.Body.CopyToAsync(ctx.Response.Body);
        });
        app.MapGet("/redirect", () => Results.Redirect("/echo?redirected=true"));

        // Any method: redirects to ?url= with ?status= (302 by default), setting the cookie in ?cookie= if given.
        app.Map("/redirect-to", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = int.TryParse(ctx.Request.Query["status"], out var status) ? status : StatusCodes.Status302Found;
            ctx.Response.Headers.Location = ctx.Request.Query["url"].ToString();
            if (ctx.Request.Query["cookie"].ToString() is { Length: > 0 } cookie)
            {
                ctx.Response.Headers.SetCookie = cookie;
            }

            return Task.CompletedTask;
        });

        // Challenges with the scheme (Basic, Digest …) until a request is authorized with it, then echoes that request.
        app.Map("/challenge/{scheme}", async (HttpContext ctx, string scheme) =>
        {
            if (ctx.Request.Headers.Authorization.ToString().StartsWith(scheme + " ", StringComparison.OrdinalIgnoreCase))
            {
                await Echo(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = scheme.Equals("digest", StringComparison.OrdinalIgnoreCase)
                ? "Digest realm=\"termrest\", nonce=\"5f2b8d1e9c7a4b30\", qop=\"auth\", algorithm=MD5"
                : $"{scheme} realm=\"termrest\"";
        });
        app.MapGet("/set-cookie", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Append("session", "abc123");
            return Results.Text("cookie set");
        });
        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        app.MapGet("/slow", async (HttpContext ctx) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
            return Results.Text("late");
        });
        app.MapGet("/xml", () => Results.Text("<root><item id=\"1\">x</item></root>", "application/xml"));
        app.MapGet("/bytes/{hex}", (HttpContext ctx, string hex, string? type, int? repeat, string? encoding) =>
        {
            if (encoding is not null)
            {
                ctx.Response.Headers.ContentEncoding = encoding;
            }

            return Results.Bytes(Enumerable.Repeat(Convert.FromHexString(hex), repeat ?? 1).SelectMany(b => b).ToArray(), type ?? "application/octet-stream");
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/");
        return new EchoServer(app, uri);
    }

    private static async Task EchoAsync(HttpContext ctx, ConcurrentDictionary<string, string[]> clientChains)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var headers = new JsonObject();
        foreach (var (key, value) in ctx.Request.Headers)
        {
            headers[key.ToLowerInvariant()] = value.ToString();
        }

        var query = new JsonObject();
        foreach (var (key, value) in ctx.Request.Query)
        {
            query[key] = value.ToString();
        }

        var clientCertificate = ctx.Connection.ClientCertificate;
        var result = new JsonObject
        {
            ["method"] = ctx.Request.Method,
            ["path"] = ctx.Request.Path.Value,
            ["rawQuery"] = ctx.Request.QueryString.Value,
            ["query"] = query,
            ["headers"] = headers,
            ["body"] = body,
            ["protocol"] = ctx.Request.Protocol,
            ["clientCertificateSubject"] = clientCertificate?.Subject,
            ["clientCertificateThumbprint"] = clientCertificate?.Thumbprint,
            ["clientCertificateChain"] = clientCertificate is not null && clientChains.TryGetValue(clientCertificate.Thumbprint, out var chain)
                ? new JsonArray([.. chain.Select(thumbprint => JsonValue.Create(thumbprint))])
                : null,
        };
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(result.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
