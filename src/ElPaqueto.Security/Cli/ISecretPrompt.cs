using System.Text;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// Reads a secret typed in a terminal, without echoing it.
/// </summary>
internal interface ISecretPrompt
{
    /// <summary>
    /// Shows <paramref name="prompt"/> on <paramref name="promptWriter"/> and reads one line from the
    /// keyboard without echoing it.
    /// </summary>
    /// <param name="promptWriter">Where the prompt is written: the error stream, never the results.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="maximumLength">The longest input accepted; longer input returns <see langword="null"/>.</param>
    /// <returns>The line typed, or <see langword="null"/> when it was too long.</returns>
    public string? ReadSecret(TextWriter promptWriter, string prompt, int maximumLength);
}

/// <summary>
/// Reads the secret from the keyboard of the process console, key by key, without echo.
/// </summary>
/// <remarks>
/// Only used when standard input is a terminal: a redirected input has no keyboard and the command
/// exits with a usage error instead of blocking.
/// </remarks>
internal sealed class ConsoleSecretPrompt : ISecretPrompt
{
    public string? ReadSecret(TextWriter promptWriter, string prompt, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(promptWriter);

        promptWriter.Write(prompt);
        promptWriter.Flush();

        var secret = new StringBuilder();
        var tooLong = false;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }

                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            if (secret.Length >= maximumLength)
            {
                tooLong = true;
                continue;
            }

            secret.Append(key.KeyChar);
        }

        promptWriter.WriteLine();
        return tooLong ? null : secret.ToString();
    }
}
