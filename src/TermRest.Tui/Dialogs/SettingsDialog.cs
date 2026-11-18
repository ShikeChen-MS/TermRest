using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;

namespace TermRest.Tui.Dialogs;

/// <summary>Workspace settings (<c>.termrest/settings.json</c>): TLS verification, CA bundle, timeouts, redirects, proxy, history, formats.</summary>
internal static class SettingsDialog
{
    /// <summary>
    /// Edits the settings; true when they changed, which applies them for this session. <paramref name="saved"/> says
    /// whether they were saved to the file too (when not, an error said why). A proxy URL saved here is the user's own
    /// choice: it is allowed for this workspace folder (see <see cref="RequestRunner.AllowedProxy"/>), in the user's
    /// settings in <paramref name="userDirectory"/> (default: <see cref="UserSettings.DefaultDirectory"/>), as is whether
    /// requests may send files from outside the folder (<see cref="RequestRunner.OutsideFilesAllowed"/>).
    /// </summary>
    public static bool Show(IApplication app, ApiWorkspace workspace, RequestRunner runner, out bool saved, string? userDirectory = null)
    {
        saved = false;
        var settings = workspace.Settings;

        // 80% of the screen, but at least 78 columns (all of a terminal of 80 but a margin): a dialog does not scroll, and
        // every control fits in 78.
        int Columns() => app.Driver?.Cols ?? 120;
        using var dialog = new Dialog { Title = "Workspace settings", Width = Dim.Func(_ => Math.Min(Columns() - 2, Math.Max(78, Columns() * 4 / 5))) };
        var y = 1;
        var strict = Ui.AddRow(dialog, "SSL verification", new CheckBox { Text = "Verify server certificates", Value = settings.StrictSsl ? CheckState.Checked : CheckState.UnChecked, Width = 30 }, y, 22);
        y += 1; // a blank line only between groups of settings: the dialog fits a terminal of 30 rows
        var caBundle = Ui.AddRow(dialog, "Extra CA bundle", new TextField { Text = settings.CaBundle ?? "", Width = Dim.Fill(13) }, y, 22);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
        Ui.OnPress(browse, () =>
        {
            if (Ui.PickFile(app, "Choose a PEM file with trusted CA certificates", workspace.RootPath) is { } path)
            {
                var relative = Path.GetRelativePath(workspace.RootPath, path);
                caBundle.Text = relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
            }
        });
        dialog.Add(browse);
        y += 1;
        dialog.Add(Ui.Hint("Trusted in addition to the OS trust store (private/corporate CAs).", 24, y));
        y += 2;
        var timeout = Ui.AddRow(dialog, "Timeout (ms)", new TextField { Text = settings.TimeoutMs.ToString(CultureInfo.InvariantCulture), Width = 12 }, y, 22);
        dialog.Add(Ui.Hint("0 = no timeout (press Esc to cancel)", 38, y));
        y += 1;
        var follow = Ui.AddRow(dialog, "Redirects", new CheckBox { Text = "Follow redirects", Value = settings.FollowRedirects ? CheckState.Checked : CheckState.UnChecked, Width = 20 }, y, 22);
        var maxRedirects = new TextField { X = 52, Y = y, Width = 6, Text = settings.MaxRedirects.ToString(CultureInfo.InvariantCulture) };
        dialog.Add(new Label { Text = "max", X = 47, Y = y }, maxRedirects);
        y += 1;
        // Its password masked, as everywhere else; left masked, it stays as it was (see Unmasked).
        var shownProxy = settings.Proxy is { } current ? PostmanUrl.MaskCredentials(current) : "system";
        var proxy = Ui.AddRow(dialog, "Proxy", new TextField { Text = shownProxy }, y, 22);
        y += 1;
        var proxyHint = Ui.Hint("system (OS / HTTPS_PROXY settings), none, or a URL like http://proxy:8080", 24, y);
        if (runner.BlockedProxy is not null)
        {
            proxyHint.Text = "⚠ Set by the workspace: not used until you save";
            proxyHint.SchemeName = ThemeCatalog.WarningScheme;
        }

        dialog.Add(proxyHint);
        y += 1;

        // Kept for the proxy in the user's private credentials file, never in the workspace; a password left masked stays.
        const string keptPassword = "********";
        var (keptUser, hasPassword) = runner.ProxyCredentials.Describe(settings.Proxy);
        var proxyUser = Ui.AddRow(dialog, "Proxy sign-in", new TextField { Text = keptUser, Width = 22 }, y, 22);
        var proxyPassword = new TextField { X = Pos.Right(proxyUser) + 11, Y = y, Width = 16, Secret = true, Text = hasPassword ? keptPassword : "" };
        dialog.Add(new Label { Text = "password", X = Pos.Right(proxyUser) + 2, Y = y }, proxyPassword);
        y += 1;
        var http = Ui.AddRow(dialog, "HTTP version", new OptionSelector { Labels = ["HTTP/1.1", "HTTP/2 (when supported)"], Orientation = Orientation.Horizontal, Value = settings.HttpVersion == "2" ? 1 : 0 }, y, 22);
        y += 1;
        var maxSize = Ui.AddRow(dialog, "Max response (MB)", new TextField { Text = settings.MaxResponseSizeMb.ToString(CultureInfo.InvariantCulture), Width = 8 }, y, 22);
        y += 2;
        var history = Ui.AddRow(dialog, "History", new CheckBox { Text = "Record sent requests", Value = settings.HistoryEnabled ? CheckState.Checked : CheckState.UnChecked, Width = 24 }, y, 22);
        y += 1;
        var historyLimit = new TextField { X = 29, Y = y, Width = 6, Text = settings.HistoryLimit.ToString(CultureInfo.InvariantCulture) };
        var bodies = new Label { Text = "entries, bodies up to", X = Pos.Right(historyLimit) + 1, Y = y };
        var historyKb = new TextField { X = Pos.Right(bodies) + 1, Y = y, Width = 6, Text = settings.HistoryResponseKb.ToString(CultureInfo.InvariantCulture) };
        dialog.Add(new Label { Text = "Keep", X = 24, Y = y }, historyLimit, bodies, historyKb, new Label { Text = "KB", X = Pos.Right(historyKb) + 1, Y = y });
        y += 1;
        dialog.Add(Ui.Hint("Stored per user (not in the workspace); credentials and secret variables are masked.", 24, y));
        y += 2;
        var format = Ui.AddRow(dialog, "New collections", new OptionSelector
        {
            Labels = ["Like this workspace", "v2.1 JSON", "v3 YAML"],
            Orientation = Orientation.Horizontal,
            Value = settings.CollectionFormat switch { "v2.1" => 1, "v3" => 2, _ => 0 },
        }, y, 22);
        y += 1;
        var wrap = Ui.AddRow(dialog, "Wrap YAML lines", new OptionSelector
        {
            Labels = ["Like the files", "Never", "At column"],
            Orientation = Orientation.Horizontal,
            Value = settings.YamlLineWidth switch { null => 0, <= 0 => 1, _ => 2 },
        }, y, 22);
        var wrapWidth = new TextField
        {
            X = Pos.Right(wrap) + 1,
            Y = y,
            Width = 6,
            Text = (settings.YamlLineWidth is > 0 and var width ? width : 80).ToString(CultureInfo.InvariantCulture),
        };
        dialog.Add(wrapWidth);
        y += 1;
        dialog.Add(Ui.Hint("Postman v3 YAML files: only new and changed values are wrapped. Older Postman versions wrapped at 80.", 24, y));
        y += 2;
        var redact = Ui.AddRow(dialog, "Recordings redact", new TextField { Text = string.Join(", ", settings.RecordingRedact ?? []) }, y, 22);
        y += 1;
        dialog.Add(Ui.Hint("Extra header, parameter and field names, comma-separated (credentials, cookies and tokens always are).", 24, y));
        y += 1; // no blank line before the last row: with the proxy sign-in, the dialog still fits a terminal of 30 rows
        var outsideFiles = Ui.AddRow(dialog, "Outside files", new CheckBox
        {
            Text = "Allow files from outside this folder",
            Value = runner.OutsideFilesAllowed ? CheckState.Checked : CheckState.UnChecked,
            Width = 40,
        }, y, 22);
        y += 1;
        dialog.Add(Ui.Hint("Body files, and certificates on network shares.", 24, y));

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
                || !int.TryParse(maxRedirects.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var redirects) || redirects < 0
                || !int.TryParse(maxSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeMb) || sizeMb < 1
                || !int.TryParse(historyLimit.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep) || keep < 1
                || !int.TryParse(historyKb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyKb) || bodyKb < 0)
            {
                Ui.Error(app, "Invalid settings", "Timeout must be ≥ 0, max redirects ≥ 0 (0 follows none), max response size ≥ 1 MB, history ≥ 1 entry and body size ≥ 0 KB.");
                continue;
            }

            // "system" and "none" in any case, as requests read them; a URL's credentials left masked as shown, as they were.
            var proxyText = Unmasked(proxy.Text.Trim(), shownProxy, settings.Proxy);
            if (proxyText.ToLowerInvariant() is "" or "system" or "none")
            {
                proxyText = proxyText.ToLowerInvariant();
            }
            else if (!Uri.TryCreate(proxyText, UriKind.Absolute, out _))
            {
                Ui.Error(app, "Invalid proxy", "Use system, none, or a proxy URL such as http://proxy:8080.");
                continue;
            }

            var lineWidth = 0;
            if (wrap.Value == 2
                && (!int.TryParse(wrapWidth.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out lineWidth)
                    || lineWidth is < WorkspaceSettings.MinYamlLineWidth or > WorkspaceSettings.MaxYamlLineWidth))
            {
                Ui.Error(app, "Invalid line width", $"Wrap YAML lines at a column from {WorkspaceSettings.MinYamlLineWidth} to {WorkspaceSettings.MaxYamlLineWidth} (80 is what Postman used).");
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
            settings.YamlLineWidth = wrap.Value switch { 1 => 0, 2 => lineWidth, _ => null };
            var names = redact.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.RecordingRedact = names.Count == 0 ? null : names;
            saved = Ui.TrySave(app, workspace.SaveSettings); // applied for this session either way
            saved &= SaveApprovals(app, workspace, runner, outsideFiles.Value == CheckState.Checked, userDirectory);

            // The sign-in goes with the proxy the settings now name (the system's, or a URL), for this session and in the
            // user's credentials file.
            var password = proxyPassword.Text == keptPassword && hasPassword ? null : proxyPassword.Text;
            if (runner.ProxyCredentials.Set(settings.Proxy, proxyUser.Text, password))
            {
                saved &= Ui.TrySave(app, () => runner.ProxyCredentials.Save(userDirectory));
            }

            return true;
        }
    }

    /// <summary>
    /// <paramref name="typed"/> with the user info it holds as <paramref name="shown"/> showed it (masked) put back as it was
    /// in <paramref name="original"/>: a proxy URL whose password was left masked keeps it, its host or port changed or not.
    /// </summary>
    internal static string Unmasked(string typed, string shown, string? original)
    {
        if (original is null || shown == original
            || PostmanUrl.FindUserInfo(typed) is not var (start, length)
            || PostmanUrl.FindUserInfo(shown) is not var (shownStart, shownLength)
            || PostmanUrl.FindUserInfo(original) is not var (originalStart, originalLength)
            || !typed.AsSpan(start, length).SequenceEqual(shown.AsSpan(shownStart, shownLength)))
        {
            return typed;
        }

        return string.Concat(typed.AsSpan(0, start), original.AsSpan(originalStart, originalLength), typed.AsSpan(start + length));
    }

    /// <summary>
    /// Allows the proxy URL the settings now name, if any, for this workspace folder, in place of the one allowed before,
    /// and files from outside the folder or not: for this session, and in the user's settings. False when those could not
    /// be saved (an error said why).
    /// </summary>
    private static bool SaveApprovals(IApplication app, ApiWorkspace workspace, RequestRunner runner, bool outsideFiles, string? userDirectory)
    {
        var proxy = workspace.Settings.Proxy;
        runner.AllowedProxy = WorkspaceSettings.IsProxyUrl(proxy) ? proxy : null;
        runner.OutsideFilesAllowed = outsideFiles;
        var user = UserSettings.Load(userDirectory);
        var changed = user.AllowProxy(workspace.RootPath, runner.AllowedProxy) | user.AllowOutsideFiles(workspace.RootPath, outsideFiles);
        return !changed || Ui.TrySave(app, () => user.Save(userDirectory));
    }
}
