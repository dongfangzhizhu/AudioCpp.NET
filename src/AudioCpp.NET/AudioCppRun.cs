using System.Text.Json;

namespace AudioCpp.NET;

/// <summary>
/// Canonical task tokens understood by the native shim, plus the model_spec
/// aliases that resolve to them. Mirrors the table in native/src/audiocpp_dotnet.cpp;
/// <see cref="AudioCppRuntime.ListTaskKinds"/> returns the native table when the
/// shim advertises <see cref="AudioCppCapabilities.TaskCatalog"/>, and this class
/// stays as the offline fallback so callers can validate input without loading
/// the native library.
/// </summary>
public static class AudioCppTaskKinds
{
    public const string Vad = "vad";
    public const string Asr = "asr";
    public const string Diarization = "diar";
    public const string SourceSeparation = "sep";
    public const string AudioGeneration = "gen";
    public const string Tts = "tts";
    public const string VoiceCloning = "clon";
    public const string VoiceConversion = "vc";
    public const string SpeechToSpeech = "s2s";
    public const string Alignment = "align";
    public const string VoiceDesign = "vdes";
    public const string SpeakerRecognition = "spk";
    public const string Svc = "svc";
    public const string Midi = "midi";

    public static IReadOnlyList<string> Canonical { get; } = new[]
    {
        Vad, Asr, Diarization, SourceSeparation, AudioGeneration, Tts, VoiceCloning, VoiceConversion,
        SpeechToSpeech, Alignment, VoiceDesign, SpeakerRecognition, Svc, Midi,
    };

    /// <summary>model_spec/*.json "tasks" tokens that are not canonical.</summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio_generation"] = AudioGeneration,
        ["music"] = AudioGeneration,
        ["sfx"] = AudioGeneration,
        ["edit"] = AudioGeneration,
        ["clone"] = VoiceCloning,
        ["design"] = VoiceDesign,
        ["speaker"] = SpeakerRecognition,
        ["codec"] = VoiceConversion,
    };

    /// <summary>Resolves a canonical token or an alias to its canonical form.</summary>
    public static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        if (Canonical.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return trimmed.ToLowerInvariant();
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : null;
    }

    public static bool IsKnown(string? token) => Normalize(token) is not null;
}

/// <summary>One entry of the native task catalog.</summary>
public sealed record AudioCppTaskInfo(string Task, string Input, IReadOnlyList<string> TypicalOutputs, IReadOnlyList<string> Aliases)
{
    /// <summary>Aliases plus the canonical token, for UI pickers.</summary>
    public IReadOnlyList<string> AllTokens => Aliases.Prepend(Task).ToArray();
}

/// <summary>A TaskRequest::input_artifacts entry. Supply <see cref="PayloadHex"/>,
/// <see cref="PayloadText"/>, or neither for an empty payload.</summary>
public sealed record AudioCppInputArtifact
{
    public required string Kind { get; init; }
    public string Id { get; init; } = "";
    public string? PayloadHex { get; init; }
    public string? PayloadText { get; init; }
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>Upstream StyleCondition: the five scalar dimensions plus free-form tags.</summary>
public sealed record AudioCppStyle
{
    public string? Language { get; init; }
    public string? Emotion { get; init; }
    public float? SpeakingRate { get; init; }
    public float? PitchShift { get; init; }
    public float? EnergyScale { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// One structured run against any task the loaded model supports. Every field is
/// optional: an audio-only request defaults to <c>asr</c> and a text-only request
/// to <c>tts</c>, matching the native shim's inference rule.
/// </summary>
public sealed record AudioCppRunRequest
{
    public string? Task { get; init; }
    public string? Text { get; init; }
    public string? TextLanguage { get; init; }
    public ReadOnlyMemory<float> Audio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; } = 1;
    public string? VoiceId { get; init; }
    public ReadOnlyMemory<float> ReferencePcm { get; init; }
    public int ReferenceSampleRate { get; init; }
    public AudioCppStyle? Style { get; init; }
    public IReadOnlyList<AudioCppInputArtifact>? Artifacts { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>Compiled-in copy of the native task catalog, used when the loaded
/// shim does not advertise <see cref="AudioCppCapabilities.TaskCatalog"/>.</summary>
internal static class AudioCppTaskCatalog
{
    internal static IReadOnlyList<AudioCppTaskInfo> Fallback { get; } = new[]
    {
        new AudioCppTaskInfo(AudioCppTaskKinds.Vad, "audio", ["speech_segments"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.Asr, "audio", ["text_output", "word_timestamps", "speech_segments"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.Diarization, "audio", ["speaker_turns"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.SourceSeparation, "audio", ["named_audio_outputs"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.AudioGeneration, "text", ["audio_output", "named_audio_outputs"], ["audio_generation", "music", "sfx", "edit"]),
        new AudioCppTaskInfo(AudioCppTaskKinds.Tts, "text", ["audio_output", "named_audio_outputs"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.VoiceCloning, "text", ["audio_output", "named_audio_outputs"], ["clone"]),
        new AudioCppTaskInfo(AudioCppTaskKinds.VoiceConversion, "audio+text", ["audio_output", "named_audio_outputs"], ["codec"]),
        new AudioCppTaskInfo(AudioCppTaskKinds.SpeechToSpeech, "audio+text", ["audio_output", "named_audio_outputs"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.Alignment, "audio+text", ["word_timestamps"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.VoiceDesign, "text", ["audio_output"], ["design"]),
        new AudioCppTaskInfo(AudioCppTaskKinds.SpeakerRecognition, "audio", ["artifact_output"], ["speaker"]),
        new AudioCppTaskInfo(AudioCppTaskKinds.Svc, "audio+text", ["audio_output"], []),
        new AudioCppTaskInfo(AudioCppTaskKinds.Midi, "audio", ["artifact_output"], []),
    };

}

public static class AudioCppRunRequests
{
    internal static IReadOnlyDictionary<string, string> BuildOptions(AudioCppStyle? style, IReadOnlyDictionary<string, string>? options)
    {
        var merged = new Dictionary<string, string>(options ?? new Dictionary<string, string>());
        if (style is null) return merged;
        if (style.Language is not null) merged["style_language"] = style.Language;
        if (style.Emotion is not null) merged["emotion"] = style.Emotion;
        if (style.SpeakingRate is not null) merged["speaking_rate"] = Number(style.SpeakingRate.Value);
        if (style.PitchShift is not null) merged["pitch_shift"] = Number(style.PitchShift.Value);
        if (style.EnergyScale is not null) merged["energy_scale"] = Number(style.EnergyScale.Value);
        if (style.Tags is not null) foreach (var tag in style.Tags) merged[$"style_tag_{tag.Key}"] = tag.Value;
        return merged;
    }

    internal static string? SerializeArtifacts(IReadOnlyList<AudioCppInputArtifact>? artifacts)
    {
        if (artifacts is null || artifacts.Count == 0) return null;
        var payload = new List<Dictionary<string, object?>>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            if (artifact.PayloadHex is not null && artifact.PayloadText is not null)
                throw new ArgumentException($"Artifact '{artifact.Id}' sets both PayloadHex and PayloadText; supply one.", nameof(artifacts));
            if (artifact.PayloadHex is not null)
            {
                if (artifact.PayloadHex.Length % 2 != 0)
                    throw new ArgumentException($"Artifact '{artifact.Id}' PayloadHex must have an even number of digits.", nameof(artifacts));
                if (!artifact.PayloadHex.All(Uri.IsHexDigit))
                    throw new ArgumentException($"Artifact '{artifact.Id}' PayloadHex contains a non-hexadecimal digit.", nameof(artifacts));
            }
            var entry = new Dictionary<string, object?>
            {
                ["kind"] = artifact.Kind,
                ["id"] = artifact.Id,
            };
            if (artifact.PayloadHex is not null) entry["payload_hex"] = artifact.PayloadHex;
            if (artifact.PayloadText is not null) entry["payload_text"] = artifact.PayloadText;
            if (artifact.Meta is not null) entry["meta"] = artifact.Meta;
            payload.Add(entry);
        }
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    internal static void Validate(AudioCppRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = AudioCppTaskKinds.Normalize(request.Task);
        if (request.Task is not null && !string.IsNullOrWhiteSpace(request.Task) && resolved is null)
            throw new ArgumentException(
                $"Unknown task '{request.Task}'. Known tasks: {string.Join(", ", AudioCppTaskKinds.Canonical)}; " +
                $"aliases: {string.Join(", ", AudioCppTaskKinds.Aliases.Keys)}.", nameof(request));
        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasAudio = !request.Audio.IsEmpty;
        if (!hasText && !hasAudio)
            throw new ArgumentException("A run requires text, audio, or both.", nameof(request));
        if (hasAudio)
        {
            if (request.SampleRate <= 0) throw new ArgumentException("SampleRate must be positive when Audio is supplied.", nameof(request));
            if (request.Channels <= 0) throw new ArgumentException("Channels must be positive when Audio is supplied.", nameof(request));
        }
        if (!request.ReferencePcm.IsEmpty && request.ReferenceSampleRate <= 0)
            throw new ArgumentException("ReferenceSampleRate must be positive when ReferencePcm is supplied.", nameof(request));
    }

    private static string Number(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions SerializerOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}
