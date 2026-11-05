namespace TermRest.Cli;

/// <summary>Console output helpers. Diagnostics go to stderr so stdout can be piped (like curl).</summary>
internal static class Output
{
    private static readonly bool UseColor =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsErrorRedirected;

    public static void Error(string message) => Write(Console.Error, ConsoleColor.Red, "error: " + message);

    public static void Warning(string message) => Write(Console.Error, ConsoleColor.Yellow, "warning: " + message);

    public static void Info(string message) => Write(Console.Error, null, message);

    public static void Status(string message, bool ok) => Write(Console.Error, ok ? ConsoleColor.Green : ConsoleColor.Red, message);

    public static void Write(TextWriter writer, ConsoleColor? color, string message)
    {
        if (UseColor && color is { } c)
        {
            Console.ForegroundColor = c;
            writer.WriteLine(message);
            Console.ResetColor();
        }
        else
        {
            writer.WriteLine(message);
        }
    }
}
