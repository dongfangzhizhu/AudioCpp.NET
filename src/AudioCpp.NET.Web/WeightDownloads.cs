using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

// ── Catalog: package id -> the weight files the pinned model_specs declare ──
//
// The native package manager (src/framework/package_manager/manager.cpp in the
// pinned engine) resolves `package_defaults.download` first and lets each package
// override it, then strips `strip_prefix` from every remote path before writing
// into `target_directory`. This index mirrors those rules so the workbench can
// download exactly the files the shim would later load.

internal sealed record WeightFile(string RemotePath, string RelativePath);

internal sealed record WeightPackage(
    string Id, string Family, string Kind, string Repo, string Revision,
    string TargetDirectory, string StripPrefix, IReadOnlyList<WeightFile> Files);

internal sealed class WeightCatalog
{
    private static readonly Lazy<WeightCatalog> Lazy = new(Load);
    internal static WeightCatalog Instance => Lazy.Value;

    private readonly Dictionary<string, WeightPackage> _byId;
    private WeightCatalog(Dictionary<string, WeightPackage> byId) => _byId = byId;

    internal IReadOnlyCollection<WeightPackage> All => _byId.Values;
    internal bool TryGet(string id, out WeightPackage package) => _byId.TryGetValue(id, out package!);

    private static WeightCatalog Load()
    {
        var index = new Dictionary<string, WeightPackage>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(AppContext.BaseDirectory, "model_specs");
        if (!Directory.Exists(root)) return new WeightCatalog(index);

        foreach (var path in Directory.EnumerateFiles(root, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var spec = document.RootElement;
                var family = Text(spec, "family") ?? Path.GetFileNameWithoutExtension(path);
                JsonElement? defaults = null;
                if (spec.TryGetProperty("package_defaults", out var packageDefaults) &&
                    packageDefaults.ValueKind == JsonValueKind.Object &&
                    packageDefaults.TryGetProperty("download", out var defaultDownload) &&
                    defaultDownload.ValueKind == JsonValueKind.Object)
                    defaults = defaultDownload;
                if (!spec.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in packages.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = Text(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var download = item.TryGetProperty("download", out var own) && own.ValueKind == JsonValueKind.Object
                        ? (JsonElement?)own : defaults;
                    if (download is null) continue;
                    var kind = Text(download.Value, "kind") ?? "";
                    if (kind is not ("huggingface_snapshot" or "modelscope_snapshot")) continue;
                    var repo = Text(download.Value, "repo") ?? "";
                    if (repo.Length == 0) continue;
                    var revision = Text(download.Value, "revision");
                    if (string.IsNullOrWhiteSpace(revision))
                        revision = kind == "modelscope_snapshot" ? "master" : "main";
                    var target = Text(item, "target_directory") ?? "";
                    if (!SafeRelative(target)) continue;
                    var strip = Text(item, "strip_prefix") ?? "";

                    var files = new List<WeightFile>();
                    if (item.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in list.EnumerateArray())
                        {
                            var remote = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                            if (string.IsNullOrWhiteSpace(remote)) continue;
                            var relative = Strip(remote, strip);
                            if (relative is null || !SafeRelative(relative)) { files.Clear(); break; }
                            files.Add(new WeightFile(remote, relative));
                        }
                    }
                    if (files.Count == 0) continue;
                    index[id] = new WeightPackage(id, family, kind, repo, revision, target, strip, files);
                }
            }
            catch (JsonException)
            {
                // One malformed spec must not take the catalog down; its packages
                // simply remain unavailable to the download workbench.
            }
        }
        return new WeightCatalog(index);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? Strip(string remote, string prefix)
    {
        var value = remote.Replace('\\', '/').TrimStart('/');
        var cut = prefix.Replace('\\', '/').Trim('/');
        if (cut.Length == 0 || cut == ".") return value;
        return value.StartsWith(cut + "/", StringComparison.Ordinal) ? value[(cut.Length + 1)..] : null;
    }

    private static bool SafeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\0')) return false;
        return value.Replace('\\', '/').Split('/')
            .All(part => part.Length > 0 && part is not ("." or ".."));
    }
}

// ── Wire types ─────────────────────────────────────────────────────────────

internal sealed record WeightDownloadRequest(string PackageId, bool Overwrite = false);
internal sealed record WeightConfigRequest(int? ChunkBytes, long? MaxBytesPerSecond, long? AbortAfterBytes,
    long? AbortAtByte, int? MaxChunkBytes, int? Seed);
internal sealed record WeightTestRequest(long SizeBytes = 4L * 1024 * 1024, int Seed = 42,
    long AbortAtByte = 0, int MaxChunkBytes = 0, int? ChunkBytes = null, string? FileName = null,
    bool NoRanges = false);
internal sealed record WeightOptions(int ChunkBytes, long MaxBytesPerSecond, long AbortAfterBytes,
    long AbortAtByte, int MaxChunkBytes, int Seed);
internal sealed record PartMeta(string Url, string? ETag, long TotalBytes, string? LastModified);

internal sealed class WeightDownloadInterruptedException(string message) : Exception(message);

// ── Job state ──────────────────────────────────────────────────────────────

internal sealed class WeightJobFile
{
    internal required string Name { get; init; }
    internal required string FullPath { get; init; }
    internal required string Url { get; init; }
    internal long Downloaded;
    internal long Total;
    internal long ResumedFrom;
    internal int RangeRequests;
    internal int FullRestarts;
    internal bool Complete;
}

internal sealed class WeightJob
{
    internal string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    internal required string PackageId { get; init; }
    internal required string Source { get; init; }
    internal required long TestSize { get; init; }
    internal required int Seed { get; init; }
    internal required bool Overwrite { get; init; }
    internal List<WeightJobFile> Files { get; } = [];
    internal object Sync { get; } = new();
    internal CancellationTokenSource Cancellation { get; set; } = new();
    internal bool Paused { get; set; }
    internal bool Running { get; set; }
    internal string State { get; set; } = "queued";
    internal string? Message { get; set; }
    internal int ResumeCount { get; set; }
    internal int RangeRequests { get; set; }
    internal int FullRestarts { get; set; }
    internal double SpeedBytesPerSecond { get; set; }
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    internal DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    internal object Snapshot()
    {
        lock (Sync)
        {
            return new
            {
                jobId = Id,
                packageId = PackageId,
                source = Source,
                state = State,
                running = Running,
                message = Message,
                testSize = TestSize,
                seed = Seed,
                bytesDownloaded = Files.Sum(file => file.Downloaded),
                bytesTotal = Files.Sum(file => file.Total),
                speedBytesPerSecond = Math.Round(SpeedBytesPerSecond, 1),
                rangeRequests = RangeRequests,
                fullRestarts = FullRestarts,
                resumeCount = ResumeCount,
                startedAt = StartedAt,
                updatedAt = UpdatedAt,
                files = Files.Select(file => new
                {
                    name = file.Name,
                    path = file.FullPath,
                    url = file.Url,
                    bytesDownloaded = file.Downloaded,
                    bytesTotal = file.Total,
                    resumedFrom = file.ResumedFrom,
                    rangeRequests = file.RangeRequests,
                    fullRestarts = file.FullRestarts,
                    complete = file.Complete,
                }).ToArray(),
            };
        }
    }
}

// ── Resumable downloader ───────────────────────────────────────────────────
//
// Every file is streamed into `<target>.part`. A `<target>.part.meta` sidecar
// records the URL, ETag and total size, so a restart can prove the bytes on disk
// still belong to the same remote object before appending with
// `Range: bytes=<offset>-...`. A 200 (server ignored the range) or a changed
// ETag forces a clean restart rather than splicing incompatible bytes.

internal sealed class WeightDownloadService
{
    private const int DefaultChunkBytes = 1024 * 1024;
    private const long MaxTestBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly AudioCppWorkbench _workbench;
    private readonly ConcurrentDictionary<string, WeightJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _optionsLock = new();
    private WeightOptions _options = new(DefaultChunkBytes, 0, 0, 0, 0, 42);

    // The DI container only considers public constructors; keep this one public
    // even though the service type itself is internal to the workbench assembly.
    public WeightDownloadService(AudioCppWorkbench workbench)
    {
        _workbench = workbench;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    private WeightOptions CurrentOptions() { lock (_optionsLock) return _options; }

    internal object ListPackages()
    {
        var packages = WeightCatalog.Instance.All
            .OrderBy(package => package.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(package => new
            {
                id = package.Id,
                family = package.Family,
                kind = package.Kind,
                repo = package.Repo,
                revision = package.Revision,
                targetDirectory = package.TargetDirectory,
                files = package.Files.Select(file => file.RelativePath).ToArray(),
            }).ToArray();
        return new
        {
            count = packages.Length,
            endpoint = _workbench.HuggingFaceEndpoint,
            modelsDirectory = _workbench.ModelsDirectory,
            packages,
        };
    }

    internal object Start(WeightDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId)) throw new ArgumentException("packageId is required.");
        if (!WeightCatalog.Instance.TryGet(request.PackageId, out var package))
            throw new ArgumentException($"Unknown package '{request.PackageId}'. Use GET /api/weights/packages to list downloadable ids.");
        var job = BuildJob(package, request.Overwrite, _workbench.HuggingFaceEndpoint,
            _workbench.ModelsDirectory, "huggingface", 0, 0);
        return Launch(job, CurrentOptions());
    }

    /// <summary>
    /// One-call resume drill: downloads a deterministic, locally served blob of
    /// `SizeBytes` bytes through the very same HTTP Range path used for real
    /// weights. `AbortAtByte` makes the test source cut the TCP stream at an
    /// absolute file position, leaving a partial file for a subsequent resume.
    /// No external network and no multi-GB weights are needed.
    /// </summary>
    internal object StartTest(HttpRequest request, WeightTestRequest body)
    {
        if (body.SizeBytes < 1 || body.SizeBytes > MaxTestBytes)
            throw new ArgumentException($"sizeBytes must be between 1 and {MaxTestBytes} bytes.");
        var options = CurrentOptions();
        var seed = body.Seed;
        var fileName = string.IsNullOrWhiteSpace(body.FileName)
            ? $"weight-test-{body.SizeBytes}-{seed}.bin"
            : Path.GetFileName(body.FileName);
        if (fileName is "." or ".." || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            throw new ArgumentException("fileName must be a plain file name without a path.");
        var baseUrl = $"{request.Scheme}://{request.Host}/api/weights/test-source";

        // A dropped connection happens once per drill. Without a token the test
        // source would cut every request that crosses abortAt, including the
        // full-restart request, and the job could never finish.
        var query = new List<string>
        {
            $"size={body.SizeBytes}", $"seed={seed}", $"cutId={Guid.NewGuid():N}",
        };
        var abortAt = body.AbortAtByte > 0 ? body.AbortAtByte : options.AbortAtByte;
        var maxChunk = body.MaxChunkBytes > 0 ? body.MaxChunkBytes : options.MaxChunkBytes;
        if (abortAt > 0) query.Add($"abortAt={abortAt}");
        if (maxChunk > 0) query.Add($"maxChunk={maxChunk}");
        // Emulates a CDN that answers 200 with the whole object even though the
        // client asked for a byte range: the downloader must discard the partial
        // file and restart instead of splicing incompatible bytes.
        if (body.NoRanges) query.Add("noRanges=1");
        var url = $"{baseUrl}/test/resolve/main/{Uri.EscapeDataString(fileName)}?{string.Join('&', query)}";

        var runOptions = body.ChunkBytes is > 0
            ? options with { ChunkBytes = Math.Clamp(body.ChunkBytes.Value, 4 * 1024, 64 * 1024 * 1024) }
            : options;

        var job = new WeightJob
        {
            PackageId = $"test:{body.SizeBytes}",
            Source = "test",
            TestSize = body.SizeBytes,
            Seed = seed,
            Overwrite = false,
        };
        var root = Path.GetFullPath(Path.Combine(_workbench.ModelsDirectory, "_weights_test"));
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Test file path escapes the test directory.");
        job.Files.Add(new WeightJobFile { Name = fileName, FullPath = fullPath, Url = url });
        return Launch(job, runOptions);
    }

    private WeightJob BuildJob(WeightPackage package, bool overwrite, string endpoint,
        string modelsDirectory, string source, long testSize, int seed)
    {
        var root = Path.GetFullPath(modelsDirectory);
        var job = new WeightJob
        {
            PackageId = package.Id, Source = source, TestSize = testSize, Seed = seed, Overwrite = overwrite,
        };
        Directory.CreateDirectory(root);
        foreach (var file in package.Files)
        {
            var full = Path.GetFullPath(Path.Combine(root, package.TargetDirectory, file.RelativePath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Package '{package.Id}' declares a path outside the models directory: {file.RelativePath}");
            job.Files.Add(new WeightJobFile
            {
                Name = file.RelativePath,
                FullPath = full,
                Url = BuildUrl(package, file.RemotePath, endpoint),
            });
        }
        return job;
    }

    private static string BuildUrl(WeightPackage package, string remote, string endpoint)
    {
        var revision = Uri.EscapeDataString(package.Revision);
        var path = string.Join('/', remote.Replace('\\', '/').TrimStart('/')
            .Split('/').Select(Uri.EscapeDataString));
        var tail = package.Kind == "modelscope_snapshot"
            ? $"/models/{package.Repo}/resolve/{revision}/{path}"
            : $"/{package.Repo}/resolve/{revision}/{path}";
        return endpoint.TrimEnd('/') + tail;
    }

    private object Launch(WeightJob job, WeightOptions options)
    {
        _jobs[job.Id] = job;
        StartRun(job, options);
        return job.Snapshot();
    }

    private void StartRun(WeightJob job, WeightOptions? options = null)
    {
        CancellationTokenSource cancellation;
        lock (job.Sync)
        {
            if (job.Running) return;
            if (job.State == "completed") throw new ArgumentException($"Job '{job.Id}' already completed.");
            job.Running = true;
            job.Paused = false;
            job.Cancellation = new CancellationTokenSource();
            cancellation = job.Cancellation;
            job.State = "running";
            job.Message = job.ResumeCount == 0 ? "starting" : "resuming";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var runOptions = options ?? CurrentOptions();
        _ = Task.Run(async () =>
        {
            try { await RunAsync(job, runOptions, cancellation.Token); }
            catch (Exception exception) { Fail(job, exception.Message); }
        });
    }

    private async Task RunAsync(WeightJob job, WeightOptions options, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long writtenThisRun = 0;
        try
        {
            foreach (var file in job.Files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.FullPath)!);
                var partPath = file.FullPath + ".part";
                var metaPath = file.FullPath + ".part.meta";

                if (!job.Overwrite && File.Exists(file.FullPath))
                {
                    lock (job.Sync)
                    {
                        file.Downloaded = new FileInfo(file.FullPath).Length;
                        file.Total = file.Downloaded;
                        file.Complete = true;
                    }
                    continue;
                }
                if (job.Overwrite)
                {
                    TryDelete(file.FullPath);
                    TryDelete(partPath);
                    TryDelete(metaPath);
                }

                string? etag;
                long total;
                long offset;
                using (var probe = await SendAsync(file.Url, 0, 0, null, ct))
                {
                    if (probe.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                        throw new InvalidDataException($"Source returned {(int)probe.StatusCode} for {file.Url}");
                    total = TotalOf(probe);
                    if (total <= 0) throw new InvalidDataException($"Source did not report a total size for {file.Url}");
                    etag = probe.Headers.ETag?.Tag;
                    var lastModified = probe.Content.Headers.LastModified?.ToString("R");
                    var supportsRanges = probe.StatusCode == HttpStatusCode.PartialContent ||
                                         probe.Headers.AcceptRanges.Contains("bytes");
                    lock (job.Sync) { file.Total = total; job.UpdatedAt = DateTimeOffset.UtcNow; }

                    offset = ResumeOffset(partPath, metaPath, file.Url, etag, total, supportsRanges);
                    if (offset > 0)
                    {
                        lock (job.Sync)
                        {
                            file.ResumedFrom = offset;
                            file.Downloaded = offset;
                            job.ResumeCount = Math.Max(job.ResumeCount, 1);
                        }
                    }
                    WriteMeta(metaPath, new PartMeta(file.Url, etag, total, lastModified));
                }

                var position = offset;
                var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                if (position == 0) stream.SetLength(0);
                stream.Seek(position, SeekOrigin.Begin);
                try
                {
                    while (position < total)
                    {
                        await WaitWhilePaused(job, ct);
                        ct.ThrowIfCancellationRequested();
                        var chunk = Math.Max(1, options.ChunkBytes);
                        var to = Math.Min(position + chunk - 1, total - 1);
                        using var response = await SendAsync(file.Url, position, to,
                            position > 0 ? etag : null, ct);
                        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                            throw new InvalidDataException($"Source returned {(int)response.StatusCode} mid-download for {file.Url}");
                        lock (job.Sync)
                        {
                            file.RangeRequests++;
                            job.RangeRequests++;
                        }
                        if (response.StatusCode == HttpStatusCode.OK && position > 0)
                        {
                            // The server ignored If-Range/Range and sent the whole
                            // object. Splicing would corrupt the file: start over.
                            await stream.FlushAsync(ct);
                            stream.SetLength(0);
                            stream.Position = 0;
                            position = 0;
                            writtenThisRun = 0;
                            lock (job.Sync)
                            {
                                file.FullRestarts++;
                                job.FullRestarts++;
                                file.Downloaded = 0;
                            }
                        }
                        else if (response.StatusCode == HttpStatusCode.PartialContent &&
                                 response.Content.Headers.ContentRange?.From is long from && from != position)
                        {
                            throw new InvalidDataException(
                                $"Server returned a range starting at {from}, expected {position}; refusing to splice bytes.");
                        }

                        await using (var network = await response.Content.ReadAsStreamAsync(ct))
                        {
                            var buffer = new byte[64 * 1024];
                            int read;
                            while ((read = await network.ReadAsync(buffer, ct)) > 0)
                            {
                                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                                position += read;
                                writtenThisRun += read;
                                lock (job.Sync)
                                {
                                    file.Downloaded = position;
                                    job.SpeedBytesPerSecond = writtenThisRun / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                                    job.UpdatedAt = DateTimeOffset.UtcNow;
                                }
                            }
                        }
                        await stream.FlushAsync(ct);

                        if (options.MaxBytesPerSecond > 0)
                        {
                            var expectedSeconds = (double)writtenThisRun / options.MaxBytesPerSecond;
                            if (expectedSeconds > stopwatch.Elapsed.TotalSeconds)
                                await Task.Delay(TimeSpan.FromSeconds(expectedSeconds - stopwatch.Elapsed.TotalSeconds), ct);
                        }
                        if (options.AbortAfterBytes > 0 && writtenThisRun >= options.AbortAfterBytes)
                            throw new WeightDownloadInterruptedException(
                                $"Injected interrupt after {writtenThisRun} bytes; partial data kept at {partPath}");
                    }
                }
                finally
                {
                    try { await stream.FlushAsync(); } catch { /* the stream may already be faulted */ }
                    await stream.DisposeAsync();
                }

                var finalLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (finalLength != total)
                    throw new InvalidDataException($"Incomplete file: {finalLength}/{total} bytes at {partPath}; kept for resume");
                File.Move(partPath, file.FullPath, overwrite: true);
                TryDelete(metaPath);
                lock (job.Sync)
                {
                    file.Complete = true;
                    file.Downloaded = total;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            lock (job.Sync)
            {
                job.State = "completed";
                job.Message = "completed";
                job.SpeedBytesPerSecond = 0;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (job.Sync)
            {
                if (job.State is not ("cancelled" or "interrupted"))
                {
                    job.State = "paused";
                    job.Message = "stopped; partial data kept for resume";
                }
                job.SpeedBytesPerSecond = 0;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception) when (exception is WeightDownloadInterruptedException or InvalidDataException
                                              or IOException or HttpRequestException)
        {
            lock (job.Sync)
            {
                job.State = exception is WeightDownloadInterruptedException ? "interrupted" : "failed";
                job.Message = exception.Message;
                job.SpeedBytesPerSecond = 0;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            lock (job.Sync) { job.Running = false; job.UpdatedAt = DateTimeOffset.UtcNow; }
        }
    }

    private static long ResumeOffset(string partPath, string metaPath, string url, string? etag,
        long total, bool supportsRanges)
    {
        if (!supportsRanges || !File.Exists(partPath)) return 0;
        var length = new FileInfo(partPath).Length;
        if (length <= 0 || length > total) { TryDelete(partPath); return 0; }
        var saved = ReadMeta(metaPath);
        if (saved is not null)
        {
            var sameObject = saved.Url == url && saved.TotalBytes == total &&
                (string.IsNullOrEmpty(saved.ETag) || string.IsNullOrEmpty(etag) || saved.ETag == etag);
            if (!sameObject) { TryDelete(partPath); return 0; }
        }
        return length;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, long from, long to, string? ifRange, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrEmpty(ifRange)) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static long TotalOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentRange?.Length is long length && length > 0
            ? length : response.Content.Headers.ContentLength ?? 0;

    private static async Task WaitWhilePaused(WeightJob job, CancellationToken ct)
    {
        while (true)
        {
            lock (job.Sync) { if (!job.Paused) return; }
            await Task.Delay(100, ct);
        }
    }

    private static void WriteMeta(string path, PartMeta meta) =>
        File.WriteAllText(path, JsonSerializer.Serialize(meta));

    private static PartMeta? ReadMeta(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(path)) : null; }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
    }

    private void Fail(WeightJob job, string message)
    {
        lock (job.Sync)
        {
            job.State = "failed";
            job.Message = message;
            job.Running = false;
            job.SpeedBytesPerSecond = 0;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    internal object ListJobs() => new { jobs = _jobs.Values.OrderBy(j => j.StartedAt).Select(j => j.Snapshot()).ToArray() };
    internal object GetJob(string id) => Find(id).Snapshot();

    internal object Pause(string id)
    {
        var job = Find(id);
        lock (job.Sync)
        {
            job.Paused = true;
            if (job.State == "running") { job.State = "paused"; job.Message = "paused at chunk boundary"; }
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return job.Snapshot();
    }

    internal object Resume(string id)
    {
        var job = Find(id);
        // Count the resume attempt here so the immediate snapshot already reports
        // "resuming" (RunAsync only learns the on-disk offset after its probe).
        lock (job.Sync)
        {
            if (job.State == "completed") throw new ArgumentException($"Job '{job.Id}' already completed.");
            // A pause taken while a chunk is still in flight leaves the worker
            // running; clear the flag here so it can leave WaitWhilePaused even
            // when StartRun below sees Running == true and does not spawn a new
            // worker. Otherwise a fast pause -> resume deadlocks the job.
            job.Paused = false;
            if (!job.Running)
            {
                job.ResumeCount++;
            }
            else if (job.State == "paused")
            {
                job.State = "running";
                job.Message = "resuming";
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        StartRun(job);
        return job.Snapshot();
    }

    internal object Stop(string id, bool interrupt)
    {
        var job = Find(id);
        lock (job.Sync)
        {
            job.State = interrupt ? "interrupted" : "cancelled";
            job.Message = interrupt ? "interrupted for resume test; partial data kept" : "cancelled; partial data kept";
            job.Cancellation.Cancel();
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return job.Snapshot();
    }

    private WeightJob Find(string id) =>
        _jobs.TryGetValue(id, out var job) ? job : throw new ArgumentException($"Unknown download job '{id}'.");

    internal object GetConfig()
    {
        var options = CurrentOptions();
        return new
        {
            chunkBytes = options.ChunkBytes,
            maxBytesPerSecond = options.MaxBytesPerSecond,
            abortAfterBytes = options.AbortAfterBytes,
            abortAtByte = options.AbortAtByte,
            maxChunkBytes = options.MaxChunkBytes,
            seed = options.Seed,
            endpoint = _workbench.HuggingFaceEndpoint,
            modelsDirectory = _workbench.ModelsDirectory,
        };
    }

    internal object SetConfig(WeightConfigRequest request)
    {
        lock (_optionsLock)
        {
            _options = _options with
            {
                ChunkBytes = request.ChunkBytes is > 0
                    ? Math.Clamp(request.ChunkBytes.Value, 4 * 1024, 64 * 1024 * 1024) : _options.ChunkBytes,
                MaxBytesPerSecond = request.MaxBytesPerSecond is >= 0 ? request.MaxBytesPerSecond.Value : _options.MaxBytesPerSecond,
                AbortAfterBytes = request.AbortAfterBytes is >= 0 ? request.AbortAfterBytes.Value : _options.AbortAfterBytes,
                AbortAtByte = request.AbortAtByte is >= 0 ? request.AbortAtByte.Value : _options.AbortAtByte,
                MaxChunkBytes = request.MaxChunkBytes is >= 0
                    ? Math.Clamp(request.MaxChunkBytes.Value, 0, 64 * 1024 * 1024) : _options.MaxChunkBytes,
                Seed = request.Seed ?? _options.Seed,
            };
        }
        return GetConfig();
    }

    /// <summary>Byte-for-byte check of a finished test download against the
    /// deterministic pattern, so a bad splice cannot look like a successful resume.</summary>
    internal object Verify(string path, long size, int seed)
    {
        var root = Path.GetFullPath(Path.Combine(_workbench.ModelsDirectory, "_weights_test"));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must be a file inside the weights test directory.");
        if (!File.Exists(full)) return new { valid = false, reason = "missing", bytes = 0L, expected = size };
        var length = new FileInfo(full).Length;
        if (size > 0 && length != size)
            return new { valid = false, reason = "size mismatch", bytes = length, expected = size };

        var buffer = new byte[64 * 1024];
        long position = 0;
        using var stream = File.OpenRead(full);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != WeightTestSource.ByteAt(position + i, seed))
                    return new { valid = false, reason = "byte mismatch", bytes = length, expected = size,
                        firstMismatch = position + i };
            }
            position += read;
        }
        return new { valid = true, reason = (string?)null, bytes = position, expected = size, firstMismatch = (long?)null };
    }

    internal object CleanTests()
    {
        WeightTestSource.Reset();
        var root = Path.GetFullPath(Path.Combine(_workbench.ModelsDirectory, "_weights_test"));
        if (!Directory.Exists(root)) return new { removed = 0 };
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(full); removed++; } catch (IOException) { /* in use by a running job */ }
        }
        return new { removed };
    }
}

// ── Built-in HTTP test source ──────────────────────────────────────────────
//
// A deterministic blob served with real 206/Content-Range/Accept-Ranges/ETag
// semantics. `maxChunk` caps each response so a client must issue several range
// requests; `abortAt` cuts the stream at an absolute byte position to emulate a
// dropped connection. `noRanges=1` emulates a CDN that answers a *resume* request
// with a full 200 object: the probe and first chunk still use 206 so a partial
// file exists before the client is forced to restart from byte zero.

internal static class WeightTestSource
{
    // One cut per drill token. A resume or a full restart must see a healthy
    // server, otherwise the safety-restart branches can never be exercised.
    private static readonly ConcurrentDictionary<string, byte> CutTokens = new(StringComparer.Ordinal);

    internal static void Reset() => CutTokens.Clear();

    internal static byte ByteAt(long position, int seed) =>
        (byte)((position * 31L + seed * 17L + 7L) & 0xFF);

    private static bool ClaimCut(string token, bool armed)
    {
        if (!armed) return false;
        return string.IsNullOrEmpty(token) || CutTokens.TryAdd(token, 0);
    }

    internal static async Task ServeAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var size = QueryLong(query, "size", 4L * 1024 * 1024);
        if (size < 1 || size > 512L * 1024 * 1024)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("size must be between 1 and 512 MiB");
            return;
        }
        var seed = (int)QueryLong(query, "seed", 42);
        var abortAt = QueryLong(query, "abortAt", 0);
        var maxChunk = QueryLong(query, "maxChunk", 0);
        var noRanges = query["noRanges"] == "1";
        var etag = $"\"wtest-{size}-{seed}\"";

        var response = context.Response;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["ETag"] = etag;
        response.ContentType = "application/octet-stream";
        if (context.Request.Headers["If-None-Match"].ToString() == etag)
        {
            response.StatusCode = 304;
            return;
        }

        long start = 0, end = size - 1;
        var partial = false;
        var rangeText = context.Request.Headers["Range"].ToString();
        // Parse Range even in noRanges mode: only a *resume* (start > 0) may be
        // degraded to a full 200 below. Skipping parsing here would make the
        // probe and the first chunk return 200 too, which consumes the one-shot
        // cut and lets the job finish in a single run, so the safety-restart
        // branch of the downloader would never be exercised.
        if (!string.IsNullOrEmpty(rangeText) &&
            RangeHeaderValue.TryParse(rangeText, out var range) && range.Ranges.Count > 0)
        {
            var item = range.Ranges.First();
            if (item.From.HasValue)
            {
                start = item.From.Value;
                end = item.To.HasValue ? Math.Min(item.To.Value, size - 1) : size - 1;
            }
            else if (item.To.HasValue)
            {
                start = Math.Max(0, size - item.To.Value);
                end = size - 1;
            }
            if (start >= size || start > end)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{size}";
                return;
            }
            partial = true;
        }
        var ifRange = context.Request.Headers["If-Range"].ToString();
        if (partial && !string.IsNullOrEmpty(ifRange) && ifRange != etag)
        {
            start = 0;
            end = size - 1;
            partial = false;
        }
        var fullObject = false;
        if (noRanges && start > 0)
        {
            start = 0;
            end = size - 1;
            partial = false;
            fullObject = true;
        }
        if (!fullObject && maxChunk > 0 && end - start + 1 > maxChunk) end = start + maxChunk - 1;

        // A cut is armed only for the request that *crosses* abortAt. The client
        // declares the full range in Content-Length but receives fewer bytes, so
        // HttpClient reports a premature end and the .part file is left exactly
        // abortAt bytes long. A resume starts at >= abortAt and is never cut again.
        var cut = ClaimCut(query["cutId"].ToString(),
            !fullObject && abortAt > 0 && start < abortAt && end >= abortAt);
        var writeEnd = cut ? abortAt - 1 : end;
        response.StatusCode = partial ? 206 : 200;
        if (partial) response.Headers["Content-Range"] = $"bytes {start}-{end}/{size}";
        response.ContentLength = end - start + 1;
        if (HttpMethods.IsHead(context.Request.Method)) return;

        var buffer = new byte[32 * 1024];
        for (var position = start; position <= writeEnd;)
        {
            var count = (int)Math.Min(buffer.Length, writeEnd - position + 1);
            for (var i = 0; i < count; i++) buffer[i] = ByteAt(position + i, seed);
            await response.Body.WriteAsync(buffer.AsMemory(0, count));
            position += count;
        }
        if (cut) { await CutAsync(context); return; }
        await response.Body.FlushAsync();
    }

    private static async Task CutAsync(HttpContext context)
    {
        try { await context.Response.Body.FlushAsync(); } catch { /* connection already gone */ }
        context.Abort();
    }

    private static long QueryLong(IQueryCollection query, string name, long fallback) =>
        long.TryParse(query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
