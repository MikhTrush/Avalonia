using System.Collections.Generic;

namespace Avalonia.LinuxFramebuffer.Udev;

public sealed class UdevLibInputBackendOptions
{
    /// <summary>
    /// List of event device paths to monitor (e.g., /dev/input/eventX).
    /// Used only when <see cref="ContextType"/> is set to <see cref="LibInputContextType.Path"/>.
    /// </summary>
    public IReadOnlyList<string>? Events { get; init; } = null;

    /// <summary>
    /// Type of initialized libinput context. 
    /// Defaults to <see cref="LibInputContextType.Udev"/> for hotplug support.
    /// </summary>
    public LibInputContextType ContextType { get; init; } = LibInputContextType.Udev;

    /// <summary>
    /// The udev seat name to assign (e.g., "seat0"). 
    /// Used only when <see cref="ContextType"/> is set to <see cref="LibInputContextType.Udev"/>.
    /// If null, "seat0" is used by default.
    /// </summary>
    public string? UdevSeat { get; init; } = null;
}
