using System.Text;

namespace TermRest.Cli;

/// <summary>Reads a password typed in the terminal, without showing it.</summary>
internal static class ConsolePassword
{
    /// <summary>The password typed after <paramref name="prompt"/> (written to stderr), or null when there is no terminal to type it in.</summary>
    public static string? Read(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write(prompt);
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                password.Length = Math.Max(0, password.Length - 1);
            }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return password.ToString();
    }
}
