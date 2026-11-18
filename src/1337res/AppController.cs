using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Res1337;

public sealed class AppController : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly App _app;
    private readonly HotkeyWindow _hotkeys;
    private readonly Dictionary<Guid, int> _hotkeyIds = [];
    private readonly Dictionary<string, DisplayMode> _previousModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Window> _identifiers = [];
    private readonly string? _settingsWarning;
    private Settings _settings;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private ModeConfirmation? _confirmation;
    private bool _exiting;
    private bool _hotkeysSuspended;
    private bool _trayHintShown;

    public AppController(App app)
    {
        _app = app;
        _settings = SettingsStore.Load(out _settingsWarning);
        ReadDisplays();
        _hotkeys = new HotkeyWindow();
        _hotkeys.Pressed += id =>
        {
            if (_hotkeysSuspended || _confirmation is not null || _exiting) return;
            var profile = Profiles.FirstOrDefault(item => item.Id == id);
            if (profile is not null) Try(() => ApplyProfile(profile));
        };
        _hotkeys.DisplaysChanged += () =>
        {
            if (!_exiting && _confirmation is null) Try(RefreshDisplays);
        };
    }

    public IReadOnlyList<DisplayTarget> Displays { get; private set; } = [];
    public IReadOnlyList<ConnectedMonitor> Monitors { get; private set; } = [];
    public string? MonitorManagementError { get; private set; }
    public IReadOnlyList<ResolutionProfile> Profiles => _settings.Profiles;
    public bool CloseToTray => _settings.CloseToTray;
    public bool StartWithWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("1337res") is string;
        }
    }

    public event EventHandler? Changed;
    public event EventHandler<string>? Notice;

    public void Start(bool startInTray)
    {
        _trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "1337res", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        RefreshTray();
        if (!startInTray) ShowWindow();
        if (_settingsWarning is not null) ReportError(_settingsWarning);
        RegisterSavedHotkeys();
    }

    public void ShowWindow()
    {
        if (_exiting) return;
        if (_confirmation is not null)
        {
            _confirmation.Activate();
            return;
        }
        if (_window is null)
        {
            _window = new MainWindow(this);
            _window.Closing += OnWindowClosing;
            _app.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public IReadOnlyList<DisplayMode> GetModes(string deviceName) => DisplayService.GetModes(deviceName);
    public DisplayMode GetCurrent(string deviceName) => DisplayService.GetCurrent(deviceName);

    public void RefreshDisplays()
    {
        ReadDisplays();
        RefreshTray();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReadDisplays()
    {
        Displays = DisplayService.GetDisplays();
        try
        {
            Monitors = MonitorTopology.GetMonitors();
            MonitorManagementError = null;
            Displays = Displays.Select(display =>
            {
                var connected = Monitors.Where(monitor => monitor.IsEnabled &&
                    string.Equals(monitor.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                return connected.Length == 1 && !string.Equals(connected[0].Name, display.Name, StringComparison.OrdinalIgnoreCase)
                    ? display with { Name = connected[0].Name }
                    : display;
            }).ToArray();
        }
        catch (InvalidOperationException error)
        {
            MonitorManagementError = error.Message;
            Monitors = Displays.Select(display => new ConnectedMonitor(
                display.StableId ?? display.DeviceName, display.Name, display.DeviceName, true, display.IsPrimary)).ToArray();
        }
    }

    public DisplayTarget? GetProfileDisplay(ResolutionProfile profile) => ResolveDisplay(profile, Displays);

    private static DisplayTarget? ResolveDisplay(ResolutionProfile profile, IReadOnlyList<DisplayTarget> displays)
    {
        return string.IsNullOrEmpty(profile.MonitorId)
            ? displays.FirstOrDefault(display => string.Equals(display.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase))
            : displays.FirstOrDefault(display => string.Equals(display.StableId, profile.MonitorId, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyProfile(ResolutionProfile profile)
    {
        var display = ResolveDisplay(profile, DisplayService.GetDisplays());
        if (display is null) throw new InvalidOperationException($"Connect or enable the monitor assigned to “{profile.Name}”, then try again.");
        ApplyMode(display.DeviceName, profile.Mode);
    }

    public void SetMonitorEnabled(ConnectedMonitor monitor, bool enabled)
    {
        if (_confirmation is not null) return;
        if (MonitorManagementError is not null) throw new InvalidOperationException(MonitorManagementError);
        if (monitor.IsEnabled == enabled) return;

        CloseIdentifiers();
        MonitorTopology.Snapshot? previous = null;
        bool kept = ConfirmChange(new ModeConfirmation(monitor, enabled),
            () => previous = MonitorTopology.SetEnabled(monitor.Id, enabled),
            () => MonitorTopology.Restore(previous!),
            "The previous monitor setup could not be restored. Open Windows Display settings to recover it.",
            useWindowCenter: false);
        RefreshDisplays();
        Announce(kept ? $"{monitor.Name} {(enabled ? "enabled" : "disabled")}." : "Restored the previous monitor setup.");
    }

    public void IdentifyMonitors()
    {
        if (_confirmation is not null) return;
        CloseIdentifiers();
        var screens = Forms.Screen.AllScreens;
        foreach (var display in Displays)
        {
            var screen = screens.FirstOrDefault(item =>
                string.Equals(item.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (screen is null) continue;
            var identifier = new MonitorIdentifier(display, screen);
            _identifiers.Add(identifier);
            identifier.Closed += (_, _) => _identifiers.Remove(identifier);
            identifier.Show();
        }
    }

    private void CloseIdentifiers()
    {
        foreach (var identifier in _identifiers.ToArray()) identifier.Close();
    }

    public bool CanRestorePreviousMode(string deviceName) => _previousModes.ContainsKey(ModeHistoryKey(deviceName));

    public void RestorePreviousMode(string deviceName)
    {
        string key = ModeHistoryKey(deviceName);
        if (!_previousModes.TryGetValue(key, out var previous)) return;
        if (GetCurrent(deviceName) == previous)
        {
            _previousModes.Remove(key);
            RefreshDisplays();
            Announce($"{previous} is already active.");
            return;
        }
        ApplyMode(deviceName, previous);
    }

    private string ModeHistoryKey(string deviceName) => Displays.FirstOrDefault(display =>
        string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))?.StableId ?? deviceName;

    public void ApplyMode(string deviceName, DisplayMode mode)
    {
        if (_confirmation is not null) return;
        var previous = GetCurrent(deviceName);
        if (previous == mode)
        {
            Announce($"{mode} is already active.");
            return;
        }

        CloseIdentifiers();
        bool kept = ConfirmChange(new ModeConfirmation(mode),
            () => DisplayService.Apply(deviceName, mode),
            () => DisplayService.Apply(deviceName, previous),
            "The previous mode could not be restored. Open Windows Display settings to recover it.");
        if (kept) _previousModes[ModeHistoryKey(deviceName)] = previous;
        RefreshDisplays();
        Announce(kept ? $"Display set to {GetCurrent(deviceName)}." : $"Restored {previous}.");
    }

    private bool ConfirmChange(ModeConfirmation confirmation, Action apply, Action revert, string revertError,
        bool useWindowCenter = true)
    {
        if (useWindowCenter && _window?.IsVisible == true && _window.IsActive && _window.WindowState != WindowState.Minimized)
        {
            confirmation.Owner = _window;
            confirmation.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _confirmation = confirmation;
        bool applied = false;
        bool kept = false;
        try
        {
            apply();
            applied = true;
            kept = confirmation.ShowDialog() == true;
            return kept;
        }
        finally
        {
            try
            {
                if (applied && !kept) revert();
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"{revertError} {error.Message}", error);
            }
            finally
            {
                _confirmation = null;
            }
        }

    }

    public void SaveProfile(ResolutionProfile profile)
    {
        string name = profile.Name.Trim();
        if (name.Length is < 1 or > 64) throw new InvalidOperationException("Enter a profile name between 1 and 64 characters.");
        if (Profiles.Any(item => item.Id != profile.Id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A profile already uses that name.");
        }
        if (profile.Hotkey is not null && Profiles.Any(item => item.Id != profile.Id && item.Hotkey == profile.Hotkey))
        {
            throw new InvalidOperationException($"{profile.Hotkey} is already assigned to another profile.");
        }

        var existing = Profiles.FirstOrDefault(item => item.Id == profile.Id);
        if (existing is null || profile.DeviceName != existing.DeviceName || profile.Mode != existing.Mode
            || !string.Equals(profile.MonitorId, existing.MonitorId, StringComparison.OrdinalIgnoreCase))
        {
            var display = DisplayService.GetDisplays().FirstOrDefault(item =>
                string.Equals(item.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(profile.MonitorId)
                    || string.Equals(item.StableId, profile.MonitorId, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException("Choose a connected display for this profile.");
            DisplayService.Validate(display.DeviceName, profile.Mode);
            profile = profile with { MonitorId = display.StableId };
        }
        else
        {
            profile = profile with { MonitorId = existing.MonitorId };
        }
        profile = profile with { Name = name };

        ResumeHotkeys();
        bool hadRegistration = _hotkeyIds.TryGetValue(profile.Id, out int oldId);
        bool keepRegistration = hadRegistration && existing?.Hotkey == profile.Hotkey;
        int? newId = !keepRegistration && profile.Hotkey is not null ? _hotkeys.Register(profile.Id, profile.Hotkey) : null;
        var updated = Profiles.ToList();
        int index = updated.FindIndex(item => item.Id == profile.Id);
        if (index < 0) updated.Add(profile);
        else updated[index] = profile;

        try
        {
            Persist(_settings with { Profiles = updated });
        }
        catch
        {
            if (newId.HasValue) _hotkeys.Unregister(newId.Value);
            throw;
        }

        if (!keepRegistration && hadRegistration)
        {
            _hotkeys.Unregister(oldId);
            _hotkeyIds.Remove(profile.Id);
        }
        if (newId.HasValue) _hotkeyIds[profile.Id] = newId.Value;
        UpdateProfiles();
        Announce($"Saved “{name}”.");
    }

    public void RemoveProfile(Guid id)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null) return;
        Persist(_settings with { Profiles = Profiles.Where(item => item.Id != id).ToList() });
        if (_hotkeyIds.Remove(id, out int registration)) _hotkeys.Unregister(registration);
        UpdateProfiles();
        Announce($"Removed “{profile.Name}”.");
    }

    public void SuspendHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        foreach (int id in _hotkeyIds.Values) _hotkeys.Unregister(id);
        _hotkeyIds.Clear();
    }

    public void ResumeHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        RegisterSavedHotkeys();
    }

    public void SetCloseToTray(bool enabled)
    {
        if (CloseToTray != enabled) Persist(_settings with { CloseToTray = enabled });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetStartWithWindows(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) throw new InvalidOperationException("Windows startup preferences could not be opened.");
        if (enabled) key.SetValue("1337res", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("1337res", throwOnMissingValue: false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void HideToTray()
    {
        _window?.Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        ShowTrayMessage("1337res is still running", "Open 1337res from the notification area. Profile hotkeys stay active.");
    }

    public void CancelPendingChange() => _confirmation?.Cancel();

    public void Exit()
    {
        if (_confirmation is not null)
        {
            CancelPendingChange();
            // Let the confirmation unwind and restore the display before shutting down WPF.
            _app.Dispatcher.BeginInvoke(new Action(Exit), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        _exiting = true;
        _app.Shutdown();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (CloseToTray) HideToTray();
        else _app.Dispatcher.BeginInvoke(Exit);
    }

    private void UpdateProfiles()
    {
        RefreshTray();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshTray()
    {
        if (_tray is null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open 1337res", null, (_, _) => ShowWindow());
        if (Profiles.Count > 0)
        {
            var profiles = new Forms.ToolStripMenuItem("Apply profile");
            foreach (var profile in Profiles)
            {
                string title = profile.Name.Replace("&", "&&", StringComparison.Ordinal);
                profiles.DropDownItems.Add(title, null, (_, _) => Try(() => ApplyProfile(profile)));
            }
            menu.Items.Add(profiles);
        }
        var restorable = Displays.Where(display => CanRestorePreviousMode(display.DeviceName)).ToArray();
        if (restorable.Length > 0)
        {
            var restore = new Forms.ToolStripMenuItem("Restore previous resolution");
            foreach (var display in restorable)
            {
                string title = display.Name.Replace("&", "&&", StringComparison.Ordinal);
                restore.DropDownItems.Add(title, null, (_, _) => Try(() => RestorePreviousMode(display.DeviceName)));
            }
            menu.Items.Add(restore);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit 1337res", null, (_, _) => Exit());
        var oldMenu = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        oldMenu?.Dispose();
    }

    private void RegisterSavedHotkeys()
    {
        var unavailable = new List<string>();
        foreach (var profile in Profiles)
        {
            if (profile.Hotkey is null || _hotkeyIds.ContainsKey(profile.Id)) continue;
            try { _hotkeyIds.Add(profile.Id, _hotkeys.Register(profile.Id, profile.Hotkey)); }
            catch (InvalidOperationException) { unavailable.Add(profile.Name); }
        }
        if (unavailable.Count > 0) ReportError($"Shortcut unavailable for {string.Join(", ", unavailable)}. Edit the profile to choose a different shortcut.");
    }

    private void Persist(Settings settings)
    {
        SettingsStore.Save(settings);
        _settings = settings;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { ReportError(error.Message); }
    }

    private void Announce(string message)
    {
        Notice?.Invoke(this, message);
        if (_window?.IsVisible != true) ShowTrayMessage("1337res", message);
    }

    private void ReportError(string message)
    {
        _window?.ShowError(message);
        if (_window?.IsVisible != true) ShowTrayMessage("1337res", message, Forms.ToolTipIcon.Warning);
    }

    private void ShowTrayMessage(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info) =>
        _tray?.ShowBalloonTip(3500, title, message, icon);

    public void Dispose()
    {
        CloseIdentifiers();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _hotkeys.Dispose();
    }
}
