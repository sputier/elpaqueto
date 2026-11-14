using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElPaqueto.Security.Authorization;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// The authorization matrix (issue #30): every row of the permission table, with every kind of credential, gets
/// exactly the answer the table prescribes. The rows come from <see cref="EndpointPermissionTable"/> itself, so a
/// new row is tested as soon as it has a sample request.
/// </summary>
/// <remarks>
/// For each case: a refusal is 401 with the Basic challenge or 403 without it, nothing behind the endpoint runs,
/// and the packages and package storage are unchanged. An allowed request reaches the code of its row.
/// </remarks>
public class AuthorizationMatrixIntegrationTests(SecuritySuiteHost host) : IClassFixture<SecuritySuiteHost>
{
    public static IEnumerable<object[]> Cases()
        => from key in EndpointSamples.Keys()
           from credential in SecuritySuiteHost.Credentials
           select new object[] { key, credential.Name };

    [Fact]
    public void EveryRowOfThePermissionTable_HasASampleRequest()
    {
        Assert.True(
            EndpointSamples.RowsWithoutSample().Count == 0,
            "Rows of the permission table without a sample request in EndpointSamples (add one): "
                + string.Join(", ", EndpointSamples.RowsWithoutSample()));
        Assert.True(
            EndpointSamples.SamplesWithoutRow().Count == 0,
            "Sample requests for rows the permission table does not have: " + string.Join(", ", EndpointSamples.SamplesWithoutRow()));
    }

    [Theory]
    [MemberData(nameof(EveryKey))]
    public async Task EverySample_IsRoutedToItsOwnRow(string key)
    {
        using var client = host.ClientFor("anonymous");
        using var request = EndpointSamples.Request(key);

        using var response = await client.SendAsync(request);

        Assert.Equal(key, Assert.Single(response.Headers.GetValues(RequestProbe.EndpointHeader)));
    }

    public static IEnumerable<object[]> EveryKey() => EndpointSamples.Keys().Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task EveryRow_WithEveryCredential_GetsTheAnswerOfTheTable(string key, string credential)
        => AssertCase(host, key, credential, hardDelete: false);

    internal static async Task AssertCase(SecuritySuiteHost host, string key, string credentialName, bool hardDelete)
    {
        var credential = SecuritySuiteHost.Credential(credentialName);
        var expected = SecuritySuiteHost.ExpectedRefusal(key, credential, hardDelete);
        var scenario = $"{EndpointSamples.MethodOf(key)} /{EndpointSamples.PathOf(key)} (row '{key}') with {credentialName}";

        using var client = host.ClientFor(credentialName);
        using var request = EndpointSamples.Request(key);
        var before = await host.SnapshotStateAsync();
        host.Probe.Clear();

        using var response = await client.SendAsync(request);

        var status = (int)response.StatusCode;
        var executed = host.Probe.Executed;
        if (expected is null)
        {
            Assert.True(
                response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && status < 500,
                $"{scenario}: expected to be allowed, answered {status}.");
            Assert.True(executed.Contains(key), $"{scenario}: allowed, but the code of the row did not run (ran: {string.Join(", ", executed)}).");
            return;
        }

        Assert.True(response.StatusCode == expected, $"{scenario}: expected {(int)expected}, answered {status}.");
        Assert.True(executed.Count == 0, $"{scenario}: refused, but this ran: {string.Join(", ", executed)}.");
        if (expected == HttpStatusCode.Unauthorized)
        {
            Assert.Equal(new[] { "Basic realm=\"ElPaqueto\"" }, response.Headers.GetValues("WWW-Authenticate"));
        }
        else
        {
            Assert.False(response.Headers.Contains("WWW-Authenticate"), $"{scenario}: a 403 must not carry a challenge.");
        }

        Assert.True(before == await host.SnapshotStateAsync(), $"{scenario}: refused, but the packages or package storage changed.");
    }
}

/// <summary>
/// The row that changes with the configuration: when packages are deleted for good, deleting needs Admin on top of
/// Publish (matrix row 9).
/// </summary>
public class HardDeleteAuthorizationMatrixIntegrationTests(HardDeleteSecuritySuiteHost host) : IClassFixture<HardDeleteSecuritySuiteHost>
{
    public static IEnumerable<object[]> Cases()
        => SecuritySuiteHost.Credentials.Select(credential => new object[] { credential.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task HardDelete_WithEveryCredential_GetsTheAnswerOfTheTable(string credential)
        => AuthorizationMatrixIntegrationTests.AssertCase(host, EndpointPermissionTable.DeleteKey, credential, hardDelete: true);
}
