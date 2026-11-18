using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Res1337;

internal sealed class HotkeyWindow : IDisposable
{
    private const int WmDisplayChange = 0x007E;
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int WmHotkey = 0x0312;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source = new(new HwndSourceParameters("1337res shortcuts")
    {
        WindowStyle = WsPopup,
        ExtendedWindowStyle = WsExToolWindow
    });
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<int, Guid> _bindings = [];
    private int _lastId;

    public HotkeyWindow()
    {
        _source.AddHook(OnMessage);
        _displayTimer.Tick += (_, _) =>
        {
            _displayTimer.Stop();
            DisplaysChanged?.Invoke();
        };
    }

    public event Action<Guid>? Pressed;
    public event Action? DisplaysChanged;

    public int Register(Guid profile, Hotkey shortcut)
    {
        if (shortcut.Modifiers == 0 || (shortcut.Modifiers & ~15u) != 0
            || shortcut.VirtualKey is < 1 or > 254 or 0x7B)
        {
            throw new InvalidOperationException("Choose a modifier and a key other than F12 for the shortcut.");
        }

        // Application-range IDs wrap so a long-running tray session can keep editing shortcuts.
        if (_bindings.Count >= 0xBFFF) throw new InvalidOperationException("No more Windows shortcut IDs are available.");
        do { _lastId = _lastId % 0xBFFF + 1; } while (_bindings.ContainsKey(_lastId));
        if (!RegisterHotKey(_source.Handle, _lastId, shortcut.Modifiers | ModNoRepeat, shortcut.VirtualKey))
        {
            throw new InvalidOperationException($"{shortcut} is unavailable. Windows or another app may already use it.");
        }
        _bindings.Add(_lastId, profile);
        return _lastId;
    }

    public void Unregister(int id)
    {
        if (_bindings.Remove(id)) UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _bindings.TryGetValue(wParam.ToInt32(), out Guid profile))
        {
            handled = true;
            _source.Dispatcher.BeginInvoke(() => Pressed?.Invoke(profile));
        }
        else if (message == WmDisplayChange || (message == WmDeviceChange && wParam.ToInt64() == DbtDevNodesChanged))
        {
            _displayTimer.Stop();
            _displayTimer.Start();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _displayTimer.Stop();
        foreach (int id in _bindings.Keys.ToArray()) Unregister(id);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
