using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Import;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Paste a curl command (from docs, a terminal or a browser's "Copy as cURL") to turn it into a request.</summary>
internal static class CurlImportDialog
{
    public static CurlImportResult? Show(IApplication app)
    {
        using var dialog = new Dialog { Title = "Import cURL command", Width = Dim.Percent(85), Height = Dim.Percent(70) };
        var hint = new Label
        {
            Text = "Paste a curl command: bash, cmd or PowerShell syntax (browsers' \"Copy as cURL\" works). Multi-line is fine.",
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
        };
        var text = new TextArea { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        if (app.Clipboard is { IsSupported: true } board && board.TryGetClipboardData(out var clipboard) && CurlImporter.LooksLikeCurl(clipboard))
        {
            text.SetText(clipboard);
        }

        dialog.Add(hint, text);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Import" });
        text.SetFocus();
        while (true)
        {
            app.Run(dialog);
            if (dialog.Canceled)
            {
                return null;
            }

            try
            {
                return CurlImporter.Import(text.GetText());
            }
            catch (FormatException ex)
            {
                Ui.Error(app, "Not a curl command", ex.Message);
            }
        }
    }
}
