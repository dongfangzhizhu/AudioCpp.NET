using AudioCpp.NET;

return await ConsoleApp.RunAsync(args);

internal static class ConsoleApp
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
            return await InteractiveMenu.RunAsync();

        if (args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "asr" or "tts" or "verify" or "vad" or "tasks" or "run")
        {
            if (!ConfigureHuggingFaceEndpoint(args)) return 1;
            var inferenceNativePath = Option(args, "--native");
            try
            {
                using var runtime = AudioCppRuntime.Create(new AudioCppRuntimeOptions { NativeLibraryPath = inferenceNativePath });
                return args[0] switch
                {
                    "asr" => RunAsr(runtime, args),
                    "tts" => RunTts(runtime, args),
                    "verify" => await VerifyAsync(runtime, args),
                    "vad" => RunVad(runtime, args),
                    "tasks" => RunTaskCatalog(runtime),
                    "run" => RunAny(runtime, args),
                    _ => 1
                };
            }
            catch (AudioCppException exception) { return Fail(exception.Message); }
            catch (DllNotFoundException exception) { return Fail($"Native shim not found: {exception.Message}"); }
        }

        if (args[0] != "models")
            return Fail("Unknown command. Use 'models help'.");

        var localCommand = args.ElementAtOrDefault(1);
        if (localCommand is "help" or null)
        {
            PrintHelp();
            return 0;
        }
        if (localCommand == "path")
            return PrintPath(args);

        var nativePath = Option(args, "--native");
        try
        {
            if (!ConfigureHuggingFaceEndpoint(args)) return 1;
            using var runtime = AudioCppRuntime.Create(new AudioCppRuntimeOptions { NativeLibraryPath = nativePath });
            return args.ElementAtOrDefault(1) switch
            {
                "list" => ListLoaders(runtime),
                "packages" => ListPackages(runtime),
                "download" => Download(runtime, args),
                "verify" => VerifyPackages(args),
                "help" or null => HelpAndSuccess(),
                _ => Fail("Unknown models command. Use 'models help'.")
            };
        }
        catch (AudioCppException exception)
        {
            return Fail(exception.Message);
        }
        catch (DllNotFoundException exception)
        {
            return Fail($"Native shim not found: {exception.Message}");
        }
    }

    private static int ListLoaders(AudioCppRuntime runtime)
    {
        Console.WriteLine($"audio.cpp {runtime.BuildInfo.AudioCppCommit} ({runtime.BuildInfo.Backend})");
        Console.WriteLine($"canonical tasks: {string.Join(", ", AudioCppTaskKinds.Canonical)}");
        foreach (var loader in runtime.ListLoaders())
        {
            // Print the canonical token first so the family -> VoiceTaskKind
            // mapping is visible; the raw model_spec token follows when it differs.
            var tasks = string.Join(", ", loader.Tasks.Select(task =>
            {
                var canonical = AudioCppTaskKinds.Normalize(task.Task);
                var token = canonical is null ? task.Task : canonical == task.Task ? task.Task : $"{canonical}<{task.Task}";
                return task.Modes.Count == 0 ? token : $"{token} ({string.Join('|', task.Modes)})";
            }));
            Console.WriteLine($"{loader.Family}: {tasks}");
        }
        return 0;
    }

    /// <summary>Prints the ABI summary and every task the shim can dispatch.</summary>
    private static int RunTaskCatalog(AudioCppRuntime runtime)
    {
        var info = runtime.BuildInfo;
        Console.WriteLine($"shim {info.ShimVersion}  abi {info.AbiMajor}.{info.AbiMinor}  backend {info.Backend}  audio.cpp {info.AudioCppCommit}");
        Console.WriteLine($"capabilities 0x{info.Capabilities:x}: {string.Join(", ", CapabilityNames(info.Capabilities))}");
        var nativeCatalog = (info.Capabilities & AudioCppCapabilities.TaskCatalog) != 0;
        Console.WriteLine($"task catalog source: {(nativeCatalog ? "native" : "compiled-in fallback (shim predates AUDIOCPP_CAP_TASK_CATALOG)")}");
        foreach (var task in runtime.ListTaskKinds())
        {
            var aliases = task.Aliases.Count == 0 ? "" : $"  aliases: {string.Join(", ", task.Aliases)}";
            Console.WriteLine($"{task.Task,-6} input={task.Input,-10} outputs={string.Join("|", task.TypicalOutputs)}{aliases}");
        }
        return 0;
    }

    private static IEnumerable<string> CapabilityNames(ulong capabilities)
    {
        foreach (var (bit, name) in new (ulong Bit, string Name)[]
        {
            (AudioCppCapabilities.Synthesize, "synthesize"),
            (AudioCppCapabilities.Transcribe, "transcribe"),
            (AudioCppCapabilities.ModelManager, "model-manager"),
            (AudioCppCapabilities.StructuredResults, "structured-results"),
            (AudioCppCapabilities.Streaming, "streaming"),
            (AudioCppCapabilities.TaskCatalog, "task-catalog"),
            (AudioCppCapabilities.Artifacts, "artifacts"),
            (AudioCppCapabilities.ExecOptions, "exec-options"),
        })
            if ((capabilities & bit) != 0) yield return name;
    }

    /// <summary>
    /// Structured run against any task the loaded model supports: text, audio, or
    /// both, plus style, input artifacts and arbitrary options. With --stream the
    /// same request drives a streaming session instead of an offline run.
    /// </summary>
    private static int RunAny(AudioCppRuntime runtime, string[] args)
    {
        var modelPath = Option(args, "--model");
        if (string.IsNullOrWhiteSpace(modelPath))
            return Fail("Usage: run --model PATH [--task TASK] [--text TEXT] [--input AUDIO.wav] [--output AUDIO.wav] [options]. See 'tasks' for the task list.");

        var task = Option(args, "--task");
        if (task is not null && task.Length > 0 && !AudioCppTaskKinds.IsKnown(task))
            return Fail($"Unknown task '{task}'. Known tasks: {string.Join(", ", AudioCppTaskKinds.Canonical)}; " +
                        $"aliases: {string.Join(", ", AudioCppTaskKinds.Aliases.Keys)}.");

        IReadOnlyList<AudioCppInputArtifact>? artifacts;
        IReadOnlyDictionary<string, string>? options;
        IReadOnlyDictionary<string, string>? styleTags;
        AudioCppStyle? style;
        int threads;
        try
        {
            artifacts = ReadArtifacts(args);
            options = Pairs(args, "--option");
            styleTags = Pairs(args, "--style-tag");
            style = ReadStyle(args, styleTags);
            // AudioCppModelOptions.Threads uses 0 for "let the engine decide".
            threads = ParseInt(Option(args, "--threads")) ?? 0;
            if (threads < 0) throw new ArgumentException("--threads must be zero (auto) or a positive integer.");
        }
        catch (ArgumentException exception)
        {
            return Fail(exception.Message);
        }

        var output = Option(args, "--output");
        var rawJson = HasFlag(args, "--json");
        try
        {
            var inputPath = Option(args, "--input");
            AudioBuffer? audio = null;
            if (!string.IsNullOrWhiteSpace(inputPath)) audio = WaveFile.Read(inputPath);
            if (HasFlag(args, "--stream") && audio is null)
                return Fail("--stream requires --input AUDIO.wav.");
            if (HasFlag(args, "--stream"))
            {
                if (ChunkMilliseconds(args) is not { } chunkMilliseconds)
                    return Fail("--chunk-ms must be a positive integer.");
                using var streamingModel = runtime.LoadModel(new AudioCppModelOptions
                {
                    ModelPath = modelPath,
                    FamilyHint = Option(args, "--family"),
                    Threads = threads,
                });
                return StreamAudio(streamingModel, audio!, new AudioCppStreamingOptions
                {
                    Task = task ?? "vad",
                    SampleRate = audio!.SampleRate,
                    Channels = audio.Channels,
                    ChunkMilliseconds = chunkMilliseconds,
                    Text = Option(args, "--text"),
                    TextLanguage = Option(args, "--text-language"),
                    Style = style,
                    Artifacts = artifacts,
                    Options = options,
                });
            }

            // A voice reference is two fields on the request but one flag on the CLI.
            var reference = Option(args, "--voice-ref") is { } referencePath ? WaveFile.Read(referencePath) : null;
            var merged = MergeReferenceText(options, reference, Option(args, "--reference-text"));
            using var model = runtime.LoadModel(new AudioCppModelOptions
            {
                ModelPath = modelPath,
                FamilyHint = Option(args, "--family"),
                Threads = threads,
            });
            var result = model.Run(new AudioCppRunRequest
            {
                Task = task,
                Text = Option(args, "--text"),
                TextLanguage = Option(args, "--text-language"),
                Audio = audio?.Samples ?? default,
                SampleRate = audio?.SampleRate ?? 0,
                Channels = audio?.Channels ?? 1,
                VoiceId = Option(args, "--voice-id"),
                ReferencePcm = reference?.Samples ?? default,
                ReferenceSampleRate = reference?.SampleRate ?? 0,
                Style = style,
                Artifacts = artifacts,
                Options = merged,
            });
            return ReportRun(result, output, rawJson);
        }
        catch (AudioCppException exception) { return Fail(exception.Message); }
        catch (DllNotFoundException exception) { return Fail($"Native shim not found: {exception.Message}"); }
        catch (FileNotFoundException exception) { return Fail($"Audio file not found: {exception.FileName}"); }
    }

    private static int ReportRun(AudioCppTaskResult result, string? outputPath, bool rawJson)
    {
        if (rawJson)
        {
            Console.WriteLine(result.RawJson.GetRawText());
            return 0;
        }

        Console.WriteLine($"task: {result.Task ?? "(not reported by this shim)"}  schema: {result.SchemaVersion?.ToString() ?? "-"}");
        if (result.Text is not null)
            Console.WriteLine($"text{(result.TextLanguage is null ? "" : $" [{result.TextLanguage}]")}: {result.Text}");
        if (result.AudioOutput is { } clip)
            Console.WriteLine(DescribeClip("audio", clip, WriteClip(outputPath, clip)));
        foreach (var named in result.NamedAudioOutputs)
            Console.WriteLine(DescribeClip($"audio[{named.Id}]", named.Audio, WriteClip(NamedOutputPath(outputPath, named.Id), named.Audio)));
        foreach (var segment in result.SpeechSegments)
            Console.WriteLine($"segment [{segment.Span.StartSample}..{segment.Span.EndSample}] {segment.Confidence:F2} {segment.Text}");
        foreach (var word in result.WordTimestamps)
            Console.WriteLine($"word [{word.Span.StartSample}..{word.Span.EndSample}] {word.Confidence:F2} {word.Word}");
        foreach (var turn in result.SpeakerTurns)
            Console.WriteLine($"speaker [{turn.Span.StartSample}..{turn.Span.EndSample}] {turn.SpeakerId} {turn.Confidence:F2} {turn.Text}");
        if (result.Artifact is { } primary)
            Console.WriteLine(DescribeArtifact("artifact", primary));
        foreach (var artifact in result.Artifacts)
            Console.WriteLine(DescribeArtifact("artifact", artifact));
        return 0;
    }

    private static string DescribeClip(string label, AudioCppAudioClip clip, string? written) =>
        $"{label}: {clip.SampleRate} Hz, {clip.Channels} channel(s), {clip.Samples.Count} samples" +
        (written is null ? "" : $" -> {written}");

    private static string DescribeArtifact(string label, AudioCppArtifact artifact) =>
        $"{label} {artifact.Id} kind={artifact.Kind} bytes={artifact.PayloadHex.Length / 2}";

    /// <summary>Writes a clip when a path was requested and the clip has samples.</summary>
    private static string? WriteClip(string? path, AudioCppAudioClip clip)
    {
        if (string.IsNullOrWhiteSpace(path) || clip.Samples.Count == 0 || clip.SampleRate <= 0) return null;
        WaveFile.Write(path, new AudioBuffer(clip.Samples.ToArray(), clip.SampleRate, clip.Channels));
        return Path.GetFullPath(path);
    }

    /// <summary>Derives a per-track path so several named outputs do not collide.</summary>
    private static string? NamedOutputPath(string? outputPath, string id)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return null;
        var safeId = string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        if (safeId.Length == 0) safeId = "track";
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(outputPath)}-{safeId}{Path.GetExtension(outputPath)}");
    }

    private static int ListPackages(AudioCppRuntime runtime)
    {
        foreach (var package in runtime.ListPackages())
            Console.WriteLine($"{package.Id}\t{(package.Installed ? "installed" : "not-installed")}");
        return 0;
    }

    private static int PrintPath(string[] args)
    {
        Console.WriteLine(Option(args, "--models-dir") ?? ModelDirectory.Default);
        return 0;
    }

    private static int Download(AudioCppRuntime runtime, string[] args)
    {
        var packageId = args.ElementAtOrDefault(2);
        if (string.IsNullOrWhiteSpace(packageId) || packageId.StartsWith('-'))
            return Fail("Usage: models download PACKAGE_ID [--models-dir PATH] [--overwrite]");
        var directory = Option(args, "--models-dir") ?? ModelDirectory.Default;
        Directory.CreateDirectory(directory);
        var result = runtime.InstallPackage(packageId, directory, HasFlag(args, "--overwrite"),
            (downloaded, total, message) =>
            {
                var suffix = total == 0 ? $"{downloaded} bytes" : $"{downloaded}/{total} bytes";
                Console.Error.WriteLine($"{suffix}{(string.IsNullOrWhiteSpace(message) ? "" : $" {message}")}");
            });
        Console.WriteLine(result);
        Console.WriteLine($"Model directory: {directory}");
        return 0;
    }

    private static int RunAsr(AudioCppRuntime runtime, string[] args)
    {
        var input = Option(args, "--input");
        if (string.IsNullOrWhiteSpace(input)) return Fail("Usage: asr --input AUDIO.wav [--models-dir PATH] [--model PATH]");
        var modelPath = Option(args, "--model") ??
            ResolveModelPath(Option(args, "--models-dir") ?? ModelDirectory.Default, "Citrinet-ASR-GGUF", "citrinet_asr_q8_0");
        // Do not force Citrinet here. LoadModel derives the family from the package
        // manifest, so Audio8 and future ASR packages receive their own loader.
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath });
        var audio = WaveFile.Read(input);
        var request = new AsrRequest { Audio = audio.Samples, SampleRate = audio.SampleRate, Channels = audio.Channels };
        if (HasFlag(args, "--stream"))
        {
            if (ChunkMilliseconds(args) is not { } streamChunkMilliseconds)
                return Fail("--chunk-ms must be a positive integer.");
            return StreamAudio(model, audio, new AudioCppStreamingOptions
            {
                Task = Option(args, "--task") ?? "asr",
                SampleRate = audio.SampleRate,
                Channels = audio.Channels,
                ChunkMilliseconds = streamChunkMilliseconds,
            });
        }
        if (HasFlag(args, "--structured"))
        {
            var structured = model.Run(audioRequest: request);
            Console.WriteLine(structured.Text ?? "");
            foreach (var segment in structured.SpeechSegments)
                Console.WriteLine($"segment [{segment.Span.StartSample}..{segment.Span.EndSample}] {segment.Confidence:F2} {segment.Text}");
            foreach (var word in structured.WordTimestamps)
                Console.WriteLine($"word [{word.Span.StartSample}..{word.Span.EndSample}] {word.Confidence:F2} {word.Word}");
            foreach (var turn in structured.SpeakerTurns)
                Console.WriteLine($"speaker [{turn.Span.StartSample}..{turn.Span.EndSample}] {turn.SpeakerId} {turn.Confidence:F2} {turn.Text}");
            foreach (var artifact in structured.Artifacts)
                Console.WriteLine($"artifact {artifact.Id} kind={artifact.Kind} bytes={artifact.PayloadHex.Length / 2}");
            return 0;
        }
        var text = model.Transcribe(request);
        Console.WriteLine(text);
        return 0;
    }

    private static int RunVad(AudioCppRuntime runtime, string[] args)
    {
        var input = Option(args, "--input");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(Option(args, "--model")))
            return Fail("Usage: vad --input AUDIO.wav --model PATH [--family NAME] [--chunk-ms N]");
        var audio = WaveFile.Read(input);
        using var model = runtime.LoadModel(new AudioCppModelOptions
        {
            ModelPath = Option(args, "--model")!,
            FamilyHint = Option(args, "--family"),
        });
        if (ChunkMilliseconds(args) is not { } chunkMilliseconds)
            return Fail("--chunk-ms must be a positive integer.");
        return StreamAudio(model, audio, new AudioCppStreamingOptions
        {
            Task = "vad",
            SampleRate = audio.SampleRate,
            Channels = audio.Channels,
            ChunkMilliseconds = chunkMilliseconds,
        });
    }

    private static int StreamAudio(AudioCppModel model, AudioBuffer audio, AudioCppStreamingOptions options)
    {
        var report = AudioCppStreaming.Run(model, audio.Samples, options,
            onStarted: (info, chunkSamples) => Console.WriteLine(
                $"streaming family={info.Family} task={info.Task} policy={info.Policy.Input}/{info.Policy.Output} " +
                $"chunk={chunkSamples} samples"),
            onEvent: batch => WriteStreamEvent(batch.Event));
        if (report.PaddedTail)
            Console.WriteLine($"padded tail {report.PaddedTailSamples} samples to keep {report.ChunkSamples}-sample alignment");
        Console.WriteLine($"final {report.Result.Text ?? ""}");
        foreach (var segment in report.Result.SpeechSegments)
            Console.WriteLine($"segment [{segment.Span.StartSample}..{segment.Span.EndSample}] {segment.Confidence:F2} {segment.Text}");
        return 0;
    }

    private static void WriteStreamEvent(AudioCppStreamEvent streamEvent)
    {
        if (streamEvent.PartialText is not null) Console.WriteLine($"partial {streamEvent.PartialText}");
        foreach (var activity in streamEvent.VoiceActivity)
        {
            var span = activity.Segment is null ? "" : $" [{activity.Segment.Span.StartSample}..{activity.Segment.Span.EndSample}]";
            Console.WriteLine($"voice {activity.Kind} @ {activity.Sample} p={activity.Probability:F2}{span}");
        }
        foreach (var turn in streamEvent.SpeakerTurns)
            Console.WriteLine($"speaker [{turn.Span.StartSample}..{turn.Span.EndSample}] {turn.SpeakerId} {turn.Confidence:F2}");
        foreach (var word in streamEvent.WordTimestamps)
            Console.WriteLine($"word [{word.Span.StartSample}..{word.Span.EndSample}] {word.Confidence:F2} {word.Word}");
    }

    /// <summary>Parses --chunk-ms, returning null when the value is not a positive integer.</summary>
    private static int? ChunkMilliseconds(string[] args)
    {
        var value = Option(args, "--chunk-ms");
        if (value is null) return AudioCppStreaming.DefaultChunkMilliseconds;
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static int RunTts(AudioCppRuntime runtime, string[] args)
    {
        var text = Option(args, "--text");
        var output = Option(args, "--output");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(output))
            return Fail("Usage: tts --text TEXT --output AUDIO.wav [--voice-ref AUDIO.wav] [--models-dir PATH] [--model PATH]");
        var modelPath = Option(args, "--model") ??
            ResolveModelPath(Option(args, "--models-dir") ?? ModelDirectory.Default, "Qwen3-TTS-12Hz-0.6B-Base-GGUF", "qwen3_tts_0_6b_base_q8_0");
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath, FamilyHint = "qwen3_tts" });
        var voiceRef = Option(args, "--voice-ref");
        var reference = voiceRef is null ? null : WaveFile.Read(voiceRef);
        var referenceText = Option(args, "--reference-text");
        if (HasFlag(args, "--structured"))
        {
            var structured = model.Run(new TtsRequest
            {
                Text = text,
                Task = Option(args, "--task") ?? "tts",
                ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
                ReferenceSampleRate = reference?.SampleRate ?? 0,
                Options = BuildTtsOptions(args, reference, referenceText)
            });
            var clip = structured.AudioOutput ?? throw new AudioCppException("structured run returned no audio");
            WaveFile.Write(output, new AudioBuffer(clip.Samples.ToArray(), clip.SampleRate, clip.Channels));
            Console.WriteLine($"Generated {output} ({clip.SampleRate} Hz, {clip.Channels} channel(s), {clip.Samples.Count} samples)");
            foreach (var named in structured.NamedAudioOutputs)
                Console.WriteLine($"audio {named.Id} ({named.Audio.SampleRate} Hz, {named.Audio.Channels} channel(s), {named.Audio.Samples.Count} samples)");
            foreach (var artifact in structured.Artifacts)
                Console.WriteLine($"artifact {artifact.Id} kind={artifact.Kind} bytes={artifact.PayloadHex.Length / 2}");
            return 0;
        }
        var audio = model.Synthesize(new TtsRequest
        {
            Text = text,
            Task = Option(args, "--task") ?? "tts",
            ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
            ReferenceSampleRate = reference?.SampleRate ?? 0,
            Options = BuildTtsOptions(args, reference, referenceText)
        });
        WaveFile.Write(output, audio);
        Console.WriteLine($"Generated {output} ({audio.SampleRate} Hz, {audio.Channels} channel(s))");
        return 0;
    }

    private static IReadOnlyDictionary<string, string>? BuildTtsOptions(string[] args, AudioBuffer? reference, string? referenceText)
    {
        var options = new Dictionary<string, string>();
        if (reference is not null && !string.IsNullOrWhiteSpace(referenceText)) options["reference_text"] = referenceText;
        foreach (var (argument, key) in new[] { ("--style-language", "style_language"), ("--emotion", "emotion"), ("--speaking-rate", "speaking_rate"), ("--pitch-shift", "pitch_shift"), ("--energy-scale", "energy_scale") })
            if (Option(args, argument) is { } value) options[key] = value;
        return options.Count == 0 ? null : options;
    }

    private static async Task<int> VerifyAsync(AudioCppRuntime runtime, string[] args)
    {
        var models = Option(args, "--models-dir") ?? ModelDirectory.Default;
        var asrPackage = Option(args, "--asr-package") ?? "citrinet_asr_q8_0";
        var ttsPackage = Option(args, "--tts-package") ?? "qwen3_tts_0_6b_base_q8_0";
        Directory.CreateDirectory(models);
        foreach (var package in new[] { asrPackage, ttsPackage })
        {
            Console.WriteLine($"Ensuring {package} is installed...");
            Console.WriteLine(runtime.InstallPackage(package, models, false, Progress));
        }
        var input = Option(args, "--input");
        if (string.IsNullOrWhiteSpace(input))
        {
            input = Path.Combine(models, "verification-silence.wav");
            WaveFile.Write(input, new AudioBuffer(new float[16000], 16000, 1));
        }
        var voiceRefPath = Option(args, "--voice-ref");
        if (string.IsNullOrWhiteSpace(voiceRefPath))
        {
            voiceRefPath = Path.Combine(models, "verification-voice-ref.wav");
            var referenceSamples = Enumerable.Range(0, 48000)
                .Select(index => 0.08f * MathF.Sin(2 * MathF.PI * 220 * index / 16000f))
                .ToArray();
            WaveFile.Write(voiceRefPath, new AudioBuffer(referenceSamples, 16000, 1));
        }
        var output = Option(args, "--output") ?? Path.Combine(models, "verification-tts.wav");
        var asrPath = Path.Combine(models, "Citrinet-ASR-GGUF");
        var ttsPath = Path.Combine(models, "Qwen3-TTS-12Hz-0.6B-Base-GGUF");
        using var asr = runtime.LoadModel(new AudioCppModelOptions { ModelPath = asrPath });
        var inputAudio = WaveFile.Read(input);
        Console.WriteLine($"ASR: {asr.Transcribe(new AsrRequest { Audio = inputAudio.Samples, SampleRate = inputAudio.SampleRate, Channels = inputAudio.Channels })}");
        using var tts = runtime.LoadModel(new AudioCppModelOptions { ModelPath = ttsPath, FamilyHint = "qwen3_tts" });
        var reference = WaveFile.Read(voiceRefPath);
        var generated = tts.Synthesize(new TtsRequest
        {
            Text = "audio cpp verification",
            Task = "tts",
            ReferencePcm = reference.Samples,
            ReferenceSampleRate = reference.SampleRate,
            Options = new Dictionary<string, string> { ["reference_text"] = "audio cpp reference" }
        });
        WaveFile.Write(output, generated);
        Console.WriteLine($"TTS: {output} ({generated.Samples.Length} samples)");
        await Task.CompletedTask;
        return 0;
    }

    private static int VerifyPackages(string[] args)
    {
        var directory = Option(args, "--models-dir") ?? ModelDirectory.Default;
        if (!Directory.Exists(directory)) return Fail($"Model directory does not exist: {Path.GetFullPath(directory)}");
        var found = false;
        var failed = false;
        var rootReport = ModelValidator.Validate(directory);
        if (rootReport.ManifestPresent)
        {
            found = true;
            if (!ReportVerification(rootReport, directory)) failed = true;
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var report = ModelValidator.Validate(sub);
            if (!report.ManifestPresent) continue;
            found = true;
            if (!ReportVerification(report, sub)) failed = true;
        }
        if (!found)
        {
            Console.WriteLine($"No installed packages found under: {Path.GetFullPath(directory)}");
            return 0;
        }
        return failed ? 1 : 0;
    }

    private static bool ReportVerification(ModelValidationReport report, string directory)
    {
        var label = report.PackageId ?? Path.GetFileName(directory);
        if (report.Complete)
        {
            Console.WriteLine($"OK    {label} (checked {report.CheckedFiles} file(s), {report.CheckedBytes} byte(s))");
            return true;
        }
        Console.WriteLine($"FAIL  {label}: {ModelValidator.FormatIssues(report)}");
        Console.WriteLine($"      repair: audiocpp-net models download {label} --overwrite --models-dir {Path.GetDirectoryName(directory)}");
        return false;
    }

    private static string ResolveModelPath(string modelsDirectory, string standardDirectory, string packageId)
    {
        var standard = Path.Combine(modelsDirectory, standardDirectory);
        if (Directory.Exists(standard)) return standard;
        return ModelValidator.FindPackageDirectory(modelsDirectory, packageId) ?? standard;
    }

    private static void Progress(ulong downloaded, ulong total, string? message)
    {
        var suffix = total == 0 ? $"{downloaded} bytes" : $"{downloaded}/{total} bytes";
        Console.Error.WriteLine($"{suffix}{(string.IsNullOrWhiteSpace(message) ? "" : $" {message}")}");
    }

    /// <summary>
    /// Parses repeatable <c>--artifact KIND[:ID][=HEX|@FILE|text:VALUE]</c> flags
    /// into TaskRequest::input_artifacts entries.
    /// </summary>
    private static IReadOnlyList<AudioCppInputArtifact>? ReadArtifacts(string[] args)
    {
        var artifacts = new List<AudioCppInputArtifact>();
        foreach (var spec in Values(args, "--artifact"))
        {
            var (head, payload) = SplitOnce(spec, '=');
            var (kind, id) = SplitOnce(head, ':');
            if (kind.Length == 0)
                throw new ArgumentException("--artifact requires KIND[:ID][=HEX|@FILE|text:VALUE].");
            var artifact = new AudioCppInputArtifact { Kind = kind, Id = id };
            if (payload.Length > 0)
            {
                if (payload[0] == '@')
                {
                    var path = payload[1..];
                    if (!File.Exists(path)) throw new ArgumentException($"--artifact payload file not found: {path}");
                    artifact = artifact with { PayloadHex = Convert.ToHexString(File.ReadAllBytes(path)).ToLowerInvariant() };
                }
                else if (payload.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                    artifact = artifact with { PayloadText = payload[5..] };
                else
                    artifact = artifact with { PayloadHex = payload };
            }
            artifacts.Add(artifact);
        }
        return artifacts.Count == 0 ? null : artifacts;
    }

    /// <summary>Reads repeatable <c>--name KEY=VALUE</c> flags; throws on a malformed pair.</summary>
    private static IReadOnlyDictionary<string, string>? Pairs(string[] args, string name)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in Values(args, name))
        {
            var (key, item) = SplitOnce(value, '=');
            if (key.Length == 0 || item.Length == 0)
                throw new ArgumentException($"{name} requires KEY=VALUE; got '{value}'.");
            pairs[key] = item;
        }
        return pairs.Count == 0 ? null : pairs;
    }

    /// <summary>All values of a repeatable flag, in command-line order.</summary>
    private static IEnumerable<string> Values(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] == name) yield return args[index + 1];
    }

    private static (string Head, string Tail) SplitOnce(string value, char separator)
    {
        var index = value.IndexOf(separator);
        return index < 0 ? (value, "") : (value[..index], value[(index + 1)..]);
    }

    private static AudioCppStyle? ReadStyle(string[] args, IReadOnlyDictionary<string, string>? tags)
    {
        var style = new AudioCppStyle
        {
            Language = Option(args, "--style-language"),
            Emotion = Option(args, "--emotion"),
            SpeakingRate = ParseFloat("--speaking-rate", Option(args, "--speaking-rate")),
            PitchShift = ParseFloat("--pitch-shift", Option(args, "--pitch-shift")),
            EnergyScale = ParseFloat("--energy-scale", Option(args, "--energy-scale")),
            Tags = tags,
        };
        return style.Language is null && style.Emotion is null && style.SpeakingRate is null &&
               style.PitchShift is null && style.EnergyScale is null && style.Tags is null ? null : style;
    }

    /// <summary>Reference audio needs an accompanying transcript; fold it into the options map.</summary>
    private static IReadOnlyDictionary<string, string>? MergeReferenceText(
        IReadOnlyDictionary<string, string>? options, AudioBuffer? reference, string? referenceText)
    {
        if (reference is null || string.IsNullOrWhiteSpace(referenceText)) return options;
        var merged = new Dictionary<string, string>(options ?? new Dictionary<string, string>())
        {
            ["reference_text"] = referenceText,
        };
        return merged;
    }

    private static int? ParseInt(string? value)
    {
        if (value is null) return null;
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"'{value}' is not an integer.");
        return parsed;
    }

    private static float? ParseFloat(string name, string? value)
    {
        if (value is null) return null;
        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{name} expects a number; got '{value}'.");
        return parsed;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

    private static bool ConfigureHuggingFaceEndpoint(string[] args)
    {
        var endpoint = Option(args, "--hf-endpoint");
        if (endpoint is null) return true;
        endpoint = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            Fail("--hf-endpoint must be an absolute HTTP(S) URL without query, fragment, or credentials.");
            return false;
        }

        Environment.SetEnvironmentVariable("AUDIOCPP_HF_BASE_URL", endpoint);
        return true;
    }

    private static int HelpAndSuccess() { PrintHelp(); return 0; }
    private static int Fail(string message) { Console.Error.WriteLine($"error: {message}"); return 1; }

    private static void PrintHelp() => Console.WriteLine("""
AudioCpp.NET console

Usage:
  audiocpp-net tasks [--native PATH]                              Show ABI capabilities and the task catalog
  audiocpp-net models list [--native PATH]                       List compiled audio.cpp model loaders
  audiocpp-net models packages [--native PATH]                   List downloadable model packages
  audiocpp-net models path [--models-dir PATH]                   Show the local model directory
  audiocpp-net models download PACKAGE_ID [options]              Download one package
  audiocpp-net models verify [--models-dir PATH]                 Verify installed packages for missing files
  audiocpp-net asr --input AUDIO.wav [options]                    Transcribe a PCM WAV file
  audiocpp-net asr --input AUDIO.wav --stream [options]           Stream a file through a streaming ASR session
  audiocpp-net vad --input AUDIO.wav --model PATH [--family NAME] Stream a file through a streaming VAD session
  audiocpp-net tts --text TEXT --output AUDIO.wav [--voice-ref WAV] [--reference-text TEXT] Synthesize speech
  audiocpp-net run --model PATH [options]                         Run any task the model supports
  audiocpp-net verify [options]                                   Download minimal ASR/TTS and run both

Run options (any of the 14 tasks, or a model_spec alias such as music/clone/design):
  --task TASK            vad|asr|diar|sep|gen|tts|clon|vc|s2s|align|vdes|spk|svc|midi
                         aliases: audio_generation, music, sfx, edit, clone, design, speaker, codec
  --model PATH           Model package directory or manifest
  --family NAME          Force a loader family instead of deriving it from the manifest
  --text TEXT            Text input (TTS, cloning, design, alignment, ...)
  --text-language LANG   Language of --text (AudioCppRunRequest.TextLanguage)
  --input AUDIO.wav      Audio input (ASR, VAD, diarization, separation, conversion, ...)
  --output AUDIO.wav     Write result audio here; named outputs get a -<id> suffix
  --voice-ref WAV        Reference speaker audio (TTS/voice cloning)
  --reference-text TEXT  Transcript of --voice-ref
  --voice-id ID          Cached voice id to reuse
  --style-language LANG  StyleCondition language
  --emotion E            StyleCondition emotion
  --speaking-rate R      StyleCondition speaking rate (float)
  --pitch-shift P        StyleCondition pitch shift in semitones (float)
  --energy-scale S       StyleCondition energy scale (float)
  --style-tag K=V        StyleCondition tag; repeatable
  --artifact SPEC        Input artifact KIND[:ID][=HEX|@FILE|text:VALUE]; repeatable
  --option K=V           Task option passed through to the engine; repeatable
  --stream               Drive a streaming session instead of an offline run
  --chunk-ms N           Streaming chunk size in milliseconds
  --threads N            Worker threads for the model
  --json                 Print the raw structured result JSON instead of a summary

Options:
  --native PATH       Native audiocpp_dotnet library path (or AUDIOCPP_NATIVE_PATH)
  --models-dir PATH   Installation directory; defaults to the platform data directory
  --hf-endpoint URL   Hugging Face mirror base URL (or HF_ENDPOINT)
  --voice-ref WAV     Reference speaker audio for voice-clone TTS
  --reference-text TEXT  Transcript of the reference audio
  --overwrite         Replace an existing package
  --structured        Use the structured-result run ABI; asr prints text plus segments,
                      words, speaker turns and artifacts, tts writes WAV from the
                      structured audio output and lists artifacts
""");
}
