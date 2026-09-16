using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

/// <summary>
/// Covers the "any task" managed surface: the token table, request validation,
/// artifact serialization, style -> options mapping and the catalog JSON readers.
/// Nothing here loads the native library, so it runs everywhere.
/// </summary>
public sealed class RunRequestTests
{
    [Fact]
    public void NormalizeAcceptsCanonicalTokensAndModelSpecAliases()
    {
        foreach (var token in AudioCppTaskKinds.Canonical)
            Assert.Equal(token, AudioCppTaskKinds.Normalize(token));

        Assert.Equal(AudioCppTaskKinds.AudioGeneration, AudioCppTaskKinds.Normalize("audio_generation"));
        Assert.Equal(AudioCppTaskKinds.AudioGeneration, AudioCppTaskKinds.Normalize("music"));
        Assert.Equal(AudioCppTaskKinds.AudioGeneration, AudioCppTaskKinds.Normalize("sfx"));
        Assert.Equal(AudioCppTaskKinds.AudioGeneration, AudioCppTaskKinds.Normalize("edit"));
        Assert.Equal(AudioCppTaskKinds.VoiceCloning, AudioCppTaskKinds.Normalize("clone"));
        Assert.Equal(AudioCppTaskKinds.VoiceDesign, AudioCppTaskKinds.Normalize("design"));
        Assert.Equal(AudioCppTaskKinds.SpeakerRecognition, AudioCppTaskKinds.Normalize("speaker"));
        Assert.Equal(AudioCppTaskKinds.VoiceConversion, AudioCppTaskKinds.Normalize("codec"));
        // Tokens are matched case-insensitively and normalized to lower case.
        Assert.Equal(AudioCppTaskKinds.Asr, AudioCppTaskKinds.Normalize("ASR"));
        Assert.Equal(AudioCppTaskKinds.AudioGeneration, AudioCppTaskKinds.Normalize("  Music  "));
    }

    [Fact]
    public void NormalizeRejectsBlankAndUnknownTokens()
    {
        Assert.Null(AudioCppTaskKinds.Normalize(null));
        Assert.Null(AudioCppTaskKinds.Normalize("   "));
        Assert.Null(AudioCppTaskKinds.Normalize("bogus"));
        Assert.False(AudioCppTaskKinds.IsKnown("bogus"));
        Assert.True(AudioCppTaskKinds.IsKnown("music"));
    }

    [Fact]
    public void AliasTablePointsOnlyAtCanonicalTokens()
    {
        // Eight model_spec tokens beyond the fourteen canonical ones. If the native
        // parser and this table disagree a model silently becomes unreachable.
        Assert.Equal(8, AudioCppTaskKinds.Aliases.Count);
        Assert.All(AudioCppTaskKinds.Aliases.Values,
            canonical => Assert.Contains(canonical, AudioCppTaskKinds.Canonical));
    }

    [Fact]
    public void TaskInfoListsTheCanonicalTokenFirst()
    {
        var info = new AudioCppTaskInfo("gen", "text", ["audio_output"], ["music", "sfx"]);
        Assert.Equal(new[] { "gen", "music", "sfx" }, info.AllTokens);
        Assert.Equal(new[] { "gen" }, new AudioCppTaskInfo("gen", "text", [], []).AllTokens);
    }

    [Fact]
    public void ValidateRejectsUnknownTaskAndListsTheAlternatives()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AudioCppRunRequests.Validate(new AudioCppRunRequest { Task = "bogus", Text = "hi" }));
        Assert.Contains("bogus", exception.Message);
        Assert.Contains("music", exception.Message);
    }

    [Fact]
    public void ValidateAcceptsEveryTaskToken()
    {
        foreach (var token in AudioCppTaskKinds.Canonical)
            AudioCppRunRequests.Validate(new AudioCppRunRequest { Task = token, Text = "hi" });
        foreach (var token in AudioCppTaskKinds.Aliases.Keys)
            AudioCppRunRequests.Validate(new AudioCppRunRequest { Task = token, Text = "hi" });
    }

    [Fact]
    public void ValidateInfersTheTaskFromTheRequestShape()
    {
        // A null or blank task is legal: the shim picks asr for audio and tts for text.
        AudioCppRunRequests.Validate(new AudioCppRunRequest { Text = "hi" });
        AudioCppRunRequests.Validate(new AudioCppRunRequest { Task = " ", Text = "hi" });
        AudioCppRunRequests.Validate(new AudioCppRunRequest { Audio = new float[] { 0f }, SampleRate = 16000 });
    }

    [Fact]
    public void ValidateRequiresAnInputAndConsistentAudioMetadata()
    {
        Assert.Throws<ArgumentException>(() => AudioCppRunRequests.Validate(new AudioCppRunRequest { Task = "asr" }));
        Assert.Throws<ArgumentException>(() =>
            AudioCppRunRequests.Validate(new AudioCppRunRequest { Audio = new float[] { 0f }, SampleRate = 0 }));
        Assert.Throws<ArgumentException>(() =>
            AudioCppRunRequests.Validate(new AudioCppRunRequest { Audio = new float[] { 0f }, SampleRate = 16000, Channels = 0 }));
        Assert.Throws<ArgumentException>(() => AudioCppRunRequests.Validate(
            new AudioCppRunRequest { Text = "hi", ReferencePcm = new float[] { 0f }, ReferenceSampleRate = 0 }));
        Assert.Throws<ArgumentNullException>(() => AudioCppRunRequests.Validate(null!));
    }

    [Fact]
    public void StyleBecomesNativeOptions()
    {
        var options = AudioCppRunRequests.BuildOptions(
            new AudioCppStyle
            {
                Language = "zh",
                Emotion = "calm",
                SpeakingRate = 1.25f,
                PitchShift = -2f,
                EnergyScale = 0.8f,
                Tags = new Dictionary<string, string> { ["whisper"] = "1" },
            },
            new Dictionary<string, string> { ["temperature"] = "0.5" });

        Assert.Equal("0.5", options["temperature"]);
        Assert.Equal("zh", options["style_language"]);
        Assert.Equal("calm", options["emotion"]);
        Assert.Equal("1.25", options["speaking_rate"]);
        Assert.Equal("-2", options["pitch_shift"]);
        Assert.Equal("0.8", options["energy_scale"]);
        Assert.Equal("1", options["style_tag_whisper"]);
    }

    [Fact]
    public void StyleIsOptionalAndOwnsTheKeysItSets()
    {
        Assert.Empty(AudioCppRunRequests.BuildOptions(null, null));
        Assert.Equal("0.7", AudioCppRunRequests.BuildOptions(null,
            new Dictionary<string, string> { ["temperature"] = "0.7" })["temperature"]);

        // The typed Style is authoritative for the keys it carries, so a raw option
        // with the same name is overridden; unrelated options pass through untouched.
        var merged = AudioCppRunRequests.BuildOptions(
            new AudioCppStyle { Language = "en" },
            new Dictionary<string, string> { ["style_language"] = "pinned", ["temperature"] = "0.7" });
        Assert.Equal("en", merged["style_language"]);
        Assert.Equal("0.7", merged["temperature"]);

        // The caller's dictionary must not be mutated in the process.
        var caller = new Dictionary<string, string> { ["style_language"] = "pinned" };
        AudioCppRunRequests.BuildOptions(new AudioCppStyle { Language = "en" }, caller);
        Assert.Equal("pinned", caller["style_language"]);
    }

    [Fact]
    public void ArtifactsAreSerializedForTheNativeParser()
    {
        var json = AudioCppRunRequests.SerializeArtifacts(
        [
            new AudioCppInputArtifact
            {
                Kind = "speaker_embedding",
                Id = "spk",
                PayloadHex = "00ff",
                Meta = new Dictionary<string, string> { ["dim"] = "32" },
            },
            new AudioCppInputArtifact { Kind = "transcript", PayloadText = "hello" },
        ]);

        Assert.NotNull(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("speaker_embedding", items[0].GetProperty("kind").GetString());
        Assert.Equal("spk", items[0].GetProperty("id").GetString());
        Assert.Equal("00ff", items[0].GetProperty("payload_hex").GetString());
        Assert.Equal("32", items[0].GetProperty("meta").GetProperty("dim").GetString());
        // A text payload must not be echoed as hex.
        Assert.Equal("hello", items[1].GetProperty("payload_text").GetString());
        Assert.False(items[1].TryGetProperty("payload_hex", out _));
        Assert.Equal("", items[1].GetProperty("id").GetString());
    }

    [Fact]
    public void ArtifactsRejectMalformedPayloadsBeforeTheNativeCall()
    {
        Assert.Null(AudioCppRunRequests.SerializeArtifacts(null));
        Assert.Null(AudioCppRunRequests.SerializeArtifacts([]));
        Assert.Throws<ArgumentException>(() => AudioCppRunRequests.SerializeArtifacts(
            [new AudioCppInputArtifact { Kind = "x", PayloadHex = "abc" }]));
        Assert.Throws<ArgumentException>(() => AudioCppRunRequests.SerializeArtifacts(
            [new AudioCppInputArtifact { Kind = "x", PayloadHex = "not-hex" }]));
        Assert.Throws<ArgumentException>(() => AudioCppRunRequests.SerializeArtifacts(
            [new AudioCppInputArtifact { Kind = "x", PayloadHex = "00", PayloadText = "y" }]));
    }

    [Fact]
    public void NativeTaskCatalogJsonIsParsed()
    {
        const string json = """
            {"schema_version":1,"tasks":[
              {"task":"gen","input":"text","typical_outputs":["audio_output"],"aliases":["music","sfx"]},
              {"task":"asr","input":"audio","typical_outputs":["text_output","word_timestamps"],"aliases":[]}
            ]}
            """;
        var tasks = CatalogJson.ParseTasks(json);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("gen", tasks[0].Task);
        Assert.Equal("text", tasks[0].Input);
        Assert.Equal(["music", "sfx"], tasks[0].Aliases);
        Assert.Equal(["gen", "music", "sfx"], tasks[0].AllTokens);
        Assert.Equal(["audio_output"], tasks[0].TypicalOutputs);
        Assert.Empty(tasks[1].Aliases);
    }

    [Fact]
    public void FallbackCatalogCoversEveryCanonicalToken()
    {
        // A caller on a shim that predates the task catalog must still see all
        // fourteen tasks, or it would wrongly conclude a task is unsupported.
        Assert.Equal(AudioCppTaskKinds.Canonical, AudioCppTaskCatalog.Fallback.Select(task => task.Task));
        Assert.All(AudioCppTaskCatalog.Fallback,
            task => Assert.Contains(task.Input, new[] { "audio", "text", "audio+text" }));
    }
}
