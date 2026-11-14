using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Views;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>The sidebar tree of collections, folders and requests.</summary>
[Collection("TUI")]
public sealed class CollectionTreeTests
{
    [Fact]
    public void Characters_outside_the_BMP_in_names_and_methods_show_as_replacement_characters()
    {
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "Emoji.postman_collection.json"), """
            {
              "info": { "name": "Emoji 🚀", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "📁 Docs", "item": [] },
                { "name": "Deploy 🚀 now", "request": { "method": "GET", "url": "https://example.com" } },
                { "name": "Custom", "request": { "method": "abc😀e", "url": "https://example.com" } }
              ]
            }
            """);
        using var app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(60, 10);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(new CollectionTree(ApiWorkspace.Open(directory)));
        var token = app.Begin(window);

        // Terminal.Gui's tree draws each UTF-16 unit in a cell of its own, and fails on half of a pair.
        app.LayoutAndDraw(forceRedraw: true);
        var screen = app.Driver.ToString();
        app.End(token!);

        Assert.Contains("Emoji \uFFFD", screen, StringComparison.Ordinal);
        Assert.Contains("\uFFFD Docs", screen, StringComparison.Ordinal);
        Assert.Contains("GET  Deploy \uFFFD now", screen, StringComparison.Ordinal);
        Assert.Contains("ABC\uFFFD Custom", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Collections_of_the_same_name_show_their_files()
    {
        // An imported update (copied in as "… (2)"), a copy in postman/collections and one converted into a v3 folder.
        var directory = TestFiles.NewTempDirectory();
        const string Collection = """{ "info": { "name": "NAME", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" }, "item": [] }""";
        var collections = Path.Combine(directory, "postman", "collections");
        Directory.CreateDirectory(collections);
        File.WriteAllText(Path.Combine(directory, "Orders API.postman_collection.json"), Collection.Replace("NAME", "Orders API", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(directory, "Orders API (2).postman_collection.json"), Collection.Replace("NAME", "Orders API", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(collections, "Orders API.postman_collection.json"), Collection.Replace("NAME", "Orders API", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(directory, "Billing.postman_collection.json"), Collection.Replace("NAME", "Billing", StringComparison.Ordinal));
        ApiWorkspace.Open(directory).CreateCollection("Orders API", v3: true);
        using var app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(100, 10);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(new CollectionTree(ApiWorkspace.Open(directory)));
        var token = app.Begin(window);

        app.LayoutAndDraw(forceRedraw: true);
        var screen = app.Driver.ToString();
        app.End(token!);

        Assert.Contains("Orders API (Orders API.postman_collection.json)", screen, StringComparison.Ordinal);
        Assert.Contains("Orders API (Orders API (2).postman_collection.json)", screen, StringComparison.Ordinal);
        Assert.Contains("Orders API (postman/collections/Orders API.postman_collection.json)", screen, StringComparison.Ordinal);
        Assert.Contains("Orders API (Orders API)", screen, StringComparison.Ordinal);
        Assert.Contains("Billing", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Billing (", screen, StringComparison.Ordinal);
    }
}
