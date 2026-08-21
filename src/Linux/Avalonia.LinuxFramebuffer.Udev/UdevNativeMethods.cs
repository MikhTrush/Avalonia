using System;
using System.Runtime.InteropServices;

namespace Avalonia.LinuxFramebuffer.Udev;

internal static class UdevNativeMethods
{
    private const string LibUdev = "libudev.so.1";
    private const string LibInput = "libinput.so.10";

    [DllImport(LibUdev, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr udev_new();

    [DllImport(LibUdev, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr udev_unref(IntPtr udev);

    [DllImport(LibInput, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe IntPtr libinput_udev_create_context(IntPtr* @interface, IntPtr user_data, IntPtr udev);

    [DllImport(LibInput, CallingConvention = CallingConvention.Cdecl)]
    public static extern int
        libinput_udev_assign_seat(IntPtr libinput, [MarshalAs(UnmanagedType.LPStr)] string seat_id);

    [DllImport(LibInput, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libinput_event_get_device(IntPtr ev);

    [DllImport(LibInput, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libinput_unref(IntPtr libinput);
}
