using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TermRest.Tui.Tests.Infrastructure;

/// <summary>Minimal in-process HTTP echo server for UI tests.</summary>
public sealed class LocalEchoServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalEchoServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>Base URL without a trailing slash, e.g. http://127.0.0.1:5123.</summary>
    public string BaseUrl { get; }

    public static async Task<LocalEchoServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Map("/echo/{**rest}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var json = new JsonObject
            {
                ["method"] = ctx.Request.Method,
                ["path"] = ctx.Request.Path.Value,
                ["query"] = ctx.Request.QueryString.Value,
                ["body"] = await reader.ReadToEndAsync(),
            };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json.ToJsonString());
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalEchoServer(app, address.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
