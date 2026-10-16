namespace Reclaim.Core;

public static class Devices
{
    public static IReadOnlyList<string> PhysicalDisks() => OperatingSystem.IsWindows() ? Native.PhysicalDevices() : [];
}
