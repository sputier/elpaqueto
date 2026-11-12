using System.Globalization;
using System.Text;
using McMaster.Extensions.CommandLineUtils;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// Obtains a password for an <c>admin</c> command. A password is never accepted as a plain argument:
/// other users can read the arguments of a process, and shells keep them in their history.
/// </summary>
/// <remarks>
/// Sources, in order: <c>--password-file</c>, <c>--password-stdin</c>, then a prompt without echo
/// (asked twice) when standard input is a terminal. Without any of them the command fails instead of
/// blocking on an input nobody will type. Error messages never contain the password.
/// </remarks>
internal sealed class PasswordInput(IConsole console, ISecretPrompt prompt)
{
    public const string FileOption = "--password-file";

    public const string StdinOption = "--password-stdin";

    /// <summary>The longest input read from any source. The policy limit (256) is checked later.</summary>
    public const int MaximumInputLength = 1024;

    // A password file holds one short line: anything bigger is the wrong file.
    private const int MaximumFileBytes = 8 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IReadOnlyList<AdminOption> Options { get; } =
    [
        new(FileOption, "path", "Read the password from a file (one line; a trailing newline is ignored). Intended for /run/secrets/<name>."),
        new(StdinOption, null, "Read the password from the first line of standard input."),
    ];

    public static string Remarks { get; } =
        "Without --password-file or --password-stdin, the password is prompted for twice, without echo, when "
        + "standard input is a terminal; otherwise the command fails with exit code 2. There is no --password "
        + "option: arguments are visible to other users and kept in shell history.";

    /// <summary>Reads the password from the source selected by <paramref name="arguments"/>.</summary>
    /// <returns><see langword="true"/> with the password, or <see langword="false"/> with an error message.</returns>
    public bool TryRead(ParsedArguments arguments, out string password, out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        password = string.Empty;
        var file = arguments.Value(FileOption);
        var stdin = arguments.Has(StdinOption);

        if (file is not null && stdin)
        {
            error = $"Use either {FileOption} or {StdinOption}, not both.";
            return false;
        }

        string? value;
        if (file is not null)
        {
            if (!TryReadFile(file, out value, out error))
            {
                return false;
            }
        }
        else if (stdin)
        {
            if (!TryReadFirstLine(console.In, out value, out error))
            {
                return false;
            }
        }
        else if (!console.IsInputRedirected)
        {
            if (!TryPrompt(out value, out error))
            {
                return false;
            }
        }
        else
        {
            error = $"No password given and standard input is not a terminal: use {FileOption} <path> or {StdinOption}.";
            return false;
        }

        if (string.IsNullOrEmpty(value))
        {
            error = "The password is empty.";
            return false;
        }

        password = value;
        error = string.Empty;
        return true;
    }

    private static bool TryReadFile(string path, out string? value, out string error)
    {
        value = null;

        // The path is never quoted back: "--password-file <the password>" is a likely mistake.
        try
        {
            if (Directory.Exists(path))
            {
                error = "The password file is a directory.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "The password file does not exist.";
                return false;
            }

            // Read at most one byte more than allowed: the length of a pipe or a device is unknown
            // beforehand, and a regular file may grow between a size check and the read.
            var buffer = new byte[MaximumFileBytes + 1];
            var length = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read;
                while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
                {
                    length += read;
                }
            }

            if (length > MaximumFileBytes)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"The password file is larger than {MaximumFileBytes} bytes: it is not a password file.");
                return false;
            }

            var content = StrictUtf8.GetString(buffer, 0, length);
            if (content.StartsWith('﻿'))
            {
                content = content[1..];
            }

            // Exactly one trailing line break is ignored: the one editors and "echo" add.
            if (content.EndsWith("\r\n", StringComparison.Ordinal))
            {
                content = content[..^2];
            }
            else if (content.EndsWith('\n'))
            {
                content = content[..^1];
            }

            if (content.Contains('\n', StringComparison.Ordinal) || content.Contains('\r', StringComparison.Ordinal))
            {
                error = "The password file must contain a single line.";
                return false;
            }

            if (content.Length > MaximumInputLength)
            {
                error = "The password in the password file is too long.";
                return false;
            }

            value = content;
            error = string.Empty;
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = "The password file is not valid UTF-8.";
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The password file cannot be read.";
            return false;
        }
    }

    private static bool TryReadFirstLine(TextReader reader, out string? value, out string error)
    {
        value = null;
        var line = new StringBuilder();

        while (true)
        {
            var next = reader.Read();
            if (next is -1 or '\n')
            {
                break;
            }

            if (line.Length >= MaximumInputLength)
            {
                error = "The password read from standard input is too long.";
                return false;
            }

            line.Append((char)next);
        }

        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        value = line.ToString();
        error = string.Empty;
        return true;
    }

    private bool TryPrompt(out string? value, out string error)
    {
        value = null;

        var first = prompt.ReadSecret(console.Error, "Password: ", MaximumInputLength);
        if (first is null)
        {
            error = "The password is too long.";
            return false;
        }

        if (first.Length == 0)
        {
            error = "The password is empty.";
            return false;
        }

        var confirmation = prompt.ReadSecret(console.Error, "Confirm the password: ", MaximumInputLength);
        if (confirmation is null)
        {
            error = "The confirmation is too long.";
            return false;
        }

        if (!string.Equals(first, confirmation, StringComparison.Ordinal))
        {
            error = "The passwords do not match.";
            return false;
        }

        value = first;
        error = string.Empty;
        return true;
    }
}
