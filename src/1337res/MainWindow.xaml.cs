using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace Res1337;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private IReadOnlyList<MonitorChoice> _monitors = [];
    private IReadOnlyList<ModeRow> _modes = [];
    private DisplayMode? _currentMode;
    private Guid? _editingProfileId;
    private Hotkey? _profileHotkey;
    private bool _ready;
    private bool _updating;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionLabel.Text = version is null ? "" : $"Version {version.ToString(3)}";
        ProfileHotkey.GotKeyboardFocus += (_, _) => _controller.SuspendHotkeys();
        ProfileHotkey.LostKeyboardFocus += (_, _) => _controller.ResumeHotkeys();
        _controller.Changed += Controller_Changed;
        _controller.Notice += Controller_Notice;
        Closed += (_, _) =>
        {
            _controller.Changed -= Controller_Changed;
            _controller.Notice -= Controller_Notice;
        };
        _ready = true;
        RefreshState();
        EditProfile(null);
    }

    public void ShowError(string message) => ShowStatus(message, true);

    public void ShowStatus(string message, bool isError = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowStatus(message, isError)));
            return;
        }

        StatusMessage.Text = message;
        StatusMessage.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Error" : "Muted");
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(isError ? "Error" : "Accent");
        (UIElementAutomationPeer.FromElement(StatusMessage) ?? UIElementAutomationPeer.CreatePeerForElement(StatusMessage))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Controller_Changed(object? sender, EventArgs e) => RefreshState();
    private void Controller_Notice(object? sender, string message) => ShowStatus(message);

    private void RefreshState()
    {
        var previousMonitor = (DisplayPicker.SelectedItem as MonitorChoice)?.Target;
        var previousProfileMonitor = ProfileMonitor.SelectedItem as MonitorChoice;
        var profile = _controller.Profiles.FirstOrDefault(item => item.Id == _editingProfileId);
        _monitors = _controller.Displays.Select(display => new MonitorChoice(display)).ToArray();
        var monitor = FindMonitor(previousMonitor) ?? _monitors.FirstOrDefault();
        _updating = true;
        try
        {
            DisplayPicker.ItemsSource = _monitors;
            DisplayPicker.SelectedItem = monitor;
            DisplayPicker.IsEnabled = _monitors.Count > 0;
            RefreshMonitorCards();
            ProfileList.ItemsSource = _controller.Profiles.Select(item => new ProfileRow(item)).ToArray();
            ProfileList.SelectedValue = _editingProfileId;
            ProfilesEmpty.Visibility = _controller.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetProfileMonitor(previousProfileMonitor?.Target ?? monitor?.Target, profile);
            RefreshSettings();
        }
        finally
        {
            _updating = false;
        }

        if (_editingProfileId.HasValue && profile is null) EditProfile(null);
        UpdateProfileButtons(profile);
        RefreshDisplayData(previousMonitor != monitor?.Target);
    }

    private void RefreshMonitorCards()
    {
        var monitors = _controller.Monitors;
        int activeCount = monitors.Count(monitor => monitor.IsEnabled);
        string? managementError = _controller.MonitorManagementError;

        MonitorCards.ItemsSource = monitors.Select(monitor => new MonitorCard(monitor, activeCount, managementError)).ToArray();
        IdentifyMonitorsButton.IsEnabled = activeCount > 0 && managementError is null;
        MonitorSummary.Text = monitors.Count == 0
            ? "No connected monitors found"
            : $"{monitors.Count} connected · {activeCount} enabled";
        MonitorHelp.Text = managementError ?? (monitors.Count == 0
            ? "Check the monitor connection, then select Refresh."
            : activeCount == 1
                ? LastMonitorHelp
                : "You’ll have 15 seconds to keep monitor changes.");
    }

    private const string LastMonitorHelp = "Disabling the last monitor turns the screen off. Move the mouse or press a key to wake it.";

    private MonitorChoice? FindMonitor(DisplayTarget? target)
    {
        if (target is null) return null;
        return string.IsNullOrEmpty(target.StableId)
            ? _monitors.FirstOrDefault(choice => string.Equals(choice.Target.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
            : _monitors.FirstOrDefault(choice => string.Equals(choice.Target.StableId, target.StableId, StringComparison.OrdinalIgnoreCase));
    }

    private void DisplayPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        RefreshDisplayData(fillCustomFields: true);
    }

    private void RefreshDisplayData(bool fillCustomFields)
    {
        var monitor = (DisplayPicker.SelectedItem as MonitorChoice)?.Target;
        _currentMode = null;
        _modes = [];
        if (monitor is not null)
        {
            Try(() =>
            {
                _currentMode = _controller.GetCurrent(monitor.DeviceName);
                _modes = _controller.GetModes(monitor.DeviceName).Select(mode => new ModeRow(mode)).ToArray();
            });
        }

        if (_currentMode is { } mode)
        {
            CurrentResolution.Text = $"{mode.Width} × {mode.Height}";
            CurrentRefresh.Text = mode.RefreshRate > 0 ? $"{mode.RefreshRate} Hz" : "Default";
            CurrentDetails.Text = $"{AspectRatio(mode)}   ·   {monitor!.Name}";
        }
        else
        {
            CurrentResolution.Text = "—";
            CurrentRefresh.Text = "—";
            CurrentDetails.Text = monitor is null ? "No enabled monitor found" : "Current mode unavailable";
        }

        ApplyCustomButton.IsEnabled = monitor is not null;
        SaveCustomButton.IsEnabled = monitor is not null;
        CopyModeButton.IsEnabled = _currentMode is not null;
        RestoreModeButton.IsEnabled = monitor is not null && _controller.CanRestorePreviousMode(monitor.DeviceName);
        MatchAspectRatio.IsEnabled = _currentMode is not null;
        if (fillCustomFields) FillModeFields(_currentMode, CustomWidth, CustomHeight, CustomRefresh);
        UpdateModeFilter(fillCustomFields ? _currentMode : null);
    }

    private static string AspectRatio(DisplayMode mode)
    {
        int divisor = mode.Width;
        int remainder = mode.Height;
        while (remainder != 0) (divisor, remainder) = (remainder, divisor % remainder);
        return divisor > 0 ? $"{mode.Width / divisor}:{mode.Height / divisor}" : "—";
    }

    private void ModeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateModeFilter();
    }

    private void MatchAspectRatio_Click(object sender, RoutedEventArgs e) => UpdateModeFilter();

    private void UpdateModeFilter(DisplayMode? preferred = null)
    {
        var selected = preferred ?? (ModeList.SelectedItem as ModeRow)?.Mode ?? _currentMode;
        string search = NormalizeSearch(ModeFilter.Text);
        var aspect = MatchAspectRatio.IsChecked == true ? _currentMode : null;
        var matching = search.Length == 0 && aspect is null
            ? _modes
            : _modes.Where(row => row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)
                && (aspect is null || (long)row.Mode.Width * aspect.Height == (long)aspect.Width * row.Mode.Height))
                .ToArray();
        ModeList.ItemsSource = matching;
        ModeList.SelectedValue = selected;
        if (ModeList.SelectedIndex < 0 && matching.Count > 0) ModeList.SelectedIndex = 0;
        if (ModeList.SelectedItem is { } selectedItem) ModeList.ScrollIntoView(selectedItem);
        ModeCount.Text = matching.Count == _modes.Count ? $"{matching.Count} modes" : $"{matching.Count} / {_modes.Count}";
        ModeEmpty.Text = matching.Count > 0 ? "" : _modes.Count == 0 ? "No modes available" : "No matching modes";
    }

    private static string NormalizeSearch(string input) => string.Concat(input.Where(c => !char.IsWhiteSpace(c))).Replace('×', 'x');

    private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplySelectedButton.IsEnabled = ModeList.SelectedItem is ModeRow;
        SaveSelectedButton.IsEnabled = ModeList.SelectedItem is ModeRow;
    }

    private void ModeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement(ModeList, origin) is ListBoxItem { IsSelected: true })
        {
            ApplySelected_Click(sender, e);
        }
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPicker.SelectedItem is MonitorChoice monitor && ModeList.SelectedItem is ModeRow row)
            Try(() => _controller.ApplyMode(monitor.Target.DeviceName, row.Mode));
    }

    private void ApplyCustom_Click(object sender, RoutedEventArgs e) => Try(() =>
        _controller.ApplyMode(SelectedMonitor().DeviceName, ReadMode(CustomWidth, CustomHeight, CustomRefresh)));

    private void RestorePreviousMode_Click(object sender, RoutedEventArgs e) => Try(() =>
        _controller.RestorePreviousMode(SelectedMonitor().DeviceName));

    private void CopyCurrentMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode is not { } mode) return;
        try
        {
            Clipboard.SetText($"{SelectedMonitor().Name} · {mode}");
            ShowStatus("Current mode copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowError("Couldn’t copy the current mode. Try again.");
        }
    }

    private void ToggleMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MonitorCard { CanToggle: true } card })
            Try(() => _controller.SetMonitorEnabled(card.Monitor, !card.Monitor.IsEnabled));
    }

    private void ConfigureMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MonitorCard { Monitor.DeviceName: { } deviceName } }) return;
        var target = _monitors.FirstOrDefault(item => string.Equals(item.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            ShowError("This monitor is unavailable. Refresh the monitor list and try again.");
            return;
        }

        DisplayPicker.SelectedItem = target;
        Navigate("Display");
    }

    private void IdentifyMonitors_Click(object sender, RoutedEventArgs e) => Try(_controller.IdentifyMonitors);

    private void SaveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedItem is ModeRow row) OpenNewProfile(row.Mode);
    }

    private void SaveCustom_Click(object sender, RoutedEventArgs e) => Try(() =>
        OpenNewProfile(ReadMode(CustomWidth, CustomHeight, CustomRefresh)));

    private void OpenNewProfile(DisplayMode? mode)
    {
        Navigate("Profiles");
        EditProfile(null, mode);
        ProfileName.Focus();
        ProfileName.SelectAll();
    }

    private DisplayTarget SelectedMonitor() => (DisplayPicker.SelectedItem as MonitorChoice)?.Target
        ?? throw new InvalidOperationException("Choose a connected monitor first.");

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        EditProfile((ProfileList.SelectedItem as ProfileRow)?.Profile);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e) => OpenNewProfile(null);

    private void EditProfile(ResolutionProfile? profile, DisplayMode? preset = null)
    {
        _editingProfileId = profile?.Id;
        _updating = true;
        try
        {
            ProfileList.SelectedValue = _editingProfileId;
            ProfileEditorTitle.Text = profile is null ? "New profile" : "Edit profile";
            ProfileName.Text = profile?.Name ?? preset?.ToString() ?? "";
            var target = profile is null
                ? (DisplayPicker.SelectedItem as MonitorChoice)?.Target
                : _controller.GetProfileDisplay(profile);
            SetProfileMonitor(target, profile);
            FillModeFields(profile?.Mode ?? preset ?? _currentMode, ProfileWidth, ProfileHeight, ProfileRefresh);
            SetProfileHotkey(profile?.Hotkey);
            UpdateProfileButtons(profile);
        }
        finally
        {
            _updating = false;
        }
    }

    private void SetProfileMonitor(DisplayTarget? target, ResolutionProfile? profile)
    {
        var selected = FindMonitor(target);
        var connected = profile is null ? null : FindMonitor(_controller.GetProfileDisplay(profile));
        MonitorChoice? offline = profile is not null && connected is null
            ? new MonitorChoice(new DisplayTarget(profile.DeviceName, "Saved monitor", false) { StableId = profile.MonitorId }, true)
            : null;
        ProfileMonitor.ItemsSource = offline is null ? _monitors : _monitors.Append(offline).ToArray();
        ProfileMonitor.SelectedItem = selected ?? connected ?? offline ?? _monitors.FirstOrDefault();
    }

    private void UpdateProfileButtons(ResolutionProfile? profile)
    {
        DeleteProfileButton.IsEnabled = profile is not null;
        ApplyProfileButton.IsEnabled = profile is not null;
    }

    private static void FillModeFields(DisplayMode? mode, TextBox width, TextBox height, TextBox refresh)
    {
        width.Text = mode?.Width.ToString(CultureInfo.InvariantCulture) ?? "";
        height.Text = mode?.Height.ToString(CultureInfo.InvariantCulture) ?? "";
        refresh.Text = mode?.RefreshRate.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private static DisplayMode ReadMode(TextBox width, TextBox height, TextBox refresh) => new(
        ReadNumber(width, "width in pixels", 1, 65535),
        ReadNumber(height, "height in pixels", 1, 65535),
        ReadNumber(refresh, "refresh rate in Hz", 0, 9999));

    private static int ReadNumber(TextBox input, string description, int minimum, int maximum)
    {
        if (int.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value >= minimum && value <= maximum) return value;
        input.Focus();
        input.SelectAll();
        throw new InvalidOperationException($"Enter a whole number from {minimum} to {maximum} for the {description}.");
    }

    private void SetProfileHotkey(Hotkey? hotkey)
    {
        _profileHotkey = hotkey;
        ProfileHotkey.Text = hotkey?.ToString() ?? "No hotkey";
    }

    private void ProfileHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin || e.IsRepeat) return;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            ProfileHotkey.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || modifiers == ModifierKeys.None)
        {
            ShowError("Hold Ctrl, Alt, Shift, or the Windows key with another key.");
            return;
        }

        uint flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= 1;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= 2;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= 4;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= 8;
        SetProfileHotkey(new Hotkey(flags, (uint)virtualKey));
        ShowStatus($"Shortcut set to {_profileHotkey}. Save the profile to activate it.");
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => SetProfileHotkey(null);

    private void SaveProfile_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (string.IsNullOrWhiteSpace(ProfileName.Text))
        {
            ProfileName.Focus();
            throw new InvalidOperationException("Enter a name for the profile.");
        }
        if (ProfileMonitor.SelectedItem is not MonitorChoice monitor)
            throw new InvalidOperationException("Choose a monitor for this profile.");

        var mode = ReadMode(ProfileWidth, ProfileHeight, ProfileRefresh);
        var id = _editingProfileId ?? Guid.NewGuid();
        var profile = new ResolutionProfile(id, ProfileName.Text.Trim(), monitor.Target.DeviceName, mode, _profileHotkey)
        {
            MonitorId = monitor.Target.StableId
        };
        _controller.SaveProfile(profile);
        EditProfile(_controller.Profiles.First(item => item.Id == id));
    });

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileRow row) Try(() => _controller.ApplyProfile(row.Profile));
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editingProfileId is { } id) Try(() => _controller.RemoveProfile(id));
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) Navigate(page);
    }

    private void Navigate(string page)
    {
        DisplayPage.Visibility = page == "Display" ? Visibility.Visible : Visibility.Collapsed;
        MonitorsPage.Visibility = page == "Monitors" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Display") DisplayNav.IsChecked = true;
        if (page == "Monitors") MonitorsNav.IsChecked = true;
        if (page == "Profiles") ProfilesNav.IsChecked = true;
        if (page == "Settings") SettingsNav.IsChecked = true;
        if (page == "About") AboutNav.IsChecked = true;
        WorkspaceScroll.ScrollToTop();
    }

    private void RefreshSettings()
    {
        CloseToTrayToggle.IsChecked = _controller.CloseToTray;
        Try(() => StartupToggle.IsChecked = _controller.StartWithWindows);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Try(_controller.RefreshDisplays);
    private void ManageHotkeys_Click(object sender, RoutedEventArgs e) => Navigate("Profiles");
    private void Tray_Click(object sender, RoutedEventArgs e) => _controller.HideToTray();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupToggle.IsChecked == true;
        if (Try(() => _controller.SetStartWithWindows(enabled)))
            ShowStatus(enabled ? "1337res will start with Windows." : "Start with Windows disabled.");
        else RefreshSettings();
    }

    private void CloseToTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = CloseToTrayToggle.IsChecked == true;
        if (Try(() => _controller.SetCloseToTray(enabled)))
            ShowStatus(enabled ? "Closing the window will keep 1337res running in the tray." : "Closing the window will exit 1337res.");
        else RefreshSettings();
    }

    private void Discord_Click(object sender, RoutedEventArgs e) => Try(() =>
        Process.Start(new ProcessStartInfo("https://discord.gg/FGk4fnYeCP") { UseShellExecute = true }));

    private bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
                                      or UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        {
            ShowError(error.Message);
            return false;
        }
    }

    private sealed record MonitorChoice(DisplayTarget Target, bool Disconnected = false)
    {
        public string DeviceName => Target.DeviceName;
        public string Label => Disconnected ? "Saved monitor (disconnected)" : Target.IsPrimary ? $"{Target.Name} · Primary" : Target.Name;
    }

    private sealed record MonitorCard(ConnectedMonitor Monitor, int ActiveCount, string? ManagementError)
    {
        public string Name => Monitor.Name;
        public bool IsEnabled => Monitor.IsEnabled;
        public bool CanConfigure => Monitor.IsEnabled && !string.IsNullOrEmpty(Monitor.DeviceName);
        public bool CanToggle => ManagementError is null;
        public string ConfigureLabel => $"Edit resolution for {Name}";
        public string ActionLabel => IsEnabled ? "Disable" : "Enable";
        public string ActionAccessibilityLabel => $"{ActionLabel} {Name}";
        public string StatusLabel => !IsEnabled ? "Disabled" : Monitor.IsPrimary ? "Enabled · Primary" : "Enabled";
        public string? ActionHelp => ManagementError ?? (IsEnabled && ActiveCount == 1 ? LastMonitorHelp : null);
    }

    private sealed record ModeRow(DisplayMode Mode)
    {
        public string Resolution => $"{Mode.Width} × {Mode.Height}";
        public string Refresh => Mode.RefreshRate > 0 ? $"{Mode.RefreshRate} Hz" : "Default";
        public string SearchText => $"{Mode.Width}x{Mode.Height}{Refresh.Replace(" ", "", StringComparison.Ordinal)}";
        public string AccessibilityLabel => Mode.ToString();
    }

    private sealed record ProfileRow(ResolutionProfile Profile)
    {
        public Guid Id => Profile.Id;
        public string Name => Profile.Name;
        public string Details => Profile.Hotkey is null ? Profile.Mode.ToString() : $"{Profile.Mode}   ·   {Profile.Hotkey}";
        public string AccessibilityLabel => $"{Name}, {Details}";
    }
}
