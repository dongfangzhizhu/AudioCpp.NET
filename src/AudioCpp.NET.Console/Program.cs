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

        if (args[0] is "asr" or "tts" or "verify" or "vad")
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
        foreach (var loader in runtime.ListLoaders())
        {
            var tasks = string.Join(", ", loader.Tasks.Select(task =>
                $"{task.Task}{(task.Modes.Count == 0 ? "" : $" ({string.Join('|', task.Modes)})")}"));
            Console.WriteLine($"{loader.Family}: {tasks}");
        }
        return 0;
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
            return StreamAudio(model, audio, Option(args, "--task") ?? "asr", streamChunkMilliseconds);
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
        return StreamAudio(model, audio, "vad", chunkMilliseconds);
    }

    private static int StreamAudio(AudioCppModel model, AudioBuffer audio, string task, int chunkMilliseconds)
    {
        var report = AudioCppStreaming.Run(model, audio.Samples,
            new AudioCppStreamingOptions
            {
                Task = task,
                SampleRate = audio.SampleRate,
                Channels = audio.Channels,
                ChunkMilliseconds = chunkMilliseconds,
            },
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
  audiocpp-net models list [--native PATH]                       List compiled audio.cpp model loaders
  audiocpp-net models packages [--native PATH]                  List downloadable model packages
  audiocpp-net models path [--models-dir PATH]                  Show the local model directory
  audiocpp-net models download PACKAGE_ID [options]              Download one package
  audiocpp-net models verify [--models-dir PATH]                 Verify installed packages for missing files
  audiocpp-net asr --input AUDIO.wav [options]                    Transcribe a PCM WAV file
  audiocpp-net asr --input AUDIO.wav --stream [options]           Stream a file through a streaming ASR session
  audiocpp-net vad --input AUDIO.wav --model PATH [--family NAME] Stream a file through a streaming VAD session
  audiocpp-net tts --text TEXT --output AUDIO.wav [--voice-ref WAV] [--reference-text TEXT] Synthesize speech
  audiocpp-net verify [options]                                   Download minimal ASR/TTS and run both

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
