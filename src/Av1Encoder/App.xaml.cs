using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Av1Encoder;

public partial class App : Application
{
    const int ShowWindowRestore = 9;

    // held for the app's lifetime; a second instance would encode the same folder and delete this one's partials
    static Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs startupArgs)
    {
        singleInstance = new Mutex(initiallyOwned: true, @"Local\Av1Encoder", out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("unobserved task exception", args.Exception);

        var version = typeof(App).Assembly.GetName().Version;
        AppLog.Info(Tools.Locate()
            ? $"started v{version}, ffmpeg {Tools.Ffmpeg}"
            : $"started v{version}, ffmpeg not found");
        base.OnStartup(startupArgs);
        new MainWindow().Show();
    }

    static void ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                    continue;
                ShowWindow(other.MainWindowHandle, ShowWindowRestore);
                SetForegroundWindow(other.MainWindowHandle);
            }
        }
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Error("unhandled UI exception", args.Exception);
        MessageBox.Show($"Something went wrong:\n\n{args.Exception.Message}\n\nDetails are in {AppLog.Folder}\\app.log",
            "AV1 Encoder", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr windowHandle);
}
