using System.Text.Json;
using AudioCpp.NET.Interop;

namespace AudioCpp.NET;

public sealed record AudioCppRuntimeOptions
{
    public string? NativeLibraryPath { get; init; }
    public string? Backend { get; init; }
}

public sealed record AudioCppModelOptions
{
    public required string ModelPath { get; init; }
    public string? FamilyHint { get; init; }
    public int Device { get; init; }
    public int Threads { get; init; }
    public IReadOnlyDictionary<string, string>? LoadOptions { get; init; }
}

public sealed record TtsRequest
{
    public required string Text { get; init; }
    public string Task { get; init; } = "tts";
    public string? VoiceId { get; init; }
    public ReadOnlyMemory<float> ReferencePcm { get; init; }
    public int ReferenceSampleRate { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }
    /// <summary>Upstream StyleCondition. Only meaningful for models whose loader
    /// catalog advertises <c>supports_style_condition</c> (for example qwen3_tts).</summary>
    public AudioCppStyle? Style { get; init; }
}

public sealed record AsrRequest
{
    public required ReadOnlyMemory<float> Audio { get; init; }
    public required int SampleRate { get; init; }
    public int Channels { get; init; } = 1;
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

public sealed record AudioBuffer(ReadOnlyMemory<float> Samples, int SampleRate, int Channels);
public sealed record AudioCppTimeSpan(long StartSample, long EndSample);
public sealed record AudioCppSpeechSegment(AudioCppTimeSpan Span, float Confidence, string? Text);
public sealed record AudioCppSpeakerTurn(AudioCppTimeSpan Span, string SpeakerId, float Confidence, string? Text);
public sealed record AudioCppWordTimestamp(AudioCppTimeSpan Span, string Word, float Confidence);
public sealed record AudioCppAudioClip(int SampleRate, int Channels, IReadOnlyList<float> Samples)
{
    public double DurationSeconds => SampleRate <= 0 || Samples.Count == 0 ? 0 : (double)Samples.Count / SampleRate / Math.Max(Channels, 1);
}
public sealed record AudioCppNamedAudio(string Id, AudioCppAudioClip Audio, IReadOnlyDictionary<string, string> Meta);
public sealed record AudioCppArtifact(string Id, string Kind, string PayloadHex, IReadOnlyDictionary<string, string> Meta);
public sealed record AudioCppTaskResult(JsonElement RawJson)
{
    public long? SchemaVersion => RawJson.TryGetProperty("schema_version", out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
    /// <summary>Canonical task token that actually ran. Present from structured
    /// result schema 2 onward; older shims return <c>null</c>, in which case the
    /// caller already knows the token it asked for.</summary>
    public string? Task => RawJson.TryGetProperty("task", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public string? Text => RawJson.TryGetProperty("text_output", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>Language the text output was produced in, when the loader reports one.</summary>
    public string? TextLanguage => RawJson.TryGetProperty("text_language", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public AudioCppAudioClip? AudioOutput => RawJson.TryGetProperty("audio_output", out var value) && value.ValueKind == JsonValueKind.Object ? ParseAudioClip(value) : null;
    public IReadOnlyList<AudioCppNamedAudio> NamedAudioOutputs => Array("named_audio_outputs").Select(ParseNamedAudio).ToArray();
    public IReadOnlyList<AudioCppSpeechSegment> SpeechSegments => Array("speech_segments").Select(ParseSpeechSegment).ToArray();
    public IReadOnlyList<AudioCppSpeakerTurn> SpeakerTurns => Array("speaker_turns").Select(ParseSpeakerTurn).ToArray();
    public IReadOnlyList<AudioCppWordTimestamp> WordTimestamps => Array("word_timestamps").Select(ParseWordTimestamp).ToArray();
    public AudioCppArtifact? Artifact => RawJson.TryGetProperty("artifact_output", out var value) && value.ValueKind == JsonValueKind.Object ? ParseArtifact(value) : null;
    public IReadOnlyList<AudioCppArtifact> Artifacts => Array("output_artifacts").Select(ParseArtifact).ToArray();
    internal IEnumerable<JsonElement> Array(string name) => Array(RawJson, name);
    internal static IEnumerable<JsonElement> Array(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object) : [];
    internal static IReadOnlyDictionary<string, string> ParseMeta(JsonElement value) => value.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
        ? meta.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText())
        : new Dictionary<string, string>();
    internal static AudioCppTimeSpan ParseSpan(JsonElement value) => new(
        value.TryGetProperty("start_sample", out var start) && start.TryGetInt64(out var startSample) ? startSample : 0,
        value.TryGetProperty("end_sample", out var end) && end.TryGetInt64(out var endSample) ? endSample : 0);
    internal static float Confidence(JsonElement value) =>
        value.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number ? confidence.GetSingle() : 0f;
    internal static AudioCppSpeechSegment ParseSpeechSegment(JsonElement value) => new(ParseSpan(value), Confidence(value),
        value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
    internal static AudioCppSpeakerTurn ParseSpeakerTurn(JsonElement value) => new(ParseSpan(value),
        value.TryGetProperty("speaker_id", out var speakerId) && speakerId.ValueKind == JsonValueKind.String ? speakerId.GetString() ?? "" : "",
        Confidence(value),
        value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
    internal static AudioCppWordTimestamp ParseWordTimestamp(JsonElement value) => new(ParseSpan(value),
        value.TryGetProperty("word", out var word) && word.ValueKind == JsonValueKind.String ? word.GetString() ?? "" : "",
        Confidence(value));
    internal static AudioCppAudioClip ParseAudioClip(JsonElement value) => new(
        value.TryGetProperty("sample_rate", out var sampleRate) && sampleRate.TryGetInt32(out var rate) ? rate : 0,
        value.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var channelCount) ? channelCount : 1,
        value.TryGetProperty("samples", out var samples) && samples.ValueKind == JsonValueKind.Array
            ? samples.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetSingle()).ToArray() : []);
    internal static AudioCppNamedAudio ParseNamedAudio(JsonElement value) => new(
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
        value.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object ? ParseAudioClip(audio) : new AudioCppAudioClip(0, 1, []),
        ParseMeta(value));
    internal static AudioCppArtifact ParseArtifact(JsonElement value) => new(
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
        value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "custom" : "custom",
        value.TryGetProperty("payload_hex", out var payload) && payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : "",
        ParseMeta(value));
}

public sealed record AudioCppBuildInfo(uint AbiMajor, uint AbiMinor, string ShimVersion, string AudioCppCommit, string Backend, ulong Capabilities);
public static class AudioCppCapabilities
{
    public const ulong Synthesize = 1UL << 0;
    public const ulong Transcribe = 1UL << 1;
    public const ulong ModelManager = 1UL << 2;
    public const ulong StructuredResults = 1UL << 3;
    public const ulong Streaming = 1UL << 4;
    public const ulong TaskCatalog = 1UL << 5;
    public const ulong Artifacts = 1UL << 6;
    public const ulong ExecOptions = 1UL << 7;
    /// <summary>audiocpp_model_run_json_batch is available (ABI 1.4+).</summary>
    public const ulong Batch = 1UL << 8;
    /// <summary>List-valued options (AudioCppRunRequest.OptionArrays) are honored (ABI 1.4+).</summary>
    public const ulong OptionArrays = 1UL << 9;
}
public sealed record AudioCppLoader(string Family, string InstructionsPolicy, IReadOnlyList<string> ApiEndpoints,
    IReadOnlyList<AudioCppLoaderTask> Tasks, IReadOnlyList<string> Languages,
    bool SupportsSpeakerReference, bool SupportsStyleCondition, bool SupportsTimestamps);
public sealed record AudioCppLoaderTask(string Task, IReadOnlyList<string> Modes);
public sealed record AudioCppPackage(string Id, bool Installed, string State, string Message,
    ulong? SizeBytes, string VersionState, string LocalRevision, string RemoteRevision);

public sealed class AudioCppRuntime : IDisposable
{
    private bool _disposed;

    private AudioCppRuntime(AudioCppBuildInfo buildInfo) => BuildInfo = buildInfo;

    public AudioCppBuildInfo BuildInfo { get; }

    public static AudioCppRuntime Create(AudioCppRuntimeOptions? options = null)
    {
        options ??= new();
        NativeLibraryLoader.Load(options.NativeLibraryPath);
        var info = InteropOperations.GetAbiInfo();
        var buildInfo = new AudioCppBuildInfo(info.AbiMajor, info.AbiMinor,
            info.ShimVersion, info.AudioCppCommit, info.Backend, info.Capabilities);
        if (buildInfo.AbiMajor != 1) throw new AudioCppAbiMismatchException($"Unsupported native ABI major {buildInfo.AbiMajor}; expected 1.");
        if (options.Backend is not null && !string.Equals(options.Backend, buildInfo.Backend, StringComparison.OrdinalIgnoreCase))
            throw new AudioCppAbiMismatchException($"Requested backend '{options.Backend}', but the native library provides '{buildInfo.Backend}'. Load the matching runtime package.");
        return new AudioCppRuntime(buildInfo);
    }

    public AudioCppModel LoadModel(AudioCppModelOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        var modelPath = options.ModelPath;
        if (!File.Exists(modelPath) && !Directory.Exists(modelPath))
            throw new AudioCppLoadException($"Model path does not exist: {Path.GetFullPath(modelPath)}");

        string? familyHint = string.IsNullOrWhiteSpace(options.FamilyHint) ? null : options.FamilyHint;
        if (Directory.Exists(modelPath))
        {
            var report = ModelValidator.Validate(modelPath);
            if (!report.Complete)
                throw new AudioCppModelIncompleteException(
                    $"Model at '{Path.GetFullPath(modelPath)}' is incomplete. {ModelValidator.FormatIssues(report)} " +
                    "Re-download the package or restore the missing files.");
            if (familyHint is null && report.PackageId is not null) familyHint = DeriveFamily(report.PackageId);
        }

        var (handle, error) = InteropOperations.LoadModel(modelPath, familyHint, BuildInfo.Backend,
            options.Device, options.Threads, ToJson(options.LoadOptions));
        if (handle.IsInvalid) { handle.Dispose(); throw new AudioCppLoadException(error); }
        return new AudioCppModel(handle, BuildInfo.Capabilities);
    }

    private string? DeriveFamily(string packageId)
    {
        try { return ModelValidator.DeriveFamily(packageId, ListLoaders().Select(loader => loader.Family)); }
        catch (Exception exception) when (exception is AudioCppException or DllNotFoundException) { return null; }
    }

    /// <summary>Resolves a package ID to one of the families compiled into the
    /// loaded native shim, or null when nothing matches.</summary>
    public string? ResolveFamily(string packageId) => DeriveFamily(packageId);

    /// <summary>The families (loaders) compiled into the native shim.</summary>
    public IReadOnlyList<string> LoaderFamilies() => ListLoaders().Select(loader => loader.Family).ToArray();

    public IReadOnlyList<AudioCppLoader> ListLoaders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return CatalogJson.ParseLoaders(InteropOperations.GetLoaderCatalog()); }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppException(exception.Message, exception); }
    }

    /// <summary>
    /// Every task the native shim can dispatch, with its accepted aliases and the
    /// typical result channels. Falls back to the compiled-in table when the shim
    /// predates <see cref="AudioCppCapabilities.TaskCatalog"/>.
    /// </summary>
    public IReadOnlyList<AudioCppTaskInfo> ListTaskKinds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((BuildInfo.Capabilities & AudioCppCapabilities.TaskCatalog) == 0) return AudioCppTaskCatalog.Fallback;
        try { return CatalogJson.ParseTasks(InteropOperations.GetTaskCatalog()); }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppException(exception.Message, exception); }
    }

    public IReadOnlyList<AudioCppPackage> ListPackages()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return CatalogJson.ParsePackages(InteropOperations.GetPackageCatalog()); }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppException(exception.Message, exception); }
    }

    public string InstallPackage(string packageId, string modelsDirectory, bool overwrite = false,
        Action<ulong, ulong, string?>? progress = null, bool verify = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        string message;
        try { message = InteropOperations.InstallPackage(packageId, ".", modelsDirectory, overwrite, progress); }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppException(exception.Message, exception); }
        if (!verify) return message;

        var directory = ModelValidator.FindPackageDirectory(modelsDirectory, packageId);
        if (directory is null) return message;
        var report = ModelValidator.Validate(directory);
        if (!report.Complete)
            throw new AudioCppModelIncompleteException(
                $"Package '{packageId}' installed but incomplete at '{directory}'. {ModelValidator.FormatIssues(report)} " +
                "Re-run the download with overwrite to repair it.");
        return $"{message}{Environment.NewLine}verified {report.CheckedFiles} file(s), {report.CheckedBytes} bytes";
    }

    public void Dispose() => _disposed = true;

    internal static IReadOnlyDictionary<string, string> BuildRequestOptions(TtsRequest? request) =>
        AudioCppRunRequests.BuildOptions(request?.Style, request?.Options);

    internal static void ValidateRunRequests(TtsRequest? request, AsrRequest? audioRequest)
    {
        if (request is null && audioRequest is null) throw new ArgumentException("Run requires a TtsRequest, an AsrRequest, or both.");
        if (request is not null && string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
        if (audioRequest is null) return;
        if (audioRequest.Audio.IsEmpty) throw new ArgumentException("Audio is required.", nameof(audioRequest));
        if (audioRequest.SampleRate <= 0 || audioRequest.Channels <= 0) throw new ArgumentException("SampleRate and Channels must be positive.", nameof(audioRequest));
    }

    internal static string? ToJson(IReadOnlyDictionary<string, string>? values) => values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values);
}

internal static class CatalogJson
{
    internal static IReadOnlyList<AudioCppLoader> ParseLoaders(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("loaders").EnumerateArray().Select(loader =>
            new AudioCppLoader(loader.GetProperty("family").GetString() ?? "",
                loader.GetProperty("instructions_policy").GetString() ?? "",
                Strings(loader, "api_endpoints"),
                loader.GetProperty("tasks").EnumerateArray().Select(task => new AudioCppLoaderTask(
                    task.GetProperty("task").GetString() ?? "", Strings(task, "modes"))).ToArray(),
                Strings(loader, "languages"),
                loader.GetProperty("supports_speaker_reference").GetBoolean(),
                loader.GetProperty("supports_style_condition").GetBoolean(),
                loader.GetProperty("supports_timestamps").GetBoolean())).ToArray();
    }

    internal static IReadOnlyList<AudioCppTaskInfo> ParseTasks(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("tasks").EnumerateArray().Select(task =>
            new AudioCppTaskInfo(task.GetProperty("task").GetString() ?? "",
                task.GetProperty("input").GetString() ?? "",
                Strings(task, "typical_outputs"),
                Strings(task, "aliases"))).ToArray();
    }

    internal static IReadOnlyList<AudioCppPackage> ParsePackages(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(package => new AudioCppPackage(
            package.GetProperty("id").GetString() ?? "", package.GetProperty("installed").GetBoolean(),
            package.GetProperty("state").GetString() ?? "", package.GetProperty("message").GetString() ?? "",
            package.GetProperty("size_bytes").ValueKind == JsonValueKind.Null ? null : package.GetProperty("size_bytes").GetUInt64(),
            package.GetProperty("version_state").GetString() ?? "", package.GetProperty("local_revision").GetString() ?? "",
            package.GetProperty("remote_revision").GetString() ?? "")).ToArray();
    }

    private static string[] Strings(JsonElement parent, string property) =>
        parent.GetProperty(property).EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
}

public sealed class AudioCppModel : IDisposable
{
    private readonly SafeModelHandle _handle;
    private readonly ulong _capabilities;
    private bool _disposed;

    internal AudioCppModel(SafeModelHandle handle, ulong capabilities)
    {
        _handle = handle;
        _capabilities = capabilities;
    }

    internal bool IsDisposed => _disposed;

    /// <summary>Opens a streaming session for <paramref name="task"/> with no prompt.</summary>
    public AudioCppStreamSession StartStreaming(string task, IReadOnlyDictionary<string, string>? options = null) =>
        StartStreaming(new AudioCppStreamingOptions { Task = task, SampleRate = 0, Options = options });

    /// <summary>
    /// Opens a streaming session. <paramref name="options"/> carries the task, the
    /// optional text prompt (streaming ASR), its language, style conditions and
    /// input artifacts.
    /// </summary>
    public AudioCppStreamSession StartStreaming(AudioCppStreamingOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Task);
        if ((_capabilities & AudioCppCapabilities.Streaming) == 0)
            throw new NotSupportedException(
                "The loaded native shim does not advertise AUDIOCPP_CAP_STREAMING; rebuild the shim to use streaming sessions.");
        var artifacts = AudioCppRunRequests.SerializeArtifacts(options.Artifacts);
        var text = string.IsNullOrWhiteSpace(options.Text) ? null : options.Text;
        var sessionOptions = AudioCppRuntime.ToJson(AudioCppRunRequests.BuildOptions(options.Style, options.Options));
        try
        {
            var (handle, info) = artifacts is null && text is null
                ? InteropOperations.OpenStream(_handle, options.Task, sessionOptions)
                : InteropOperations.OpenStream(_handle, options.Task, text, options.TextLanguage, artifacts, sessionOptions);
            return new AudioCppStreamSession(this, handle, AudioCppStreamInfo.Parse(info));
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
    }

    /// <summary>
    /// Structured run for any task the loaded model supports: text, audio, a voice
    /// reference, style conditions and input artifacts are all optional. The
    /// returned result exposes every TaskResult channel, including the ones the
    /// scalar <see cref="Synthesize"/> / <see cref="Transcribe"/> helpers drop
    /// (named audio outputs, speech segments, speaker turns, word timestamps and
    /// artifacts).
    /// </summary>
    public AudioCppTaskResult Run(AudioCppRunRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioCppRunRequests.Validate(request);
        var input = new AudioCppRunInput(
            request.Task,
            string.IsNullOrWhiteSpace(request.Text) ? null : request.Text,
            request.TextLanguage,
            request.Audio,
            request.Audio.IsEmpty ? 0 : request.SampleRate,
            request.Audio.IsEmpty ? 1 : request.Channels,
            request.VoiceId,
            request.ReferencePcm,
            request.ReferenceSampleRate,
            AudioCppRunRequests.SerializeArtifacts(request.Artifacts),
            AudioCppRunRequests.SerializeOptions(request.Style, request.Options, request.OptionArrays));
        string json;
        try { json = InteropOperations.RunJson(_handle, input); }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppInferenceException(exception.Message, exception); }
        using var document = JsonDocument.Parse(json);
        return new AudioCppTaskResult(document.RootElement.Clone());
    }

    /// <summary>
    /// Batched structured run (requires <see cref="AudioCppCapabilities.Batch"/>, ABI 1.4+).
    /// All requests go to one task family in a single native call: models implementing
    /// upstream's batched offline session run natively batched, the rest execute
    /// sequentially with identical results. Every request's audio is copied into one
    /// shared pool, so all audio requests must share one sample rate and channel count.
    /// Voice references (<see cref="AudioCppRunRequest.ReferencePcm"/>) are not supported
    /// in batches; use <see cref="AudioCppModel.Run(AudioCppRunRequest)"/> for those.
    /// </summary>
    /// <param name="requests">One or more requests.</param>
    /// <param name="task">Optional explicit task token. Task resolution: the explicit
    /// <paramref name="task"/> token wins; otherwise every request's Task field must
    /// agree after normalization; otherwise audio-only batches become <c>asr</c> and
    /// text-only batches <c>tts</c>. Mixed audio/text batches need an explicit task.</param>
    public IReadOnlyList<AudioCppTaskResult> RunBatch(IReadOnlyList<AudioCppRunRequest> requests, string? task = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("At least one request is required.", nameof(requests));
        if ((_capabilities & AudioCppCapabilities.Batch) == 0)
            throw new NotSupportedException(
                "The loaded native shim does not advertise AUDIOCPP_CAP_BATCH; rebuild the shim (ABI 1.4+) to use RunBatch.");
        foreach (var request in requests)
        {
            AudioCppRunRequests.Validate(request);
            if (!request.ReferencePcm.IsEmpty)
                throw new NotSupportedException("ReferencePcm is not supported in batches; run such requests individually.");
        }

        // Task resolution: explicit argument > unanimous per-request Task > shape
        // inference. Requests that declare different task tokens are refused: a
        // batch is one task family, and silently running a declared "clon" as the
        // shape-inferred "tts" would be wrong.
        string? resolved = AudioCppTaskKinds.Normalize(task);
        if (resolved is null)
        {
            var declared = requests
                .Select(request => AudioCppTaskKinds.Normalize(request.Task))
                .Where(normalized => normalized is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (declared.Length > 1)
                throw new ArgumentException(
                    $"Requests declare different tasks ({string.Join(", ", declared)}); run them separately or pass an explicit task token.",
                    nameof(requests));
            if (declared.Length == 1) resolved = declared[0];
        }
        var hasAudio = requests.Any(request => !request.Audio.IsEmpty);
        var allAudio = requests.All(request => !request.Audio.IsEmpty);
        if (resolved is null)
        {
            if (hasAudio != allAudio)
                throw new ArgumentException(
                    "Mixed batch: some requests carry audio and some do not. Pass an explicit task token.", nameof(requests));
            resolved = hasAudio ? AudioCppTaskKinds.Asr : AudioCppTaskKinds.Tts;
        }

        // One shared interleaved audio pool; per-request entries index into it.
        var pool = new List<float>(requests.Sum(request => request.Audio.Length));
        var entries = new List<Dictionary<string, object?>>(requests.Count);
        int? poolSampleRate = null;
        int? poolChannels = null;
        foreach (var request in requests)
        {
            var entry = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(request.Text)) entry["text"] = request.Text;
            if (!string.IsNullOrWhiteSpace(request.TextLanguage)) entry["text_language"] = request.TextLanguage;
            if (!string.IsNullOrWhiteSpace(request.VoiceId)) entry["voice_id"] = request.VoiceId;
            if (!request.Audio.IsEmpty)
            {
                if (request.SampleRate <= 0 || request.Channels <= 0)
                    throw new ArgumentException("SampleRate and Channels must be positive when Audio is supplied.", nameof(requests));
                if (poolSampleRate is null) { poolSampleRate = request.SampleRate; poolChannels = request.Channels; }
                else if (poolSampleRate != request.SampleRate || poolChannels != request.Channels)
                    throw new ArgumentException(
                        "All audio in one batch must share one sample rate and channel count " +
                        $"(first request: {poolSampleRate} Hz x {poolChannels} ch, this request: {request.SampleRate} Hz x {request.Channels} ch).",
                        nameof(requests));
                entry["audio"] = new Dictionary<string, object?> { ["offset"] = pool.Count, ["count"] = request.Audio.Length };
                pool.AddRange(request.Audio.ToArray());
            }
            var artifacts = AudioCppRunRequests.SerializeArtifacts(request.Artifacts);
            if (artifacts is not null) entry["artifacts"] = JsonSerializer.Deserialize<JsonElement>(artifacts);
            var options = AudioCppRunRequests.SerializeOptions(request.Style, request.Options, request.OptionArrays);
            if (options is not null) entry["options"] = JsonSerializer.Deserialize<JsonElement>(options);
            entries.Add(entry);
        }

        string json;
        try
        {
            json = InteropOperations.RunJsonBatch(_handle, resolved,
                JsonSerializer.Serialize(entries, AudioCppRunRequests.SerializerOptions),
                pool.ToArray(), poolSampleRate ?? 0, poolChannels ?? 0);
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        { throw new AudioCppInferenceException(exception.Message, exception); }

        using var document = JsonDocument.Parse(json);
        var results = new List<AudioCppTaskResult>(requests.Count);
        foreach (var item in AudioCppTaskResult.Array(document.RootElement.Clone(), "results"))
            results.Add(new AudioCppTaskResult(item));
        if (results.Count != requests.Count)
            throw new AudioCppInferenceException(
                $"Native batch returned {results.Count} results for {requests.Count} requests.");
        return results;
    }

    public AudioBuffer Synthesize(TtsRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
        if (!request.ReferencePcm.IsEmpty && request.ReferenceSampleRate <= 0) throw new ArgumentException("ReferenceSampleRate must be positive when ReferencePcm is supplied.", nameof(request));
        try
        {
            var options = AudioCppRuntime.BuildRequestOptions(request);
            var result = InteropOperations.Synthesize(_handle, request.Task, request.Text, request.VoiceId,
                request.ReferencePcm.Span, request.ReferenceSampleRate, AudioCppRuntime.ToJson(options));
            return new AudioBuffer(result.Samples, result.SampleRate, result.Channels);
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
    }

    public string Transcribe(AsrRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Audio.IsEmpty) throw new ArgumentException("Audio is required.", nameof(request));
        if (request.SampleRate <= 0 || request.Channels <= 0) throw new ArgumentException("SampleRate and Channels must be positive.", nameof(request));
        try
        {
            return InteropOperations.Transcribe(_handle, request.Audio.Span, request.SampleRate, request.Channels,
                AudioCppRuntime.ToJson(request.Options));
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
    }

    public AudioCppTaskResult Run(TtsRequest? request = null, AsrRequest? audioRequest = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioCppRuntime.ValidateRunRequests(request, audioRequest);
        var text = request?.Text;
        var audio = audioRequest?.Audio ?? ReadOnlyMemory<float>.Empty;
        string json;
        try
        {
            json = InteropOperations.RunJson(_handle, request?.Task, text, audio.Span,
                audioRequest?.SampleRate ?? 0, audioRequest?.Channels ?? 0, request?.VoiceId,
                request is null ? ReadOnlySpan<float>.Empty : request.ReferencePcm.Span, request?.ReferenceSampleRate ?? 0,
                AudioCppRuntime.ToJson(AudioCppRuntime.BuildRequestOptions(request)));
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
        using var document = JsonDocument.Parse(json);
        return new AudioCppTaskResult(document.RootElement.Clone());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

public sealed record AudioCppStreamPolicy(string Input, string Output, long PreferredChunkSamples, double PreferredChunkSeconds);

public sealed record AudioCppStreamInfo(string Family, string Task, AudioCppStreamPolicy Policy)
{
    internal static AudioCppStreamInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new AudioCppStreamInfo(
            root.TryGetProperty("family", out var family) && family.ValueKind == JsonValueKind.String ? family.GetString() ?? "" : "",
            root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.String ? task.GetString() ?? "" : "",
            new AudioCppStreamPolicy(
                root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String ? input.GetString() ?? "" : "",
                root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String ? output.GetString() ?? "" : "",
                root.TryGetProperty("preferred_chunk_samples", out var chunkSamples) && chunkSamples.TryGetInt64(out var samples) ? samples : 0,
                root.TryGetProperty("preferred_chunk_seconds", out var chunkSeconds) && chunkSeconds.ValueKind == JsonValueKind.Number ? chunkSeconds.GetDouble() : 0));
    }
}

public sealed record AudioCppVoiceActivity(string Kind, long Sample, float Probability, AudioCppSpeechSegment? Segment);

public sealed record AudioCppStreamEvent(
    string? PartialText, string? Language, IReadOnlyList<AudioCppVoiceActivity> VoiceActivity,
    AudioCppAudioClip? AudioOutput, IReadOnlyList<AudioCppNamedAudio> NamedAudioOutputs,
    IReadOnlyList<AudioCppSpeakerTurn> SpeakerTurns,
    IReadOnlyList<AudioCppWordTimestamp> WordTimestamps, IReadOnlyList<AudioCppArtifact> Artifacts,
    bool IsFinal)
{
    internal static IReadOnlyList<AudioCppStreamEvent> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyList<AudioCppStreamEvent> Parse(JsonElement root) =>
        AudioCppTaskResult.Array(root, "events").Select(ParseEvent).ToArray();

    private static AudioCppStreamEvent ParseEvent(JsonElement value) => new(
        value.TryGetProperty("partial_text", out var partial) && partial.ValueKind == JsonValueKind.Object
            ? partial.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null : null,
        value.TryGetProperty("partial_text", out var partialLanguage) && partialLanguage.ValueKind == JsonValueKind.Object
            ? partialLanguage.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null : null,
        value.TryGetProperty("voice_activity", out var voiceActivity) && voiceActivity.ValueKind == JsonValueKind.Array
            ? voiceActivity.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(ParseVoiceActivity).ToArray() : [],
        value.TryGetProperty("audio_output", out var audio) && audio.ValueKind == JsonValueKind.Object
            ? AudioCppTaskResult.ParseAudioClip(audio) : null,
        AudioCppTaskResult.Array(value, "named_audio_outputs").Select(AudioCppTaskResult.ParseNamedAudio).ToArray(),
        AudioCppTaskResult.Array(value, "speaker_turns").Select(AudioCppTaskResult.ParseSpeakerTurn).ToArray(),
        AudioCppTaskResult.Array(value, "word_timestamps").Select(AudioCppTaskResult.ParseWordTimestamp).ToArray(),
        AudioCppTaskResult.Array(value, "output_artifacts").Select(AudioCppTaskResult.ParseArtifact).ToArray(),
        value.TryGetProperty("is_final", out var isFinal) && isFinal.ValueKind == JsonValueKind.True);

    private static AudioCppVoiceActivity ParseVoiceActivity(JsonElement value) => new(
        value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "speech_segment" : "speech_segment",
        value.TryGetProperty("sample", out var sample) && sample.TryGetInt64(out var sampleValue) ? sampleValue : 0,
        value.TryGetProperty("probability", out var probability) && probability.ValueKind == JsonValueKind.Number ? probability.GetSingle() : 0f,
        value.TryGetProperty("segment", out var segment) && segment.ValueKind == JsonValueKind.Object
            ? AudioCppTaskResult.ParseSpeechSegment(segment) : null);

    /// <summary>
    /// True when the event carried something other than the empty poll envelope that
    /// loaders emit for every chunk they consume.
    /// </summary>
    public bool HasContent =>
        PartialText is not null || !string.IsNullOrEmpty(Language) || VoiceActivity.Count > 0 ||
        AudioOutput is not null || NamedAudioOutputs.Count > 0 || SpeakerTurns.Count > 0 ||
        WordTimestamps.Count > 0 || Artifacts.Count > 0;
}

/// <summary>
/// Live streaming session fed with PCM chunks. The session borrows the lifetime of
/// its owning <see cref="AudioCppModel"/>; dispose the stream before the model.
/// </summary>
public sealed class AudioCppStreamSession : IDisposable
{
    private readonly AudioCppModel _model;
    private readonly SafeStreamHandle _handle;
    private bool _disposed;
    private bool _finished;

    internal AudioCppStreamSession(AudioCppModel model, SafeStreamHandle handle, AudioCppStreamInfo info)
    {
        _model = model;
        _handle = handle;
        Info = info;
    }

    public AudioCppStreamInfo Info { get; }

    public IReadOnlyList<AudioCppStreamEvent> PushPcm(ReadOnlyMemory<float> samples, int sampleRate, int channels = 1)
    {
        ThrowIfUsable();
        if (samples.IsEmpty) throw new ArgumentException("Samples must not be empty.", nameof(samples));
        if (sampleRate <= 0 || channels <= 0) throw new ArgumentException("SampleRate and channels must be positive.");
        try
        {
            return AudioCppStreamEvent.Parse(InteropOperations.StreamPushPcm(_handle, samples.Span, sampleRate, channels));
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
    }

    public AudioCppTaskResult Finish()
    {
        ThrowIfUsable();
        if (_finished) throw new InvalidOperationException("The stream has already been finished.");
        _finished = true;
        try
        {
            var json = InteropOperations.StreamFinish(_handle);
            using var document = JsonDocument.Parse(json);
            return new AudioCppTaskResult(document.RootElement.Clone());
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
    }

    private void ThrowIfUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(_model.IsDisposed, _model);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

public class AudioCppException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class AudioCppAbiMismatchException(string message) : AudioCppException(message);
public class AudioCppLoadException(string message) : AudioCppException(message);
public sealed class AudioCppModelIncompleteException(string message) : AudioCppLoadException(message);
public sealed class AudioCppInferenceException(string message, Exception? inner = null) : AudioCppException(message, inner);
