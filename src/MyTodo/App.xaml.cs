using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MyTodo;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private int _errorDialogVisible;
    private DateTime _lastErrorDialogUtc = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 先注册兜底处理，再创建主窗口，确保启动阶段异常也不会形成弹窗风暴。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _singleInstanceMutex = new Mutex(true, @"Local\MyTodo.SingleInstance.1.0", out var isFirstInstance);
        if (!isFirstInstance)
        {
            AppDialog.Notify(null, "MyTodo", "MyTodo 已经在运行。");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 必须先标记已处理；若显示错误窗口期间再次触发绑定异常，不会递归创建新窗口。
        e.Handled = true;
        WriteErrorLog(e.Exception);

        var now = DateTime.UtcNow;
        if (now - _lastErrorDialogUtc < TimeSpan.FromSeconds(10)) return;
        if (Interlocked.Exchange(ref _errorDialogVisible, 1) != 0) return;
        _lastErrorDialogUtc = now;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                AppDialog.Notify(
                    Current?.MainWindow,
                    "操作未完成",
                    $"程序仍可继续运行。\n\n{e.Exception.Message}\n\n详细信息已写入 error.log。");
            }
            finally
            {
                Interlocked.Exchange(ref _errorDialogVisible, 0);
            }
        }), DispatcherPriority.Background);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) WriteErrorLog(exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteErrorLog(e.Exception);
        e.SetObserved();
    }

    private static void WriteErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MyTodoData");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\r\n\r\n");
        }
        catch
        {
            // 日志失败不能再次影响主程序。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // 进程退出阶段不再抛出异常。
        }
        base.OnExit(e);
    }
}
