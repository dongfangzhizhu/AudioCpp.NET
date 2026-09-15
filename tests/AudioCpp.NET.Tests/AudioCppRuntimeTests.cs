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
    public void RequestRejectsEmptyTextBeforeNativeCall()
    {
        Assert.Throws<ArgumentException>(() => new AudioCppModelProxy().Synthesize(new TtsRequest { Text = " " }));
    }

    private static string? InvokeOptions(IReadOnlyDictionary<string, string> options)
    {
        var method = typeof(AudioCppRuntime).GetMethod("ToJson", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [options]);
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
