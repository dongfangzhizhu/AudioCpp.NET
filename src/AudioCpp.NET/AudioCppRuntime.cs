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
    public string? Language { get; init; }
    public string? Emotion { get; init; }
    public float? SpeakingRate { get; init; }
    public float? PitchShift { get; init; }
    public float? EnergyScale { get; init; }
    public IReadOnlyDictionary<string, string>? StyleTags { get; init; }
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
    public string? Text => RawJson.TryGetProperty("text_output", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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

    public IReadOnlyList<AudioCppLoader> ListLoaders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return CatalogJson.ParseLoaders(InteropOperations.GetLoaderCatalog()); }
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

    internal static IReadOnlyDictionary<string, string> BuildRequestOptions(TtsRequest? request)
    {
        var options = new Dictionary<string, string>(request?.Options ?? new Dictionary<string, string>());
        if (request is null) return options;
        if (request.Language is not null) options["style_language"] = request.Language;
        if (request.Emotion is not null) options["emotion"] = request.Emotion;
        if (request.SpeakingRate is not null) options["speaking_rate"] = request.SpeakingRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.PitchShift is not null) options["pitch_shift"] = request.PitchShift.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.EnergyScale is not null) options["energy_scale"] = request.EnergyScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.StyleTags is not null) foreach (var tag in request.StyleTags) options[$"style_tag_{tag.Key}"] = tag.Value;
        return options;
    }

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

    public AudioCppStreamSession StartStreaming(string task, IReadOnlyDictionary<string, string>? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        if ((_capabilities & AudioCppCapabilities.Streaming) == 0)
            throw new NotSupportedException(
                "The loaded native shim does not advertise AUDIOCPP_CAP_STREAMING; rebuild the shim to use streaming sessions.");
        try
        {
            var (handle, info) = InteropOperations.OpenStream(_handle, task, AudioCppRuntime.ToJson(options));
            return new AudioCppStreamSession(this, handle, AudioCppStreamInfo.Parse(info));
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
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
    AudioCppAudioClip? AudioOutput, IReadOnlyList<AudioCppSpeakerTurn> SpeakerTurns,
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
