using System.Reflection;
using System.Runtime.InteropServices;

namespace AudioCpp.NET.Interop;

internal static class NativeLibraryLoader
{
    private const int MaxRepositoryWalkLevels = 7;

    private static readonly object Sync = new();
    private static IntPtr _handle;

    internal static void Load(string? explicitPath)
    {
        lock (Sync)
        {
            if (_handle != IntPtr.Zero) return;

            var candidates = new List<string>();
            AddCandidate(candidates, explicitPath);
            AddCandidate(candidates, Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_PATH"));
            candidates.Add(DefaultLibraryFileName());
            candidates.AddRange(DiscoverRepositoryCandidates());

            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, Assembly.GetExecutingAssembly(), DllImportSearchPath.SafeDirectories, out _handle)) return;
                if (NativeLibrary.TryLoad(candidate, out _handle)) return;
            }

            throw new DllNotFoundException(
                $"Unable to load {NativeMethods.DefaultLibraryName}. Probed: {string.Join("; ", candidates)}. " +
                "Build the native shim (cmake --build build/native --config Release --target audiocpp_dotnet_native --parallel), " +
                "or set AUDIOCPP_NATIVE_PATH / AudioCppRuntimeOptions.NativeLibraryPath to the built library file.");
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !candidates.Contains(value)) candidates.Add(value);
    }

    private static string DefaultLibraryFileName() =>
        OperatingSystem.IsWindows() ? "audiocpp_dotnet_native.dll" :
        OperatingSystem.IsMacOS() ? "libaudiocpp_dotnet_native.dylib" : "libaudiocpp_dotnet_native.so";

    /// <summary>
    /// Last-resort discovery for hosts that run without configuration: walk up from the
    /// application directory and probe the repository's native build outputs plus the
    /// conventional runtimes layout, so dotnet run / IDE launches inside the repository
    /// work out of the box. Newest build output first, so stale artifacts lose against
    /// fresh ones.
    /// </summary>
    private static IEnumerable<string> DiscoverRepositoryCandidates()
    {
        var libraryName = DefaultLibraryFileName();
        var found = new List<string>();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < MaxRepositoryWalkLevels && directory is not null; level++, directory = directory.Parent)
            ProbeRepositoryLayout(directory.FullName, libraryName, found);
        return found.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void ProbeRepositoryLayout(string root, string libraryName, ICollection<string> found)
    {
        var buildRoot = Path.Combine(root, "build");
        if (Directory.Exists(buildRoot))
        {
            var variants = Directory.EnumerateDirectories(buildRoot, "native*", SearchOption.TopDirectoryOnly);
            foreach (var variant in variants)
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(variant, configuration, libraryName);
                    if (File.Exists(candidate)) found.Add(candidate);
                }
        }
        var runtimes = Path.Combine(root, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", libraryName);
        if (File.Exists(runtimes)) found.Add(runtimes);
    }
}
