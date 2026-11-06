using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Core.Recording;

namespace TermRest.Tui.Dialogs;

/// <summary>Why the recording dialog is shown; it decides the title and the button that leaves without saving.</summary>
internal enum RecordingDialogPurpose
{
    /// <summary>The user stopped the recording: "Keep recording" goes on recording.</summary>
    Stop,

    /// <summary>TermRest or the workspace is closing: "Cancel" stays.</summary>
    Close,

    /// <summary>A recording left over from an earlier session: "Later" keeps it for next time.</summary>
    Recover,
}

internal enum RecordingDialogResult
{
    Saved,
    Discarded,
    Kept,
}

/// <summary>Asks where to save a recording (HAR, a JSON file), or discards it.</summary>
internal static class RecordingDialog
{
    public static (RecordingDialogResult Result, RecordingResult? Saved) Show(IApplication app, RequestRecorder recorder, string defaultPath, RecordingDialogPurpose purpose)
    {
        using var dialog = new Dialog
        {
            Title = purpose switch
            {
                RecordingDialogPurpose.Stop => "Stop recording",
                RecordingDialogPurpose.Close => "Save the recording first?",
                _ => "Unsaved recording",
            },
            Width = Dim.Percent(80),
        };

        var count = recorder.Count;
        var requests = count == 1 ? "1 request" : $"{count} requests";
        var summary = purpose == RecordingDialogPurpose.Recover
            ? $"{requests} recorded from {recorder.StartedAt:yyyy-MM-dd HH:mm}{(recorder.WorkspaceName is { } name ? $" in '{name}'" : "")} were not saved: TermRest closed while recording."
            : $"{requests} recorded since {recorder.StartedAt:HH:mm:ss} ({Duration(DateTimeOffset.Now - recorder.StartedAt)}), {HttpExchange.FormatSize(recorder.Bytes)} so far.";
        var redactions = recorder.IsRecovered || recorder.Redactions == 0 ? "" : $" ({recorder.Redactions.ToString(CultureInfo.InvariantCulture)} values)";
        dialog.Add(
            new Label { Text = summary, X = 1, Y = 1, Width = Dim.Fill(1) },
            new Label { Text = $"Secrets are redacted{redactions}: credentials, cookies, tokens and secret variables read {Redactor.Placeholder}.", X = 1, Y = 2, Width = Dim.Fill(1) });
        if (recorder.RequestsWithFiles is var withFiles and > 0)
        {
            var sent = withFiles == 1 ? "1 of them sent a file" : $"{withFiles} of them sent files";
            dialog.Add(new Label { Text = $"{sent}: the recording keeps the file's path, not its content.", X = 1, Y = 3, Width = Dim.Fill(1) });
        }
        var path = Ui.AddRow(dialog, "Save as", new TextField { Text = defaultPath, Width = Dim.Fill(13) }, 4, 8);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = 4 };
        browse.Accepted += (_, _) =>
        {
            var folder = Path.GetDirectoryName(path.Text.Trim());
            if (Ui.PickSaveFile(app, "Save recording as", folder) is { } chosen)
            {
                path.Text = Path.HasExtension(chosen) ? chosen : chosen + ".har";
            }
        };
        dialog.Add(
            browse,
            new Label { Text = "JSON in HAR 1.2 format (HTTP Archive): opens in browser dev tools and HAR viewers.", X = 10, Y = 5, Width = Dim.Fill(1) });
        dialog.AddButton(new Button { Text = purpose switch { RecordingDialogPurpose.Stop => "Keep recording", RecordingDialogPurpose.Close => "Cancel", _ => "Later" } });
        dialog.AddButton(new Button { Text = "Discard" });
        dialog.AddButton(new Button { Text = "Save" });
        path.SetFocus();

        while (true)
        {
            dialog.Result = null;
            app.Run(dialog);
            switch (dialog.Result)
            {
                case 2:
                    if (TrySave(app, recorder, path.Text) is { } saved)
                    {
                        return (RecordingDialogResult.Saved, saved);
                    }

                    continue;
                case 1:
                    if (Ui.Confirm(app, "Discard recording", $"Discard the {requests} recorded? This cannot be undone.", "Discard", "Back"))
                    {
                        recorder.Discard();
                        return (RecordingDialogResult.Discarded, null);
                    }

                    continue;
                default:
                    return (RecordingDialogResult.Kept, null);
            }
        }
    }

    /// <summary>A file name for a recording started at <paramref name="startedAt"/> in <paramref name="folder"/> that does not exist yet.</summary>
    public static string DefaultPath(string folder, DateTimeOffset startedAt)
    {
        var name = $"termrest-recording-{startedAt:yyyyMMdd-HHmmss}";
        var path = Path.Combine(folder, name + ".har");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{name}-{i}.har");
        }

        return path;
    }

    private static RecordingResult? TrySave(IApplication app, RequestRecorder recorder, string text)
    {
        var path = text.Trim().Trim('"');
        if (path.Length == 0)
        {
            Ui.Error(app, "Save recording", "Enter the file to save the recording to.");
            return null;
        }

        try
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                path = DefaultPath(path, recorder.StartedAt);
            }
            else if (File.Exists(path) && !Ui.Confirm(app, "Save recording", $"{path} exists. Replace it?", "Replace", "Back"))
            {
                return null;
            }

            return recorder.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Ui.Error(app, "Save recording", $"The recording could not be saved to {path}:\n{ex.Message}\n\nNothing was lost: choose another file, or keep recording.");
            return null;
        }
    }

    private static string Duration(TimeSpan duration) => duration.TotalMinutes < 1
        ? $"{Math.Max(0, (int)duration.TotalSeconds)} s"
        : duration.TotalHours < 1 ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s" : $"{(int)duration.TotalHours} h {duration.Minutes} min";
}
