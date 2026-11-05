using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Workspace;

namespace TermRest.Tui.Dialogs;

/// <summary>Workspace settings (<c>.termrest/settings.json</c>): TLS verification, CA bundle, timeouts, redirects, proxy, history, formats.</summary>
internal static class SettingsDialog
{
    public static bool Show(IApplication app, ApiWorkspace workspace)
    {
        var settings = workspace.Settings;
        using var dialog = new Dialog { Title = "Workspace settings", Width = Dim.Percent(80) };
        var y = 1;
        var strict = Ui.AddRow(dialog, "SSL verification", new CheckBox { Text = "Verify server certificates", Value = settings.StrictSsl ? CheckState.Checked : CheckState.UnChecked, Width = 30 }, y, 22);
        y += 2;
        var caBundle = Ui.AddRow(dialog, "Extra CA bundle", new TextField { Text = settings.CaBundle ?? "", Width = Dim.Fill(13) }, y, 22);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
        browse.Accepted += (_, _) =>
        {
            if (Ui.PickFile(app, "Choose a PEM file with trusted CA certificates", workspace.RootPath) is { } path)
            {
                var relative = Path.GetRelativePath(workspace.RootPath, path);
                caBundle.Text = relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
            }
        };
        dialog.Add(browse);
        y += 1;
        dialog.Add(new Label { Text = "Trusted in addition to the OS trust store (private/corporate CAs).", X = 24, Y = y });
        y += 2;
        var timeout = Ui.AddRow(dialog, "Timeout (ms)", new TextField { Text = settings.TimeoutMs.ToString(CultureInfo.InvariantCulture), Width = 12 }, y, 22);
        dialog.Add(new Label { Text = "0 = no timeout (press Esc to cancel)", X = 38, Y = y });
        y += 2;
        var follow = Ui.AddRow(dialog, "Redirects", new CheckBox { Text = "Follow redirects", Value = settings.FollowRedirects ? CheckState.Checked : CheckState.UnChecked, Width = 20 }, y, 22);
        var maxRedirects = new TextField { X = 52, Y = y, Width = 6, Text = settings.MaxRedirects.ToString(CultureInfo.InvariantCulture) };
        dialog.Add(new Label { Text = "max", X = 47, Y = y }, maxRedirects);
        y += 2;
        var proxy = Ui.AddRow(dialog, "Proxy", new TextField { Text = settings.Proxy ?? "system" }, y, 22);
        y += 1;
        dialog.Add(new Label { Text = "system (OS / HTTPS_PROXY settings), none, or a URL like http://proxy:8080", X = 24, Y = y });
        y += 2;
        var http = Ui.AddRow(dialog, "HTTP version", new OptionSelector { Labels = ["HTTP/1.1", "HTTP/2 (when supported)"], Orientation = Orientation.Horizontal, Value = settings.HttpVersion == "2" ? 1 : 0 }, y, 22);
        y += 2;
        var maxSize = Ui.AddRow(dialog, "Max response (MB)", new TextField { Text = settings.MaxResponseSizeMb.ToString(CultureInfo.InvariantCulture), Width = 8 }, y, 22);
        y += 2;
        var history = Ui.AddRow(dialog, "History", new CheckBox { Text = "Record sent requests", Value = settings.HistoryEnabled ? CheckState.Checked : CheckState.UnChecked, Width = 24 }, y, 22);
        var historyLimit = new TextField { X = 54, Y = y, Width = 6, Text = settings.HistoryLimit.ToString(CultureInfo.InvariantCulture) };
        var historyKb = new TextField { X = Pos.Right(historyLimit) + 22, Y = y, Width = 6, Text = settings.HistoryResponseKb.ToString(CultureInfo.InvariantCulture) };
        dialog.Add(
            new Label { Text = "keep", X = 49, Y = y },
            historyLimit,
            new Label { Text = "entries, bodies up to", X = Pos.Right(historyLimit) + 1, Y = y },
            historyKb,
            new Label { Text = "KB", X = Pos.Right(historyKb) + 1, Y = y });
        y += 1;
        dialog.Add(new Label { Text = "Stored per user (not in the workspace); credentials and secret variables are masked.", X = 24, Y = y });
        y += 2;
        var format = Ui.AddRow(dialog, "New collections", new OptionSelector
        {
            Labels = ["Like this workspace", "Postman v2.1 JSON", "Postman v3 YAML folder"],
            Orientation = Orientation.Horizontal,
            Value = settings.CollectionFormat switch { "v2.1" => 1, "v3" => 2, _ => 0 },
        }, y, 22);

        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Save" });
        while (true)
        {
            app.Run(dialog);
            if (dialog.Canceled)
            {
                return false;
            }

            if (!int.TryParse(timeout.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs < 0
                || !int.TryParse(maxRedirects.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var redirects) || redirects < 1
                || !int.TryParse(maxSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeMb) || sizeMb < 1
                || !int.TryParse(historyLimit.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep) || keep < 1
                || !int.TryParse(historyKb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyKb) || bodyKb < 0)
            {
                Ui.Error(app, "Invalid settings", "Timeout must be ≥ 0, max redirects ≥ 1, max response size ≥ 1 MB, history ≥ 1 entry and body size ≥ 0 KB.");
                continue;
            }

            var proxyText = proxy.Text.Trim();
            if (proxyText is not ("" or "system" or "none") && !Uri.TryCreate(proxyText, UriKind.Absolute, out _))
            {
                Ui.Error(app, "Invalid proxy", "Use system, none, or a proxy URL such as http://proxy:8080.");
                continue;
            }

            settings.StrictSsl = strict.Value == CheckState.Checked;
            settings.CaBundle = string.IsNullOrWhiteSpace(caBundle.Text) ? null : caBundle.Text.Trim();
            settings.TimeoutMs = timeoutMs;
            settings.FollowRedirects = follow.Value == CheckState.Checked;
            settings.MaxRedirects = redirects;
            settings.Proxy = proxyText is "" or "system" ? null : proxyText;
            settings.HttpVersion = http.Value == 1 ? "2" : "1.1";
            settings.MaxResponseSizeMb = sizeMb;
            settings.HistoryEnabled = history.Value == CheckState.Checked;
            settings.HistoryLimit = keep;
            settings.HistoryResponseKb = bodyKb;
            settings.CollectionFormat = format.Value switch { 1 => "v2.1", 2 => "v3", _ => null };
            workspace.SaveSettings();
            return true;
        }
    }
}
