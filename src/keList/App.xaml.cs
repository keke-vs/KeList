using System.Threading;
using System.Windows;
using KeList.Services;

namespace KeList;

public partial class App : System.Windows.Application
{
    private const string ActivationEventName = @"Local\keList.Activate.6B856435";
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.Write(args.Exception);
            System.Windows.MessageBox.Show(
                $"keList 遇到意外错误。\n\n{args.Exception.Message}",
                "keList",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };

        try
        {
            _activationEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ActivationEventName,
                out var isFirstInstance);

            if (!isFirstInstance)
            {
                _activationEvent.Set();
                Shutdown();
                return;
            }

            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            _activationCancellation = new CancellationTokenSource();
            var cancellationToken = _activationCancellation.Token;

            _ = Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_activationEvent.WaitOne(500))
                    {
                        Dispatcher.Invoke(mainWindow.ShowAndActivate);
                    }
                }
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            CrashLogger.Write(exception);
            System.Windows.MessageBox.Show(
                $"keList 无法启动。\n\n{exception.Message}",
                "keList",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationEvent?.Dispose();
        base.OnExit(e);
    }
}
