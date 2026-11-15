using System.Globalization;
using System.Text;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// An option accepted by an <c>admin</c> command.
/// </summary>
/// <param name="Name">The option name, with its leading dashes (<c>--username</c>).</param>
/// <param name="ValueName">The placeholder of its value, or <see langword="null"/> for a flag.</param>
/// <param name="Description">The help text.</param>
internal sealed record AdminOption(string Name, string? ValueName, string Description)
{
    public bool TakesValue => ValueName is not null;
}

/// <summary>
/// A positional argument accepted by an <c>admin</c> command. All of them are required.
/// </summary>
/// <param name="Name">The placeholder shown in help and errors.</param>
/// <param name="Description">The help text.</param>
internal sealed record AdminArgument(string Name, string Description);

/// <summary>
/// The arguments of an <c>admin</c> command once parsed.
/// </summary>
internal sealed class ParsedArguments
{
    private readonly Dictionary<string, string?> _options;

    public ParsedArguments(Dictionary<string, string?> options, IReadOnlyList<string> positionals)
    {
        _options = options;
        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Strict parser for the arguments of the <c>admin</c> commands.
/// </summary>
/// <remarks>
/// The commands collect their arguments themselves instead of declaring options to the command-line
/// library: the library throws on a missing option value, which the host turns into exit code 1 with a
/// stack trace, and it reports unknown arguments by repeating them. Here every syntax error is a usage
/// error (exit code 2), and an error message never repeats an argument value, which could be a password
/// typed in the wrong place.
/// </remarks>
internal static class CommandLineSyntax
{
    /// <summary>The usual placeholders of passwords on other tools: they get a dedicated explanation.</summary>
    private static readonly string[] PasswordOptionNames = ["--password", "--pass", "--pwd", "-p"];

    public static bool TryParse(
        IReadOnlyList<string> tokens,
        IReadOnlyList<AdminOption> options,
        IReadOnlyList<AdminArgument> arguments,
        out ParsedArguments parsed,
        out string error)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionals = new List<string>();
        parsed = new ParsedArguments(values, positionals);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var position = index + 1;

            if (!token.StartsWith('-') || token == "-")
            {
                if (positionals.Count >= arguments.Count)
                {
                    error = string.Create(CultureInfo.InvariantCulture, $"Unexpected argument at position {position}.");
                    return false;
                }

                positionals.Add(token);
                continue;
            }

            var separator = token.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? token : token[..separator];
            var option = options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

            if (option is null)
            {
                error = UnknownOption(name, position, options);
                return false;
            }

            if (values.ContainsKey(option.Name))
            {
                error = $"The option '{option.Name}' is given more than once.";
                return false;
            }

            if (!option.TakesValue)
            {
                if (separator >= 0)
                {
                    error = $"The option '{option.Name}' does not take a value.";
                    return false;
                }

                values[option.Name] = null;
                continue;
            }

            string value;
            if (separator >= 0)
            {
                value = token[(separator + 1)..];
            }
            else if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++index];
            }
            else
            {
                error = $"The option '{option.Name}' needs a value: {option.Name} <{option.ValueName}>.";
                return false;
            }

            if (value.Length == 0)
            {
                error = $"The option '{option.Name}' needs a non-empty value.";
                return false;
            }

            values[option.Name] = value;
        }

        if (positionals.Count < arguments.Count)
        {
            error = $"Missing argument <{arguments[positionals.Count].Name}>.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Builds the help section that describes the arguments and options of a command.
    /// </summary>
    public static string Describe(IReadOnlyList<AdminArgument> arguments, IReadOnlyList<AdminOption> options, string? remarks = null)
    {
        var text = new StringBuilder();

        if (arguments.Count > 0)
        {
            text.AppendLine("Command arguments:");
            foreach (var argument in arguments)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {"<" + argument.Name + ">",-32}{argument.Description}");
            }

            text.AppendLine();
        }

        if (options.Count > 0)
        {
            text.AppendLine("Command options:");
            foreach (var option in options)
            {
                var shape = option.TakesValue ? $"{option.Name} <{option.ValueName}>" : option.Name;
                text.AppendLine(CultureInfo.InvariantCulture, $"  {shape,-32}{option.Description}");
            }

            text.AppendLine();
        }

        if (remarks is not null)
        {
            text.AppendLine(remarks);
        }

        return text.ToString();
    }

    private static string UnknownOption(string name, int position, IReadOnlyList<AdminOption> options)
    {
        var known = PasswordOptionNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            // The name quoted back is the constant, not the argument.
            return $"Unknown option '{known}'. Passwords are never accepted as command-line arguments, which other "
                + "users and the shell history can read: use --password-file <path>, --password-stdin, or run "
                + "the command in a terminal to be prompted.";
        }

        // The argument itself is never quoted back: it may be a misplaced secret.
        var valid = options.Count == 0 ? "none" : string.Join(", ", options.Select(o => o.Name));
        return string.Create(CultureInfo.InvariantCulture, $"Unknown option at position {position} after the command name. Valid options: {valid}.");
    }
}
