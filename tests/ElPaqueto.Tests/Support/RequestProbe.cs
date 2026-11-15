using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Security.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ElPaqueto.Tests;

/// <summary>
/// Test instrumentation for the security suites: it tells which endpoint a request was routed to, and whether
/// the code behind that endpoint ran.
/// </summary>
/// <remarks>
/// <para>Every response carries <see cref="EndpointHeader"/>: the key of the permission-table row of the endpoint
/// the router chose, or <c>(none)</c>. It is how the matrix proves that each sample request reaches the row it
/// is meant for, and how the tampering suites show which endpoint a variant reached.</para>
/// <para><see cref="Executed"/> records the key of every MVC action and Razor page that started to run (a
/// resource filter: the first step after authorization), and every run of the health checks. A refused request
/// must leave it empty.</para>
/// </remarks>
public sealed class RequestProbe
{
    /// <summary>The response header that names the endpoint the router chose.</summary>
    public const string EndpointHeader = "X-Test-Endpoint";

    private readonly Lock _gate = new();
    private readonly List<string> _executed = [];

    /// <summary>The keys of the actions, pages and health endpoints that ran since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Executed
    {
        get
        {
            lock (_gate)
            {
                return _executed.ToArray();
            }
        }
    }

    /// <summary>Forgets what ran so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _executed.Clear();
        }
    }

    /// <summary>Installs the probe in a host: the endpoint header, the MVC filter and a health check.</summary>
    public void Install(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(this);
            services.AddSingleton<IStartupFilter>(new EndpointHeaderFilter());
            services.Configure<MvcOptions>(options => options.Filters.Add(new RecordingFilter(this)));
            services.AddHealthChecks().AddCheck("request-probe", new RecordingHealthCheck(this));
        });
    }

    private void Record(string key)
    {
        lock (_gate)
        {
            _executed.Add(key);
        }
    }

    private static string KeyOf(HttpContext context) => EndpointKey.Resolve(context?.GetEndpoint()) ?? "(none)";

    /// <summary>
    /// Wraps the application pipeline (a test startup filter, registered after those of the application): names the
    /// chosen endpoint on the way out, whatever the answer. A request refused before the pipeline, by Kestrel, has no
    /// such header.
    /// </summary>
    private sealed class EndpointHeaderFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[EndpointHeader] = KeyOf(context);
                    return Task.CompletedTask;
                });

                await nextMiddleware();
            });

            next(app);
        };
    }

    /// <summary>Runs for every controller action and Razor page, after authorization and before model binding.</summary>
    private sealed class RecordingFilter(RequestProbe probe) : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context) => probe.Record(KeyOf(context.HttpContext));

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }

    /// <summary>Runs whenever a health endpoint evaluates its checks.</summary>
    private sealed class RecordingHealthCheck(RequestProbe probe) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            // The checks run inside the request, and the application registers IHttpContextAccessor (the security
            // module does), so the accessor's async-local context is the request's.
            probe.Record(KeyOf(new HttpContextAccessor().HttpContext));
            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}
