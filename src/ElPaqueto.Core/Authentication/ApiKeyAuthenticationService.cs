using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Core;

public class ApiKeyAuthenticationService : IAuthenticationService
{
    private readonly string _apiKey;
    private readonly ApiKey[] _apiKeys;

    public ApiKeyAuthenticationService(IOptionsSnapshot<ElPaquetoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _apiKey = string.IsNullOrEmpty(options.Value.ApiKey) ? null : options.Value.ApiKey;
        _apiKeys = options.Value.Authentication?.ApiKeys ?? [];
    }

    public Task<bool> AuthenticateAsync(string apiKey, CancellationToken cancellationToken)
        => Task.FromResult(Authenticate(apiKey));

    private bool Authenticate(string apiKey)
    {
        // No authentication is necessary if there is no required API key.
        if (_apiKey == null && (_apiKeys.Length == 0)) return true;

        return _apiKey == apiKey || _apiKeys.Any(x => x.Key.Equals(apiKey));
    }
}
