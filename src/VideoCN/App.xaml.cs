using System.Windows;

namespace VideoCN;
public partial class App : Application
{
    private Mutex? instance;
    internal static bool IsShuttingDown { get; private set; }

    internal static void BeginShutdown()
    {
        IsShuttingDown = true;
        ProcessJob.ShutdownAll();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\VideoCN.Desktop", out var created);
        if (!created) { MessageBox.Show("VideoCN 已在运行，请切换到已打开的窗口。"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => {
            // An interrupted async callback must not create a new dialog during exit.
            if (IsShuttingDown) { args.Handled = true; return; }
            if (e.Args.Length > 1 && e.Args[0] == "--smoke-test") {
                System.IO.Directory.CreateDirectory(e.Args[1]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "error.txt"), args.Exception.ToString());
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show(args.Exception.Message, "VideoCN"); args.Handled = true;
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try { BeginShutdown(); }
        finally { instance?.Dispose(); base.OnExit(e); }
    }
}
