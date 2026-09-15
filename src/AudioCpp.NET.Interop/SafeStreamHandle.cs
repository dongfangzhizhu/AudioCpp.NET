using Microsoft.Win32.SafeHandles;

namespace AudioCpp.NET.Interop;

internal sealed class SafeStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeStreamHandle() : base(ownsHandle: true) { }

    internal SafeStreamHandle(IntPtr handle) : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        NativeMethods.StreamFree(handle);
        return true;
    }
}