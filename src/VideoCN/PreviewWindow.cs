using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoCN;
public sealed class PreviewWindow : Window
{
    private readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly string path, projectFolder;
    private readonly double start, end, duration;
    private CancellationTokenSource? cancellation;
    private bool loading, compatible, closed;

    public PreviewWindow(string path, double start, double end, string? projectFolder = null, double duration = 0)
    {
        this.path = path; this.start = start; this.end = end; this.duration = duration;
        this.projectFolder = projectFolder ?? Path.GetDirectoryName(path)!;
        Title = $"原声预览 · {start:F2} – {end:F2} 秒"; Width = 840; Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.Black;
        var grid = new Grid(); grid.Children.Add(media); grid.Children.Add(status); Content = grid;
        Loaded += async (_, _) => await OpenPreview(false);
        media.MediaOpened += async (_, _) => {
            if (closed || App.IsShuttingDown) return;
            if (!media.HasVideo || media.NaturalVideoWidth <= 0) { await Recover("播放器未能解码视频画面。"); return; }
            status.Visibility = Visibility.Collapsed;
            media.Position = TimeSpan.FromSeconds(Math.Max(.001, start)); media.Play(); timer.Start();
        };
        media.MediaFailed += async (_, e) => await Recover(e.ErrorException.Message);
        timer.Tick += (_, _) => { if (media.Position.TotalSeconds >= end) { media.Pause(); timer.Stop(); } };
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); timer.Stop(); media.Close(); media.Source = null; };
    }

    private async Task Recover(string error)
    {
        if (closed || App.IsShuttingDown || loading) return;
        timer.Stop(); media.Close();
        if (compatible) { status.Text = "兼容预览仍无法播放：" + error; status.Visibility = Visibility.Visible; return; }
        await OpenPreview(true);
    }

    private async Task OpenPreview(bool force)
    {
        if (closed || App.IsShuttingDown || loading) return;
        loading = true; status.Text = "正在准备视频预览…"; status.Visibility = Visibility.Visible;
        using var request = new CancellationTokenSource(); cancellation = request;
        var progress = new Progress<string>(message => { if (!closed && loading) status.Text = message + "\n关闭窗口可停止准备。"; });
        try {
            var source = force
                ? await CompatibleVideoPreview.GetAsync(path, projectFolder, duration, progress, request.Token)
                : await CompatibleVideoPreview.GetPlaybackPathAsync(path, projectFolder, duration, progress, request.Token);
            if (closed || App.IsShuttingDown || request.IsCancellationRequested) return;
            compatible = !source.Equals(path, StringComparison.OrdinalIgnoreCase);
            loading = false;
            media.Source = new Uri(source); media.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !App.IsShuttingDown) status.Text = ex.Message; }
        finally { cancellation = null; loading = false; }
    }
}
