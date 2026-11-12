using ElPaqueto.Security.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security;

/// <summary>
/// Startup validation of the security module's configuration.
/// </summary>
public static partial class ElPaquetoSecurityHostExtensions
{
    /// <summary>
    /// Validates the security options and logs every failure.
    /// </summary>
    /// <param name="host">The built host.</param>
    /// <returns>
    /// <see langword="true"/> when the configuration is valid; <see langword="false"/> otherwise,
    /// in which case the caller must stop before doing any work.
    /// </returns>
    /// <remarks>
    /// Failure messages name the offending setting and never contain its value. A host that did not
    /// register the module with <c>AddElPaquetoSecurity</c> is reported as invalid: validating
    /// nothing must not look like success.
    /// </remarks>
    public static bool ValidateElPaquetoSecurityOptions(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ElPaqueto.Security.Configuration");

        if (!host.Services.GetServices<IValidateOptions<ElPaquetoSecurityOptions>>().Any())
        {
            LogModuleNotRegistered(logger);
            return false;
        }

        try
        {
            // Reading the value forces binding and validation to run.
            _ = host.Services.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value;
            return true;
        }
        catch (OptionsValidationException e)
        {
            foreach (var failure in e.Failures)
            {
                LogInvalidSetting(logger, failure);
            }

            LogInvalidConfiguration(logger);
            return false;
        }
        catch (InvalidOperationException)
        {
            // The configuration binder could not convert a value to its type. Its message repeats
            // the value, which may be a secret, so neither the exception nor its message is logged.
            LogUnconvertibleSetting(logger);
            LogInvalidConfiguration(logger);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Failure}")]
    private static partial void LogInvalidSetting(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Security setting has a value that cannot be converted to its type.")]
    private static partial void LogUnconvertibleSetting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The security module is not registered: call AddElPaquetoSecurity.")]
    private static partial void LogModuleNotRegistered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The security configuration is invalid.")]
    private static partial void LogInvalidConfiguration(ILogger logger);
}
