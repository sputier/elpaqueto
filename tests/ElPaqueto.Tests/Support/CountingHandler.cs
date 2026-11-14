using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ElPaqueto.Tests;

/// <summary>Counts the requests sent through it, for example to an upstream source in mirror tests.</summary>
internal sealed class CountingHandler : DelegatingHandler
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        return base.SendAsync(request, cancellationToken);
    }
}
