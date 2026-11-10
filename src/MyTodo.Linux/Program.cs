using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
#if HEADLESS
using Avalonia.Headless;
#endif
using Avalonia.Themes.Fluent;

namespace MyTodo.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--smoke-test"))
        {
#if HEADLESS
            var directory = Path.Combine(Path.GetTempPath(), "mytodo-ui-test-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("MYTODO_DATA_DIR", directory);
            try
            {
                AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
                var window = new MainWindow();
                window.Show();
                window.RunSmokeTest();
                if (Environment.GetEnvironmentVariable("MYTODO_SCREENSHOT") is { } screenshot)
                {
                    window.UpdateLayout();
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(screenshot);
                }
                window.Close();
                Console.WriteLine("PASS: Linux views, task editor, plan editor, node editor, settings and history.");
            }
            finally { Directory.Delete(directory, true); }
            return;
#else
            throw new InvalidOperationException("Build with -p:EnableHeadless=true for UI smoke testing.");
#endif
        }
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { desktop.MainWindow = new MainWindow(); }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                desktop.MainWindow = new Avalonia.Controls.Window
                {
                    Title = "MyTodo · 启动失败", Width = 520, Height = 240,
                    Content = new Avalonia.Controls.TextBlock { Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = "无法打开本地数据，未清空数据库。请先备份数据目录后重试。\n\n" + e.Message }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
