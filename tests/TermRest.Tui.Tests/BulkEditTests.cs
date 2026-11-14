using TermRest.Core.Model;
using TermRest.Tui.Dialogs;
using Xunit;

namespace TermRest.Tui.Tests;

public sealed class BulkEditTests
{
    private static List<KeyValueEntry> Rows() =>
    [
        new() { Key = "cert", Value = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", Type = "default" },
        new() { Key = "json", Value = "{\r\n  \"note\": \"a\\nb\"\r\n}" },
        new() { Key = "path", Value = @"C:\new\temp" },
        new() { Key = "flag", Value = null },
        new() { Key = "files", Type = "file", Src = "a.png\nb.png" },
        new() { Key = "token", Value = "s3cr3t", Type = "secret", Enabled = false },
    ];

    [Fact]
    public void Applying_the_text_unchanged_keeps_every_row_as_it_was()
    {
        var rows = Rows();

        var text = BulkEditDialog.Format(rows);
        var parsed = BulkEditDialog.Parse(text, rows);

        Assert.Equal(rows.Count, text.Split('\n').Length);
        Assert.DoesNotContain("s3cr3t", text, StringComparison.Ordinal);
        Assert.True(KeyValueEntry.ListEquals(rows, parsed), string.Join('\n', parsed));
    }

    [Fact]
    public void Edited_rows_keep_their_line_breaks_files_and_secrets()
    {
        var rows = Rows();
        var lines = BulkEditDialog.Format(rows).Split('\n');
        Assert.Equal("cert:-----BEGIN CERTIFICATE-----\\nMIIB\\n-----END CERTIFICATE-----", lines[0]);
        Assert.Equal("files:a.png;b.png", lines[4]);
        lines[0] = lines[0].Replace("MIIB", "MIIC", StringComparison.Ordinal);
        lines[4] = "files:a.png; c.png";
        lines[5] = lines[5].Replace("token:", "Token2:", StringComparison.Ordinal); // renamed, value left masked

        var parsed = BulkEditDialog.Parse(string.Join('\n', lines), rows);

        Assert.Equal("-----BEGIN CERTIFICATE-----\nMIIC\n-----END CERTIFICATE-----", parsed[0].Value);
        Assert.Equal("a.png\nc.png", parsed[4].Src);
        Assert.Equal(("Token2", "s3cr3t", true, false), (parsed[5].Key, parsed[5].Value, parsed[5].IsSecret, parsed[5].Enabled));
    }

    [Fact]
    public void Keys_holding_colons_keep_their_rows_unchanged_and_edited()
    {
        List<KeyValueEntry> rows =
        [
            new() { Key = "Api", Value = "BaseUrl:https://old.example.com" }, // its line starts like the next row's
            new() { Key = "Api:BaseUrl", Value = "https://api.example.com" },
            new() { Key = "Db:Password", Value = "hunter2", Type = "secret" },
        ];
        var lines = BulkEditDialog.Format(rows).Split('\n');

        var unchanged = BulkEditDialog.Parse(string.Join('\n', lines), rows);
        lines[1] = lines[1].Replace("api.example", "api2.example", StringComparison.Ordinal);
        var edited = BulkEditDialog.Parse(string.Join('\n', lines), rows);

        Assert.True(KeyValueEntry.ListEquals(rows, unchanged), string.Join('\n', unchanged));
        Assert.Equal(
            ["Api = BaseUrl:https://old.example.com", "Api:BaseUrl = https://api2.example.com", "Db:Password = hunter2 (secret)"],
            edited.Select(e => $"{e.Key} = {e.Value}{(e.IsSecret ? " (secret)" : "")}"));
    }

    [Fact]
    public void A_renamed_row_is_the_row_in_its_place_and_a_mask_keeps_a_secret_only_when_it_is_clear_which()
    {
        var rows = Rows();
        var lines = BulkEditDialog.Format(rows).Split('\n');
        lines[0] = "client_" + lines[0]; // cert renamed, its value left as shown

        var renamed = BulkEditDialog.Parse(string.Join('\n', lines), rows);

        Assert.Equal(("client_cert", rows[0].Value), (renamed[0].Key, renamed[0].Value));

        List<KeyValueEntry> secrets = [new() { Key = "api_key", Value = "K", Type = "secret" }, new() { Key = "password", Value = "P", Type = "secret" }];
        var mask = BulkEditDialog.SecretMask;
        List<string> unclear = [], clear = [];

        var removedAndRenamed = BulkEditDialog.Parse($"admin_password:{mask}", secrets, unclear); // which one is it?
        var renamedAndKept = BulkEditDialog.Parse($"admin_password:{mask}\napi_key:{mask}", secrets, clear);

        Assert.Equal(["admin_password"], unclear);
        Assert.Equal(mask, Assert.Single(removedAndRenamed).Value);
        Assert.Empty(clear);
        Assert.Equal(["P", "K"], renamedAndKept.Select(e => e.Value));
    }

    [Fact]
    public void A_line_left_as_written_is_its_own_row_where_rows_share_a_key()
    {
        List<KeyValueEntry> parts = [new() { Key = "file", Type = "file", Src = "a.png" }, new() { Key = "file", Value = "x", Type = "text" }];
        List<KeyValueEntry> tags = [new() { Key = "tag", Value = "a", Description = "first" }, new() { Key = "tag", Value = "b", Description = "second" }];
        List<KeyValueEntry> variables = [new() { Key = "X", Value = "p" }, new() { Key = "X", Value = "s3cr3t", Type = "secret" }];
        var unclear = new List<string>();

        // The first line deleted, or the lines swapped.
        var part = Assert.Single(BulkEditDialog.Parse(BulkEditDialog.Format(parts).Split('\n')[1], parts));
        var swapped = BulkEditDialog.Parse(string.Join('\n', BulkEditDialog.Format(tags).Split('\n').Reverse()), tags);
        var secret = Assert.Single(BulkEditDialog.Parse(BulkEditDialog.Format(variables).Split('\n')[1], variables, unclear));

        Assert.Equal(("x", null, false), (part.Value, part.Src, part.IsFile));
        Assert.Equal(["b second", "a first"], swapped.Select(e => $"{e.Value} {e.Description}"));
        Assert.Equal(("s3cr3t", true), (secret.Value, secret.IsSecret));
        Assert.Empty(unclear);
    }
}
