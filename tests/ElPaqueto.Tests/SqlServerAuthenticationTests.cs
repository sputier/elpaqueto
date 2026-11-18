using Microsoft.Data.SqlClient;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// Microsoft.Data.SqlClient 7 moved Microsoft Entra ID authentication out of the core package into
/// Microsoft.Data.SqlClient.Extensions.Azure. These tests need no SQL Server: they check that the package is part of
/// what ElPaqueto ships, so that connection strings with <c>Authentication=Active Directory ...</c> keep working.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SqlServerAuthenticationTests
{
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity)]
    public void EntraIdAuthenticationMethods_HaveAProvider(SqlAuthenticationMethod method)
    {
        // Touching the SQL Server provider assembly first, as the host does when Database:Type=SqlServer.
        _ = typeof(ElPaqueto.Database.SqlServer.SqlServerContext);

        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.NotNull(provider);
        Assert.True(provider.IsSupported(method));
    }
}
