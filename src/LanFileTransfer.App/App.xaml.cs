using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LanFileTransfer.App.Infrastructure;

namespace LanFileTransfer.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private AppLogService? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);

        _instance = SingleInstanceCoordinator.Create(AppContext.BaseDirectory);
        if (!_instance.IsPrimary)
        {
            _instance.SignalPrimary();
            Shutdown();
            return;
        }

        AppRuntime runtime;
        try
        {
            runtime = AppRuntime.Start();
            _log = runtime.Log;
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
            Shutdown(-1);
            return;
        }

        var mainWindow = new MainWindow(runtime);
        MainWindow = mainWindow;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(ActivateMainWindow));
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += DispatcherUnhandledExceptionHandler;
        TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionHandler;
        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
    }

    private void DispatcherUnhandledExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("界面发生未处理异常", e.Exception);
        e.Handled = true;
        MessageBox.Show("程序发生未处理错误，将安全退出。请查看 Logs 目录中的日志。", "内网文件传输工具", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }

    private void UnobservedTaskExceptionHandler(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("后台任务发生未观察异常", e.Exception);
        e.SetObserved();
    }

    private void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _log?.Error("进程发生严重未处理异常", exception);
        }
    }

    private static void ShowStartupFailure(Exception exception)
    {
        var message = "当前程序目录无法保存配置、日志或共享文件。请先完整解压软件，并移动到桌面、下载目录或其他普通可写磁盘目录后重新运行。\n\n" + exception.Message;
        if (MessageBox.Show(message, "无法启动内网文件传输工具", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true }); } catch { }
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not Window window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
