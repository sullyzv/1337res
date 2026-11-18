using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Res1337;

internal sealed class MonitorIdentifier : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MonitorIdentifier(DisplayTarget display, Forms.Screen screen)
    {
        Title = "1337res monitor identification";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        string number = display.DeviceName.Split("DISPLAY", StringSplitOptions.None)[^1];
        var labels = new StackPanel { Margin = new Thickness(24, 14, 24, 16), MinWidth = 142, MaxWidth = 320 };
        labels.Children.Add(new TextBlock
        {
            Text = int.TryParse(number, out int displayNumber) ? displayNumber.ToString() : "1337res",
            FontSize = 50,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(198, 172, 255))
        });
        labels.Children.Add(new TextBlock { Text = display.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(96, 54, 161)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromRgb(20, 11, 35)),
            Child = labels
        };

        ContentRendered += (_, _) =>
        {
            var area = screen.WorkingArea;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            const uint NoSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
            SetWindowPos(handle, IntPtr.Zero, area.Left + 32, area.Top + 32, 0, 0, NoSizeNoZOrderNoActivate);
            _timer.Start();
        };
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
