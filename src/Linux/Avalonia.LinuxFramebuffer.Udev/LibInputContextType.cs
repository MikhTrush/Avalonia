namespace Avalonia.LinuxFramebuffer.Udev;

public enum LibInputContextType
{
    /// <summary>
    /// Uses libinput_path_create_context. Requires explicit device paths. 
    /// Does not support hotplug (device reconnect).
    /// </summary>
    Path,

    /// <summary>
    /// Uses libinput_udev_create_context. Automatically discovers devices via udev.
    /// Fully supports hotplug (device reconnect).
    /// </summary>
    Udev,
}
