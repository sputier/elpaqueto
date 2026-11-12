#nullable enable

// Shared with ElPaqueto.Tests, which has no implicit usings: names outside these namespaces are qualified.
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Tests.Logging;

/// <summary>
/// One captured log entry: everything a logging provider could write anywhere.
/// </summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The level.</param>
/// <param name="EventId">The event id and name.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="State">The structured state, each value converted to text.</param>
/// <param name="Scopes">Every active scope: its text and its key/value pairs.</param>
/// <param name="Exception">The exception's full text (type, message, inner exceptions, stack), if any.</param>
internal sealed record LogRecord(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, string>> State,
    System.Collections.Generic.IReadOnlyList<string> Scopes,
    string? Exception)
{
    /// <summary>The value of a structured property, or <see langword="null"/>.</summary>
    public string? this[string key]
    {
        get
        {
            foreach (var pair in State)
            {
                if (pair.Key == key)
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }

    /// <summary>Every piece of text of the entry, for secret searches.</summary>
    public string AllText
    {
        get
        {
            var text = new StringBuilder()
                .Append(Category).Append('\n')
                .Append(EventId.Name).Append('\n')
                .Append(Message).Append('\n');
            foreach (var pair in State)
            {
                text.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            }

            foreach (var scope in Scopes)
            {
                text.Append(scope).Append('\n');
            }

            return text.Append(Exception).ToString();
        }
    }

    public override string ToString() => $"{Level} {Category} [{EventId.Id} {EventId.Name}] {Message}";
}

/// <summary>Assertions over recorded entries.</summary>
internal static class LogRecords
{
    /// <summary>
    /// Fails when any part of any entry contains one of <paramref name="secrets"/>. The failure names the entry
    /// and the index of the secret, never the secret.
    /// </summary>
    public static void AssertContainNone(System.Collections.Generic.IReadOnlyList<LogRecord> records, params string[] secrets)
    {
        Xunit.Assert.NotEmpty(records);

        for (var i = 0; i < secrets.Length; i++)
        {
            Xunit.Assert.False(string.IsNullOrEmpty(secrets[i]), $"Secret #{i} is empty: the search would prove nothing.");
        }

        foreach (var record in records)
        {
            var text = record.AllText;
            for (var i = 0; i < secrets.Length; i++)
            {
                if (text.Contains(secrets[i], System.StringComparison.Ordinal))
                {
                    Xunit.Assert.Fail($"Secret #{i} appears in the log entry {record.Level} {record.Category} [{record.EventId.Id} {record.EventId.Name}].");
                }
            }
        }
    }
}

/// <summary>
/// A logging provider that records every entry with its category, event id, formatted message, structured state,
/// active scopes and exception text. Register it with a <see cref="LogLevel.Trace"/> filter to see everything.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly System.Collections.Generic.List<LogRecord> _records = [];
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>A copy of the entries recorded so far.</summary>
    public System.Collections.Generic.IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    /// <summary>Text of a value as a provider would write it: collections element by element.</summary>
    internal static string Describe(object? value)
    {
        switch (value)
        {
            case null:
                return "(null)";
            case string text:
                return text;
            case System.Collections.IEnumerable items:
                var joined = new StringBuilder();
                foreach (var item in items)
                {
                    joined.Append(item is System.Collections.Generic.KeyValuePair<string, object?> pair
                        ? pair.Key + "=" + Describe(pair.Value)
                        : Describe(item)).Append(", ");
                }

                return joined.ToString();
            case System.IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private void Add(LogRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public System.IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            System.Exception? exception,
            System.Func<TState, System.Exception?, string> formatter)
        {
            var values = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            if (state is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    values.Add(new(pair.Key, Describe(pair.Value)));
                }
            }

            var scopes = new System.Collections.Generic.List<string>();
            provider._scopes.ForEachScope((scope, list) => list.Add(Describe(scope) + " | " + scope), scopes);

            provider.Add(new LogRecord(category, logLevel, eventId, formatter(state, exception), values, scopes, exception?.ToString()));
        }
    }
}
