using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

public sealed class WaveFileTests
{
    [Fact]
    public void Pcm16RoundTripPreservesFormatAndSamples()
    {
        using var stream = new MemoryStream();
        WaveFile.Write(stream, new AudioBuffer(new float[] { -1f, -0.5f, 0f, 0.5f, 1f }, 16000, 1));
        stream.Position = 0;

        var result = WaveFile.Read(stream);

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.Equal(5, result.Samples.Length);
        Assert.InRange(result.Samples.Span[3], 0.49f, 0.51f);
    }

    [Fact]
    public void ReadRejectsNonWaveData()
    {
        using var stream = new MemoryStream("not a wave file"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WaveFile.Read(stream));
    }
}