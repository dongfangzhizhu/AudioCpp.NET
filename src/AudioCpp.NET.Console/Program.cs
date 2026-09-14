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

        if (args[0] is "asr" or "tts" or "verify")
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
        using var model = runtime.LoadModel(new AudioCppModelOptions { ModelPath = modelPath, FamilyHint = "citrinet_asr" });
        var audio = WaveFile.Read(input);
        var text = model.Transcribe(new AsrRequest { Audio = audio.Samples, SampleRate = audio.SampleRate, Channels = audio.Channels });
        Console.WriteLine(text);
        return 0;
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
        var audio = model.Synthesize(new TtsRequest
        {
            Text = text,
            Task = "tts",
            ReferencePcm = reference?.Samples ?? ReadOnlyMemory<float>.Empty,
            ReferenceSampleRate = reference?.SampleRate ?? 0,
            Options = reference is null || referenceText is null
                ? null
                : new Dictionary<string, string> { ["reference_text"] = referenceText }
        });
        WaveFile.Write(output, audio);
        Console.WriteLine($"Generated {output} ({audio.SampleRate} Hz, {audio.Channels} channel(s))");
        return 0;
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
        using var asr = runtime.LoadModel(new AudioCppModelOptions { ModelPath = asrPath, FamilyHint = "citrinet_asr" });
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
  audiocpp-net tts --text TEXT --output AUDIO.wav [--voice-ref WAV] [--reference-text TEXT] Synthesize speech
  audiocpp-net verify [options]                                   Download minimal ASR/TTS and run both

Options:
  --native PATH       Native audiocpp_dotnet library path (or AUDIOCPP_NATIVE_PATH)
  --models-dir PATH   Installation directory; defaults to the platform data directory
  --hf-endpoint URL   Hugging Face mirror base URL (or HF_ENDPOINT)
  --voice-ref WAV     Reference speaker audio for voice-clone TTS
  --reference-text TEXT  Transcript of the reference audio
  --overwrite         Replace an existing package
""");
}
