using System;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using ElPaqueto.Aws;
using ElPaqueto.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public static class AwsApplicationExtensions
{
    public static ElPaquetoApplication AddAwsS3Storage(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoOptions<S3StorageOptions>(nameof(ElPaquetoOptions.Storage));

        app.Services.AddTransient<S3StorageService>();
        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<S3StorageService>());

        app.Services.AddProvider<IStorageService>((provider, config) =>
        {
            if (!config.HasStorageType("AwsS3")) return null;

            return provider.GetRequiredService<S3StorageService>();
        });

        app.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<S3StorageOptions>>().Value;

            var config = CreateConfig(options);

            if (options.UseInstanceProfile)
            {
                var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials(config);
                return new AmazonS3Client(credentials, config);
            }

            if (!string.IsNullOrEmpty(options.AssumeRoleArn))
            {
                var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials(config);
                var assumedCredentials = AssumeRoleAsync(
                        credentials,
                        options.AssumeRoleArn,
                        $"ElPaqueto-Session-{Guid.NewGuid()}")
                    .GetAwaiter()
                    .GetResult();

                return new AmazonS3Client(assumedCredentials, config);
            }

            if (!string.IsNullOrEmpty(options.AccessKey))
            {
                return new AmazonS3Client(
                    new BasicAWSCredentials(
                        options.AccessKey,
                        options.SecretKey),
                    config);
            }

            return new AmazonS3Client(config);
        });

        return app;
    }

    public static ElPaquetoApplication AddAwsS3Storage(
        this ElPaquetoApplication app,
        Action<S3StorageOptions> configure)
    {
        app.AddAwsS3Storage();
        app.Services.Configure(configure);
        return app;
    }

    /// <summary>
    /// The client configuration. AWS SDK v4 sends CRC32 trailing checksums on uploads and validates them on
    /// downloads by default, which older MinIO, Ceph and other S3-compatible servers reject. For a custom endpoint
    /// the checksums are used only when the operation requires them (the behaviour of SDK v3); real AWS (a region,
    /// no service URL) keeps the v4 defaults.
    /// </summary>
    internal static AmazonS3Config CreateConfig(S3StorageOptions options)
    {
        if (options.Endpoint != null)
        {
            return new AmazonS3Config
            {
                ServiceURL = options.Endpoint.AbsoluteUri,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };
        }

        return new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region) };
    }

    private static async Task<AWSCredentials> AssumeRoleAsync(
        AWSCredentials credentials,
        string roleArn,
        string roleSessionName)
    {
        var assumedCredentials = new AssumeRoleAWSCredentials(credentials, roleArn, roleSessionName);
        var immutableCredentials = await credentials.GetCredentialsAsync();

        if (string.IsNullOrWhiteSpace(immutableCredentials.Token))
        {
            throw new InvalidOperationException($"Unable to assume role {roleArn}");
        }

        return assumedCredentials;
    }
}
