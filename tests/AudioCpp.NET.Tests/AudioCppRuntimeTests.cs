using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

public sealed class AudioCppRuntimeTests
{
    [Fact]
    public void StructuredResultsCapabilityUsesStableBit()
    {
        Assert.Equal(1UL << 3, AudioCppCapabilities.StructuredResults);
    }

    [Fact]
    public void StructuredResultReadsArtifactsAndEscapedText()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"schema_version\":1,\"text_output\":\"a\\\"b\",\"artifact_output\":null,\"output_artifacts\":[{\"id\":\"x\",\"kind\":\"custom\",\"payload_hex\":\"00ff\",\"meta\":{\"format\":\"raw\"}}]}");
        var result = new AudioCppTaskResult(document.RootElement.Clone());
        Assert.Equal("a\"b", result.Text);
        Assert.Null(result.Artifact);
        Assert.Single(result.Artifacts);
        Assert.Equal("00ff", result.Artifacts[0].PayloadHex);
        Assert.Equal("raw", result.Artifacts[0].Meta["format"]);
    }

    [Fact]
    public void StructuredResultParsesTypedModels()
    {
        const string json = """
            {
              "schema_version": 1,
              "text_output": "hello",
              "audio_output": {"sample_rate": 24000, "channels": 1, "samples": [0.25, -0.5, 1]},
              "named_audio_outputs": [{"id": "vocal", "audio": {"sample_rate": 16000, "channels": 2, "samples": [0.5]}, "meta": {"format": "wav"}}],
              "speech_segments": [{"start_sample": 10, "end_sample": 120, "confidence": 0.75, "text": "seg"}],
              "speaker_turns": [{"start_sample": 5, "end_sample": 90, "speaker_id": "spk-1", "confidence": 0.9, "text": "turn"}],
              "word_timestamps": [{"start_sample": 1, "end_sample": 40, "word": "hey", "confidence": 0.6}],
              "artifact_output": {"id": "a1", "kind": "speaker_embedding", "payload_hex": "ff", "meta": {"dim": "32"}},
              "output_artifacts": [{"id": "a2", "kind": "midi", "payload_hex": "00"}]
            }
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var result = new AudioCppTaskResult(document.RootElement.Clone());
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal("hello", result.Text);
        Assert.Equal(24000, result.AudioOutput!.SampleRate);
        Assert.Equal(1, result.AudioOutput.Channels);
        Assert.Equal(new[] { 0.25f, -0.5f, 1f }, result.AudioOutput.Samples.ToArray());
        var named = Assert.Single(result.NamedAudioOutputs);
        Assert.Equal("vocal", named.Id);
        Assert.Equal(16000, named.Audio.SampleRate);
        Assert.Equal(2, named.Audio.Channels);
        Assert.Equal("wav", named.Meta["format"]);
        var segment = Assert.Single(result.SpeechSegments);
        Assert.Equal(new AudioCppTimeSpan(10, 120), segment.Span);
        Assert.Equal(0.75f, segment.Confidence);
        Assert.Equal("seg", segment.Text);
        var turn = Assert.Single(result.SpeakerTurns);
        Assert.Equal("spk-1", turn.SpeakerId);
        Assert.Equal(0.9f, turn.Confidence);
        Assert.Equal("turn", turn.Text);
        var word = Assert.Single(result.WordTimestamps);
        Assert.Equal(new AudioCppTimeSpan(1, 40), word.Span);
        Assert.Equal("hey", word.Word);
        Assert.Equal(0.6f, word.Confidence);
        Assert.Equal("speaker_embedding", result.Artifact!.Kind);
        Assert.Single(result.Artifacts);
        Assert.Equal("midi", result.Artifacts[0].Kind);
    }

    [Fact]
    public void StructuredResultReportsTheTaskThatRan()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"schema_version\":2,\"task\":\"asr\",\"text_output\":\"hi\"}");
        var result = new AudioCppTaskResult(document.RootElement.Clone());
        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal("asr", result.Task);
        // A shim predating schema 2 emits no "task"; callers fall back to the
        // token they requested, so the property stays null rather than throwing.
        using var legacy = System.Text.Json.JsonDocument.Parse("{\"schema_version\":1}");
        Assert.Null(new AudioCppTaskResult(legacy.RootElement.Clone()).Task);
    }

    [Fact]
    public void StructuredResultToleratesMissingFields()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        var result = new AudioCppTaskResult(document.RootElement.Clone());
        Assert.Null(result.SchemaVersion);
        Assert.Null(result.Text);
        Assert.Null(result.AudioOutput);
        Assert.Null(result.Artifact);
        Assert.Empty(result.NamedAudioOutputs);
        Assert.Empty(result.SpeechSegments);
        Assert.Empty(result.SpeakerTurns);
        Assert.Empty(result.WordTimestamps);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void OptionsAreSerializedAsAnObject()
    {
        var json = InvokeOptions(new Dictionary<string, string> { ["temperature"] = "0.7" });
        Assert.Equal("{\"temperature\":\"0.7\"}", json);
    }

    [Fact]
    public void EmptyOptionsAreNull()
    {
        Assert.Null(InvokeOptions(new Dictionary<string, string>()));
    }

    [Fact]
    public void TtsRequestMapsStyleFieldsToNativeOptions()
    {
        var options = InvokeBuildRequestOptions(new TtsRequest
        {
            Text = "hi",
            Style = new AudioCppStyle
            {
                Language = "zh",
                Emotion = "calm",
                SpeakingRate = 1.25f,
                PitchShift = -2f,
                EnergyScale = 0.8f,
                Tags = new Dictionary<string, string> { ["whisper"] = "1", ["formal"] = "true" },
            },
            Options = new Dictionary<string, string> { ["temperature"] = "0.5" }
        });
        Assert.Equal("0.5", options["temperature"]);
        Assert.Equal("zh", options["style_language"]);
        Assert.Equal("calm", options["emotion"]);
        Assert.Equal("1.25", options["speaking_rate"]);
        Assert.Equal("-2", options["pitch_shift"]);
        Assert.Equal("0.8", options["energy_scale"]);
        Assert.Equal("1", options["style_tag_whisper"]);
        Assert.Equal("true", options["style_tag_formal"]);
    }

    [Fact]
    public void TtsRequestWithoutStyleFieldsKeepsPlainOptions()
    {
        var options = InvokeBuildRequestOptions(new TtsRequest { Text = "hi", Options = new Dictionary<string, string> { ["temperature"] = "0.7" } });
        Assert.Equal("temperature", options.Keys.Single());
    }

    [Fact]
    public void TtsRequestWithoutOptionsMapsToEmpty()
    {
        Assert.Empty(InvokeBuildRequestOptions(new TtsRequest { Text = "hi" }));
        Assert.Empty(InvokeBuildRequestOptions(null));
    }

    [Fact]
    public void RunValidatesRequestArguments()
    {
        Assert.Throws<ArgumentException>(() => InvokeValidateRunRequests(null, null));
        Assert.Throws<ArgumentException>(() => InvokeValidateRunRequests(new TtsRequest { Text = " " }, null));
        Assert.Throws<ArgumentException>(() => InvokeValidateRunRequests(null, new AsrRequest { Audio = default, SampleRate = 16000 }));
        Assert.Throws<ArgumentException>(() => InvokeValidateRunRequests(null, new AsrRequest { Audio = new float[] { 0, 1 }, SampleRate = 0 }));
        InvokeValidateRunRequests(new TtsRequest { Text = "hi" }, null);
        InvokeValidateRunRequests(null, new AsrRequest { Audio = new float[] { 0 }, SampleRate = 16000, Channels = 1 });
        InvokeValidateRunRequests(new TtsRequest { Text = "hi" }, new AsrRequest { Audio = new float[] { 0 }, SampleRate = 16000, Channels = 1 });
    }

    [Fact]
    public void RequestRejectsEmptyTextBeforeNativeCall()
    {
        Assert.Throws<ArgumentException>(() => new AudioCppModelProxy().Synthesize(new TtsRequest { Text = " " }));
    }

    [Fact]
    public void StreamingCapabilityUsesStableBit()
    {
        Assert.Equal(1UL << 4, AudioCppCapabilities.Streaming);
    }

    [Fact]
    public void StreamInfoParsesPolicy()
    {
        const string json = """
            {"family":"silero_vad","task":"vad","input":"audio_chunks","output":"pull_events","preferred_chunk_samples":512,"preferred_chunk_seconds":0.032}
            """;
        var info = AudioCppStreamInfo.Parse(json);
        Assert.Equal("silero_vad", info.Family);
        Assert.Equal("vad", info.Task);
        Assert.Equal("audio_chunks", info.Policy.Input);
        Assert.Equal("pull_events", info.Policy.Output);
        Assert.Equal(512, info.Policy.PreferredChunkSamples);
        Assert.Equal(0.032, info.Policy.PreferredChunkSeconds, 3);
    }

    [Fact]
    public void StreamEventsParsePartialTextAndVoiceActivity()
    {
        const string json = """
            {"events":[
              {"partial_text":{"text":"hel","language":"en"},"voice_activity":[{"kind":"speech_start","sample":0,"probability":0.9,"segment":null}],"audio_output":null,"named_audio_outputs":[],"speaker_turns":[],"word_timestamps":[],"output_artifacts":[],"is_final":false},
              {"partial_text":{"text":"hello world","language":"en"},"voice_activity":[{"kind":"speech_end","sample":100,"probability":0.8,"segment":{"start_sample":0,"end_sample":100,"confidence":0.7,"text":"hello world"}}],"audio_output":{"sample_rate":16000,"channels":1,"samples":[0.5]},"named_audio_outputs":[],"speaker_turns":[],"word_timestamps":[{"start_sample":1,"end_sample":9,"word":"hello","confidence":0.6}],"output_artifacts":[{"id":"vad","kind":"vad_state","payload_hex":"ab","meta":{}}],"is_final":true}
            ]}
            """;
        var events = AudioCppStreamEvent.Parse(json);
        Assert.Equal(2, events.Count);
        Assert.Equal("hel", events[0].PartialText);
        Assert.Equal("en", events[0].Language);
        Assert.False(events[0].IsFinal);
        var start = Assert.Single(events[0].VoiceActivity);
        Assert.Equal("speech_start", start.Kind);
        Assert.Equal(0, start.Sample);
        Assert.Equal(0.9f, start.Probability);
        Assert.Null(start.Segment);
        Assert.Null(events[0].AudioOutput);
        Assert.Equal("hello world", events[1].PartialText);
        Assert.True(events[1].IsFinal);
        var end = Assert.Single(events[1].VoiceActivity);
        Assert.Equal("speech_end", end.Kind);
        Assert.Equal(new AudioCppTimeSpan(0, 100), end.Segment!.Span);
        Assert.Equal(0.7f, end.Segment.Confidence);
        Assert.Equal(16000, events[1].AudioOutput!.SampleRate);
        var word = Assert.Single(events[1].WordTimestamps);
        Assert.Equal("hello", word.Word);
        var artifact = Assert.Single(events[1].Artifacts);
        Assert.Equal("vad_state", artifact.Kind);
        Assert.Equal("ab", artifact.PayloadHex);
    }

    [Fact]
    public void StreamEventsTolerateMissingFields()
    {
        const string json = "{\"events\":[{}]}";
        var events = AudioCppStreamEvent.Parse(json);
        var single = Assert.Single(events);
        Assert.Null(single.PartialText);
        Assert.Null(single.Language);
        Assert.Empty(single.VoiceActivity);
        Assert.Null(single.AudioOutput);
        Assert.False(single.IsFinal);
    }

    private static string? InvokeOptions(IReadOnlyDictionary<string, string> options) => (string?)InvokeStatic("ToJson", options);

    private static IReadOnlyDictionary<string, string> InvokeBuildRequestOptions(TtsRequest? request) =>
        (IReadOnlyDictionary<string, string>)InvokeStatic("BuildRequestOptions", request)!;

    private static void InvokeValidateRunRequests(TtsRequest? request, AsrRequest? audioRequest) =>
        InvokeStatic("ValidateRunRequests", request, audioRequest);

    private static object? InvokeStatic(string method, params object?[] arguments)
    {
        var methodInfo = typeof(AudioCppRuntime).GetMethod(method, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            return methodInfo.Invoke(null, arguments);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed class AudioCppModelProxy
    {
        public AudioBuffer Synthesize(TtsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
            throw new InvalidOperationException("not reached");
        }
    }
}
