using System.Runtime.InteropServices;
using System.Text;

namespace AudioCpp.NET.Interop;

internal static class InteropOperations
{
    internal const int ErrorBufferLength = 4096;

    internal sealed record AbiSnapshot(uint AbiMajor, uint AbiMinor, string ShimVersion, string AudioCppCommit, string Backend, ulong Capabilities);

    internal static unsafe AbiSnapshot GetAbiInfo()
    {
        var info = new NativeMethods.AbiInfo { StructSize = (uint)sizeof(NativeMethods.AbiInfo) };
        using var error = new NativeErrorBuffer();
        var status = NativeMethods.GetAbiInfo(ref info, error.Pointer, (nuint)error.Length);
        if (status != 0) throw new NativeCallException(status, error.Text);
        return new AbiSnapshot(info.AbiMajor, info.AbiMinor, Utf8(info.ShimVersion), Utf8(info.AudioCppCommit), Utf8(info.Backend), info.Capabilities);
    }

    internal static string GetLoaderCatalog()
    {
        using var error = new NativeErrorBuffer();
        var status = NativeMethods.GetLoaderCatalog(out var json, error.Pointer, (nuint)error.Length);
        try
        {
            if (status != 0) throw new NativeCallException(status, error.Text);
            return Marshal.PtrToStringUTF8(json) ?? "";
        }
        finally { if (json != IntPtr.Zero) NativeMethods.BufferFree(json); }
    }

    internal static string GetPackageCatalog()
    {
        using var error = new NativeErrorBuffer();
        var status = NativeMethods.GetPackageCatalog(out var json, error.Pointer, (nuint)error.Length);
        try
        {
            if (status != 0) throw new NativeCallException(status, error.Text);
            return Marshal.PtrToStringUTF8(json) ?? "";
        }
        finally { if (json != IntPtr.Zero) NativeMethods.BufferFree(json); }
    }

    internal static string InstallPackage(string packageId, string? repositoryRoot, string? modelsRoot,
        bool overwrite, Action<ulong, ulong, string?>? progress)
    {
        using var error = new NativeErrorBuffer();
        NativeMethods.DownloadProgressCallback? callback = null;
        if (progress is not null)
        {
            callback = (downloaded, total, message, _) => progress(downloaded, total,
                message == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(message));
        }
        var status = NativeMethods.InstallPackage(packageId, repositoryRoot, modelsRoot, overwrite ? 1 : 0,
            callback, IntPtr.Zero, out var result, error.Pointer, (nuint)error.Length);
        try
        {
            if (status != 0) throw new NativeCallException(status, error.Text);
            return Marshal.PtrToStringUTF8(result) ?? "";
        }
        finally { if (result != IntPtr.Zero) NativeMethods.BufferFree(result); }
    }

    private static unsafe string Utf8(byte* value) => value is null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)value) ?? string.Empty;

    internal static unsafe (SafeModelHandle Handle, string Error) LoadModel(
        string modelPath, string? familyHint, string? backend, int device, int threads, string? options)
    {
        using var error = new NativeErrorBuffer();
        var native = NativeMethods.ModelLoad(modelPath, familyHint, backend, device, threads, options, error.Pointer, (nuint)error.Length);
        return (new SafeModelHandle(native), error.Text);
    }

    internal static unsafe (float[] Samples, int SampleRate, int Channels) Synthesize(
        SafeModelHandle model, string? task, string text, string? voiceId, ReadOnlySpan<float> reference,
        int referenceSampleRate, string? options)
    {
        fixed (float* referencePtr = reference)
        {
            float* samples = null;
            var count = 0;
            var sampleRate = 0;
            var channels = 0;
            using var error = new NativeErrorBuffer();
            var status = NativeMethods.ModelSynthesize(model, task, text, voiceId, referencePtr, reference.Length,
                referenceSampleRate, options, &samples, &count, &sampleRate, &channels, error.Pointer, (nuint)error.Length);
            try
            {
                if (status != 0) throw new NativeCallException(status, error.Text);
                if (samples is null || count <= 0 || sampleRate <= 0 || channels <= 0) throw new NativeCallException(3, "Native model returned an invalid audio buffer.");
                var managed = new float[count];
                Marshal.Copy((IntPtr)samples, managed, 0, count);
                return (managed, sampleRate, channels);
            }
            finally
            {
                if (samples is not null) NativeMethods.BufferFree((IntPtr)samples);
            }
        }
    }

    internal static unsafe string Transcribe(SafeModelHandle model, ReadOnlySpan<float> audio, int sampleRate,
        int channels, string? options)
    {
        fixed (float* audioPtr = audio)
        {
            using var error = new NativeErrorBuffer();
            var status = NativeMethods.ModelTranscribe(model, audioPtr, audio.Length, sampleRate, channels,
                options, out var text, error.Pointer, (nuint)error.Length);
            try
            {
                if (status != 0) throw new NativeCallException(status, error.Text);
                return Marshal.PtrToStringUTF8(text) ?? string.Empty;
            }
            finally { if (text != IntPtr.Zero) NativeMethods.BufferFree(text); }
        }
    }

    internal static unsafe string RunJson(SafeModelHandle model, string? task, string? text, ReadOnlySpan<float> audio,
        int sampleRate, int channels, string? voiceId, ReadOnlySpan<float> reference, int referenceRate, string? options)
    {
        fixed (float* audioPtr = audio) fixed (float* referencePtr = reference)
        using (var error = new NativeErrorBuffer())
        {
            var status = NativeMethods.ModelRunJson(model, task, text, audioPtr, audio.Length, sampleRate, channels,
                voiceId, referencePtr, reference.Length, referenceRate, options, out var json, error.Pointer, (nuint)error.Length);
            try { if (status != 0) throw new NativeCallException(status, error.Text); return Marshal.PtrToStringUTF8(json) ?? "{}"; }
            finally { if (json != IntPtr.Zero) NativeMethods.BufferFree(json); }
        }
    }

    internal static (SafeStreamHandle Handle, string Info) OpenStream(SafeModelHandle model, string task, string? options)
    {
        using var error = new NativeErrorBuffer();
        var status = NativeMethods.StreamOpen(model, task, options, out var native, out var info,
            error.Pointer, (nuint)error.Length);
        try
        {
            if (status != 0) throw new NativeCallException(status, error.Text);
            return (new SafeStreamHandle(native), Marshal.PtrToStringUTF8(info) ?? "{}");
        }
        finally { if (info != IntPtr.Zero) NativeMethods.BufferFree(info); }
    }

    internal static unsafe string StreamPushPcm(SafeStreamHandle stream, ReadOnlySpan<float> samples,
        int sampleRate, int channels)
    {
        fixed (float* samplesPtr = samples)
        using (var error = new NativeErrorBuffer())
        {
            var status = NativeMethods.StreamPushPcm(stream, samplesPtr, samples.Length, sampleRate, channels,
                out var json, error.Pointer, (nuint)error.Length);
            try
            {
                if (status != 0) throw new NativeCallException(status, error.Text);
                return Marshal.PtrToStringUTF8(json) ?? "{}";
            }
            finally { if (json != IntPtr.Zero) NativeMethods.BufferFree(json); }
        }
    }

    internal static string StreamFinish(SafeStreamHandle stream)
    {
        using var error = new NativeErrorBuffer();
        var status = NativeMethods.StreamFinish(stream, out var json, error.Pointer, (nuint)error.Length);
        try
        {
            if (status != 0) throw new NativeCallException(status, error.Text);
            return Marshal.PtrToStringUTF8(json) ?? "{}";
        }
        finally { if (json != IntPtr.Zero) NativeMethods.BufferFree(json); }
    }

    internal sealed class NativeErrorBuffer : IDisposable
    {
        private readonly IntPtr _memory = Marshal.AllocHGlobal(ErrorBufferLength);
        internal IntPtr Pointer => _memory;
        internal int Length => ErrorBufferLength;
        internal string Text
        {
            get
            {
                var bytes = new byte[ErrorBufferLength];
                Marshal.Copy(_memory, bytes, 0, bytes.Length);
                var length = Array.IndexOf(bytes, (byte)0);
                return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
            }
        }
        public NativeErrorBuffer()
        {
            var bytes = new byte[ErrorBufferLength];
            Marshal.Copy(bytes, 0, _memory, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(_memory);
    }
}

internal sealed class NativeCallException(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;
}
