using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Res1337;

public sealed record DisplayTarget(string DeviceName, string Name, bool IsPrimary)
{
    public string? StableId { get; init; }
}

public sealed record DisplayMode(int Width, int Height, int RefreshRate)
{
    public override string ToString() => RefreshRate > 0
        ? $"{Width} × {Height} · {RefreshRate} Hz"
        : $"{Width} × {Height} · Driver default";
}

internal static class DisplayService
{
    private const uint AttachedToDesktop = 0x00000001;
    private const uint ActiveMonitor = 0x00000001;
    private const uint GetMonitorInterfaceName = 0x00000001;
    private const uint PrimaryDevice = 0x00000004;
    private const uint MirroringDriver = 0x00000008;

    private const uint DisplayOrientationField = 0x00000080;
    private const uint BitsPerPixelField = 0x00040000;
    private const uint WidthField = 0x00080000;
    private const uint HeightField = 0x00100000;
    private const uint DisplayFlagsField = 0x00200000;
    private const uint RefreshRateField = 0x00400000;
    private const uint FixedOutputField = 0x20000000;

    private const uint TestMode = 0x00000002;
    private const uint ApplyDynamically = 0;
    private const int CurrentSettings = -1;

    public static IReadOnlyList<DisplayTarget> GetDisplays()
    {
        var displays = new List<DisplayTarget>();
        for (uint index = 0; TryGetDevice(null, index, 0, out var device); index++)
        {
            if ((device.StateFlags & AttachedToDesktop) == 0 || (device.StateFlags & MirroringDriver) != 0)
                continue;

            string deviceName = device.DeviceName;
            bool isPrimary = (device.StateFlags & PrimaryDevice) != 0;
            var (name, stableId) = GetDisplayDetails(deviceName);
            displays.Add(new DisplayTarget(deviceName, name, isPrimary) { StableId = stableId });
        }

        return displays.OrderByDescending(display => display.IsPrimary).ToArray();
    }

    public static IReadOnlyList<DisplayMode> GetModes(string deviceName)
    {
        var current = GetCurrentSettings(deviceName);
        var modes = new HashSet<DisplayMode> { ToDisplayMode(current) };

        for (int index = 0; TryGetSettings(deviceName, index, out var settings); index++)
        {
            if (settings.Width > 0 && settings.Height > 0 && settings.BitsPerPixel == current.BitsPerPixel)
                modes.Add(ToDisplayMode(settings));
        }

        return modes.OrderByDescending(mode => mode.Width)
            .ThenByDescending(mode => mode.Height)
            .ThenByDescending(mode => mode.RefreshRate)
            .ToArray();
    }

    public static DisplayMode GetCurrent(string deviceName) => ToDisplayMode(GetCurrentSettings(deviceName));

    public static void Validate(string deviceName, DisplayMode mode) => PrepareChange(deviceName, mode, out _);

    public static void Apply(string deviceName, DisplayMode mode)
    {
        // Applying dynamically keeps Windows' saved mode available after sign-out or reboot.
        if (PrepareChange(deviceName, mode, out var settings))
            ChangeSettings(deviceName, ref settings, mode, ApplyDynamically);
    }

    private static bool PrepareChange(string deviceName, DisplayMode mode, out NativeDisplayMode settings)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Width <= 0 || mode.Height <= 0 || mode.RefreshRate < 0 || mode.RefreshRate == 1)
            throw new ArgumentException("Enter positive dimensions and a refresh rate of at least 2 Hz, or use the driver default.", nameof(mode));

        settings = GetCurrentSettings(deviceName);
        if (ToDisplayMode(settings) == mode)
            return false;

        // Reuse the current display's orientation, color depth and scaling settings.
        // Windows keeps control of monitor placement when the desktop area changes.
        settings.Fields &= DisplayOrientationField | BitsPerPixelField | FixedOutputField;
        settings.Fields |= WidthField | HeightField;
        settings.Width = mode.Width;
        settings.Height = mode.Height;
        if (mode.RefreshRate > 0)
        {
            settings.Fields |= RefreshRateField;
            settings.RefreshRate = mode.RefreshRate;
        }

        if (TryGetMatchingFlags(deviceName, mode, settings.BitsPerPixel, out var flags))
        {
            settings.Fields |= DisplayFlagsField;
            settings.DisplayFlags = flags;
        }

        ChangeSettings(deviceName, ref settings, mode, TestMode);
        return true;
    }

    private static bool TryGetMatchingFlags(string deviceName, DisplayMode mode, int bitsPerPixel, out uint flags)
    {
        for (int index = 0; TryGetSettings(deviceName, index, out var settings); index++)
        {
            if (settings.BitsPerPixel == bitsPerPixel && (settings.Fields & DisplayFlagsField) != 0 &&
                ToDisplayMode(settings) == mode)
            {
                flags = settings.DisplayFlags;
                return true;
            }
        }

        flags = 0;
        return false;
    }

    private static (string Name, string? StableId) GetDisplayDetails(string deviceName)
    {
        const string prefix = @"\\.\DISPLAY";
        string label = deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? $"Display {deviceName[prefix.Length..]}"
            : deviceName;

        var monitorIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? model = null;
        for (uint index = 0; TryGetDevice(deviceName, index, GetMonitorInterfaceName, out var monitor); index++)
        {
            if ((monitor.StateFlags & ActiveMonitor) == 0)
                continue;

            if (!string.IsNullOrWhiteSpace(monitor.DeviceId))
                monitorIds.Add(monitor.DeviceId);

            string name = monitor.DeviceString.Trim();
            if (model is null && name.Length > 0 && !name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase))
                model = name;
        }

        return (model is null ? label : $"{label} ({model})", monitorIds.Count > 0 ? string.Join('|', monitorIds) : null);
    }

    private static bool TryGetDevice(string? adapter, uint index, uint flags, out NativeDisplayDevice device)
    {
        device = new NativeDisplayDevice { Size = (uint)Marshal.SizeOf<NativeDisplayDevice>() };
        return EnumDisplayDevices(adapter, index, ref device, flags);
    }

    private static NativeDisplayMode GetCurrentSettings(string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (TryGetSettings(deviceName, CurrentSettings, out var settings) && settings.Width > 0 && settings.Height > 0)
            return settings;

        throw new InvalidOperationException("Windows could not read this display. Check that it is connected and active.");
    }

    private static bool TryGetSettings(string deviceName, int index, out NativeDisplayMode settings)
    {
        settings = new NativeDisplayMode { Size = (ushort)Marshal.SizeOf<NativeDisplayMode>() };
        bool found = EnumDisplaySettings(deviceName, index, ref settings, 0);
        settings.DriverExtra = 0;
        return found;
    }

    private static DisplayMode ToDisplayMode(NativeDisplayMode settings) =>
        new(settings.Width, settings.Height, settings.RefreshRate > 1 ? settings.RefreshRate : 0);

    private static void ChangeSettings(string deviceName, ref NativeDisplayMode settings, DisplayMode mode, uint flags)
    {
        int result = ChangeDisplaySettings(deviceName, ref settings, IntPtr.Zero, flags, IntPtr.Zero);
        if (result == 0)
            return;

        string refreshRate = mode.RefreshRate > 0 ? $" at {mode.RefreshRate} Hz" : "";
        string resolution = $"{mode.Width} × {mode.Height}{refreshRate}";
        string message = result switch
        {
            1 => $"Windows requires a restart to use {resolution} on this display.",
            -1 => $"The display driver could not switch to {resolution}.",
            -2 => $"This display or its graphics driver does not support {resolution}.",
            -6 => $"{resolution} is unavailable with the current multi-monitor configuration.",
            _ => $"Windows could not switch to {resolution} (display error {result})."
        };
        throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint deviceIndex, ref NativeDisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeIndex, ref NativeDisplayMode settings, uint flags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ChangeDisplaySettings(string deviceName, ref NativeDisplayMode settings, IntPtr window, uint flags, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;

        // This is the display member of the native display/printer union.
        public int PositionX;
        public int PositionY;
        public uint Orientation;
        public uint FixedOutput;

        public short Color;
        public short Duplex;
        public short YResolution;
        public short TrueTypeOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public int BitsPerPixel;
        public int Width;
        public int Height;
        public uint DisplayFlags;
        public int RefreshRate;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
