using System.Text.Json;
using AudioCpp.NET;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebRoot("wwwroot");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 32 * 1024 * 1024);
builder.Services.AddSingleton<AudioCppWorkbench>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (AudioCppWorkbench workbench) => workbench.GetConfiguration());
app.MapPost("/api/config", (WorkbenchConfiguration configuration, AudioCppWorkbench workbench) =>
    workbench.Configure(configuration));
app.MapGet("/api/packages", (AudioCppWorkbench workbench) => workbench.ListPackagesAsync());
app.MapGet("/api/models", (AudioCppWorkbench workbench) => workbench.ListModelDirectories());
app.MapPost("/api/packages/download", (DownloadRequest request, AudioCppWorkbench workbench) =>
    workbench.DownloadAsync(request));
app.MapPost("/api/verify", (VerifyRequest request, AudioCppWorkbench workbench) =>
    workbench.VerifyModelAsync(request));
app.MapPost("/api/asr", (HttpRequest request, AudioCppWorkbench workbench) => workbench.TranscribeAsync(request));
app.MapPost("/api/tts", (HttpRequest request, AudioCppWorkbench workbench) => workbench.SynthesizeAsync(request));
app.MapGet("/api/audio/{fileName}", (string fileName, AudioCppWorkbench workbench) => workbench.GetAudio(fileName));

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error is ArgumentException or InvalidDataException or AudioCppModelIncompleteException ? 400 : 500;
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Unknown server error." });
}));

app.Run();

internal sealed record WorkbenchConfiguration(string NativePath, string ModelsDirectory, string HuggingFaceEndpoint);
internal sealed record DownloadRequest(string PackageId, bool Overwrite = false);
internal sealed record VerifyRequest(string Path);

internal sealed class AudioCppWorkbench
{
    private readonly SemaphoreSlim _nativeLock = new(1, 1);
    private readonly string _artifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "artifacts", "web"));
    private WorkbenchConfiguration _configuration = new(
        Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_PATH") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_MODELS_DIR") ?? ModelDirectory.Default,
        Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? "https://hf-mirror.com");

    internal WorkbenchConfiguration GetConfiguration() => _configuration;

    internal WorkbenchConfiguration Configure(WorkbenchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = string.IsNullOrWhiteSpace(configuration.HuggingFaceEndpoint)
            ? _configuration.HuggingFaceEndpoint
            : NormalizeEndpoint(configuration.HuggingFaceEndpoint);
        var models = string.IsNullOrWhiteSpace(configuration.ModelsDirectory)
            ? _configuration.ModelsDirectory
            : Path.GetFullPath(configuration.ModelsDirectory);
        _configuration = new WorkbenchConfiguration(configuration.NativePath?.Trim() ?? "", models, endpoint);
        return _configuration;
    }

    internal async Task<object> ListPackagesAsync() => await Locked(() =>
    {
        using var runtime = CreateRuntime();
        return (object)new { build = runtime.BuildInfo, packages = runtime.ListPackages() };
    });

    internal IReadOnlyList<object> ListModelDirectories()
    {
        var result = new List<object>();
        if (!Directory.Exists(_configuration.ModelsDirectory)) return result;
        foreach (var dir in Directory.EnumerateDirectories(_configuration.ModelsDirectory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var report = ModelValidator.Validate(dir);
            result.Add(new
            {
                name = Path.GetFileName(dir),
                path = Path.GetFullPath(dir),
                models = Directory.EnumerateFiles(dir, "*.gguf").Select(Path.GetFileName).ToArray(),
                packageId = report.PackageId,
                manifest = report.ManifestPresent,
                complete = report.Complete,
                issues = ModelValidator.FormatIssues(report)
            });
        }
        return result;
    }

    internal object VerifyModelAsync(VerifyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("Path is required.");
        var full = Path.GetFullPath(request.Path);
        if (!Directory.Exists(full)) throw new ArgumentException($"Model directory does not exist: {full}");
        var report = ModelValidator.Validate(full);
        return (object)new
        {
            path = report.Directory,
            packageId = report.PackageId,
            manifest = report.ManifestPresent,
            complete = report.Complete,
            checkedFiles = report.CheckedFiles,
            checkedBytes = report.CheckedBytes,
            issues = report.Issues.Select(issue => new { kind = issue.Kind, path = issue.Path, detail = issue.Detail }).ToArray()
        };
    }

    internal async Task<object> DownloadAsync(DownloadRequest request) => await Locked(() =>
    {
        if (string.IsNullOrWhiteSpace(request.PackageId)) throw new ArgumentException("Package ID is required.");
        ConfigureEndpoint();
        using var runtime = CreateRuntime();
        var progress = "Starting download...";
        var result = runtime.InstallPackage(request.PackageId, _configuration.ModelsDirectory, request.Overwrite,
            (downloaded, total, message) => progress = total == 0
                ? $"{request.PackageId}: {downloaded} bytes {message}".TrimEnd()
                : $"{request.PackageId}: {downloaded}/{total} bytes {message}".TrimEnd());
        return (object)new { message = result, progress };
    });

    internal async Task<object> TranscribeAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var upload = form.Files.GetFile("audio") ?? throw new ArgumentException("A PCM16 WAV file is required.");
        var audio = ReadWave(upload);
        var modelPath = Value(form, "modelPath", ResolveModelPath("citrinet_asr", "Citrinet-ASR-GGUF"));
        var family = Value(form, "family", "citrinet_asr");
        var threads = Integer(form, "threads");
        using var runtime = CreateRuntime();
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath, FamilyHint = family, Threads = threads });
        var text = model.Transcribe(new AsrRequest { Audio = audio.Samples, SampleRate = audio.SampleRate,
            Channels = audio.Channels, Options = Options(form["options"]) });
        return (object)new { text, audio.SampleRate, audio.Channels, samples = audio.Samples.Length };
    });

    internal async Task<object> SynthesizeAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var text = Value(form, "text", "");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.");
        var referenceFile = form.Files.GetFile("voiceRef");
        var reference = referenceFile is null ? null : ReadWave(referenceFile);
        var modelPath = Value(form, "modelPath", ResolveModelPath("qwen3_tts", "Qwen3-TTS-12Hz-0.6B-Base-GGUF"));
        var family = Value(form, "family", "qwen3_tts");
        var options = new Dictionary<string, string>(Options(form["options"]) ?? new Dictionary<string, string>());
        var referenceText = Value(form, "referenceText", "");
        if (reference is not null && referenceText.Length > 0) options["reference_text"] = referenceText;
        using var runtime = CreateRuntime();
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath, FamilyHint = family, Threads = Integer(form, "threads") });
        var audio = model.Synthesize(new TtsRequest { Text = text, Task = "tts",
            ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
            ReferenceSampleRate = reference?.SampleRate ?? 0, Options = options });
        Directory.CreateDirectory(_artifactDirectory);
        var fileName = $"tts-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav";
        WaveFile.Write(Path.Combine(_artifactDirectory, fileName), audio);
        return (object)new { audioUrl = $"/api/audio/{fileName}", fileName, audio.SampleRate, audio.Channels,
            samples = audio.Samples.Length, duration = (double)audio.Samples.Length / audio.SampleRate / audio.Channels };
    });

    internal IResult GetAudio(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        var path = Path.Combine(_artifactDirectory, fileName);
        return File.Exists(path) ? Results.File(path, "audio/wav", fileName, enableRangeProcessing: true) : Results.NotFound();
    }

    private AudioCppRuntime CreateRuntime() => AudioCppRuntime.Create(new AudioCppRuntimeOptions
        { NativeLibraryPath = string.IsNullOrWhiteSpace(_configuration.NativePath) ? null : _configuration.NativePath });
    private string ResolveModelPath(string family, string standardDirectory)
    {
        var standard = Path.Combine(_configuration.ModelsDirectory, standardDirectory);
        if (Directory.Exists(standard)) return standard;
        foreach (var packageId in ModelValidator.FindPackageIds(_configuration.ModelsDirectory))
        {
            if (ModelValidator.DeriveFamily(packageId) != family) continue;
            var found = ModelValidator.FindPackageDirectory(_configuration.ModelsDirectory, packageId);
            if (found is not null) return found;
        }
        return standard;
    }
    private void ConfigureEndpoint() => Environment.SetEnvironmentVariable("AUDIOCPP_HF_BASE_URL", _configuration.HuggingFaceEndpoint);
    private static AudioBuffer ReadWave(IFormFile file)
    {
        if (file.Length == 0 || file.Length > 32 * 1024 * 1024) throw new ArgumentException("WAV must be between 1 byte and 32 MiB.");
        using var stream = file.OpenReadStream();
        return WaveFile.Read(stream);
    }
    private static string Value(IFormCollection form, string name, string fallback) =>
        string.IsNullOrWhiteSpace(form[name]) ? fallback : form[name].ToString().Trim();
    private static int Integer(IFormCollection form, string name) => int.TryParse(form[name], out var value) && value >= 0 ? value : 0;
    private static IReadOnlyDictionary<string, string>? Options(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? throw new ArgumentException("Options must be a JSON object.");
    }
    private static string NormalizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Hugging Face endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        return value.TrimEnd('/');
    }
    private async Task<T> Locked<T>(Func<T> action)
    {
        await _nativeLock.WaitAsync();
        try { return action(); } finally { _nativeLock.Release(); }
    }
    private async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await _nativeLock.WaitAsync();
        try { return await action(); } finally { _nativeLock.Release(); }
    }
}