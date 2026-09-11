using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

public sealed class AudioCppRuntimeTests
{
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
