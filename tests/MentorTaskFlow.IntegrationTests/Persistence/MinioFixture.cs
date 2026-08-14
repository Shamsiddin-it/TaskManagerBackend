using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;

namespace MentorTaskFlow.IntegrationTests.Persistence;

/// <summary>
/// A real MinIO instance with the submissions bucket created.
/// </summary>
/// <remarks>
/// <c>TEST-004</c> forbids substituting a fake here for the same reason it forbids the in-memory EF
/// provider: what is being tested — presigned URLs, response-header overrides, the bucket refusing
/// anonymous reads — exists only in the real implementation. A stub would pass while the deployed
/// system failed.
/// </remarks>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Bucket = "mentortaskflow-tests";
    public const string AccessKey = "minioadmin";
    public const string SecretKey = "minioadmin";

    private readonly MinioContainer _container = new MinioBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .WithUsername(AccessKey)
        .WithPassword(SecretKey)
        .Build();

    public string Endpoint => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Opens a client for arrange and assert steps that go straight to storage.</summary>
    public IAmazonS3 CreateClient() => new AmazonS3Client(
        new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = "us-east-1",
        });

    /// <summary>Lists every object key, so a test can assert what did — and did not — reach the bucket.</summary>
    public async Task<IReadOnlyList<string>> ListKeysAsync()
    {
        using var client = CreateClient();

        var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });

        return response.S3Objects?.Select(o => o.Key).ToList() ?? [];
    }

    /// <summary>Empties the bucket between tests.</summary>
    public async Task ResetAsync()
    {
        using var client = CreateClient();

        foreach (var key in await ListKeysAsync())
        {
            await client.DeleteObjectAsync(Bucket, key);
        }
    }
}
