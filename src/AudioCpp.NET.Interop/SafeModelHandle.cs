using Microsoft.Win32.SafeHandles;

namespace AudioCpp.NET.Interop;

internal sealed class SafeModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeModelHandle() : base(ownsHandle: true) { }

    internal SafeModelHandle(IntPtr handle) : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        NativeMethods.ModelFree(handle);
        return true;
    }
}
