using System.Text;
using ElPaqueto.Core;
using ElPaqueto.Security.Cli;
using ElPaqueto.Security.Data;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Tests.Cli;

/// <summary>
/// Runs the real <c>admin</c> command tree in process, over a real SQLite security database in a
/// temporary directory, with an injectable console and terminal prompt and every log captured.
/// </summary>
internal sealed class AdminCli : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<string> _logs = [];

    public AdminCli(AuthDatabaseFixture database, IInterceptor? interceptor = null)
    {
        Database = database;

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(_logs));
        });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Database:ConnectionString"] = database.ConnectionString,

                // The lowest accepted cost keeps the tests fast; the cost is not what they test.
                ["Security:Passwords:IterationCount"] = "100000",
            })
            .Build());

        new ElPaquetoApplication(services).AddElPaquetoSecurity();
        services.Replace(ServiceDescriptor.Singleton<ISecretPrompt>(Prompt));

        if (interceptor is not null)
        {
            services.ConfigureDbContext<AuthDbContext>(builder => builder.AddInterceptors(interceptor));
        }

        _services = services.BuildServiceProvider();
    }

    public AuthDatabaseFixture Database { get; }

    public FakeSecretPrompt Prompt { get; } = new();

    public IServiceProvider Services => _services;

    public IReadOnlyList<string> Logs
    {
        get
        {
            lock (_logs)
            {
                return [.. _logs];
            }
        }
    }

    /// <summary>Runs <c>elpaqueto &lt;arguments&gt;</c>.</summary>
    /// <param name="arguments">The arguments, starting with <c>admin</c>.</param>
    /// <param name="stdin">The standard input, or <see langword="null"/> for an empty one.</param>
    /// <param name="terminal">Whether standard input is a terminal (not redirected).</param>
    public async Task<CliRun> RunAsync(string[] arguments, string? stdin = null, bool terminal = false)
    {
        using var console = new TestConsole(stdin ?? string.Empty, inputRedirected: !terminal);
        var app = new CommandLineApplication(console) { Name = "elpaqueto" };
        app.HelpOption(inherited: true);

        var reported = new List<int>();
        ElPaquetoCommandLineExtensions.AddElPaquetoCommands(app, _services, reported.Add);

        var exitCode = await app.ExecuteAsync(arguments);

        // The host process ignores the return value: the reported code is the one the process exits with.
        if (reported.Count > 0)
        {
            Assert.Equal(exitCode, Assert.Single(reported));
        }

        return new CliRun(exitCode, console.OutText, console.ErrorText);
    }

    public Task<CliRun> RunAsync(params string[] arguments) => RunAsync(arguments, stdin: null);

    public async Task<List<User>> UsersAsync()
    {
        await using var context = Database.CreateContext();
        return await context.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
    }

    public void Dispose() => _services.Dispose();

    private sealed class CapturingLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = new StringBuilder()
                    .Append(eventId.Id).Append(' ').Append(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        line.Append(' ').Append(key).Append('=').Append(value);
                    }
                }

                if (exception is not null)
                {
                    line.Append(' ').Append(exception);
                }

                lock (logs)
                {
                    logs.Add(line.ToString());
                }
            }
        }
    }
}

/// <summary>The outcome of one command run.</summary>
internal sealed record CliRun(int ExitCode, string Out, string Error)
{
    public string AllOutput => Out + Error;

    public override string ToString() => $"exit {ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{Out}stderr:{Environment.NewLine}{Error}";
}

/// <summary>A console whose streams are strings.</summary>
internal sealed class TestConsole(string stdin, bool inputRedirected) : IConsole, IDisposable
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public event ConsoleCancelEventHandler? CancelKeyPress
    {
        add { }
        remove { }
    }

    public TextWriter Out => _out;

    public TextWriter Error => _error;

    public TextReader In { get; } = new StringReader(stdin);

    public bool IsInputRedirected { get; } = inputRedirected;

    public bool IsOutputRedirected => true;

    public bool IsErrorRedirected => true;

    public ConsoleColor ForegroundColor { get; set; }

    public ConsoleColor BackgroundColor { get; set; }

    public string OutText => _out.ToString();

    public string ErrorText => _error.ToString();

    public void ResetColor()
    {
    }

    public void Dispose()
    {
        _out.Dispose();
        _error.Dispose();
        In.Dispose();
    }
}

/// <summary>A terminal prompt that answers from a queue and records the prompts shown.</summary>
internal sealed class FakeSecretPrompt : ISecretPrompt
{
    private readonly Queue<string?> _answers = new();

    public List<string> Prompts { get; } = [];

    public void Answer(params string?[] answers)
    {
        foreach (var answer in answers)
        {
            _answers.Enqueue(answer);
        }
    }

    public string? ReadSecret(TextWriter promptWriter, string prompt, int maximumLength)
    {
        promptWriter.Write(prompt);
        promptWriter.WriteLine();
        Prompts.Add(prompt);

        var answer = _answers.Count > 0 ? _answers.Dequeue() : string.Empty;
        return answer is not null && answer.Length > maximumLength ? null : answer;
    }
}
