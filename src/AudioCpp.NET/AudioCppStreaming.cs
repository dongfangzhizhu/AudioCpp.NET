namespace AudioCpp.NET;

/// <summary>How a buffer is fed into a streaming session.</summary>
public sealed record AudioCppStreamingOptions
{
    public required string Task { get; init; }
    public required int SampleRate { get; init; }
    public int Channels { get; init; } = 1;
    public int ChunkMilliseconds { get; init; } = AudioCppStreaming.DefaultChunkMilliseconds;
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>One chunk of PCM scheduled for a streaming session.</summary>
public sealed record AudioCppStreamChunk(long Offset, float[] Samples, int ActualSamples)
{
    /// <summary>Samples appended to satisfy a loader that demands fixed-size windows.</summary>
    public int PaddedSamples => Samples.Length - ActualSamples;
}

/// <summary>A stream event paired with the offset of the chunk that produced it.</summary>
public sealed record AudioCppStreamEventBatch(long ChunkOffset, AudioCppStreamEvent Event);

/// <summary>Everything a buffer-driven streaming run produced.</summary>
public sealed record AudioCppStreamReport(
    AudioCppStreamInfo Info,
    int ChunkSamples,
    IReadOnlyList<AudioCppStreamChunk> Chunks,
    IReadOnlyList<AudioCppStreamEventBatch> Events,
    AudioCppTaskResult Result)
{
    public int PaddedTailSamples => Chunks.Count == 0 ? 0 : Chunks[^1].PaddedSamples;
    public bool PaddedTail => PaddedTailSamples > 0;

    /// <summary>Events that carried a payload; the empty per-chunk polls are dropped.</summary>
    public IReadOnlyList<AudioCppStreamEventBatch> ContentEvents => Events.Where(batch => batch.Event.HasContent).ToArray();
}

/// <summary>
/// Drives an <see cref="AudioCppStreamSession"/> over a complete PCM buffer: resolves the
/// chunk size from the session policy, slices the buffer, zero-pads a short tail when the
/// loader demands fixed-size windows, and collects the events each chunk produced.
/// </summary>
public static class AudioCppStreaming
{
    public const int DefaultChunkMilliseconds = 100;

    /// <summary>Chunk size in samples: the policy wins, otherwise milliseconds at the input rate.</summary>
    public static int ResolveChunkSamples(AudioCppStreamPolicy policy, int sampleRate, int chunkMilliseconds = DefaultChunkMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (sampleRate <= 0) throw new ArgumentException("SampleRate must be positive.", nameof(sampleRate));
        if (chunkMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(chunkMilliseconds), "ChunkMilliseconds must be positive.");
        if (policy.PreferredChunkSamples > 0) return (int)Math.Min(policy.PreferredChunkSamples, int.MaxValue);
        var samples = (long)sampleRate * chunkMilliseconds / 1000;
        return (int)Math.Clamp(samples, 1, int.MaxValue);
    }

    /// <summary>
    /// Slices <paramref name="samples"/> into chunks. The tail is zero-padded only when
    /// <paramref name="policy"/> advertises a preferred chunk size, because those loaders
    /// (e.g. Silero's 512-sample window) reject short chunks.
    /// </summary>
    public static IReadOnlyList<AudioCppStreamChunk> PlanChunks(
        ReadOnlyMemory<float> samples, AudioCppStreamPolicy policy, int sampleRate,
        int chunkMilliseconds = DefaultChunkMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var chunkSamples = ResolveChunkSamples(policy, sampleRate, chunkMilliseconds);
        return PlanChunks(samples, chunkSamples, policy.PreferredChunkSamples > 0);
    }

    public static IReadOnlyList<AudioCppStreamChunk> PlanChunks(
        ReadOnlyMemory<float> samples, int chunkSamples, bool padTail)
    {
        if (samples.IsEmpty) throw new ArgumentException("Samples must not be empty.", nameof(samples));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples), "ChunkSamples must be positive.");
        var chunks = new List<AudioCppStreamChunk>();
        for (var offset = 0; offset < samples.Length; offset += chunkSamples)
        {
            var length = Math.Min(chunkSamples, samples.Length - offset);
            if (length < chunkSamples && padTail)
            {
                var padded = new float[chunkSamples];
                samples.Slice(offset, length).CopyTo(padded);
                chunks.Add(new AudioCppStreamChunk(offset, padded, length));
            }
            else
            {
                chunks.Add(new AudioCppStreamChunk(offset, samples.Slice(offset, length).ToArray(), length));
            }
        }
        return chunks;
    }

    /// <summary>Validates a buffer streaming request. Exposed so callers can fail fast
    /// before a session is opened.</summary>
    public static void ValidateBuffer(ReadOnlyMemory<float> samples, AudioCppStreamingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Task);
        if (samples.IsEmpty) throw new ArgumentException("Samples must not be empty.", nameof(samples));
        if (options.SampleRate <= 0) throw new ArgumentException("SampleRate must be positive.", nameof(options));
        if (options.Channels <= 0) throw new ArgumentException("Channels must be positive.", nameof(options));
    }

    /// <summary>
    /// Streams the whole buffer through a fresh session and returns the collected events plus
    /// the final structured result. <paramref name="onStarted"/> fires before the first chunk so
    /// callers can log the negotiated policy; <paramref name="onEvent"/> fires per event.
    /// </summary>
    public static AudioCppStreamReport Run(
        AudioCppModel model, ReadOnlyMemory<float> samples, AudioCppStreamingOptions options,
        Action<AudioCppStreamInfo, int>? onStarted = null, Action<AudioCppStreamEventBatch>? onEvent = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateBuffer(samples, options);
        using var session = model.StartStreaming(options.Task, options.Options);
        var info = session.Info;
        var chunkSamples = ResolveChunkSamples(info.Policy, options.SampleRate, options.ChunkMilliseconds);
        var chunks = PlanChunks(samples, info.Policy, options.SampleRate, options.ChunkMilliseconds);
        onStarted?.Invoke(info, chunkSamples);
        var events = new List<AudioCppStreamEventBatch>();
        foreach (var chunk in chunks)
        {
            foreach (var streamEvent in session.PushPcm(chunk.Samples, options.SampleRate, options.Channels))
            {
                var batch = new AudioCppStreamEventBatch(chunk.Offset, streamEvent);
                events.Add(batch);
                onEvent?.Invoke(batch);
            }
        }
        return new AudioCppStreamReport(info, chunkSamples, chunks, events, session.Finish());
    }
}