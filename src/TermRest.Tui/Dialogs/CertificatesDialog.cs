using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Certificates;
using TermRest.Core.IO;
using TermRest.Core.Workspace;

namespace TermRest.Tui.Dialogs;

/// <summary>
/// Client certificate manager for <c>.termrest/certificates.json</c>: add PEM, PFX or OS-store certificates, bind them to
/// URL patterns, and test that they load (including passphrases and private-key access).
/// </summary>
internal static class CertificatesDialog
{
    public static void Show(IApplication app, ApiWorkspace workspace, RequestRunner runner, string? suggestedUrl)
    {
        var registry = workspace.Certificates;
        using var dialog = new Dialog { Title = "Client certificates (mutual TLS)", Width = Dim.Percent(92), Height = Dim.Percent(85) };
        var table = new TableView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(8), FullRowSelect = true };
        table.Style.ExpandLastColumn = true;
        var details = new Label { X = 1, Y = Pos.AnchorEnd(7), Width = Dim.Fill(1), Height = 3, Text = "", HotKeySpecifier = Ui.NoHotKey };

        void Refresh(int select)
        {
            table.Table = new EnumerableTableSource<CertificateEntry>(registry.Entries, new Dictionary<string, Func<CertificateEntry, object>>
            {
                ["On"] = e => e.Disabled ? "off" : "on",
                ["Name"] = e => e.Name,
                ["Matches"] = e => string.Join(", ", e.Matches),
                ["Source"] = e => e.DescribeSource(),
            });
            table.Update();
            if (registry.Entries.Count > 0)
            {
                table.SetSelection(0, Math.Clamp(select, 0, registry.Entries.Count - 1), false);
            }
        }

        CertificateEntry? Selected() =>
            table.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < registry.Entries.Count
                ? registry.Entries[selection.SelectedCell.Y]
                : null;

        void Persist()
        {
            try
            {
                workspace.SaveCertificates();
            }
            catch (FileChangedException)
            {
                // A git pull or another TermRest changed the file: saving the list shown here would undo that.
                if (MessageBox.Query(app, "Changed on disk",
                        $"{Path.GetRelativePath(workspace.RootPath, workspace.CertificatesPath)} changed on disk since TermRest read it (a git pull, another TermRest).\n\n" +
                        "Saving the list shown here would undo those changes. Choose Cancel to load the file's entries instead: this change is then not saved.",
                        "Overwrite", "Cancel") != 0)
                {
                    Reload();
                    return;
                }

                if (!Ui.TrySave(app, () => workspace.SaveCertificates(overwriteChanged: true)))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Error(app, "Save failed", ex.Message);
                return;
            }

            runner.ReloadCertificates();
        }

        void Reload()
        {
            try
            {
                workspace.ReloadCertificates();
                registry = workspace.Certificates;
                runner.ReloadCertificates();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Ui.Error(app, "Load failed", $"{workspace.CertificatesPath}: {ex.Message}\n\nThe change was not saved.");
            }
        }

        void Add(CertificateEntry? template)
        {
            var entry = CertificateEntryDialog.Show(app, workspace, template ?? new CertificateEntry(), suggestedUrl);
            if (entry is not null)
            {
                registry.Entries.Add(entry);
                Persist();
                Refresh(registry.Entries.Count - 1);
            }
        }

        // Two rows of buttons, adding and changing entries, so that they fit a terminal of 80 columns.
        var addPem = new Button { Text = "Add PEM…", X = 1, Y = Pos.AnchorEnd(3) };
        Ui.OnPress(addPem, () => Add(new CertificateEntry { Cert = new FileSource(), Key = new FileSource() }));
        var addPfx = new Button { Text = "Add PFX…", X = Pos.Right(addPem) + 1, Y = Pos.AnchorEnd(3) };
        Ui.OnPress(addPfx, () => Add(new CertificateEntry { Pfx = new FileSource() }));
        var addStore = new Button { Text = "Add from OS store…", X = Pos.Right(addPfx) + 1, Y = Pos.AnchorEnd(3) };
        Ui.OnPress(addStore, () =>
        {
            if (StorePickerDialog.Show(app) is { } picked)
            {
                Add(new CertificateEntry
                {
                    Name = picked.CommonName,
                    Store = new StoreSource { Location = picked.Source?.Split('\\')[0] ?? "CurrentUser", StoreName = "My", Thumbprint = picked.Thumbprint },
                });
            }
        });
        var edit = new Button { Text = "Edit…", X = 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(edit, () =>
        {
            if (Selected() is { } selected && CertificateEntryDialog.Show(app, workspace, selected.Clone(), suggestedUrl) is { } edited)
            {
                var index = registry.Entries.IndexOf(selected);
                registry.Entries[index] = edited;
                Persist();
                Refresh(index);
            }
        });
        var toggle = new Button { Text = "On/Off", X = Pos.Right(edit) + 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(toggle, () =>
        {
            if (Selected() is { } selected)
            {
                var index = registry.Entries.IndexOf(selected);
                selected.Disabled = !selected.Disabled;
                Persist();
                Refresh(index);
            }
        });
        var remove = new Button { Text = "Remove", X = Pos.Right(toggle) + 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(remove, () =>
        {
            if (Selected() is { } selected && Ui.Confirm(app, "Remove certificate", $"Remove '{selected.Name}' from the workspace registry? (Files and store entries are not deleted.)", "Remove", "Cancel"))
            {
                var index = registry.Entries.IndexOf(selected);
                registry.Entries.Remove(selected);
                Persist();
                Refresh(index);
            }
        });
        var test = new Button { Text = "Test", X = Pos.Right(remove) + 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(test, () =>
        {
            if (Selected() is { } selected)
            {
                details.Text = TestLoad(selected, workspace, runner);
            }
        });
        table.ValueChanged += (_, _) => details.Text = Selected() is { } s ? $"{s.Name}: {s.DescribeSource()}\nPress Test to load it and check the private key." : "";

        // Enter or a double-click on a row tests it, rather than closing the dialog.
        table.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (Selected() is { } selected)
            {
                details.Text = TestLoad(selected, workspace, runner);
            }
        };

        dialog.Add(
            new Label { Text = $"Registry: {Path.GetRelativePath(workspace.RootPath, workspace.CertificatesPath)} (Newman --ssl-client-cert-list format). The most specific matching pattern wins.", X = 1, Y = 0 },
            table,
            details,
            addPem,
            addPfx,
            addStore,
            edit,
            toggle,
            remove,
            test);
        dialog.AddButton(new Button { Text = "Close" });
        Refresh(0);
        app.Run(dialog);
    }

    private static string TestLoad(CertificateEntry entry, ApiWorkspace workspace, RequestRunner runner)
    {
        try
        {
            var resolver = runner.CreateResolver(null);
            var loaded = runner.Certificates.Load(entry, workspace.RootPath, value =>
                value is not null && value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetEnvironmentVariable(value[4..])
                    : resolver.Resolve(value), runner.FilesFolder);
            var summary = loaded.Summary;
            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"✓ {summary.Subject}  issued by {summary.IssuerCommonName}\n");
            builder.Append(CultureInfo.InvariantCulture, $"  valid {summary.NotBefore:yyyy-MM-dd} → {summary.NotAfter:yyyy-MM-dd}{(summary.IsExpired ? " (EXPIRED)" : "")} · private key: {(summary.HasPrivateKey ? "yes" : "NO")} · thumbprint {summary.Thumbprint}");
            if (loaded.Chain.Count > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $" · +{loaded.Chain.Count} intermediate(s)");
            }

            return builder.ToString();
        }
        catch (CertificateLoadException ex)
        {
            return $"✗ {ex.Message}";
        }
    }
}

/// <summary>Edits one registry entry: name, URL patterns and its PEM/PFX/store source.</summary>
internal static class CertificateEntryDialog
{
    public static CertificateEntry? Show(IApplication app, ApiWorkspace workspace, CertificateEntry entry, string? suggestedUrl)
    {
        using var dialog = new Dialog { Title = "Client certificate", Width = Dim.Percent(85) };
        var y = 1;
        var name = Ui.AddRow(dialog, "Name", new TextField { Text = entry.Name }, y);
        y += 2;
        var suggestion = SuggestPattern(suggestedUrl);
        var matches = Ui.AddRow(dialog, "URL patterns", new TextField { Text = entry.Matches.Count > 0 ? string.Join(", ", entry.Matches) : suggestion }, y);
        y += 1;
        dialog.Add(new Label { Text = "Comma separated, e.g. https://*.contoso.com/*, https://api.contoso.com:8443/*", X = 20, Y = y });
        y += 1;
        dialog.Add(Ui.Hint("None: it goes to no server, and can sign OAuth 2.0 client assertions (as any can).", 20, y));
        y += 2;

        var kind = Ui.AddRow(dialog, "Source", new OptionSelector
        {
            Labels = ["PEM cert + key", "PFX / PKCS#12", "OS certificate store"],
            Orientation = Orientation.Horizontal,
            Value = (int)entry.Kind,
        }, y);
        y += 2;

        var certFile = FileRow(dialog, app, workspace, "Certificate file", entry.Cert?.Src ?? "", y);
        y += 2;
        var keyFile = FileRow(dialog, app, workspace, "Private key file", entry.Key?.Src ?? "", y);
        y += 1;
        dialog.Add(new Label { Text = "Leave the key empty when it is in the certificate file. Encrypted PKCS#8 keys are supported.", X = 20, Y = y });
        y += 2;
        var pfxFile = FileRow(dialog, app, workspace, "PFX file", entry.Pfx?.Src ?? "", y);
        y += 2;
        var passphrase = Ui.AddRow(dialog, "Passphrase", new TextField { Text = entry.Passphrase ?? "", Secret = !(entry.Passphrase?.StartsWith("env:", StringComparison.OrdinalIgnoreCase) == true || entry.Passphrase?.Contains("{{", StringComparison.Ordinal) == true) }, y);
        y += 1;
        dialog.Add(new Label { Text = "Use env:NAME or {{variable}} to avoid storing the passphrase in the file.", X = 20, Y = y });
        y += 2;
        var location = Ui.AddRow(dialog, "Store location", new OptionSelector { Labels = ["CurrentUser", "LocalMachine"], Orientation = Orientation.Horizontal, Value = string.Equals(entry.Store?.Location, "LocalMachine", StringComparison.OrdinalIgnoreCase) ? 1 : 0 }, y);
        y += 2;
        var thumbprint = Ui.AddRow(dialog, "Thumbprint", new TextField { Text = entry.Store?.Thumbprint ?? "" }, y);
        y += 2;
        var subject = Ui.AddRow(dialog, "or Subject", new TextField { Text = entry.Store?.Subject ?? "" }, y);
        y += 2;
        if (OperatingSystem.IsMacOS())
        {
            dialog.Add(new Label { Text = "On macOS the store is your login keychain (identities with private keys).", X = 20, Y = y });
        }

        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Save" });

        while (true)
        {
            app.Run(dialog);
            if (dialog.Canceled)
            {
                return null;
            }

            var result = new CertificateEntry
            {
                Name = name.Text.Trim(),
                Matches = matches.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Passphrase = string.IsNullOrEmpty(passphrase.Text) ? null : passphrase.Text,
                Disabled = entry.Disabled,
            };
            switch (kind.Value)
            {
                case 0:
                    result.Cert = new FileSource { Src = certFile.Text.Trim() };
                    result.Key = string.IsNullOrWhiteSpace(keyFile.Text) ? null : new FileSource { Src = keyFile.Text.Trim() };
                    break;
                case 1:
                    result.Pfx = new FileSource { Src = pfxFile.Text.Trim() };
                    break;
                default:
                    result.Store = new StoreSource
                    {
                        Location = location.Value == 1 ? "LocalMachine" : "CurrentUser",

                        // Not shown here: an entry for another store (TrustedPeople…) stays in it.
                        StoreName = entry.Store?.StoreName ?? "My",
                        Thumbprint = string.IsNullOrWhiteSpace(thumbprint.Text) ? null : CertificateLoader.NormalizeThumbprint(thumbprint.Text),
                        Subject = string.IsNullOrWhiteSpace(subject.Text) ? null : subject.Text.Trim(),
                    };
                    result.Passphrase = null;
                    break;
            }

            var error = Validate(result);
            if (error is null)
            {
                return result;
            }

            Ui.Error(app, "Check the certificate", error);
        }
    }

    private static string? Validate(CertificateEntry entry)
    {
        if (entry.Name.Length == 0)
        {
            return "Enter a name.";
        }

        // Without URL patterns it is sent to no server: it signs OAuth 2.0 client assertions.
        var invalid = entry.Matches.FirstOrDefault(m => !UrlMatchPattern.TryParse(m, out _));
        if (invalid is not null)
        {
            return $"'{invalid}' is not a valid pattern. Use scheme://host[:port]/path with optional * wildcards.";
        }

        return entry.Kind switch
        {
            CertificateSourceKind.Pem when string.IsNullOrEmpty(entry.Cert?.Src) => "Choose the certificate (PEM) file.",
            CertificateSourceKind.Pfx when string.IsNullOrEmpty(entry.Pfx?.Src) => "Choose the PFX file.",
            CertificateSourceKind.Store when entry.Store!.Thumbprint is null && entry.Store.Subject is null => "Enter a thumbprint or subject.",
            _ => null,
        };
    }

    private static TextField FileRow(Dialog dialog, IApplication app, ApiWorkspace workspace, string label, string value, int y)
    {
        var field = Ui.AddRow(dialog, label, new TextField { Text = value, Width = Dim.Fill(13) }, y);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
        Ui.OnPress(browse, () =>
        {
            if (Ui.PickFile(app, label, workspace.RootPath) is { } path)
            {
                // Store paths relative to the workspace when possible so the registry can be committed and shared.
                var relative = Path.GetRelativePath(workspace.RootPath, path);
                field.Text = relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
            }
        });
        dialog.Add(browse);
        return field;
    }

    private static string SuggestPattern(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? $"https://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/*"
            : "";
}

/// <summary>Lists OS-store certificates that have a private key (Windows store, macOS keychain, .NET store on Linux).</summary>
internal static class StorePickerDialog
{
    public static CertificateSummary? Show(IApplication app)
    {
        var certificates = CertificateLoader.ListStore("CurrentUser").ToList();
        if (OperatingSystem.IsWindows())
        {
            certificates.AddRange(CertificateLoader.ListStore("LocalMachine"));
        }

        using var dialog = new Dialog { Title = "Choose a certificate with a private key", Width = Dim.Percent(90), Height = Dim.Percent(80) };
        if (certificates.Count == 0)
        {
            dialog.Add(new Label { Text = "No certificates with private keys were found in the personal store.", X = 1, Y = 1 });
        }

        var table = new TableView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), FullRowSelect = true };
        table.Style.ExpandLastColumn = true;
        table.Table = new EnumerableTableSource<CertificateSummary>(certificates, new Dictionary<string, Func<CertificateSummary, object>>
        {
            ["Subject"] = c => c.CommonName,
            ["Issuer"] = c => c.IssuerCommonName,
            ["Expires"] = c => c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (c.IsExpired ? " (expired)" : ""),
            ["Store"] = c => c.Source ?? "",
            ["Thumbprint"] = c => c.Thumbprint,
        });
        dialog.Add(table);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Select" });
        app.Run(dialog);
        if (dialog.Canceled || table.Value is not { } selection || selection.SelectedCell.Y < 0 || selection.SelectedCell.Y >= certificates.Count)
        {
            return null;
        }

        return certificates[selection.SelectedCell.Y];
    }
}
