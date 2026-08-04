using Microsoft.Extensions.Logging.Abstractions;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using Xunit;

namespace SmartDocQA.Application.Tests.UseCases;

// ═════════════════════════════════════════════════════════════════════════
// FAKE STAND-INS
//
// Same reasoning as DeleteDocumentsUseCaseTests: IngestFolderUseCase's job
// is deciding WHICH files to process and HOW to handle each outcome
// (skip/success/fail) — it delegates the actual folder-reading to
// IFolderScanner and the actual single-document pipeline to
// IIngestDocumentUseCase (the interface we just extracted specifically so
// this could be faked instead of needing the real, heavy 10-dependency
// ingestion pipeline running in a unit test).
//
// FakeDocumentRegistry is reused from DeleteDocumentsUseCaseTests.cs (same
// project, same namespace) — see that file for its definition.
// ═════════════════════════════════════════════════════════════════════════

public class FakeFolderScanner : IFolderScanner
{
    // Test setup decides exactly what "exists in the folder" for a given
    // scenario — no real disk I/O happens.
    public List<string> FilesToReturn { get; set; } = new();

    public List<string> Scan(string folderPath) => FilesToReturn;
}

public class FakeIngestDocumentUseCase : IIngestDocumentUseCase
{
    // Records every source this was asked to ingest, in call order — lets
    // tests assert both WHICH files were sent through the pipeline and
    // which were NOT (e.g. already-ingested files should never appear here).
    public List<DocumentSource> ExecutedSources { get; } = new();

    // Lets a test say "simulate the ingestion pipeline failing for this
    // exact file path" — this is how we prove IngestFolderUseCase's
    // non-fatal per-file error handling actually works.
    public HashSet<string> PathsThatShouldThrow { get; set; } = new();

    public Task<DocumentMetadata> ExecuteAsync(DocumentSource source, CancellationToken ct = default)
    {
        ExecutedSources.Add(source);

        if (PathsThatShouldThrow.Contains(source.Path))
            throw new InvalidOperationException($"Simulated ingestion pipeline failure for {source.Path}");

        // A minimal, deterministic-enough fake result — most tests only
        // care THAT ingestion happened and what was passed in, not the
        // specific page/chunk counts coming back.
        return Task.FromResult(new DocumentMetadata(
            DocumentId: $"doc-{ExecutedSources.Count}",
            FileName: source.FileName ?? Path.GetFileName(source.Path),
            SourcePath: source.Path,
            SourceType: source.SourceType,
            IngestedAt: DateTime.UtcNow,
            TotalPages: 1,
            TotalChunks: 1));
    }
}

// ═════════════════════════════════════════════════════════════════════════
// THE ACTUAL TESTS
// ═════════════════════════════════════════════════════════════════════════

public class IngestFolderUseCaseTests : IDisposable
{
    // Points at a folder that genuinely exists on any machine running these
    // tests — we need SOME real, existing path for Directory.Exists(...) to
    // pass, since that check isn't behind an injected abstraction.
    private static readonly string ExistingFolderPath = Path.GetTempPath();
    private const string NonExistentFolderPath = @"Z:\this_folder_should_never_exist_12345";

    // ProcessFileAsync (in the real IngestFolderUseCase) reads the actual
    // file size via `new FileInfo(filePath).Length` BEFORE registering a
    // document — a real filesystem touch that IFolderScanner's fake doesn't
    // cover (that fake only fakes the DIRECTORY LISTING step, not this
    // separate per-file size read). So any test expecting a file to be
    // successfully INGESTED needs a real (even if empty) file to exist at
    // that exact path — otherwise FileInfo.Length throws, gets caught by
    // the class's own try/catch, and the file silently lands in Failed
    // instead of Ingested. Files that are never actually processed (wrong
    // type, not matched by name, already registered) don't need this.
    private readonly List<string> _createdFiles = new();

    private string CreateRealTempFile(string fileName)
    {
        var path = Path.Combine(ExistingFolderPath, fileName);
        File.WriteAllText(path, "test content");
        _createdFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _createdFiles)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static (IngestFolderUseCase UseCase, FakeFolderScanner Scanner,
        FakeDocumentRegistry Registry, FakeIngestDocumentUseCase IngestDocument)
        CreateSystemUnderTest()
    {
        var scanner = new FakeFolderScanner();
        var registry = new FakeDocumentRegistry();
        var ingestDocument = new FakeIngestDocumentUseCase();

        var useCase = new IngestFolderUseCase(
            scanner, registry, ingestDocument,
            NullLogger<IngestFolderUseCase>.Instance);

        return (useCase, scanner, registry, ingestDocument);
    }

    // ── Guard clauses ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NothingProvided_ReturnsValidationError_AndNeverScansTheFolder()
    {
        // Claim: same principle as DeleteDocumentsUseCase's empty-request
        // guard — an empty request is a caller mistake, and the guard must
        // return BEFORE any real work (not even scanning the folder) happens.
        var (useCase, scanner, _, ingestDocument) = CreateSystemUnderTest();
        scanner.FilesToReturn = new List<string> { "SomeFile.pdf" }; // would be found IF scanned

        var request = new FolderIngestRequest(
            FolderPath: ExistingFolderPath,
            FilesToIngest: new List<string>(),
            FileTypes: new List<string>(),
            IngestAll: false);

        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(IngestMode.None, result.Mode);
        Assert.NotNull(result.Error);
        Assert.Empty(ingestDocument.ExecutedSources); // proves scanning/processing never started
    }

    [Fact]
    public async Task ExecuteAsync_FolderDoesNotExist_ReturnsErrorWithoutScanning()
    {
        // Claim: a nonexistent folder path fails fast with a clear error,
        // rather than the scanner being asked to scan something that isn't
        // there (which could throw a much less clear low-level IO exception).
        var (useCase, _, _, ingestDocument) = CreateSystemUnderTest();

        var request = new FolderIngestRequest(
            FolderPath: NonExistentFolderPath,
            IngestAll: true);

        var result = await useCase.ExecuteAsync(request);

        Assert.NotNull(result.Error);
        Assert.Contains(NonExistentFolderPath, result.Error);
        Assert.Empty(ingestDocument.ExecutedSources);
    }

    // ── Priority ordering ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FilesToIngestProvided_TakesPriorityOverFileTypesAndIngestAll()
    {
        // Claim: mirrors delete's priority test — FilesToIngest must win
        // even when FileTypes AND IngestAll are also set on the request.
        var (useCase, scanner, _, _) = CreateSystemUnderTest();
        var realFile = CreateRealTempFile("FileA.pdf");
        scanner.FilesToReturn = new List<string>
        {
            realFile,
            Path.Combine(ExistingFolderPath, "FileB.docx") // never touched -- filtered out by priority, doesn't need to be real
        };

        var request = new FolderIngestRequest(
            FolderPath: ExistingFolderPath,
            FilesToIngest: new List<string> { "FileA.pdf" },
            FileTypes: new List<string> { "docx" },  // should be ignored
            IngestAll: true);                         // should be ignored

        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(IngestMode.SpecificFiles, result.Mode);
        Assert.Single(result.Ingested);
    }

    [Fact]
    public async Task ExecuteAsync_FileTypesProvided_WhenFilesToIngestEmpty_UsesFileTypesMode()
    {
        var (useCase, scanner, _, _) = CreateSystemUnderTest();
        scanner.FilesToReturn = new List<string>
        {
            Path.Combine(ExistingFolderPath, "Report.docx")
        };

        var request = new FolderIngestRequest(
            FolderPath: ExistingFolderPath,
            FilesToIngest: new List<string>(),  // empty -> falls through
            FileTypes: new List<string> { "docx" },
            IngestAll: true);                    // should still be ignored

        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(IngestMode.FileTypes, result.Mode);
    }

    // ── Skip-already-ingested (registry dedup) ──────────────────────────

    [Fact]
    public async Task ProcessFile_AlreadyInRegistry_IsSkipped_NeverSentToIngestPipeline()
    {
        // Claim: the class-level docstring's dedup promise — "already-
        // ingested files are skipped (registry check)" — must actually
        // prevent the file from reaching the (expensive) ingestion pipeline
        // at all, not just be reported as skipped after the fact.
        var (useCase, scanner, registry, ingestDocument) = CreateSystemUnderTest();
        var existingFilePath = Path.Combine(ExistingFolderPath, "AlreadyDone.pdf");

        scanner.FilesToReturn = new List<string> { existingFilePath };
        registry.Entries = new List<RegistryEntry>
        {
            new(existingFilePath, "doc-existing", "AlreadyDone.pdf", 1024, DateTime.UtcNow)
        };

        var request = new FolderIngestRequest(FolderPath: ExistingFolderPath, IngestAll: true);
        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Skipped);
        Assert.Empty(result.Ingested);
        Assert.Empty(ingestDocument.ExecutedSources); // never even attempted
    }

    // ── Successful new-file ingestion ───────────────────────────────────

    [Fact]
    public async Task ProcessFile_NewFile_IsIngestedThenRegistered_AndAddedToIngestedList()
    {
        // Claim: the full happy path — a new file gets sent through the
        // ingestion pipeline, THEN registered (so future scans correctly
        // skip it), and shows up in the result's Ingested list.
        var (useCase, scanner, registry, ingestDocument) = CreateSystemUnderTest();
        var filePath = CreateRealTempFile("NewFile.pdf");
        scanner.FilesToReturn = new List<string> { filePath };

        var request = new FolderIngestRequest(FolderPath: ExistingFolderPath, IngestAll: true);
        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Ingested);
        Assert.Single(ingestDocument.ExecutedSources);
        Assert.Equal(filePath, ingestDocument.ExecutedSources[0].Path);
        Assert.Single(registry.RegisteredCalls);
        Assert.Equal(filePath, registry.RegisteredCalls[0].FilePath);
    }

    // ── Non-fatal per-file failure ───────────────────────────────────────

    [Fact]
    public async Task ProcessFile_IngestionPipelineThrows_FileGoesToFailedList_AndNextFileStillProcesses()
    {
        // Claim: mirrors delete's non-fatal-graph-failure test — one file's
        // ingestion pipeline failure must not abort the whole folder scan.
        // The failing file is reported in Failed with its error message,
        // and the NEXT file in the same request is still fully processed.
        var (useCase, scanner, _, ingestDocument) = CreateSystemUnderTest();
        var badFile = Path.Combine(ExistingFolderPath, "Corrupt.pdf"); // never reaches FileInfo -- our fake throws first
        var goodFile = CreateRealTempFile("Fine.pdf"); // must be real -- expected to succeed all the way through
        scanner.FilesToReturn = new List<string> { badFile, goodFile };
        ingestDocument.PathsThatShouldThrow = new HashSet<string> { badFile };

        var request = new FolderIngestRequest(FolderPath: ExistingFolderPath, IngestAll: true);
        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Failed);
        Assert.Equal("Corrupt.pdf", result.Failed[0].FileName);
        Assert.Contains("Simulated ingestion pipeline failure", result.Failed[0].Error);

        // The good file, requested in the SAME call, still succeeded.
        Assert.Single(result.Ingested);
        Assert.Equal(2, ingestDocument.ExecutedSources.Count); // both were attempted
    }

    // ── SpecificFiles: not-found reporting ──────────────────────────────

    [Fact]
    public async Task IngestSpecificFiles_FileNotInScannedFolder_ReportedAsNotFound()
    {
        var (useCase, scanner, _, _) = CreateSystemUnderTest();
        var realFile = CreateRealTempFile("FileA.pdf");
        scanner.FilesToReturn = new List<string> { realFile };

        var request = new FolderIngestRequest(
            FolderPath: ExistingFolderPath,
            FilesToIngest: new List<string> { "FileA.pdf", "DoesNotExist.pdf" });

        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Ingested);
        Assert.Single(result.NotFound);
        Assert.Equal("DoesNotExist.pdf", result.NotFound[0]);
    }

    // ── FileTypes: normalization ─────────────────────────────────────────

    [Fact]
    public async Task IngestByFileTypes_AcceptsExtensionWithOrWithoutLeadingDot_CaseInsensitive()
    {
        var (useCase, scanner, _, _) = CreateSystemUnderTest();
        var realPdf = CreateRealTempFile("Report.PDF"); // uppercase on disk
        scanner.FilesToReturn = new List<string>
        {
            realPdf,
            Path.Combine(ExistingFolderPath, "Notes.docx") // wrong type, never touched
        };

        var request = new FolderIngestRequest(
            FolderPath: ExistingFolderPath,
            FileTypes: new List<string> { "pdf" }); // no leading dot, lowercase

        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Ingested);
    }

    // ── IngestAll ────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAll_ProcessesEveryFileTheScannerReturns()
    {
        var (useCase, scanner, _, ingestDocument) = CreateSystemUnderTest();
        scanner.FilesToReturn = new List<string>
        {
            CreateRealTempFile("A.pdf"),
            CreateRealTempFile("B.docx"),
            CreateRealTempFile("C.pdf")
        };

        var request = new FolderIngestRequest(FolderPath: ExistingFolderPath, IngestAll: true);
        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(3, result.Ingested.Count);
        Assert.Equal(3, ingestDocument.ExecutedSources.Count);
        Assert.Empty(result.Failed);
        Assert.Empty(result.Skipped);
    }
}
