using System.Text.Json;
using AudioCpp.NET.Interop;

namespace AudioCpp.NET;

public sealed record AudioCppRuntimeOptions
{
    public string? NativeLibraryPath { get; init; }
    public string Backend { get; init; } = "cpu";
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
}

public sealed record AudioBuffer(ReadOnlyMemory<float> Samples, int SampleRate, int Channels);

public sealed record AudioCppBuildInfo(uint AbiMajor, uint AbiMinor, string ShimVersion, string AudioCppCommit, string Backend, ulong Capabilities);

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
        if (!string.Equals(options.Backend, buildInfo.Backend, StringComparison.OrdinalIgnoreCase))
            throw new AudioCppAbiMismatchException($"Requested backend '{options.Backend}', but the native library provides '{buildInfo.Backend}'. Load the matching runtime package.");
        return new AudioCppRuntime(buildInfo);
    }

    public AudioCppModel LoadModel(AudioCppModelOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        var (handle, error) = InteropOperations.LoadModel(options.ModelPath, options.FamilyHint, BuildInfo.Backend,
            options.Device, options.Threads, ToJson(options.LoadOptions));
        if (handle.IsInvalid) { handle.Dispose(); throw new AudioCppLoadException(error); }
        return new AudioCppModel(handle);
    }

    public void Dispose() => _disposed = true;

    internal static string? ToJson(IReadOnlyDictionary<string, string>? values) => values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values);
}

public sealed class AudioCppModel : IDisposable
{
    private readonly SafeModelHandle _handle;
    private bool _disposed;

    internal AudioCppModel(SafeModelHandle handle) => _handle = handle;

    public AudioBuffer Synthesize(TtsRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
        if (!request.ReferencePcm.IsEmpty && request.ReferenceSampleRate <= 0) throw new ArgumentException("ReferenceSampleRate must be positive when ReferencePcm is supplied.", nameof(request));
        try
        {
            var result = InteropOperations.Synthesize(_handle, request.Task, request.Text, request.VoiceId,
                request.ReferencePcm.Span, request.ReferenceSampleRate, AudioCppRuntime.ToJson(request.Options));
            return new AudioBuffer(result.Samples, result.SampleRate, result.Channels);
        }
        catch (Exception exception) when (exception.GetType().Name == "NativeCallException")
        {
            throw new AudioCppInferenceException(exception.Message, exception);
        }
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
public sealed class AudioCppLoadException(string message) : AudioCppException(message);
public sealed class AudioCppInferenceException(string message, Exception? inner = null) : AudioCppException(message, inner);
