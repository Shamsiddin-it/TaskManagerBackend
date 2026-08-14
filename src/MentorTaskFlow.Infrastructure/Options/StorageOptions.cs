using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Object storage and the file limits of Приложение L.
/// </summary>
/// <remarks>
/// Every limit here is a defence, not a preference, so each has a default that is safe on its own:
/// a deployment that forgets to set them still refuses a 500 MB upload and still gives up on a zip
/// bomb after five seconds. Credentials have no defaults at all — they come from the environment
/// (<c>SEC-010</c>).
/// </remarks>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string Endpoint { get; init; } = null!;

    [Required]
    public string AccessKey { get; init; } = null!;

    [Required]
    public string SecretKey { get; init; } = null!;

    /// <summary>
    /// One bucket for the whole installation (<c>TEN-066</c>). A bucket per branch would turn creating
    /// a branch — a product operation — into an infrastructure one, since MinIO policies would have to
    /// be managed on every creation. Isolation comes from the permission check that precedes a URL and
    /// from the scope prefix in the key, not from separate buckets.
    /// </summary>
    [Required]
    public string Bucket { get; init; } = "mentortaskflow";

    /// <summary>MinIO is path-style; virtual-host style requires DNS per bucket.</summary>
    public bool UsePathStyle { get; init; } = true;

    public bool UseSsl { get; init; }

    /// <summary>Creates the bucket at startup when missing. Development and Test only.</summary>
    public bool EnsureBucketOnStartup { get; init; }

    [Range(1, 60)]
    public int PresignedUrlMinutes { get; init; } = 10;

    [Range(1024, 1_073_741_824)]
    public long MaxFileBytes { get; init; } = 52_428_800;

    [Range(1, 100_000)]
    public int ZipMaxEntries { get; init; } = 2_000;

    [Range(1024, 10_737_418_240)]
    public long ZipMaxUncompressedBytes { get; init; } = 314_572_800;

    [Range(1, 10_000)]
    public int ZipMaxRatio { get; init; } = 100;

    [Range(1, 120)]
    public int ZipValidationTimeoutSeconds { get; init; } = 5;

    [Range(1, 8_760)]
    public int OrphanTtlHours { get; init; } = 24;
}
