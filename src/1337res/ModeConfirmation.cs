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
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(196, 250, 101));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(241, 245, 239));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(169, 182, 172));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(22, 28, 25));
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _countdown;
    private readonly ProgressBar _progress;

    public ModeConfirmation(DisplayMode mode)
    {
        Title = "Keep this display mode?";
        Background = Panel;
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var body = new StackPanel { Margin = new Thickness(27, 23, 27, 24) };
        body.Children.Add(new TextBlock { Text = "KEEP THIS DISPLAY MODE?", Foreground = Accent, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        body.Children.Add(new TextBlock
        {
            Text = mode.ToString(),
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
            Background = new SolidColorBrush(Color.FromRgb(43, 55, 46))
        };
        body.Children.Add(_progress);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var revert = new Button { Content = "Revert", IsCancel = true, MinWidth = 90, Padding = new Thickness(14, 7, 14, 7) };
        revert.Click += (_, _) => Cancel();
        actions.Children.Add(revert);
        var keep = new Button
        {
            Content = "Keep mode",
            FontWeight = FontWeights.SemiBold,
            MinWidth = 104,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(9, 0, 0, 0),
            Background = Accent,
            Foreground = Brushes.Black
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
        string text = $"Previous mode returns in {seconds} {(seconds == 1 ? "second" : "seconds")}.";
        if (_countdown.Text != text) _countdown.Text = text;
        if (remaining <= 0) Cancel();
    }
}
