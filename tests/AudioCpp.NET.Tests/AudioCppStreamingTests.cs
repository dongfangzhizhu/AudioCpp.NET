using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

public sealed class AudioCppStreamingTests
{
    private static AudioCppStreamPolicy Policy(long preferredChunkSamples) =>
        new("audio_chunks", "pull_events", preferredChunkSamples, preferredChunkSamples <= 0 ? 0 : preferredChunkSamples / 16000d);

    [Fact]
    public void StreamingOptionsUseStreamingDefaults()
    {
        var options = new AudioCppStreamingOptions { Task = "vad", SampleRate = 16000 };
        Assert.Equal(1, options.Channels);
        Assert.Equal(100, options.ChunkMilliseconds);
        Assert.Equal(AudioCppStreaming.DefaultChunkMilliseconds, options.ChunkMilliseconds);
        Assert.Null(options.Options);
    }

    [Fact]
    public void ResolveChunkSamplesPrefersPolicyOverMilliseconds()
    {
        Assert.Equal(512, AudioCppStreaming.ResolveChunkSamples(Policy(512), 16000, 100));
        Assert.Equal(1024, AudioCppStreaming.ResolveChunkSamples(Policy(1024), 44100, 10));
    }

    [Fact]
    public void ResolveChunkSamplesFallsBackToMilliseconds()
    {
        Assert.Equal(1600, AudioCppStreaming.ResolveChunkSamples(Policy(0), 16000, 100));
        Assert.Equal(512, AudioCppStreaming.ResolveChunkSamples(Policy(0), 16000, 32));
        Assert.Equal(441, AudioCppStreaming.ResolveChunkSamples(Policy(0), 44100, 10));
    }

    [Fact]
    public void ResolveChunkSamplesKeepsAtLeastOneSampleAndClampsHugePolicies()
    {
        Assert.Equal(8, AudioCppStreaming.ResolveChunkSamples(Policy(0), 8000, 1));
        Assert.Equal(1, AudioCppStreaming.ResolveChunkSamples(Policy(0), 1, 1));
        Assert.Equal(int.MaxValue, AudioCppStreaming.ResolveChunkSamples(Policy(long.MaxValue), 16000, 100));
    }

    [Fact]
    public void ResolveChunkSamplesValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AudioCppStreaming.ResolveChunkSamples(null!, 16000));
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.ResolveChunkSamples(Policy(0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioCppStreaming.ResolveChunkSamples(Policy(0), 16000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioCppStreaming.ResolveChunkSamples(Policy(0), 16000, -5));
    }

    [Fact]
    public void PlanChunksPadsTailWhenPolicyDemandsFixedWindows()
    {
        var samples = new float[1200];
        var chunks = AudioCppStreaming.PlanChunks(samples, Policy(512), 16000);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new long[] { 0, 512, 1024 }, chunks.Select(chunk => chunk.Offset).ToArray());
        Assert.All(chunks, chunk => Assert.Equal(512, chunk.Samples.Length));
        Assert.Equal(new[] { 512, 512, 176 }, chunks.Select(chunk => chunk.ActualSamples).ToArray());
        Assert.Equal(new[] { 0, 0, 336 }, chunks.Select(chunk => chunk.PaddedSamples).ToArray());
        Assert.True(chunks[2].PaddedSamples > 0);
    }

    [Fact]
    public void PlanChunksCopiesSamplesWithoutSharingBuffers()
    {
        var samples = Enumerable.Range(1, 5).Select(value => (float)value).ToArray();
        var chunks = AudioCppStreaming.PlanChunks(samples, 2, padTail: false);

        samples[0] = 99f;
        Assert.Equal(new float[] { 1, 2 }, chunks[0].Samples);
        Assert.Equal(new float[] { 5 }, chunks[2].Samples);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(2, chunks[1].ActualSamples);
    }

    [Fact]
    public void PlanChunksLeavesShortTailUnpaddedWhenPolicyIsAdaptive()
    {
        var chunks = AudioCppStreaming.PlanChunks(new float[10], Policy(0), 16000, 1);

        var single = Assert.Single(chunks);
        Assert.Equal(10, single.ActualSamples);
        Assert.Equal(10, single.Samples.Length);
        Assert.Equal(0, single.PaddedSamples);
    }

    [Fact]
    public void PlanChunksSplitsAdaptiveBuffersAtTheFallbackChunkSize()
    {
        var chunks = AudioCppStreaming.PlanChunks(new float[100], Policy(0), 16000);

        Assert.Equal(1600, AudioCppStreaming.ResolveChunkSamples(Policy(0), 16000));
        var single = Assert.Single(chunks);
        Assert.Equal(100, single.ActualSamples);
        Assert.Equal(100, single.Samples.Length);
    }

    [Fact]
    public void PlanChunksAlignsTailForFixedWindowLoaders()
    {
        var chunks = AudioCppStreaming.PlanChunks(new float[400], 512, padTail: true);

        var single = Assert.Single(chunks);
        Assert.Equal(400, single.ActualSamples);
        Assert.Equal(112, single.PaddedSamples);
        Assert.Equal(512, single.Samples.Length);
    }

    [Fact]
    public void PlanChunksValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.PlanChunks(ReadOnlyMemory<float>.Empty, 512, padTail: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioCppStreaming.PlanChunks(new float[4], 0, padTail: true));
        Assert.Throws<ArgumentNullException>(() => AudioCppStreaming.PlanChunks(new float[4], null!, 16000));
    }

    [Fact]
    public void StreamReportExposesPaddedTail()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"text_output\":\"ok\"}");
        var chunks = AudioCppStreaming.PlanChunks(new float[600], 512, padTail: true);
        var report = new AudioCppStreamReport(
            new AudioCppStreamInfo("silero_vad", "vad", Policy(512)), 512, chunks, [],
            new AudioCppTaskResult(document.RootElement.Clone()));

        Assert.True(report.PaddedTail);
        Assert.Equal(424, report.PaddedTailSamples);
        Assert.Equal("ok", report.Result.Text);
        Assert.Equal("silero_vad", report.Info.Family);
    }

    [Fact]
    public void StreamReportWithoutChunksHasNoPaddedTail()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        var report = new AudioCppStreamReport(
            new AudioCppStreamInfo("", "", Policy(0)), 0, [], [], new AudioCppTaskResult(document.RootElement.Clone()));

        Assert.False(report.PaddedTail);
        Assert.Equal(0, report.PaddedTailSamples);
    }

    [Fact]
    public void ValidateBufferRejectsEmptyOrInvertedArguments()
    {
        var valid = new AudioCppStreamingOptions { Task = "vad", SampleRate = 16000 };
        Assert.Throws<ArgumentNullException>(() => AudioCppStreaming.ValidateBuffer(new float[1], null!));
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.ValidateBuffer(new float[1],
            new AudioCppStreamingOptions { Task = " ", SampleRate = 16000 }));
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.ValidateBuffer(ReadOnlyMemory<float>.Empty, valid));
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.ValidateBuffer(new float[1],
            new AudioCppStreamingOptions { Task = "vad", SampleRate = 0 }));
        Assert.Throws<ArgumentException>(() => AudioCppStreaming.ValidateBuffer(new float[1],
            new AudioCppStreamingOptions { Task = "vad", SampleRate = 16000, Channels = 0 }));
        AudioCppStreaming.ValidateBuffer(new float[1], valid);
    }

    [Fact]
    public void RunRejectsNullModelBeforeOpeningASession()
    {
        Assert.Throws<ArgumentNullException>(() => AudioCppStreaming.Run(null!, new float[1],
            new AudioCppStreamingOptions { Task = "vad", SampleRate = 16000 }));
    }
}