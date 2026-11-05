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
/// body, client certificate) as JSON; other endpoints cover redirects, cookies, status codes and slow responses.
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

    public static async Task<EchoServer> StartAsync(X509Certificate2? serverCertificate = null, bool requireClientCertificate = false)
    {
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
                        if (requireClientCertificate)
                        {
                            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                            https.AllowAnyClientCertificate();
                        }
                    });
                }
            });
        });

        var app = builder.Build();
        app.Map("/echo/{**rest}", Echo);
        app.Map("/echo", Echo);
        app.MapGet("/redirect", () => Results.Redirect("/echo?redirected=true"));
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

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/");
        return new EchoServer(app, uri);
    }

    private static async Task Echo(HttpContext ctx)
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
