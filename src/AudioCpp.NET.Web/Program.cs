using System.Text.Json;
using AudioCpp.NET;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 32 * 1024 * 1024);
builder.Services.AddSingleton<AudioCppWorkbench>();

// A published build launched from an arbitrary directory still has to find its
// static assets: fall back to the app base directory when the content root has no wwwroot.
if (!Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")) &&
    Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
{
    builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
}

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (AudioCppWorkbench workbench) => workbench.GetConfiguration());
app.MapPost("/api/config", (WorkbenchConfiguration configuration, AudioCppWorkbench workbench) =>
    workbench.Configure(configuration));
app.MapGet("/api/build", (AudioCppWorkbench workbench) => workbench.DescribeBuild());
app.MapGet("/api/tasks", (AudioCppWorkbench workbench) => workbench.ListTasks());
app.MapGet("/api/loaders", (AudioCppWorkbench workbench) => workbench.ListLoaders());
app.MapGet("/api/packages", (AudioCppWorkbench workbench) => workbench.ListPackagesAsync());
app.MapGet("/api/models", (AudioCppWorkbench workbench) => workbench.ListModelDirectories());
app.MapPost("/api/packages/download", (DownloadRequest request, AudioCppWorkbench workbench) =>
    workbench.DownloadAsync(request));
app.MapPost("/api/verify", (VerifyRequest request, AudioCppWorkbench workbench) =>
    workbench.VerifyModelAsync(request));
app.MapPost("/api/run", (HttpRequest request, AudioCppWorkbench workbench) => workbench.RunAsync(request));
app.MapPost("/api/asr", (HttpRequest request, AudioCppWorkbench workbench) => workbench.TranscribeAsync(request));
app.MapPost("/api/stream", (HttpRequest request, AudioCppWorkbench workbench) => workbench.StreamAsync(request));
app.MapGet("/api/stream/policy", (HttpRequest request, AudioCppWorkbench workbench) => workbench.ProbeStreamAsync(request));
app.MapPost("/api/tts", (HttpRequest request, AudioCppWorkbench workbench) => workbench.SynthesizeAsync(request));
app.MapGet("/api/audio/{fileName}", (string fileName, AudioCppWorkbench workbench) => workbench.GetAudio(fileName));

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error is ArgumentException or InvalidDataException or AudioCppModelIncompleteException or NotSupportedException ? 400 : 500;
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Unknown server error." });
}));

app.Run();

internal sealed record WorkbenchConfiguration(string NativePath, string ModelsDirectory, string HuggingFaceEndpoint);
internal sealed record DownloadRequest(string PackageId, bool Overwrite = false);
internal sealed record VerifyRequest(string Path);

internal sealed class AudioCppWorkbench
{
    /// <summary>Artifact payloads are echoed back to the browser; anything larger is
    /// reported by size only so a MIDI or embedding dump cannot stall the UI.</summary>
    private const int MaxArtifactHexBytes = 64 * 1024;

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

    /// <summary>ABI version plus the capability bits, so the UI can hide decks the
    /// loaded shim cannot serve instead of failing on the first click.</summary>
    internal async Task<object> DescribeBuild() => await Locked(() =>
    {
        using var runtime = CreateRuntime();
        var build = runtime.BuildInfo;
        var capabilities = build.Capabilities;
        return (object)new
        {
            build.AbiMajor,
            build.AbiMinor,
            build.ShimVersion,
            build.AudioCppCommit,
            build.Backend,
            capabilities,
            flags = CapabilityFlags(capabilities),
        };
    });

    internal async Task<object> ListTasks() => await Locked(() =>
    {
        using var runtime = CreateRuntime();
        return (object)new
        {
            build = runtime.BuildInfo,
            tasks = runtime.ListTaskKinds().Select(task => new
            {
                task.Task,
                task.Input,
                task.TypicalOutputs,
                task.Aliases,
                tokens = task.AllTokens,
            }).ToArray(),
        };
    });

    internal async Task<object> ListLoaders() => await Locked(() =>
    {
        using var runtime = CreateRuntime();
        return (object)new
        {
            build = runtime.BuildInfo,
            loaders = runtime.ListLoaders().Select(loader => new
            {
                loader.Family,
                loader.InstructionsPolicy,
                loader.Languages,
                loader.SupportsSpeakerReference,
                loader.SupportsStyleCondition,
                loader.SupportsTimestamps,
                tasks = loader.Tasks.Select(task => new
                {
                    task.Task,
                    canonical = AudioCppTaskKinds.Normalize(task.Task),
                    task.Modes,
                    streaming = task.Modes.Contains("streaming", StringComparer.OrdinalIgnoreCase),
                }).ToArray(),
            }).ToArray(),
        };
    });

    internal async Task<object> ListPackagesAsync() => await Locked(() =>
    {
        using var runtime = CreateRuntime();
        // AUDIOCPP_CAP_MODEL_MANAGER is only advertised when the native build was
        // configured with AUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON; without it the
        // catalog/download endpoints would fail with a hard error.
        var enabled = (runtime.BuildInfo.Capabilities & AudioCppCapabilities.ModelManager) != 0;
        return (object)new
        {
            build = runtime.BuildInfo,
            modelManager = enabled,
            message = enabled ? null : "model manager is not enabled in this native build; rebuild with -DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON to enable package listing and download",
            packages = enabled ? runtime.ListPackages() : [],
        };
    });

    internal async Task<object> ListModelDirectories() => await Locked(() =>
    {
        var result = new List<object>();
        if (!Directory.Exists(_configuration.ModelsDirectory)) return (object)result;
        var families = LoaderFamilies();
        foreach (var dir in Directory.EnumerateDirectories(_configuration.ModelsDirectory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var report = ModelValidator.Validate(dir);
            var metadata = ModelMetadata.Resolve(report.PackageId);
            var family = report.PackageId is null ? null : ModelValidator.DeriveFamily(report.PackageId, families);
            result.Add(new
            {
                name = Path.GetFileName(dir),
                path = Path.GetFullPath(dir),
                models = Directory.EnumerateFiles(dir, "*.gguf").Select(Path.GetFileName).ToArray(),
                packageId = report.PackageId,
                family = family ?? metadata.Family,
                metadata.Task,
                metadata.Languages,
                metadata.Tasks,
                metadata.CanonicalTasks,
                manifest = report.ManifestPresent,
                complete = report.Complete,
                issues = ModelValidator.FormatIssues(report)
            });
        }
        return (object)result;
    });

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
        if ((runtime.BuildInfo.Capabilities & AudioCppCapabilities.ModelManager) == 0)
            throw new NotSupportedException(
                "model manager is not enabled in this native build; rebuild with -DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON to enable downloads");
        var progress = "Starting download...";
        var result = runtime.InstallPackage(request.PackageId, _configuration.ModelsDirectory, request.Overwrite,
            (downloaded, total, message) => progress = total == 0
                ? $"{request.PackageId}: {downloaded} bytes {message}".TrimEnd()
                : $"{request.PackageId}: {downloaded}/{total} bytes {message}".TrimEnd());
        return (object)new { message = result, progress };
    });

    /// <summary>
    /// Generic structured run: any task the native shim can dispatch, with optional
    /// text, audio, voice reference, style conditions and input artifacts. Audio
    /// outputs are written as WAV files and returned as URLs; every other TaskResult
    /// channel is returned as JSON.
    /// </summary>
    internal async Task<object> RunAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var modelPath = Value(form, "modelPath", "");
        if (modelPath.Length == 0) throw new ArgumentException("modelPath is required.");
        var audioFile = form.Files.GetFile("audio");
        var audio = audioFile is null ? null : ReadWave(audioFile);
        var referenceFile = form.Files.GetFile("voiceRef");
        var reference = referenceFile is null ? null : ReadWave(referenceFile);
        var task = Value(form, "task", "");
        var text = Value(form, "text", "");
        if (text.Length == 0 && audio is null)
            throw new ArgumentException("Supply text, an audio file, or both.");

        using var runtime = CreateRuntime();
        using var model = runtime.LoadModel(new AudioCppModelOptions
        {
            ModelPath = modelPath,
            FamilyHint = Optional(form, "family"),
            Threads = Integer(form, "threads"),
        });
        var options = new Dictionary<string, string>(Options(form["options"]) ?? new Dictionary<string, string>());
        var referenceText = Value(form, "referenceText", "");
        if (reference is not null && referenceText.Length > 0) options["reference_text"] = referenceText;

        var runRequest = new AudioCppRunRequest
        {
            Task = task.Length == 0 ? null : task,
            Text = text.Length == 0 ? null : text,
            TextLanguage = Optional(form, "textLanguage"),
            Audio = audio?.Samples ?? ReadOnlyMemory<float>.Empty,
            SampleRate = audio?.SampleRate ?? 0,
            Channels = audio?.Channels ?? 1,
            VoiceId = Optional(form, "voiceId"),
            ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
            ReferenceSampleRate = reference?.SampleRate ?? 0,
            Style = ReadStyle(form),
            Artifacts = ReadArtifacts(form["artifacts"]),
            Options = options.Count == 0 ? null : options,
        };
        var result = model.Run(runRequest);
        return DescribeResult(AudioCppTaskKinds.Normalize(task) ?? task, result);
    });

    internal async Task<object> TranscribeAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var upload = form.Files.GetFile("audio") ?? throw new ArgumentException("A PCM16 WAV file is required.");
        var audio = ReadWave(upload);
        using var runtime = CreateRuntime();
        var task = Value(form, "task", "asr");
        var modelPath = Value(form, "modelPath", "");
        if (modelPath.Length == 0) modelPath = ResolveModelPath(runtime, "citrinet_asr", "Citrinet-ASR-GGUF");
        using var model = runtime.LoadModel(new AudioCppModelOptions
        {
            ModelPath = modelPath,
            FamilyHint = Optional(form, "family"),
            Threads = Integer(form, "threads"),
        });
        var structured = model.Run(new AudioCppRunRequest
        {
            Task = task,
            Text = Optional(form, "text"),
            TextLanguage = Optional(form, "textLanguage"),
            Audio = audio.Samples,
            SampleRate = audio.SampleRate,
            Channels = audio.Channels,
            Style = ReadStyle(form),
            Artifacts = ReadArtifacts(form["artifacts"]),
            Options = Options(form["options"]),
        });
        return new
        {
            task = structured.Task ?? AudioCppTaskKinds.Normalize(task) ?? task,
            text = structured.Text,
            structured.TextLanguage,
            audio.SampleRate,
            audio.Channels,
            samples = audio.Samples.Length,
            structuredResult = true,
            segments = structured.SpeechSegments.Select(ParseSegment).ToArray(),
            words = structured.WordTimestamps.Select(word => new
            {
                startSample = word.Span.StartSample,
                endSample = word.Span.EndSample,
                confidence = word.Confidence,
                word = word.Word
            }).ToArray(),
            turns = structured.SpeakerTurns.Select(ParseTurn).ToArray(),
            artifacts = structured.Artifacts.Select(ParseArtifact).ToArray(),
            artifact = structured.Artifact is null ? null : ParseArtifact(structured.Artifact),
        };
    });

    internal async Task<object> StreamAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var upload = form.Files.GetFile("audio") ?? throw new ArgumentException("A PCM16 WAV file is required.");
        var audio = ReadWave(upload);
        var family = Optional(form, "family");
        var task = Value(form, "task", "vad");
        var modelPath = Value(form, "modelPath", "");
        if (modelPath.Length == 0)
        {
            var standard = Path.Combine(_configuration.ModelsDirectory, "Silero-VAD");
            if (!Directory.Exists(standard))
                throw new ArgumentException("Streaming probes need an explicit modelPath; no Silero-VAD package is installed.");
            modelPath = standard;
        }
        var chunkMilliseconds = Integer(form, "chunkMs");
        if (chunkMilliseconds <= 0) chunkMilliseconds = AudioCppStreaming.DefaultChunkMilliseconds;
        using var runtime = CreateRuntime();
        using var model = runtime.LoadModel(new AudioCppModelOptions
        {
            ModelPath = modelPath,
            FamilyHint = family,
            Threads = Integer(form, "threads"),
        });
        var streamOptions = new AudioCppStreamingOptions
        {
            Task = task,
            SampleRate = audio.SampleRate,
            Channels = audio.Channels,
            ChunkMilliseconds = chunkMilliseconds,
            Text = Optional(form, "text"),
            TextLanguage = Optional(form, "textLanguage"),
            Style = ReadStyle(form),
            Artifacts = ReadArtifacts(form["artifacts"]),
            Options = Options(form["options"]),
        };
        var report = AudioCppStreaming.Run(model, audio.Samples, streamOptions);
        return (object)new
        {
            family = report.Info.Family,
            task = report.Info.Task,
            policy = new
            {
                input = report.Info.Policy.Input,
                output = report.Info.Policy.Output,
                preferredChunkSamples = report.Info.Policy.PreferredChunkSamples,
                preferredChunkSeconds = report.Info.Policy.PreferredChunkSeconds,
            },
            chunkSamples = report.ChunkSamples,
            chunks = report.Chunks.Count,
            eventCount = report.Events.Count,
            contentEventCount = report.ContentEvents.Count,
            paddedTailSamples = report.PaddedTailSamples,
            audio.SampleRate,
            audio.Channels,
            samples = audio.Samples.Length,
            text = report.Result.Text,
            events = report.ContentEvents.Select(StreamEvent).ToArray(),
            segments = report.Result.SpeechSegments.Select(ParseSegment).ToArray(),
            words = report.Result.WordTimestamps.Select(word => new
            {
                startSample = word.Span.StartSample,
                endSample = word.Span.EndSample,
                confidence = word.Confidence,
                word = word.Word
            }).ToArray(),
            turns = report.Result.SpeakerTurns.Select(ParseTurn).ToArray(),
            artifacts = report.Result.Artifacts.Select(ParseArtifact).ToArray(),
        };
    });

    internal async Task<object> ProbeStreamAsync(HttpRequest request) => await Locked(() =>
    {
        var query = request.Query;
        var family = Query(query, "family", null);
        var task = Query(query, "task", "vad") ?? "vad";
        var modelPath = Query(query, "modelPath", "");
        if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentException("modelPath is required to probe a streaming policy.");
        if (!Directory.Exists(modelPath)) throw new ArgumentException($"Model directory does not exist: {Path.GetFullPath(modelPath)}");
        using var runtime = CreateRuntime();
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath, FamilyHint = family });
        using var session = model.StartStreaming(task);
        return (object)new
        {
            family = session.Info.Family,
            task = session.Info.Task,
            input = session.Info.Policy.Input,
            output = session.Info.Policy.Output,
            preferredChunkSamples = session.Info.Policy.PreferredChunkSamples,
            preferredChunkSeconds = session.Info.Policy.PreferredChunkSeconds,
        };
    });

    internal async Task<object> SynthesizeAsync(HttpRequest request) => await Locked(async () =>
    {
        var form = await request.ReadFormAsync();
        var text = Value(form, "text", "");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.");
        var referenceFile = form.Files.GetFile("voiceRef");
        var reference = referenceFile is null ? null : ReadWave(referenceFile);
        using var runtime = CreateRuntime();
        var modelPath = Value(form, "modelPath", "");
        if (modelPath.Length == 0) modelPath = ResolveModelPath(runtime, "qwen3_tts", "Qwen3-TTS-12Hz-0.6B-Base-GGUF");
        using var model = runtime.LoadModel(new AudioCppModelOptions
        {
            ModelPath = modelPath,
            FamilyHint = Optional(form, "family"),
            Threads = Integer(form, "threads"),
        });
        var options = new Dictionary<string, string>(Options(form["options"]) ?? new Dictionary<string, string>());
        var referenceText = Value(form, "referenceText", "");
        if (reference is not null && referenceText.Length > 0) options["reference_text"] = referenceText;
        var task = Value(form, "task", "tts");
        var structured = model.Run(new AudioCppRunRequest
        {
            Task = task,
            Text = text,
            TextLanguage = Optional(form, "textLanguage"),
            VoiceId = Optional(form, "voiceId"),
            ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
            ReferenceSampleRate = reference?.SampleRate ?? 0,
            Style = ReadStyle(form),
            Artifacts = ReadArtifacts(form["artifacts"]),
            Options = options.Count == 0 ? null : options,
        });
        var described = (RunResult)DescribeResult(AudioCppTaskKinds.Normalize(task) ?? task, structured);
        var primary = described.Audio;
        if (primary is null && described.NamedAudio.Length > 0) primary = described.NamedAudio[0];
        if (primary is null) throw new AudioCppInferenceException("the model returned no audio output");
        return new
        {
            audioUrl = primary.Url,
            fileName = primary.FileName,
            sampleRate = primary.SampleRate,
            channels = primary.Channels,
            samples = primary.Samples,
            duration = primary.Duration,
            namedAudio = described.NamedAudio,
            artifacts = described.Artifacts,
            text = described.Text,
        };
    });

    internal IResult GetAudio(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        var path = Path.Combine(_artifactDirectory, fileName);
        return File.Exists(path) ? Results.File(path, "audio/wav", fileName, enableRangeProcessing: true) : Results.NotFound();
    }

    /// <summary>Projects a TaskResult into the wire shape shared by /api/run, /api/asr
    /// and /api/tts. Every audio channel is persisted so the browser can play it.</summary>
    private RunResult DescribeResult(string task, AudioCppTaskResult result)
    {
        Directory.CreateDirectory(_artifactDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        AudioClipResult? primary = null;
        if (result.AudioOutput is { } audio && audio.Samples.Count > 0)
            primary = Save($"{task}-{stamp}-{Guid.NewGuid():N}.wav", audio);
        var named = new List<AudioClipResult>();
        foreach (var item in result.NamedAudioOutputs)
        {
            if (item.Audio.Samples.Count == 0) continue;
            var safeId = string.Concat(item.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
            if (safeId.Length == 0) safeId = "track";
            named.Add(Save($"{task}-{safeId}-{stamp}-{Guid.NewGuid():N}.wav", item.Audio) with { Id = item.Id, Meta = item.Meta });
        }
        return new RunResult(
            result.SchemaVersion ?? 1,
            task,
            result.Text,
            result.TextLanguage,
            primary,
            named.ToArray(),
            result.SpeechSegments.Select(ParseSegment).ToArray(),
            result.WordTimestamps.Select(word => new WordResult(word.Span.StartSample, word.Span.EndSample, word.Confidence, word.Word)).ToArray(),
            result.SpeakerTurns.Select(ParseTurn).ToArray(),
            result.Artifacts.Select(ParseArtifact).ToArray(),
            result.Artifact is null ? null : ParseArtifact(result.Artifact),
            result.RawJson.GetRawText());
    }

    private AudioClipResult Save(string fileName, AudioCppAudioClip clip)
    {
        var buffer = new AudioBuffer(clip.Samples.ToArray(), clip.SampleRate, clip.Channels);
        WaveFile.Write(Path.Combine(_artifactDirectory, fileName), buffer);
        return new AudioClipResult(
            $"/api/audio/{fileName}", fileName, clip.SampleRate, clip.Channels, clip.Samples.Count,
            Math.Round(clip.DurationSeconds, 3), null, null);
    }

    private static object StreamEvent(AudioCppStreamEventBatch batch) => new
    {
        offset = batch.ChunkOffset,
        isFinal = batch.Event.IsFinal,
        partialText = batch.Event.PartialText,
        language = batch.Event.Language,
        voiceActivity = batch.Event.VoiceActivity.Select(activity => new
        {
            kind = activity.Kind,
            sample = activity.Sample,
            probability = activity.Probability,
            segment = activity.Segment is null ? null : ParseSegment(activity.Segment),
        }).ToArray(),
        audio = batch.Event.AudioOutput is null ? null : new
        {
            sampleRate = batch.Event.AudioOutput.SampleRate,
            channels = batch.Event.AudioOutput.Channels,
            samples = batch.Event.AudioOutput.Samples.Count,
        },
        namedAudio = batch.Event.NamedAudioOutputs.Select(item => new
        {
            id = item.Id,
            sampleRate = item.Audio.SampleRate,
            channels = item.Audio.Channels,
            samples = item.Audio.Samples.Count,
        }).ToArray(),
        speakerTurns = batch.Event.SpeakerTurns.Select(ParseTurn).ToArray(),
        wordTimestamps = batch.Event.WordTimestamps.Select(word => new
        {
            startSample = word.Span.StartSample,
            endSample = word.Span.EndSample,
            confidence = word.Confidence,
            word = word.Word,
        }).ToArray(),
        artifacts = batch.Event.Artifacts.Select(ParseArtifact).ToArray(),
    };

    private static SpeechSegmentResult ParseSegment(AudioCppSpeechSegment segment) =>
        new(segment.Span.StartSample, segment.Span.EndSample, segment.Confidence, segment.Text);

    private static SpeakerTurnResult ParseTurn(AudioCppSpeakerTurn turn) =>
        new(turn.Span.StartSample, turn.Span.EndSample, turn.Confidence, turn.SpeakerId, turn.Text);

    private static ArtifactResult ParseArtifact(AudioCppArtifact artifact)
    {
        var bytes = artifact.PayloadHex.Length / 2;
        var truncated = bytes > MaxArtifactHexBytes;
        return new ArtifactResult(
            artifact.Id,
            artifact.Kind,
            bytes,
            artifact.Meta,
            truncated ? null : artifact.PayloadHex,
            truncated);
    }

    private static AudioCppStyle? ReadStyle(IFormCollection form)
    {
        var language = Optional(form, "styleLanguage");
        var emotion = Optional(form, "emotion");
        var speakingRate = Decimal(form, "speakingRate");
        var pitchShift = Decimal(form, "pitchShift");
        var energyScale = Decimal(form, "energyScale");
        var tags = Options(form["styleTags"]);
        if (language is null && emotion is null && speakingRate is null && pitchShift is null && energyScale is null && tags is null)
            return null;
        return new AudioCppStyle
        {
            Language = language,
            Emotion = emotion,
            SpeakingRate = speakingRate,
            PitchShift = pitchShift,
            EnergyScale = energyScale,
            Tags = tags,
        };
    }

    private static IReadOnlyList<AudioCppInputArtifact>? ReadArtifacts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("artifacts must be a JSON array of {kind,id,payload_hex|payload_text,meta}.");
        var artifacts = new List<AudioCppInputArtifact>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("artifacts entries must be JSON objects.");
            var kind = item.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "custom" : "custom";
            artifacts.Add(new AudioCppInputArtifact
            {
                Kind = kind,
                Id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "",
                PayloadHex = item.TryGetProperty("payload_hex", out var hex) && hex.ValueKind == JsonValueKind.String ? hex.GetString() : null,
                PayloadText = item.TryGetProperty("payload_text", out var textValue) && textValue.ValueKind == JsonValueKind.String ? textValue.GetString() : null,
                Meta = item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    ? meta.EnumerateObject().ToDictionary(property => property.Name,
                        property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText())
                    : null,
            });
        }
        return artifacts;
    }

    internal IReadOnlyList<string> LoaderFamilies()
    {
        try
        {
            using var runtime = CreateRuntime();
            return runtime.LoaderFamilies();
        }
        catch (Exception exception) when (exception is AudioCppException or DllNotFoundException) { return []; }
    }

    private static string[] CapabilityFlags(ulong capabilities)
    {
        var flags = new List<string>();
        if ((capabilities & AudioCppCapabilities.Synthesize) != 0) flags.Add("synthesize");
        if ((capabilities & AudioCppCapabilities.Transcribe) != 0) flags.Add("transcribe");
        if ((capabilities & AudioCppCapabilities.ModelManager) != 0) flags.Add("model_manager");
        if ((capabilities & AudioCppCapabilities.StructuredResults) != 0) flags.Add("structured_results");
        if ((capabilities & AudioCppCapabilities.Streaming) != 0) flags.Add("streaming");
        if ((capabilities & AudioCppCapabilities.TaskCatalog) != 0) flags.Add("task_catalog");
        if ((capabilities & AudioCppCapabilities.Artifacts) != 0) flags.Add("artifacts");
        if ((capabilities & AudioCppCapabilities.ExecOptions) != 0) flags.Add("exec_options");
        return flags.ToArray();
    }

    private AudioCppRuntime CreateRuntime() => AudioCppRuntime.Create(new AudioCppRuntimeOptions
    { NativeLibraryPath = string.IsNullOrWhiteSpace(_configuration.NativePath) ? null : _configuration.NativePath });

    private string ResolveModelPath(AudioCppRuntime runtime, string family, string standardDirectory)
    {
        var standard = Path.Combine(_configuration.ModelsDirectory, standardDirectory);
        if (Directory.Exists(standard)) return standard;
        var families = runtime.LoaderFamilies();
        foreach (var packageId in ModelValidator.FindPackageIds(_configuration.ModelsDirectory))
        {
            if (ModelValidator.DeriveFamily(packageId, families) != family) continue;
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

    private static string? Optional(IFormCollection form, string name) =>
        string.IsNullOrWhiteSpace(form[name]) ? null : form[name].ToString().Trim();

    private static string? Query(IQueryCollection query, string name, string? fallback) =>
        string.IsNullOrWhiteSpace(query[name]) ? fallback : query[name].ToString().Trim();

    private static int Integer(IFormCollection form, string name) => int.TryParse(form[name], out var value) && value >= 0 ? value : 0;

    private static float? Decimal(IFormCollection form, string name) =>
        float.TryParse(form[name], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

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

internal sealed record AudioClipResult(string Url, string FileName, int SampleRate, int Channels, int Samples, double Duration,
    string? Id, IReadOnlyDictionary<string, string>? Meta);

internal sealed record SpeechSegmentResult(long StartSample, long EndSample, float Confidence, string? Text);
internal sealed record WordResult(long StartSample, long EndSample, float Confidence, string Word);
internal sealed record SpeakerTurnResult(long StartSample, long EndSample, float Confidence, string SpeakerId, string? Text);
internal sealed record ArtifactResult(string Id, string Kind, int Bytes, IReadOnlyDictionary<string, string> Meta, string? PayloadHex, bool PayloadTruncated);

internal sealed record RunResult(
    long SchemaVersion, string Task, string? Text, string? TextLanguage,
    AudioClipResult? Audio, AudioClipResult[] NamedAudio,
    SpeechSegmentResult[] Segments, WordResult[] Words, SpeakerTurnResult[] Turns,
    ArtifactResult[] Artifacts, ArtifactResult? Artifact, string RawJson);
