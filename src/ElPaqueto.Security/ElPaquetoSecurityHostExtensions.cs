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
    /// Failure messages name the offending setting and never contain its value.
    /// </remarks>
    public static bool ValidateElPaquetoSecurityOptions(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ElPaqueto.Security.Configuration");

        try
        {
            // Reading the value forces validation to run.
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
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Failure}")]
    private static partial void LogInvalidSetting(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "The security configuration is invalid.")]
    private static partial void LogInvalidConfiguration(ILogger logger);
}
