using System.Reflection;
using System.Runtime.InteropServices;

namespace AudioCpp.NET.Interop;

internal static class NativeLibraryLoader
{
    private static readonly object Sync = new();
    private static IntPtr _handle;

    internal static void Load(string? explicitPath)
    {
        lock (Sync)
        {
            if (_handle != IntPtr.Zero) return;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
            var environmentPath = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_PATH");
            if (!string.IsNullOrWhiteSpace(environmentPath)) candidates.Add(environmentPath);
            candidates.Add(DefaultLibraryFileName());

            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, Assembly.GetExecutingAssembly(), DllImportSearchPath.SafeDirectories, out _handle)) return;
                if (NativeLibrary.TryLoad(candidate, out _handle)) return;
            }

            throw new DllNotFoundException($"Unable to load {NativeMethods.DefaultLibraryName} from: {string.Join(", ", candidates)}");
        }
    }

    private static string DefaultLibraryFileName() =>
        OperatingSystem.IsWindows() ? "audiocpp_dotnet_native.dll" :
        OperatingSystem.IsMacOS() ? "libaudiocpp_dotnet_native.dylib" : "libaudiocpp_dotnet_native.so";
}
