using System.Reflection;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Users;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Tests.Auditing;

/// <summary>
/// The event catalogue of the security module: every <c>LoggerMessage</c> event has a fixed, unique, documented id.
/// </summary>
public sealed class AuditLogCatalogueTests
{
    [Fact]
    public void EveryLogEventOfTheSecurityModule_HasAnExplicitIdAndName()
    {
        var events = LogEvents();

        Assert.NotEmpty(events);
        foreach (var (method, attribute) in events)
        {
            Assert.True(attribute.EventId > 0, $"{Describe(method)} has no explicit event id.");
            Assert.False(string.IsNullOrEmpty(attribute.EventName), $"{Describe(method)} has no event name.");
        }
    }

    [Fact]
    public void NoTwoLogEventsOfTheSecurityModule_ShareAnIdOrAName()
    {
        var events = LogEvents();

        AssertUnique(events, e => e.Attribute.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), "id");
        AssertUnique(events, e => e.Attribute.EventName!, "name");
    }

    [Fact]
    public void EveryLogEvent_IsDocumentedWithItsIdAndName()
    {
        var catalogue = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "technical", "audit-logging.md"));

        foreach (var (method, attribute) in LogEvents())
        {
            var row = $"| {attribute.EventId} | `{attribute.EventName}` |";
            Assert.True(catalogue.Contains(row, StringComparison.Ordinal), $"{Describe(method)}: no row starting with '{row}' in audit-logging.md.");
        }
    }

    [Fact]
    public void TheAuditCategory_IsTheOneOperatorsConfigure()
    {
        Assert.Equal("ElPaqueto.Security.Audit", AuditLog.Category);
        Assert.Equal(AuditLog.Category, UserAdministrationLog.Category);

        // ILogger<Audit> is how the audit logger is injected: its category must be the same.
        using var factory = new CategoryRecordingFactory();
        _ = new Logger<Audit>(factory);
        Assert.Equal(AuditLog.Category, Assert.Single(factory.Categories));
    }

    [Theory]
    [InlineData(nameof(AuditLog))]
    [InlineData(nameof(UserAdministrationLog))]
    public void AuditEvents_UseTheReservedBlocks(string className)
    {
        var type = typeof(AuditLog).Assembly.GetTypes().Single(t => t.Name == className);

        foreach (var (method, attribute) in LogEvents().Where(e => e.Method.DeclaringType == type))
        {
            // 1000-2999 is the audit trail; 3000 and above are operational events of the module.
            Assert.True(attribute.EventId is >= 1000 and <= 2999, $"{Describe(method)} uses {attribute.EventId}, outside 1000-2999.");
        }
    }

    private static List<(MethodInfo Method, LoggerMessageAttribute Attribute)> LogEvents()
        => typeof(AuditLog).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(e => e.Attribute is not null)
            .Select(e => (e.Method, e.Attribute!))
            .ToList();

    private static void AssertUnique(
        List<(MethodInfo Method, LoggerMessageAttribute Attribute)> events,
        Func<(MethodInfo Method, LoggerMessageAttribute Attribute), string> key,
        string what)
    {
        var duplicates = events
            .GroupBy(key)
            .Where(g => g.Count() > 1)
            .Select(g => $"{what} {g.Key}: {string.Join(", ", g.Select(e => Describe(e.Method)))}")
            .ToList();

        Assert.True(duplicates.Count == 0, "Shared event " + string.Join("; ", duplicates));
    }

    private static string Describe(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class CategoryRecordingFactory : ILoggerFactory
    {
        public List<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }
}
