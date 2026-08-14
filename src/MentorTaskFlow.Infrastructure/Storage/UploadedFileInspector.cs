using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Storage;

/// <summary>
/// A file that has passed steps 8 and 9 of 17.2, spooled to disk and rewound.
/// </summary>
/// <remarks>
/// Spooled rather than buffered in memory: 50 MB per concurrent upload held in the managed heap would
/// put the process on the large-object heap under any real load. The temporary file is opened with
/// <see cref="FileOptions.DeleteOnClose"/>, so it disappears when the request ends however the request
/// ends.
/// </remarks>
public sealed class InspectedFile(Stream content, long sizeBytes, string sha256Hash, FileExtension extension)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long SizeBytes { get; } = sizeBytes;

    public string Sha256Hash { get; } = sha256Hash;

    public FileExtension Extension { get; } = extension;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Steps 7–9 of the upload validation order (TZ 17.2, 17.3).
/// </summary>
/// <remarks>
/// The whole point of the ordering is that a rejected file never reaches the bucket, so everything
/// here runs before the first byte is handed to storage.
/// </remarks>
public sealed class UploadedFileInspector(IOptions<StorageOptions> options, ILogger<UploadedFileInspector> logger)
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] PdfTrailer = "%%EOF"u8.ToArray();
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    private const string ContentTypesEntry = "[Content_Types].xml";
    private const string PresentationEntry = "ppt/presentation.xml";
    private const string PresentationContentType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";

    /// <summary>A content-types manifest larger than this is not a manifest.</summary>
    private const long ContentTypesMaxBytes = 1_048_576;

    private readonly StorageOptions _options = options.Value;

    /// <summary>
    /// Resolves the declared extension and MIME against the allowlist (step 7).
    /// </summary>
    /// <remarks>
    /// Both must agree. A <c>.pdf</c> declared as a presentation is either a confused client or a
    /// deliberate attempt to have the file stored under one type and served as another
    /// (<c>SUB-011</c>).
    /// </remarks>
    public static FileExtension ResolveExtension(string? fileName, string? declaredContentType)
    {
        var suffix = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        var extension = suffix switch
        {
            ".pdf" => FileExtension.Pdf,
            ".pptx" => FileExtension.Pptx,
            _ => throw new UnsupportedMediaTypeException(
                ErrorCodes.FileTypeNotAllowed,
                "Допустимы только файлы .pdf и .pptx."),
        };

        // The browser sends application/octet-stream often enough that refusing it would break real
        // uploads; anything else must match the extension.
        var expected = Submission.ContentTypeOf(extension);
        var declared = declaredContentType?.Split(';')[0].Trim();

        if (!string.IsNullOrEmpty(declared)
            && !string.Equals(declared, expected, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedMediaTypeException(
                ErrorCodes.FileTypeNotAllowed,
                "Заявленный тип содержимого не соответствует расширению файла.");
        }

        return extension;
    }

    /// <summary>
    /// Reads the upload under a byte limit, hashes it, and checks its structure (steps 8 and 9).
    /// </summary>
    /// <param name="declaredLength">
    /// <c>Content-Length</c> when the client sent one. Absent is not an error — a chunked upload is
    /// legitimate (<c>SUB-013</c>) — but a value that disagrees with what actually arrived is
    /// (<c>SUB-014</c>).
    /// </param>
    public async Task<InspectedFile> InspectAsync(
        Stream source,
        FileExtension extension,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        // SUB-014: an oversized Content-Length is refused before the body is read at all. There is no
        // reason to spend bandwidth on a file already known to be too large.
        if (declaredLength > _options.MaxFileBytes)
        {
            throw new PayloadTooLargeException(
                ErrorCodes.FileTooLarge,
                $"Файл превышает допустимый размер {_options.MaxFileBytes} байт.");
        }

        var spooled = CreateTemporaryFile();

        try
        {
            var (size, hash) = await CopyWithLimitAsync(source, spooled, cancellationToken);

            if (size == 0)
            {
                throw new UnprocessableEntityException(ErrorCodes.FileEmpty, "Файл пуст.");
            }

            // SUB-014 again, from the other side: the header claimed one size and the body was another.
            // Neither value is trustworthy on its own, and the disagreement itself is the problem.
            if (declaredLength is { } declared && declared != size)
            {
                throw new ValidationAppException(
                    "file",
                    "Фактический размер файла не совпадает с заявленным в Content-Length.");
            }

            spooled.Position = 0;

            switch (extension)
            {
                case FileExtension.Pdf:
                    await ValidatePdfAsync(spooled, size, cancellationToken);
                    break;

                case FileExtension.Pptx:
                    await ValidatePptxAsync(spooled, cancellationToken);
                    break;
            }

            spooled.Position = 0;

            return new InspectedFile(spooled, size, hash, extension);
        }
        catch
        {
            await spooled.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Copies the upload while counting bytes and hashing them in one pass.
    /// </summary>
    /// <remarks>
    /// <c>SUB-012</c>: the limit applies to bytes actually read, never to the declared length. A client
    /// controls its own headers, so a limit enforced on <c>Content-Length</c> is not a limit at all —
    /// reading stops the moment the total is exceeded, and the rest of the body is never pulled.
    /// </remarks>
    private async Task<(long Size, string Hash)> CopyWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[81_920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (total > _options.MaxFileBytes)
            {
                throw new PayloadTooLargeException(
                    ErrorCodes.FileTooLarge,
                    $"Файл превышает допустимый размер {_options.MaxFileBytes} байт.");
            }

            sha256.TransformBlock(buffer, 0, read, null, 0);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        sha256.TransformFinalBlock([], 0, 0);

        return (total, Convert.ToHexStringLower(sha256.Hash!));
    }

    /// <summary>
    /// <c>SUB-016</c>: the signature and the trailer, and nothing else.
    /// </summary>
    /// <remarks>
    /// No structural parsing, no text extraction, no server-side rendering. That is the decision the TZ
    /// makes explicitly, and it removes an entire class of parser vulnerabilities rather than trying to
    /// survive them.
    /// </remarks>
    private static async Task ValidatePdfAsync(Stream content, long size, CancellationToken cancellationToken)
    {
        var header = new byte[PdfSignature.Length];
        await content.ReadExactlyAsync(header, cancellationToken);

        if (!header.AsSpan().SequenceEqual(PdfSignature))
        {
            throw new UnprocessableEntityException(
                ErrorCodes.FileSignatureMismatch,
                "Файл не является PDF: отсутствует сигнатура %PDF-.");
        }

        var tailLength = (int)Math.Min(1024, size);
        content.Position = size - tailLength;

        var tail = new byte[tailLength];
        await content.ReadExactlyAsync(tail, cancellationToken);

        if (tail.AsSpan().IndexOf(PdfTrailer) < 0)
        {
            throw new UnprocessableEntityException(
                ErrorCodes.FileSignatureMismatch,
                "Файл не является PDF: в последних 1024 байтах нет маркера %%EOF.");
        }
    }

    /// <summary>
    /// <c>SUB-017</c> and <c>SUB-018</c>: OPC structure plus the archive-bomb limits.
    /// </summary>
    /// <remarks>
    /// A PPTX is a ZIP, so <c>PK\x03\x04</c> proves nothing — any renamed archive carries it. What
    /// distinguishes a presentation is its OPC parts, which is why the signature is only the first of
    /// five checks. The limits run first: an archive is untrusted input, and asking it what it contains
    /// before bounding it is how zip bombs work.
    /// </remarks>
    private async Task ValidatePptxAsync(Stream content, CancellationToken cancellationToken)
    {
        var signature = new byte[ZipSignature.Length];
        await content.ReadExactlyAsync(signature, cancellationToken);

        if (!signature.AsSpan().SequenceEqual(ZipSignature))
        {
            throw new UnprocessableEntityException(
                ErrorCodes.FileSignatureMismatch,
                "Файл не является PPTX: отсутствует сигнатура ZIP-контейнера.");
        }

        content.Position = 0;

        // SUB-019: the inspection is bounded in time as well as in size. A crafted central directory
        // can make enumeration itself expensive, which no per-entry limit catches.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ZipValidationTimeoutSeconds));

        try
        {
            await Task.Run(() => InspectArchive(content, timeout.Token), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "PPTX validation exceeded {Timeout}s and was abandoned.",
                _options.ZipValidationTimeoutSeconds);

            throw new UnprocessableEntityException(
                ErrorCodes.ZipSafetyLimitExceeded,
                "Проверка структуры PPTX превысила допустимое время.");
        }
        catch (InvalidDataException)
        {
            throw new UnprocessableEntityException(
                ErrorCodes.PptxStructureInvalid,
                "Файл не открывается как OPC-контейнер.");
        }
    }

    private void InspectArchive(Stream content, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > _options.ZipMaxEntries)
        {
            throw Bomb($"в архиве больше {_options.ZipMaxEntries} записей");
        }

        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnsureSafeName(entry.FullName);

            totalUncompressed += entry.Length;
            totalCompressed += entry.CompressedLength;

            if (totalUncompressed > _options.ZipMaxUncompressedBytes)
            {
                throw Bomb($"суммарный несжатый размер превышает {_options.ZipMaxUncompressedBytes} байт");
            }

            if (ExceedsRatio(entry.Length, entry.CompressedLength))
            {
                throw Bomb($"коэффициент сжатия записи превышает {_options.ZipMaxRatio}:1");
            }
        }

        if (ExceedsRatio(totalUncompressed, totalCompressed))
        {
            throw Bomb($"суммарный коэффициент сжатия превышает {_options.ZipMaxRatio}:1");
        }

        // The declared lengths above come from the central directory and are not trusted on their own;
        // reading the manifest below is bounded independently for exactly that reason.
        var contentTypes = archive.GetEntry(ContentTypesEntry)
            ?? throw Invalid($"в архиве нет {ContentTypesEntry}");

        if (archive.GetEntry(PresentationEntry) is null)
        {
            throw Invalid($"в архиве нет {PresentationEntry}");
        }

        if (contentTypes.Length > ContentTypesMaxBytes)
        {
            throw Bomb($"{ContentTypesEntry} неправдоподобно велик");
        }

        using var manifest = contentTypes.Open();
        using var reader = new StreamReader(manifest, Encoding.UTF8);

        var buffer = new char[(int)Math.Min(ContentTypesMaxBytes, contentTypes.Length + 1)];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);

        if (new ReadOnlySpan<char>(buffer, 0, read).IndexOf(PresentationContentType) < 0)
        {
            throw Invalid("в [Content_Types].xml не объявлен тип презентации");
        }
    }

    /// <summary>
    /// <c>SUB-018</c>: entry names are attacker-controlled and would become file paths in any tool that
    /// extracts the archive later. Nothing is extracted here, but a name that could escape its
    /// directory is evidence enough on its own.
    /// </summary>
    private void EnsureSafeName(string name)
    {
        if (name.Length > 255)
        {
            throw Bomb("длина имени записи превышает 255 символов");
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || name.Contains('\\')
            || name.Contains('\0')
            || Path.IsPathRooted(name)
            || name.StartsWith('/'))
        {
            throw Bomb($"недопустимое имя записи «{name}»");
        }
    }

    private bool ExceedsRatio(long uncompressed, long compressed) =>
        compressed > 0
            ? uncompressed / compressed > _options.ZipMaxRatio
            : uncompressed > 0;

    private static UnprocessableEntityException Bomb(string reason) =>
        new(ErrorCodes.ZipSafetyLimitExceeded, $"PPTX отклонён: {reason}.");

    private static UnprocessableEntityException Invalid(string reason) =>
        new(ErrorCodes.PptxStructureInvalid, $"PPTX отклонён: {reason}.");

    private static FileStream CreateTemporaryFile() => new(
        Path.Combine(Path.GetTempPath(), $"mtf-{Guid.CreateVersion7():N}.upload"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81_920,

        // Removed when the handle closes, whatever happens in between — including an unhandled
        // exception mid-upload.
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}
