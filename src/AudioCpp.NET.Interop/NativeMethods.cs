using System.Runtime.InteropServices;

namespace AudioCpp.NET.Interop;

internal static partial class NativeMethods
{
    internal const string DefaultLibraryName = "audiocpp_dotnet_native";

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AbiInfo
    {
        internal uint StructSize;
        internal uint AbiMajor;
        internal uint AbiMinor;
        internal byte* ShimVersion;
        internal byte* AudioCppCommit;
        internal byte* Backend;
        internal ulong Capabilities;
    }

    [LibraryImport(DefaultLibraryName, EntryPoint = "audiocpp_get_abi_info")]
    internal static partial int GetAbiInfo(ref AbiInfo info, IntPtr error, nuint errorLength);

    [LibraryImport(DefaultLibraryName, EntryPoint = "audiocpp_model_load", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ModelLoad(
        string modelPath,
        string? familyHint,
        string? backend,
        int device,
        int threads,
        string? loadOptionsJson,
        IntPtr error,
        nuint errorLength);

    [LibraryImport(DefaultLibraryName, EntryPoint = "audiocpp_model_synthesize", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int ModelSynthesize(
        SafeModelHandle model,
        string? task,
        string text,
        string? voiceId,
        float* referencePcm,
        int referenceCount,
        int referenceSampleRate,
        string? optionsJson,
        float** outputSamples,
        int* outputCount,
        int* outputSampleRate,
        int* outputChannels,
        IntPtr error,
        nuint errorLength);

    [LibraryImport(DefaultLibraryName, EntryPoint = "audiocpp_buffer_free")]
    internal static partial void BufferFree(IntPtr buffer);

    [LibraryImport(DefaultLibraryName, EntryPoint = "audiocpp_model_free")]
    internal static partial void ModelFree(IntPtr model);
}
