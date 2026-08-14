using System.IO.Compression;
using System.Text;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.UnitTests.Submissions;

/// <summary>Steps 7–9 of the upload validation order (TZ 17.2, 17.3).</summary>
public sealed class UploadedFileInspectorTests
{
    /// <summary>
    /// Archive limits tightened well below production so a bomb fixture stays a few kilobytes, but the
    /// file-size limit left roomy — otherwise a fixture would trip the size check first and the test
    /// would pass for the wrong reason.
    /// </summary>
    private readonly UploadedFileInspector _inspector = Inspector(maxFileBytes: 1_048_576);

    /// <summary>Only for the two size tests, where a small ceiling is the point.</summary>
    private readonly UploadedFileInspector _smallLimit = Inspector(maxFileBytes: 1024);

    private static UploadedFileInspector Inspector(long maxFileBytes) => new(
        Options.Create(new StorageOptions
        {
            Endpoint = "http://localhost:9000",
            AccessKey = "key",
            SecretKey = "secret",
            MaxFileBytes = maxFileBytes,
            ZipMaxEntries = 5,
            ZipMaxUncompressedBytes = 4096,
            ZipMaxRatio = 10,
            ZipValidationTimeoutSeconds = 5,
        }),
        NullLogger<UploadedFileInspector>.Instance);

    // -----------------------------------------------------------------
    // Step 7: the allowlist
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("работа.pdf", "application/pdf", FileExtension.Pdf)]
    [InlineData("работа.PDF", "application/pdf", FileExtension.Pdf)]
    [InlineData("презентация.pptx", null, FileExtension.Pptx)]
    public void An_allowed_extension_resolves(string name, string? contentType, FileExtension expected) =>
        UploadedFileInspector.ResolveExtension(name, contentType).ShouldBe(expected);

    [Theory]
    [InlineData("работа.txt")]
    [InlineData("работа.exe")]
    [InlineData("работа")]
    [InlineData("работа.pdf.exe")]
    public void Anything_outside_the_allowlist_is_refused(string name) =>
        Should.Throw<UnsupportedMediaTypeException>(() => UploadedFileInspector.ResolveExtension(name, null))
            .Code.ShouldBe(ErrorCodes.FileTypeNotAllowed);

    /// <summary>
    /// A PDF declared as a presentation is either a confused client or an attempt to have the file
    /// stored under one type and served as another (<c>SUB-011</c>).
    /// </summary>
    [Fact]
    public void A_declared_type_that_contradicts_the_extension_is_refused() =>
        Should.Throw<UnsupportedMediaTypeException>(() => UploadedFileInspector.ResolveExtension(
            "работа.pdf",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation"));

    /// <summary>Browsers send this often enough that refusing it would break real uploads.</summary>
    [Fact]
    public void An_octet_stream_declaration_is_accepted() =>
        UploadedFileInspector.ResolveExtension("работа.pdf", "application/octet-stream")
            .ShouldBe(FileExtension.Pdf);

    // -----------------------------------------------------------------
    // Step 8: size
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect([], FileExtension.Pdf));

        exception.Code.ShouldBe(ErrorCodes.FileEmpty);
    }

    /// <summary>
    /// <c>SUB-012</c>: the limit is enforced on bytes actually read. A client controls its own headers,
    /// so a limit checked only against <c>Content-Length</c> is not a limit at all — here the header is
    /// absent and the body is still stopped.
    /// </summary>
    [Fact]
    public async Task An_oversized_body_is_refused_even_without_a_content_length()
    {
        var exception = await Should.ThrowAsync<PayloadTooLargeException>(
            Inspect(new byte[2048], FileExtension.Pdf, inspector: _smallLimit));

        exception.Code.ShouldBe(ErrorCodes.FileTooLarge);
    }

    /// <summary><c>SUB-014</c>: an oversized declared length is refused before the body is read at all.</summary>
    [Fact]
    public async Task An_oversized_declared_length_is_refused_up_front()
    {
        await Should.ThrowAsync<PayloadTooLargeException>(
            Inspect(Pdf(), FileExtension.Pdf, declaredLength: 10_000, inspector: _smallLimit));
    }

    /// <summary>
    /// <c>SUB-014</c> from the other side: neither value is trustworthy alone, and the disagreement is
    /// itself the problem.
    /// </summary>
    [Fact]
    public async Task A_declared_length_that_disagrees_with_the_body_is_refused()
    {
        await Should.ThrowAsync<ValidationAppException>(
            Inspect(Pdf(), FileExtension.Pdf, declaredLength: 17));
    }

    [Fact]
    public async Task A_valid_pdf_is_hashed_and_measured()
    {
        var content = Pdf();

        await using var inspected = await _inspector.InspectAsync(
            new MemoryStream(content),
            FileExtension.Pdf,
            content.LongLength,
            CancellationToken.None);

        inspected.SizeBytes.ShouldBe(content.LongLength);
        inspected.Sha256Hash.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));
        inspected.Sha256Hash.ShouldMatch("^[0-9a-f]{64}$");

        // Rewound, because the caller streams it straight into storage next.
        inspected.Content.Position.ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // Step 9: PDF
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_pdf_without_the_signature_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Encoding.ASCII.GetBytes("не pdf, но с расширением\n%%EOF"), FileExtension.Pdf));

        exception.Code.ShouldBe(ErrorCodes.FileSignatureMismatch);
    }

    [Fact]
    public async Task A_pdf_without_the_trailer_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Encoding.ASCII.GetBytes("%PDF-1.7\nсодержимое без маркера"), FileExtension.Pdf));

        exception.Code.ShouldBe(ErrorCodes.FileSignatureMismatch);
    }

    /// <summary>The trailer is looked for in the last 1024 bytes only, as 17.3 specifies.</summary>
    [Fact]
    public async Task A_trailer_outside_the_last_kilobyte_does_not_count()
    {
        var content = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF" + new string('x', 1024));

        await Should.ThrowAsync<UnprocessableEntityException>(Inspect(content, FileExtension.Pdf));
    }

    // -----------------------------------------------------------------
    // Step 9: PPTX
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_valid_presentation_passes()
    {
        var content = Pptx();

        await using var inspected = await _inspector.InspectAsync(
            new MemoryStream(content),
            FileExtension.Pptx,
            content.LongLength,
            CancellationToken.None);

        inspected.Extension.ShouldBe(FileExtension.Pptx);
    }

    /// <summary>
    /// <c>SUB-017</c>: <c>PK\x03\x04</c> proves nothing — every renamed archive carries it. The OPC
    /// parts are what distinguish a presentation.
    /// </summary>
    [Fact]
    public async Task A_renamed_archive_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Zip(("readme.txt", "обычный архив")), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.PptxStructureInvalid);
    }

    [Fact]
    public async Task A_file_that_is_not_an_archive_at_all_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Encoding.ASCII.GetBytes("совсем не архив"), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.FileSignatureMismatch);
    }

    [Theory]
    [InlineData("[Content_Types].xml")]
    [InlineData("ppt/presentation.xml")]
    public async Task A_presentation_missing_a_required_part_is_refused(string omitted)
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Pptx(omit: omitted), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.PptxStructureInvalid);
    }

    [Fact]
    public async Task A_presentation_that_does_not_declare_its_own_type_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Pptx(declaresPresentationType: false), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.PptxStructureInvalid);
    }

    // -----------------------------------------------------------------
    // Step 9: SUB-018, the archive limits
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("../escaped.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("windows\\path.xml")]
    public async Task An_entry_name_that_could_escape_its_directory_is_refused(string name)
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Pptx(extraEntry: name), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);
    }

    [Fact]
    public async Task An_entry_name_over_255_characters_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Pptx(extraEntry: new string('и', 256) + ".xml"), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);
    }

    [Fact]
    public async Task Too_many_entries_are_refused()
    {
        var entries = Enumerable.Range(0, 10).Select(i => ($"part{i}.xml", "<x/>")).ToArray();

        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Zip(entries), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);
    }

    /// <summary>
    /// The bomb: small on the wire, far over the ratio expanded. Nothing is extracted — the central
    /// directory's own numbers settle it.
    /// </summary>
    [Fact]
    public async Task A_high_compression_ratio_is_refused()
    {
        var exception = await Should.ThrowAsync<UnprocessableEntityException>(
            Inspect(Bomb(), FileExtension.Pptx));

        exception.Code.ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);
    }

    // -----------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------

    private Func<Task> Inspect(
        byte[] content,
        FileExtension extension,
        long? declaredLength = null,
        UploadedFileInspector? inspector = null) =>
        async () =>
        {
            await using var inspected = await (inspector ?? _inspector).InspectAsync(
                new MemoryStream(content),
                extension,
                declaredLength,
                CancellationToken.None);
        };

    private static byte[] Pdf() => Encoding.UTF8.GetBytes("%PDF-1.7\nсодержимое\ntrailer\n%%EOF\n");

    private static byte[] Pptx(
        bool declaresPresentationType = true,
        string? omit = null,
        string? extraEntry = null)
    {
        var contentType = declaresPresentationType
            ? "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"
            : "application/xml";

        var entries = new List<(string Name, string Content)>
        {
            ("[Content_Types].xml", $"""<?xml version="1.0"?><Types><Override ContentType="{contentType}"/></Types>"""),
            ("ppt/presentation.xml", "<presentation/>"),
        };

        if (omit is not null)
        {
            entries.RemoveAll(e => e.Name == omit);
        }

        if (extraEntry is not null)
        {
            entries.Add((extraEntry, "<x/>"));
        }

        return Zip([.. entries]);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Bomb()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open());
            writer.Write("<Types/>");
        }

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("ppt/presentation.xml", CompressionLevel.SmallestSize).Open();
            entry.Write(new byte[64 * 1024]);
        }

        return buffer.ToArray();
    }
}
