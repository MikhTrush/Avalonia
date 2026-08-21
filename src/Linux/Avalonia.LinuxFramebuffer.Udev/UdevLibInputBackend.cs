using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LinuxFramebuffer.Input;
using Avalonia.LinuxFramebuffer.Input.LibInput;
using Avalonia.Logging;
using Avalonia.Platform;

namespace Avalonia.LinuxFramebuffer.Udev;

public sealed class UdevLibInputBackend : IInputBackend, IDisposable
{
    private IScreenInfoProvider? _screen;
    private IInputRoot? _inputRoot;
    private Action<RawInputEventArgs>? _onInput;
    private readonly UdevLibInputBackendOptions _options;

    private CancellationTokenSource? _cts;
    private Thread? _inputThread;
    private IntPtr _ctx;
    private IntPtr _udevCtx;

    // State required for input handling (self-contained for this separate project)
    private readonly Dictionary<int, Point> _pointers = new();
    private readonly TouchDevice _touch = new();
    private readonly MouseDevice _mouse = new();
    private Point _mousePosition;
    private const string LogArea = "LinuxFramebuffer/UdevLibInput";

    public UdevLibInputBackend() : this(new UdevLibInputBackendOptions()) { }

    public UdevLibInputBackend(UdevLibInputBackendOptions options)
    {
        _options = options;
    }

    private IInputRoot InputRoot
        => _inputRoot ?? throw new InvalidOperationException($"{nameof(InputRoot)} hasn't been set");

    private void ScheduleInput(RawInputEventArgs ev) => _onInput?.Invoke(ev);

    private void HandleTouch(IntPtr ev, LibInputNativeUnsafeMethods.LibInputEventType type)
    {
        var tev = LibInputNativeUnsafeMethods.libinput_event_get_touch_event(ev);
        if (tev == IntPtr.Zero) return;

        if (type < LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_FRAME)
        {
            var info = _screen!.ScaledSize;
            var slot = LibInputNativeUnsafeMethods.libinput_event_touch_get_slot(tev);
            Point pt;

            if (type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_DOWN ||
                type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_MOTION)
            {
                var x = LibInputNativeUnsafeMethods.libinput_event_touch_get_x_transformed(tev, (int)info.Width);
                var y = LibInputNativeUnsafeMethods.libinput_event_touch_get_y_transformed(tev, (int)info.Height);
                pt = new Point(x, y);
                _pointers[slot] = pt;
            }
            else
            {
                _pointers.TryGetValue(slot, out pt);
                _pointers.Remove(slot);
            }

            var ts = LibInputNativeUnsafeMethods.libinput_event_touch_get_time_usec(tev) / 1000;
            if (_inputRoot == null) return;

            ScheduleInput(new RawTouchEventArgs(_touch, ts, _inputRoot,
                type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_DOWN ? RawPointerEventType.TouchBegin :
                type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_UP ? RawPointerEventType.TouchEnd :
                type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_MOTION ? RawPointerEventType.TouchUpdate :
                RawPointerEventType.TouchCancel,
                pt, RawInputModifiers.None, slot));
        }
    }

    private void HandlePointer(IntPtr ev, LibInputNativeUnsafeMethods.LibInputEventType type)
    {
        var modifiers = RawInputModifiers.None;
        var pev = LibInputNativeUnsafeMethods.libinput_event_get_pointer_event(ev);
        var info = _screen!.ScaledSize;
        var ts = LibInputNativeUnsafeMethods.libinput_event_pointer_get_time_usec(pev) / 1000;

        switch (type)
        {
            case LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_MOTION_ABSOLUTE:
                _mousePosition = new Point(
                    LibInputNativeUnsafeMethods.libinput_event_pointer_get_absolute_x_transformed(pev, (int)info.Width),
                    LibInputNativeUnsafeMethods.libinput_event_pointer_get_absolute_y_transformed(pev, (int)info.Height));
                ScheduleInput(new RawPointerEventArgs(_mouse, ts, InputRoot, RawPointerEventType.Move, _mousePosition,
                    modifiers));
                break;

            case LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_BUTTON:
            {
                var button = (EvKey)LibInputNativeUnsafeMethods.libinput_event_pointer_get_button(pev);
                var buttonState = LibInputNativeUnsafeMethods.libinput_event_pointer_get_button_state(pev);

                RawPointerEventArgs? evnt = button switch
                {
                    EvKey.BTN_LEFT when buttonState == 1 => new(_mouse, ts, InputRoot,
                        RawPointerEventType.LeftButtonDown, _mousePosition, modifiers),
                    EvKey.BTN_LEFT when buttonState == 0 => new(_mouse, ts, InputRoot, RawPointerEventType.LeftButtonUp,
                        _mousePosition, modifiers),
                    EvKey.BTN_RIGHT when buttonState == 1 => new(_mouse, ts, InputRoot,
                        RawPointerEventType.RightButtonDown, _mousePosition, modifiers), // Fixed Avalonia repo typo
                    EvKey.BTN_RIGHT when buttonState == 0 => new(_mouse, ts, InputRoot,
                        RawPointerEventType.RightButtonUp, _mousePosition, modifiers), // Fixed Avalonia repo typo
                    EvKey.BTN_MIDDLE when buttonState == 1 => new(_mouse, ts, InputRoot,
                        RawPointerEventType.MiddleButtonDown, _mousePosition, modifiers),
                    EvKey.BTN_MIDDLE when buttonState == 0 => new(_mouse, ts, InputRoot,
                        RawPointerEventType.MiddleButtonUp, _mousePosition, modifiers),
                    _ => default,
                };

                if (evnt is not null) ScheduleInput(evnt);
                else Logger.TryGet(LogEventLevel.Warning, LogArea)?.Log(this, $"The button {button} is not associated");
            }
                break;

            case LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_AXIS:
            {
                var sourceAxis = LibInputNativeUnsafeMethods.libinput_event_pointer_get_axis_source(pev);
                if (sourceAxis == LibInputNativeUnsafeMethods.LibInputPointerAxisSource.LIBINPUT_POINTER_AXIS_SOURCE_WHEEL)
                {
                    var value = LibInputNativeUnsafeMethods.libinput_event_pointer_get_axis_value_discrete(pev,
                        LibInputNativeUnsafeMethods.LibInputPointerAxis.LIBINPUT_POINTER_AXIS_SCROLL_VERTICAL);
                    ScheduleInput(new RawMouseWheelEventArgs(_mouse, ts, InputRoot, _mousePosition,
                        new Vector(0, -value), modifiers));
                }
                else
                {
                    Logger.TryGet(LogEventLevel.Debug, LogArea)
                        ?.Log(this, $"The pointer axis {sourceAxis} is not managed.");
                }
            }
                break;

            case LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_SCROLL_WHEEL:
            {
                var value = new Vector(0,
                    -LibInputNativeUnsafeMethods.libinput_event_pointer_get_scroll_value_v120(pev,
                        LibInputNativeUnsafeMethods.LibInputPointerAxis.LIBINPUT_POINTER_AXIS_SCROLL_VERTICAL) / 120);
                ScheduleInput(new RawMouseWheelEventArgs(_mouse, ts, InputRoot, _mousePosition, value, modifiers));
            }
                break;

            default:
                Logger.TryGet(LogEventLevel.Warning, LogArea)?.Log(this, $"The pointer event {type} is not mapped.");
                break;
        }
    }

    private unsafe void InputThread(IntPtr ctx, CancellationToken token)
    {
        var fd = LibInputNativeUnsafeMethods.libinput_get_fd(ctx);
        var screenOrientation = _screen is ISurfaceOrientation surfaceOrientation ?
            surfaceOrientation.Orientation :
            SurfaceOrientation.Rotation0;

        float[] matrix = screenOrientation switch
        {
            SurfaceOrientation.Rotation90 => [0, 1, 0, -1, 0, 1],
            SurfaceOrientation.Rotation180 => [-1, 0, 1, 0, -1, 1],
            SurfaceOrientation.Rotation270 => [0, -1, 1, 1, 0, 0],
            _ => [1, 0, 0, 0, 1, 0],
        };

        if (_options.ContextType == LibInputContextType.Path)
        {
            var paths = _options.Events ?? Directory.GetFiles("/dev/input", "event*");
            foreach (var path in paths)
            {
                var device = LibInputNativeUnsafeMethods.libinput_path_add_device(ctx, path);
                if (device != IntPtr.Zero)
                {
                    LibInputNativeUnsafeMethods.libinput_device_config_calibration_set_matrix(device, matrix);
                }
            }
        }

        while (!token.IsCancellationRequested)
        {
            IntPtr ev;
            LibInputNativeUnsafeMethods.libinput_dispatch(ctx);
            while ((ev = LibInputNativeUnsafeMethods.libinput_get_event(ctx)) != IntPtr.Zero)
            {
                var type = LibInputNativeUnsafeMethods.libinput_event_get_type(ev);

                if (type == LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_DEVICE_ADDED)
                {
                    var device = UdevNativeMethods.libinput_event_get_device(ev);
                    if (device != IntPtr.Zero)
                    {
                        // Apply calibration matrix to newly hotplugged devices
                        LibInputNativeUnsafeMethods.libinput_device_config_calibration_set_matrix(device, matrix);
                    }
                }
                else if (type >= LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_DOWN &&
                         type <= LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_TOUCH_CANCEL)
                {
                    HandleTouch(ev, type);
                }
                else if (type >= LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_MOTION &&
                         type <= LibInputNativeUnsafeMethods.LibInputEventType.LIBINPUT_EVENT_POINTER_AXIS)
                {
                    HandlePointer(ev, type);
                }

                LibInputNativeUnsafeMethods.libinput_event_destroy(ev);
                LibInputNativeUnsafeMethods.libinput_dispatch(ctx);
            }

            if (token.IsCancellationRequested) break;

            var pfd = new PollFd { fd = fd, events = 1 };
            NativeUnsafeMethods.poll(&pfd, new IntPtr(1), 10);
        }

        // Cleanup native resources
        UdevNativeMethods.libinput_unref(ctx);
        if (_udevCtx != IntPtr.Zero)
        {
            UdevNativeMethods.udev_unref(_udevCtx);
            _udevCtx = IntPtr.Zero;
        }
    }

    public unsafe void Initialize(IScreenInfoProvider screen, Action<RawInputEventArgs> onInput)
    {
        _screen = screen;
        _onInput = onInput;
        _cts = new CancellationTokenSource();

        if (_options.ContextType == LibInputContextType.Udev)
        {
            try
            {
                _udevCtx = UdevNativeMethods.udev_new();
                if (_udevCtx == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to initialize udev context.");

                _ctx = UdevNativeMethods.libinput_udev_create_context(LibInputUdevInterface.Interface, IntPtr.Zero, _udevCtx);
                if (_ctx == IntPtr.Zero)
                {
                    UdevNativeMethods.udev_unref(_udevCtx);
                    _udevCtx = IntPtr.Zero;
                    throw new InvalidOperationException("Failed to initialize libinput context via udev.");
                }

                string seat = _options.UdevSeat ?? "seat0";
                int rc = UdevNativeMethods.libinput_udev_assign_seat(_ctx, seat);
                if (rc != 0)
                {
                    UdevNativeMethods.libinput_unref(_ctx);
                    UdevNativeMethods.udev_unref(_udevCtx);
                    _udevCtx = IntPtr.Zero;
                    throw new InvalidOperationException(
                        $"Failed to assign udev seat '{seat}'. Ensure the app has appropriate permissions (e.g., 'input' group).");
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "libudev is not available on this system. Hotplug support requires libudev. " +
                    "Please use LibInputContextType.Path instead.", ex);
            }
        }
        else
        {
            _ctx = LibInputNativeUnsafeMethods.libinput_path_create_context();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("Failed to initialize libinput path context.");
        }

        _inputThread = new Thread(() => InputThread(_ctx, _cts.Token))
        {
            Name = "Input Manager Worker (Udev)", IsBackground = true
        };
        _inputThread.Start();
    }

    public void SetInputRoot(IInputRoot root)
    {
        _inputRoot = root;
    }

    public void Dispose()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        _inputThread?.Join(500);
    }
}
