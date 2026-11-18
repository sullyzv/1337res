using System.Threading;
using System.Windows;

namespace Res1337;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\1337res.Show");
        _instance = new Mutex(false, @"Local\1337res.Instance", out bool firstInstance);
        if (!firstInstance)
        {
            _activation.Set();
            Shutdown();
            return;
        }

        try
        {
            _controller = new AppController(this);
            _controller.Start(e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activation, (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowWindow()),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "1337res could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _controller?.CancelPendingChange();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _controller?.Dispose();
        _activation?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
