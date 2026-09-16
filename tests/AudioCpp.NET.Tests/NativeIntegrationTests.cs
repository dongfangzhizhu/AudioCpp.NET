using AudioCpp.NET;
using Xunit;

namespace AudioCpp.NET.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that turns itself into a skip when the native
/// shim (and, for run tests, an installed model package) is not available, so the
/// suite stays green on machines that only build the managed assemblies.
/// </summary>
public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute(bool needsModel = false)
    {
        if (NativeIntegration.LibraryPath is null)
            Skip = "Set AUDIOCPP_TEST_NATIVE to the built native shim to enable integration tests.";
        else if (needsModel && NativeIntegration.ModelDirectory is null)
            Skip = "Set AUDIOCPP_TEST_MODEL to an installed model package to enable this integration test.";
    }
}

/// <summary>
/// Locates the native shim and model package named by the environment:
/// AUDIOCPP_TEST_NATIVE, AUDIOCPP_TEST_MODEL, and optionally AUDIOCPP_TEST_TASK /
/// AUDIOCPP_TEST_STREAM_TASK to pick the task exercised by the run tests.
/// </summary>
internal static class NativeIntegration
{
    /// <summary>Path to audiocpp_dotnet_native.{dll,so,dylib}.</summary>
    internal static string? LibraryPath { get; } = ExistingPath("AUDIOCPP_TEST_NATIVE", File.Exists);

    /// <summary>Directory or manifest of one installed model package.</summary>
    internal static string? ModelDirectory { get; } = ExistingPath("AUDIOCPP_TEST_MODEL", path => File.Exists(path) || Directory.Exists(path));

    /// <summary>Task the run tests exercise; must match <see cref="ModelDirectory"/>.</summary>
    internal static string RunTask { get; } = Normalized("AUDIOCPP_TEST_TASK") ?? AudioCppTaskKinds.Asr;

    /// <summary>Task for the streaming test; unset disables it (needs a streaming-capable package).</summary>
    internal static string? StreamTask { get; } = Normalized("AUDIOCPP_TEST_STREAM_TASK");

    internal static AudioCppRuntime CreateRuntime() =>
        AudioCppRuntime.Create(new AudioCppRuntimeOptions { NativeLibraryPath = LibraryPath });

    internal static AudioCppModel LoadModel(AudioCppRuntime runtime) =>
        runtime.LoadModel(new AudioCppModelOptions { ModelPath = ModelDirectory! });

    private static string? Normalized(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return AudioCppTaskKinds.Normalize(value) ?? value;
    }

    private static string? ExistingPath(string variable, Func<string, bool> exists)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        return exists(value) ? Path.GetFullPath(value) : null;
    }
}

/// <summary>
/// Exercises the real shim: ABI/capability advertisement, the native task and
/// loader catalogs, family derivation, and a structured run against an installed
/// model. Run it with:
/// <code>
/// $env:AUDIOCPP_TEST_NATIVE     = ".../audiocpp_dotnet_native.dll"
/// $env:AUDIOCPP_TEST_MODEL      = ".../Citrinet-ASR-GGUF"
/// $env:AUDIOCPP_TEST_TASK       = "asr"
/// $env:AUDIOCPP_TEST_STREAM_TASK = "vad"   # plus a matching model, optional
/// dotnet test -c Release
/// </code>
/// </summary>
public sealed class NativeIntegrationTests
{
    [NativeFact]
    public void AbiAdvertisesEveryCapabilityTheWrappersRelyOn()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        var info = runtime.BuildInfo;

        Assert.Equal(1u, info.AbiMajor);
        Assert.True(info.AbiMinor >= 3, $"the wrappers need structured-result schema 2 / abi 1.3+, reported 1.{info.AbiMinor}");
        Assert.False(string.IsNullOrWhiteSpace(info.ShimVersion));
        Assert.Equal("78d47706c30ef215ba9ad3559baff309efeb5260", info.AudioCppCommit);

        foreach (var (bit, name) in new (ulong Bit, string Name)[]
        {
            (AudioCppCapabilities.Synthesize, nameof(AudioCppCapabilities.Synthesize)),
            (AudioCppCapabilities.Transcribe, nameof(AudioCppCapabilities.Transcribe)),
            (AudioCppCapabilities.ModelManager, nameof(AudioCppCapabilities.ModelManager)),
            (AudioCppCapabilities.StructuredResults, nameof(AudioCppCapabilities.StructuredResults)),
            (AudioCppCapabilities.Streaming, nameof(AudioCppCapabilities.Streaming)),
            (AudioCppCapabilities.TaskCatalog, nameof(AudioCppCapabilities.TaskCatalog)),
            (AudioCppCapabilities.Artifacts, nameof(AudioCppCapabilities.Artifacts)),
            (AudioCppCapabilities.ExecOptions, nameof(AudioCppCapabilities.ExecOptions)),
        })
            Assert.True((info.Capabilities & bit) != 0, $"missing capability {name}");
    }

    [NativeFact]
    public void NativeTaskCatalogMatchesTheManagedTable()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        var tasks = runtime.ListTaskKinds();

        Assert.Equal(AudioCppTaskKinds.Canonical, tasks.Select(task => task.Task));

        // The alias mapping must survive the round trip through the shim: an alias
        // the shim accepts but the managed table does not (or vice versa) is exactly
        // how a model ends up unreachable.
        var aliases = tasks
            .SelectMany(task => task.Aliases.Select(alias => (Alias: alias, Task: task.Task)))
            .ToDictionary(item => item.Alias, item => item.Task, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(AudioCppTaskKinds.Aliases.Count, aliases.Count);
        foreach (var (alias, canonical) in AudioCppTaskKinds.Aliases)
        {
            Assert.True(aliases.ContainsKey(alias), $"the shim does not accept the alias '{alias}'");
            Assert.Equal(canonical, aliases[alias]);
        }

        foreach (var task in tasks)
        {
            Assert.Contains(task.Input, new[] { "audio", "text", "audio+text" });
            Assert.NotEmpty(task.TypicalOutputs);
            Assert.Equal(task.Task, task.AllTokens[0]);
        }
    }

    [NativeFact]
    public void LoadersOnlyAdvertiseTokensTheTaskParserAccepts()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        var loaders = runtime.ListLoaders();

        Assert.NotEmpty(loaders);
        foreach (var loader in loaders)
        {
            Assert.False(string.IsNullOrWhiteSpace(loader.Family));
            Assert.NotEmpty(loader.Tasks);
            foreach (var task in loader.Tasks)
                Assert.True(AudioCppTaskKinds.IsKnown(task.Task),
                    $"loader '{loader.Family}' advertises '{task.Task}', which the shim's task parser does not accept");
        }
    }

    [NativeFact]
    public void ResolveFamilyDerivesFromTheLoaderCatalog()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        var families = runtime.LoaderFamilies();

        Assert.NotEmpty(families);
        foreach (var family in families)
        {
            Assert.Equal(family, runtime.ResolveFamily(family));
            Assert.Equal(family, runtime.ResolveFamily($"{family}_q8_0"));
        }
        Assert.Null(runtime.ResolveFamily("no_such_family_package"));
    }

    [NativeFact(needsModel: true)]
    public void StructuredRunEchoesTheTaskItRan()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        using var model = NativeIntegration.LoadModel(runtime);

        var result = model.Run(new AudioCppRunRequest
        {
            Task = NativeIntegration.RunTask,
            Audio = new float[16000],
            SampleRate = 16000,
            Channels = 1,
        });

        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal(NativeIntegration.RunTask, result.Task);
        Assert.Contains($"\"task\":\"{NativeIntegration.RunTask}\"", result.RawJson.GetRawText());
    }

    [NativeFact(needsModel: true)]
    public void StructuredRunAlwaysReportsItsTask()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        using var model = NativeIntegration.LoadModel(runtime);

        // No task token: the shim infers the family from the request shape and must
        // say which one it picked.
        var result = model.Run(new AudioCppRunRequest
        {
            Audio = new float[16000],
            SampleRate = 16000,
            Channels = 1,
        });

        Assert.Equal(2, result.SchemaVersion);
        Assert.NotNull(result.Task);
        Assert.Contains(result.Task!, AudioCppTaskKinds.Canonical);
    }

    [NativeFact(needsModel: true)]
    public void StructuredRunSerializesEveryResultChannel()
    {
        using var runtime = NativeIntegration.CreateRuntime();
        using var model = NativeIntegration.LoadModel(runtime);

        var result = model.Run(new AudioCppRunRequest
        {
            Task = NativeIntegration.RunTask,
            Audio = new float[16000],
            SampleRate = 16000,
            Channels = 1,
        });

        // The reader must never throw on a real payload, and the schema the shim
        // emits today must still carry every channel the managed record exposes.
        foreach (var channel in new[] { "audio_output", "named_audio_outputs", "speech_segments",
                                        "speaker_turns", "word_timestamps", "artifact_output", "output_artifacts" })
            Assert.True(result.RawJson.TryGetProperty(channel, out _), $"structured result is missing '{channel}'");
    }

    [NativeFact(needsModel: true)]
    public void StreamingSessionReportsItsTask()
    {
        if (NativeIntegration.StreamTask is null) return; // no streaming package configured

        using var runtime = NativeIntegration.CreateRuntime();
        using var model = NativeIntegration.LoadModel(runtime);
        var task = NativeIntegration.StreamTask;

        var report = AudioCppStreaming.Run(model, new float[16000], new AudioCppStreamingOptions
        {
            Task = task,
            SampleRate = 16000,
            Channels = 1,
            ChunkMilliseconds = 100,
        });

        Assert.NotEmpty(report.Chunks);
        Assert.Equal(AudioCppTaskKinds.Normalize(task), AudioCppTaskKinds.Normalize(report.Result.Task));
    }
}
