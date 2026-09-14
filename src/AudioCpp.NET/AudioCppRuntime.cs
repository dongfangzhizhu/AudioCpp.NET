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

public sealed record AsrRequest
{
    public required ReadOnlyMemory<float> Audio { get; init; }
    public required int SampleRate { get; init; }
    public int Channels { get; init; } = 1;
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

public sealed record AudioBuffer(ReadOnlyMemory<float> Samples, int SampleRate, int Channels);

public sealed record AudioCppBuildInfo(uint AbiMajor, uint AbiMinor, string ShimVersion, string AudioCppCommit, string Backend, ulong Capabilities);
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
        if (!string.Equals(options.Backend, buildInfo.Backend, StringComparison.OrdinalIgnoreCase))
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
        return new AudioCppModel(handle);
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
