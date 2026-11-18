using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Res1337;

internal sealed class ModeConfirmation : Window
{
    private const int CountdownSeconds = 15;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(188, 156, 255));
    private static readonly Brush Text = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(207, 195, 224));
    private static readonly Brush Panel = Brushes.Black;
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _countdown;
    private readonly ProgressBar _progress;
    private readonly string _returnMessage;

    public ModeConfirmation(DisplayMode mode) : this("Keep this display mode?", mode.ToString(), "Keep mode", "Previous mode") { }

    public ModeConfirmation(ConnectedMonitor monitor, bool enabled)
        : this("Keep this monitor setup?", $"{monitor.Name} {(enabled ? "enabled" : "disabled")}", "Keep setup", "Previous setup") { }

    private ModeConfirmation(string title, string detail, string keepLabel, string returnMessage)
    {
        Title = title;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/1337res.ico"));
        _returnMessage = returnMessage;
        Background = Panel;
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var body = new StackPanel { Margin = new Thickness(27, 23, 27, 24) };
        body.Children.Add(new TextBlock { Text = title, Foreground = Accent, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        body.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 9),
            TextWrapping = TextWrapping.Wrap
        });
        _countdown = new TextBlock { Foreground = Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_countdown, AutomationLiveSetting.Off);
        body.Children.Add(_countdown);
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = CountdownSeconds,
            Height = 4,
            Margin = new Thickness(0, 15, 0, 23),
            Foreground = Accent,
            Background = new SolidColorBrush(Color.FromRgb(46, 30, 66))
        };
        body.Children.Add(_progress);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var revert = new Button
        {
            Content = "Revert",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(14, 7, 14, 7),
            Background = new SolidColorBrush(Color.FromRgb(38, 23, 57)),
            Foreground = Text
        };
        revert.Click += (_, _) => Cancel();
        actions.Children.Add(revert);
        var keep = new Button
        {
            Content = keepLabel,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 104,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(9, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(82, 42, 140)),
            Foreground = Text
        };
        keep.Click += (_, _) => DialogResult = _elapsed.Elapsed.TotalSeconds < CountdownSeconds;
        actions.Children.Add(keep);
        body.Children.Add(actions);
        Content = body;

        UpdateCountdown();
        _timer.Tick += (_, _) => UpdateCountdown();
        SourceInitialized += (_, _) =>
        {
            _elapsed.Start();
            _timer.Start();
        };
        ContentRendered += (_, _) => revert.Focus();
        Closed += (_, _) => _timer.Stop();
    }

    public void Cancel()
    {
        if (IsVisible) DialogResult = false;
    }

    private void UpdateCountdown()
    {
        double remaining = CountdownSeconds - _elapsed.Elapsed.TotalSeconds;
        _progress.Value = Math.Max(0, remaining);
        int seconds = Math.Max(1, (int)Math.Ceiling(remaining));
        string text = $"{_returnMessage} returns in {seconds} {(seconds == 1 ? "second" : "seconds")}.";
        if (_countdown.Text != text) _countdown.Text = text;
        if (remaining <= 0) Cancel();
    }
}
