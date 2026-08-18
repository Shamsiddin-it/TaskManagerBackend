using MentorTaskFlow.Infrastructure.Storage;
using Minio;
using Minio.DataModel.Args;
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
        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket));
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Opens a client for arrange and assert steps that go straight to storage.
    /// </summary>
    /// <remarks>
    /// Built the same way the application builds its own — through <see cref="StorageEndpoint"/> —
    /// so a change to how the endpoint is interpreted cannot pass here and fail in production.
    /// The container serves plain HTTP, so SSL is off.
    /// </remarks>
    public IMinioClient CreateClient()
    {
        var endpoint = StorageEndpoint.Parse(Endpoint);

        return new MinioClient()
            .WithEndpoint(endpoint.Host, endpoint.Port)
            .WithCredentials(AccessKey, SecretKey)
            .WithSSL(false)
            .Build();
    }

    /// <summary>Lists every object key, so a test can assert what did — and did not — reach the bucket.</summary>
    public async Task<IReadOnlyList<string>> ListKeysAsync()
    {
        using var client = CreateClient();
        var keys = new List<string>();

        var listing = client.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(Bucket).WithRecursive(true));

        await foreach (var item in listing.ConfigureAwait(false))
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    /// <summary>Empties the bucket between tests.</summary>
    public async Task ResetAsync()
    {
        using var client = CreateClient();

        foreach (var key in await ListKeysAsync())
        {
            await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key));
        }
    }
}
