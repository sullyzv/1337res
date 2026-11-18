using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Res1337;

public sealed record ConnectedMonitor(string Id, string Name, string? DeviceName, bool IsEnabled, bool IsPrimary);

internal static class MonitorTopology
{
    private const uint QueryAllPaths = 0x00000001;
    private const uint QueryVirtualModeAware = 0x00000010;
    private const uint QueryVirtualRefreshAware = 0x00000040;
    private const uint PathActive = 0x00000001;
    private const uint PathSupportsVirtualMode = 0x00000008;
    private const uint UseSuppliedConfiguration = 0x00000020;
    private const uint ValidateConfiguration = 0x00000040;
    private const uint ApplyConfiguration = 0x00000080;
    private const uint AllowChanges = 0x00000400;
    private const uint ApplyVirtualModeAware = 0x00008000;
    private const uint ApplyVirtualRefreshAware = 0x00020000;
    private const uint InvalidIndex = uint.MaxValue;
    private const uint InvalidVirtualIndex = ushort.MaxValue;
    private const uint SourceModeType = 1;
    private const uint IdentityRotation = 1;
    private const uint PreferredScaling = 128;
    private const int InsufficientBuffer = 122;
    private const int BadConfiguration = 1610;

    private static readonly bool SupportsVirtualRefresh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal sealed record Snapshot(NativePath[] Paths, NativeMode[] Modes);

    public static IReadOnlyList<ConnectedMonitor> GetMonitors()
    {
        var configuration = Query(QueryAllPaths);
        return ReadMonitors(configuration.Paths)
            .Select((monitor, index) =>
            {
                var path = monitor.Paths[0];
                bool enabled = IsActive(path);
                var source = enabled ? ReadSourceMode(path, configuration.Modes) : null;
                string? deviceName = enabled ? ReadSourceName(path.Source) : null;
                string name = monitor.Name ?? $"Display {index + 1}";
                return new ConnectedMonitor(monitor.Id, name, deviceName, enabled, source is { X: 0, Y: 0 });
            })
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenByDescending(monitor => monitor.IsEnabled)
            .ToArray();
    }

    public static Snapshot? SetEnabled(string monitorId, bool enabled)
    {
        var configuration = Query(QueryAllPaths);
        var monitor = ReadMonitors(configuration.Paths).FirstOrDefault(item =>
            string.Equals(item.Id, monitorId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This monitor is no longer connected. Refresh the monitor list and try again.");
        var activePaths = configuration.Paths.Where(IsActive).ToArray();
        if (activePaths.Length == 0)
            throw new InvalidOperationException("Windows isn't reporting an active desktop to restore if the change is reverted.");
        var previous = CompactModes(activePaths, configuration.Modes);
        if (monitor.Paths.Any(IsActive) == enabled) return previous;

        if (enabled)
        {
            Enable(monitor, previous);
        }
        else
        {
            var targets = monitor.Paths.Select(TargetKey).ToHashSet();
            var remaining = activePaths.Where(path => !targets.Contains(TargetKey(path))).ToArray();
            // A desktop without an active connected path needs display power control.
            if (!remaining.Any(path => path.Target.Available != 0))
                return null;

            var next = CompactModes(remaining, configuration.Modes);
            RebasePrimary(next);
            ApplyValidated(next, "disable this monitor");
        }

        return previous;
    }

    public static void Restore(Snapshot snapshot) => ApplyValidated(snapshot, "restore the previous monitor arrangement");

    private static void Enable(MonitorPaths monitor, Snapshot previous)
    {
        var usedSources = previous.Paths.Select(SourceKey).ToHashSet();
        var candidates = monitor.Paths.Where(path => path.Target.Available != 0 && !usedSources.Contains(SourceKey(path)));
        int failure = BadConfiguration;
        foreach (var candidate in candidates)
        {
            var path = candidate;
            path.Flags |= PathActive;
            path.Source.ModeIndex = InvalidIndex;
            path.Target.ModeIndex = InvalidIndex;
            path.Target.Rotation = IdentityRotation;
            path.Target.Scaling = PreferredScaling;
            path.Target.RefreshRate = default;
            path.Target.ScanlineOrdering = 0;
            var next = new Snapshot([.. previous.Paths, path], previous.Modes);

            // First try the request with all current modes held fixed. Some drivers need
            // permission to solve the new path and other timings together.
            failure = Validate(next, out uint adjustments);
            if (failure == 0)
            {
                ThrowIfError(SetConfiguration(next, ApplyConfiguration | adjustments), "enable this monitor");
                return;
            }
            if (failure is 5 or 50) break;
        }

        ThrowIfError(failure, "enable this monitor as a separate display");
    }

    private static void ApplyValidated(Snapshot configuration, string operation)
    {
        ThrowIfError(Validate(configuration, out uint adjustments), operation);
        ThrowIfError(SetConfiguration(configuration, ApplyConfiguration | adjustments), operation);
    }

    private static int Validate(Snapshot configuration, out uint adjustments)
    {
        int result = SetConfiguration(configuration, ValidateConfiguration);
        adjustments = result is 5 or 50 or 0 ? 0 : AllowChanges;
        return adjustments == 0 ? result : SetConfiguration(configuration, ValidateConfiguration | adjustments);
    }

    private static void RebasePrimary(Snapshot configuration)
    {
        // Windows treats the desktop source at (0, 0) as primary. If it was removed,
        // translate the remaining desktop together so their relative positions stay intact.
        var sourceModes = configuration.Modes.Where(mode => mode.Type == SourceModeType).ToArray();
        if (sourceModes.Length == 0 || sourceModes.Any(mode => mode.Source is { X: 0, Y: 0 })) return;

        var origin = sourceModes[0].Source;
        for (int index = 0; index < configuration.Modes.Length; index++)
        {
            ref var mode = ref configuration.Modes[index];
            if (mode.Type != SourceModeType) continue;
            mode.Source.X -= origin.X;
            mode.Source.Y -= origin.Y;
        }
    }

    private static Snapshot CompactModes(NativePath[] paths, NativeMode[] modes)
    {
        var indexes = new Dictionary<uint, uint>();
        var retained = new List<NativeMode>();
        var mapped = new NativePath[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            if ((path.Flags & PathSupportsVirtualMode) != 0)
            {
                // Virtual-aware paths store two 16-bit values in each mode-index slot.
                uint source = Retain(path.Source.ModeIndex >> 16, InvalidVirtualIndex);
                uint target = Retain(path.Target.ModeIndex >> 16, InvalidVirtualIndex);
                uint desktop = Retain(path.Target.ModeIndex & InvalidVirtualIndex, InvalidVirtualIndex);
                path.Source.ModeIndex = (source << 16) | (path.Source.ModeIndex & InvalidVirtualIndex);
                path.Target.ModeIndex = (target << 16) | desktop;
            }
            else
            {
                path.Source.ModeIndex = Retain(path.Source.ModeIndex, InvalidIndex);
                path.Target.ModeIndex = Retain(path.Target.ModeIndex, InvalidIndex);
            }
            mapped[index] = path;
        }
        return new Snapshot(mapped, retained.ToArray());

        uint Retain(uint original, uint invalid)
        {
            if (original == invalid || original >= modes.Length) return invalid;
            if (indexes.TryGetValue(original, out uint existing)) return existing;
            uint compact = (uint)retained.Count;
            indexes.Add(original, compact);
            retained.Add(modes[original]);
            return compact;
        }
    }

    private static NativeSourceMode? ReadSourceMode(NativePath path, NativeMode[] modes)
    {
        uint index = (path.Flags & PathSupportsVirtualMode) == 0 ? path.Source.ModeIndex : path.Source.ModeIndex >> 16;
        return index < modes.Length && modes[index].Type == SourceModeType ? modes[index].Source : null;
    }

    private static IReadOnlyList<MonitorPaths> ReadMonitors(NativePath[] paths)
    {
        return paths.Where(path => path.Target.Available != 0 || IsActive(path))
            .GroupBy(TargetKey)
            .Select(group =>
            {
                var (id, name) = ReadTargetName(group.First().Target);
                return new MonitorPaths(id, name, group.ToArray());
            })
            .GroupBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MonitorPaths(group.Key, group.Select(item => item.Name).FirstOrDefault(name => name is not null),
                group.SelectMany(item => item.Paths).OrderByDescending(IsActive).ToArray()))
            .ToArray();
    }

    private sealed record MonitorPaths(string Id, string? Name, NativePath[] Paths);

    private static (NativeLuid Adapter, uint Id) TargetKey(NativePath path) => (path.Target.Adapter, path.Target.Id);
    private static (NativeLuid Adapter, uint Id) SourceKey(NativePath path) => (path.Source.Adapter, path.Source.Id);
    private static bool IsActive(NativePath path) => (path.Flags & PathActive) != 0;

    private static (string Id, string? Name) ReadTargetName(NativePathTarget target)
    {
        var request = new NativeTargetName
        {
            Header = new NativeDeviceHeader(2, (uint)Marshal.SizeOf<NativeTargetName>(), target.Adapter, target.Id)
        };
        string fallback = $"target:{target.Adapter.HighPart:x8}:{target.Adapter.LowPart:x8}:{target.Id:x8}";
        if (GetTargetDeviceInfo(ref request) != 0) return (fallback, null);
        string id = string.IsNullOrWhiteSpace(request.MonitorPath) ? fallback : request.MonitorPath;
        string? name = string.IsNullOrWhiteSpace(request.FriendlyName) ? null : request.FriendlyName.Trim();
        return (id, name);
    }

    private static string? ReadSourceName(NativePathSource source)
    {
        var request = new NativeSourceName
        {
            Header = new NativeDeviceHeader(1, (uint)Marshal.SizeOf<NativeSourceName>(), source.Adapter, source.Id)
        };
        return GetSourceDeviceInfo(ref request) == 0 && !string.IsNullOrWhiteSpace(request.DeviceName)
            ? request.DeviceName : null;
    }

    private static Snapshot Query(uint kind)
    {
        uint flags = kind | QueryVirtualModeAware | (SupportsVirtualRefresh ? QueryVirtualRefreshAware : 0);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ThrowIfError(GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount), "read the monitor list");
            if (pathCount == 0) return new Snapshot([], []);
            var paths = new NativePath[pathCount];
            // QueryDisplayConfig requires a non-null mode buffer even when there are no active modes.
            var modes = new NativeMode[Math.Max(modeCount, 1)];
            int result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == InsufficientBuffer) continue;
            ThrowIfError(result, "read the monitor list");
            return new Snapshot(paths[..(int)pathCount], modes[..(int)modeCount]);
        }
        throw new InvalidOperationException("The monitor configuration is changing. Wait for Windows to settle and try again.");
    }

    private static int SetConfiguration(Snapshot snapshot, uint action)
    {
        uint flags = action | UseSuppliedConfiguration | ApplyVirtualModeAware
            | (SupportsVirtualRefresh ? ApplyVirtualRefreshAware : 0);
        return SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths, (uint)snapshot.Modes.Length,
            snapshot.Modes.Length == 0 ? null : snapshot.Modes, flags);
    }

    private static void ThrowIfError(int code, string operation)
    {
        if (code == 0) return;
        string detail = code switch
        {
            5 => "Use 1337res in your local Windows desktop session.",
            50 => "This display driver does not support monitor arrangement changes.",
            BadConfiguration => "The graphics driver does not support the requested combination of active monitors and modes.",
            _ => new Win32Exception(code).Message
        };
        throw new InvalidOperationException($"Windows could not {operation}. {detail}");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] NativePath[] paths,
        ref uint modeCount, [Out] NativeMode[] modes, IntPtr topologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetDisplayConfig(uint pathCount, [In] NativePath[] paths,
        uint modeCount, [In] NativeMode[]? modes, uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetTargetDeviceInfo(ref NativeTargetName device);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetSourceDeviceInfo(ref NativeSourceName device);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeLuid(uint LowPart, int HighPart);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePath
    {
        public NativePathSource Source;
        public NativePathTarget Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePathSource
    {
        public NativeLuid Adapter;
        public uint Id;
        public uint ModeIndex;
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePathTarget
    {
        public NativeLuid Adapter;
        public uint Id;
        public uint ModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public NativeRational RefreshRate;
        public uint ScanlineOrdering;
        public int Available;
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeRational(uint Numerator, uint Denominator);

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct NativeMode
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public NativeLuid Adapter;
        [FieldOffset(16)] public NativeSourceMode Source;
        [FieldOffset(16)] private NativeVideoSignal _target;
        [FieldOffset(16)] private NativeDesktopImage _desktopImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVideoSignal
    {
        public ulong PixelRate;
        public NativeRational HorizontalSync;
        public NativeRational VerticalSync;
        public uint ActiveWidth;
        public uint ActiveHeight;
        public uint TotalWidth;
        public uint TotalHeight;
        public uint AdditionalSignalInfo;
        public uint ScanlineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDesktopImage
    {
        public int SourceWidth;
        public int SourceHeight;
        public NativeRectangle Region;
        public NativeRectangle Clip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeDeviceHeader(uint Type, uint Size, NativeLuid Adapter, uint Id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeTargetName
    {
        public NativeDeviceHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturer;
        public ushort EdidProduct;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeSourceName
    {
        public NativeDeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
}
