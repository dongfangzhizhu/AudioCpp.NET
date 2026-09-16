using AudioCpp.NET;

internal static class InteractiveMenu
{
    private sealed class Settings
    {
        internal string NativePath { get; set; } = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_PATH") ?? "";
        internal string ModelsDirectory { get; set; } = ModelDirectory.Default;
        internal string HuggingFaceEndpoint { get; set; } = Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? "https://hf-mirror.com";
    }

    internal static async Task<int> RunAsync()
    {
        var settings = new Settings();
        Console.WriteLine("\nAudioCpp.NET interactive studio");
        Console.WriteLine("Choose an operation and follow the prompts. Press Enter to accept [defaults].");
        while (true)
        {
            PrintMenu(settings);
            switch (Prompt("Select", "1"))
            {
                case "1": await Execute(["models", "packages", .. Common(settings)]); break;
                case "2": await Download(settings); break;
                case "3": await Asr(settings); break;
                case "4": await Tts(settings); break;
                case "5": await Verify(settings); break;
                case "6": Configure(settings); break;
                case "7": await Execute(["models", "list", .. Common(settings)]); break;
                case "8": await Execute(["tasks", .. Common(settings)]); break;
                case "9": await AnyTask(settings); break;
                case "h" or "help" or "?": await ConsoleApp.RunAsync(["help"]); break;
                case "0" or "q" or "quit" or "exit": return 0;
                default: WriteError("Please choose 0-9."); break;
            }
            Console.WriteLine("\nPress Enter to return to the menu...");
            Console.ReadLine();
        }
    }

    private static void PrintMenu(Settings settings)
    {
        Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  1  Model packages/status     6  Settings                      │");
        Console.WriteLine("│  2  Download a model          7  Compiled model loaders        │");
        Console.WriteLine("│  3  Speech recognition        8  Task catalog & capabilities   │");
        Console.WriteLine("│  4  Text to speech            9  Run any task (structured)     │");
        Console.WriteLine("│  5  End-to-end verification   0  Exit                          │");
        Console.WriteLine("└────────────────────────────────────────────────────────────────┘");
        Console.WriteLine($"Models: {settings.ModelsDirectory}");
        Console.WriteLine($"Mirror: {settings.HuggingFaceEndpoint}");
    }

    private static async Task Download(Settings settings)
    {
        var package = Prompt("Package ID", "citrinet_asr_q8_0");
        var overwrite = YesNo("Overwrite an existing installation", false);
        await Execute(["models", "download", package, .. Common(settings), .. (overwrite ? new[] { "--overwrite" } : [])]);
    }

    private static async Task Asr(Settings settings)
    {
        var input = PromptRequired("Input PCM16 WAV path");
        var model = Prompt("Model path", Path.Combine(settings.ModelsDirectory, "Citrinet-ASR-GGUF"));
        await Execute(["asr", "--input", input, "--model", model, .. Common(settings)]);
    }

    private static async Task Tts(Settings settings)
    {
        var text = PromptRequired("Text to synthesize");
        var output = Prompt("Output WAV path", Path.Combine(Environment.CurrentDirectory, "audiocpp-output.wav"));
        var model = Prompt("Model path", Path.Combine(settings.ModelsDirectory, "Qwen3-TTS-12Hz-0.6B-Base-GGUF"));
        var voiceRef = PromptRequired("Voice reference PCM16 WAV path (required by Qwen3 Base)");
        var referenceText = PromptRequired("Transcript of the voice reference");
        await Execute(["tts", "--text", text, "--output", output, "--model", model,
            "--voice-ref", voiceRef, "--reference-text", referenceText, .. Common(settings)]);
    }

    /// <summary>
    /// Prompts for the inputs of any task; blank answers simply omit the flag, so
    /// the shim falls back to inferring the task from what was supplied.
    /// </summary>
    private static async Task AnyTask(Settings settings)
    {
        var model = Prompt("Model path", Path.Combine(settings.ModelsDirectory, "Qwen3-TTS-12Hz-0.6B-Base-GGUF"));
        var task = Prompt("Task token (blank lets the shim infer it)", "tts");
        var text = Prompt("Text input (blank for none)", "");
        var input = Prompt("Audio input PCM16 WAV path (blank for none)", "");
        var output = Prompt("Output WAV path (blank skips writing audio)", "");
        var extra = new List<string> { "run", "--model", model };
        if (task.Length > 0) { extra.Add("--task"); extra.Add(task); }
        if (text.Length > 0) { extra.Add("--text"); extra.Add(text); }
        if (input.Length > 0) { extra.Add("--input"); extra.Add(input); }
        if (output.Length > 0) { extra.Add("--output"); extra.Add(output); }
        await Execute([.. extra, .. Common(settings)]);
    }

    private static async Task Verify(Settings settings)
    {
        var output = Prompt("TTS output WAV path", Path.Combine(settings.ModelsDirectory, "verification-tts.wav"));
        await Execute(["verify", "--output", output, .. Common(settings)]);
    }

    private static void Configure(Settings settings)
    {
        settings.NativePath = Prompt("Native library path (blank uses loader/environment)", settings.NativePath);
        settings.ModelsDirectory = Path.GetFullPath(Prompt("Models directory", settings.ModelsDirectory));
        settings.HuggingFaceEndpoint = Prompt("Hugging Face endpoint", settings.HuggingFaceEndpoint);
    }

    private static string[] Common(Settings settings)
    {
        var result = new List<string> { "--models-dir", settings.ModelsDirectory, "--hf-endpoint", settings.HuggingFaceEndpoint };
        if (!string.IsNullOrWhiteSpace(settings.NativePath)) { result.Add("--native"); result.Add(settings.NativePath); }
        return [.. result];
    }

    private static async Task Execute(string[] args)
    {
        Console.WriteLine($"\n→ audiocpp-net {string.Join(' ', args.Select(Quote))}\n");
        var code = await ConsoleApp.RunAsync(args);
        Console.WriteLine(code == 0 ? "\n✓ Operation completed." : $"\n✗ Operation failed (exit code {code}).");
    }

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write(defaultValue.Length == 0 ? $"{label}: " : $"{label} [{defaultValue}]: ");
        var value = Console.ReadLine()?.Trim();
        if (value is null) Environment.Exit(0); // stdin closed: leave the menu cleanly
        return value.Length == 0 ? defaultValue : value;
    }

    private static string PromptRequired(string label)
    {
        while (true) { var value = Prompt(label, ""); if (value.Length > 0) return value; WriteError("A value is required."); }
    }

    private static bool YesNo(string label, bool defaultValue) =>
        Prompt($"{label} (y/n)", defaultValue ? "y" : "n").Equals("y", StringComparison.OrdinalIgnoreCase);
    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
    private static void WriteError(string message) { var old = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(message); Console.ForegroundColor = old; }
}
