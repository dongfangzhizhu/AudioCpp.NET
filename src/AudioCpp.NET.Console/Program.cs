using AudioCpp.NET;

return await ConsoleApp.RunAsync(args);

internal static class ConsoleApp
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
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
            using var runtime = AudioCppRuntime.Create(new AudioCppRuntimeOptions { NativeLibraryPath = nativePath });
            return args.ElementAtOrDefault(1) switch
            {
                "list" => ListLoaders(runtime),
                "packages" => ListPackages(runtime),
                "download" => Download(runtime, args),
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

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);
    private static int HelpAndSuccess() { PrintHelp(); return 0; }
    private static int Fail(string message) { Console.Error.WriteLine($"error: {message}"); return 1; }

    private static void PrintHelp() => Console.WriteLine("""
AudioCpp.NET console

Usage:
  audiocpp-net models list [--native PATH]                       List compiled audio.cpp model loaders
  audiocpp-net models packages [--native PATH]                  List downloadable model packages
  audiocpp-net models path [--models-dir PATH]                  Show the local model directory
  audiocpp-net models download PACKAGE_ID [options]              Download one package

Options:
  --native PATH       Native audiocpp_dotnet library path (or AUDIOCPP_NATIVE_PATH)
  --models-dir PATH   Installation directory; defaults to the platform data directory
  --overwrite         Replace an existing package
""");
}

internal static class ModelDirectory
{
    internal static string Default
    {
        get
        {
            var root = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "audiocpp.net", "models");
        }
    }
}