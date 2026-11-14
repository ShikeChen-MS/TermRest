using System.Text;

namespace TermRest.Cli;

/// <summary>
/// The text of the standard streams on Windows, where .NET would use the console's code page (437, 850, 936 …) and lose
/// every character it lacks. Files and pipes get UTF-8 without a byte order mark (standard input may start with one); a
/// console window gets UTF-16, which shows and reads every character without changing the console's code page.
/// </summary>
internal static class ConsoleText
{
    /// <summary>For the commands, not the full-screen UI: Terminal.Gui drives the console itself.</summary>
    public static void UseUnicode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // UTF-8 already
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsInputRedirected)
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true));
        }

        // .NET writes to a console window with WriteConsoleW while the encoding is UTF-16, which sets no code page: the
        // streams are opened then. The encoding is set back afterwards, so that Console.OpenStandardOutput() still writes
        // the bytes of a binary body as they are.
        var codePage = Console.IsOutputRedirected && Console.IsErrorRedirected ? null : Console.OutputEncoding;
        if (codePage is not null)
        {
            Console.OutputEncoding = Encoding.Unicode;
        }

        var output = Console.OpenStandardOutput();
        var error = Console.OpenStandardError();
        if (codePage is not null)
        {
            Console.OutputEncoding = codePage;
        }

        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        Console.SetOut(new StreamWriter(output, Console.IsOutputRedirected ? utf8 : utf16) { AutoFlush = true });
        Console.SetError(new StreamWriter(error, Console.IsErrorRedirected ? utf8 : utf16) { AutoFlush = true });
    }

    /// <summary>
    /// All of standard input. From a console window on Windows, what is typed or pasted up to a line that starts with
    /// Ctrl+Z, read as UTF-16 (as <see cref="UseUnicode"/> writes): every character comes through.
    /// </summary>
    public static string ReadInput()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected)
        {
            return Console.In.ReadToEnd();
        }

        // A stream opened while the encoding is UTF-16 reads with ReadConsoleW, which sets no code page; the encoding is
        // set back afterwards (to the console's own code page: no change either).
        var codePage = Console.InputEncoding;
        Console.InputEncoding = Encoding.Unicode;
        var input = Console.OpenStandardInput();
        Console.InputEncoding = codePage;

        // ReadConsoleW returns a line of Ctrl+Z, which ends console input, as text (ReadFile returns nothing for it).
        using var reader = new StreamReader(input, new UnicodeEncoding(bigEndian: false, byteOrderMark: false));
        var text = new StringBuilder();
        while (reader.ReadLine() is { } line && !line.StartsWith('\u001a'))
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }
}
