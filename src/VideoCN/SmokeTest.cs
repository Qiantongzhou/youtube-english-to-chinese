using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoCN;
internal static class SmokeTest
{
    public static async Task Run(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var cookieResult = await window.VerifyCookieImport(directory);
        File.WriteAllText(Path.Combine(directory, "cookie-import-smoke.json"), JsonSerializer.Serialize(cookieResult, new JsonSerializerOptions { WriteIndented = true }));
        var processResult = await window.VerifyProcessBoundary(directory);
        File.WriteAllText(Path.Combine(directory, "process-smoke.json"), JsonSerializer.Serialize(processResult, new JsonSerializerOptions { WriteIndented = true }));
        var pages = (TabControl)window.FindName("Pages");
        var args = Environment.GetCommandLineArgs();
        var hasProject = args.Length > 3;
        if (hasProject) {
            var result = window.VerifySubtitleEditor(args[3], directory);
            File.WriteAllText(Path.Combine(directory, "editor-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (Environment.GetEnvironmentVariable("VIDEOCN_TEST_STOP_EDGE") == "1") {
            var result = await window.StopEdgeAndTest();
            File.WriteAllText(Path.Combine(directory, "edge-retry.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        for (int page = 0; page < 3; page++) {
            pages.SelectedIndex = page;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            Capture(window, Path.Combine(directory, $"page-{page}.png"));
        }
        File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new {
            window.Title, window.ActualWidth, window.ActualHeight, Pages = pages.Items.Count,
            StartEnabled = ((Button)window.FindName("StartButton")).IsEnabled,
            RenderButtonMatchesProjectState = ((Button)window.FindName("RenderButton")).IsEnabled == hasProject,
            EnvironmentPanel = window.FindName("EnvironmentText") != null
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
