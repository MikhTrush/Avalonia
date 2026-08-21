using System;
using System.Runtime.InteropServices;

namespace Avalonia.LinuxFramebuffer.Udev;

internal static unsafe class LibInputUdevInterface
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpenRestrictedCallbackDelegate(IntPtr path, int flags, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseRestrictedCallbackDelegate(int fd, IntPtr userData);

    private static int OpenRestricted(IntPtr path, int flags, IntPtr userData)
    {
        // NativeUnsafeMethods is accessible via InternalsVisibleTo
        var fd = NativeUnsafeMethods.open(Marshal.PtrToStringAnsi(path) ?? string.Empty, flags, 0);
        if (fd == -1)
            return -Marshal.GetLastWin32Error();

        return fd;
    }

    private static void CloseRestricted(int fd, IntPtr userData)
    {
        NativeUnsafeMethods.close(fd);
    }

    public static readonly IntPtr* Interface;

    static LibInputUdevInterface()
    {
        Interface = (IntPtr*)Marshal.AllocHGlobal(IntPtr.Size * 2);

        IntPtr Convert<TDelegate>(TDelegate del) where TDelegate : notnull
        {
            GCHandle.Alloc(del);
            return Marshal.GetFunctionPointerForDelegate(del);
        }

        Interface[0] = Convert(new OpenRestrictedCallbackDelegate(OpenRestricted));
        Interface[1] = Convert(new CloseRestrictedCallbackDelegate(CloseRestricted));
    }
}
