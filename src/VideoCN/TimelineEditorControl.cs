using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VideoCN;

public sealed class VoiceTimelineClip
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double Duration { get; set; }
    public string FilePath { get; set; } = "";
    public int[] SourceIds { get; set; } = [];
    public string GeneratedText { get; set; } = "";
    public string Voice { get; set; } = "";
    public string GeneratedVoice { get; set; } = "";
    public string GeneratedModel { get; set; } = "";
    public string Speaker { get; set; } = "";
    public double Speed { get; set; } = 1;
    public double RawDuration { get; set; }
    public bool NeedsGeneration => Text != GeneratedText || string.IsNullOrWhiteSpace(FilePath)
        || (Voice.Length > 0 && Voice != GeneratedVoice);
    public VoiceTimelineClip Copy() => (VoiceTimelineClip)MemberwiseClone();
}

/// <summary>Edits clip placement only; generated audio and clip duration remain unchanged.</summary>
public sealed class TimelineEditorControl : UserControl
{
    private const double RulerHeight = 30;
    private const double VideoHeight = 34;
    private List<VideoSection> videoSections = [];
    private bool canUndoVideo;
    private readonly Button splitVideoButton;
    private readonly Button undoVideoButton;
    private const double LaneHeight = 49;
    private const double BackgroundHeight = 42;
    private readonly Border backgroundLane = new() { Background = Brush("#D7E4EF"), IsHitTestVisible = false };
    private readonly Rectangle backgroundSelection = new() { Fill = Brush("#B2CCD9"), Stroke = Brush("#276986"), StrokeThickness = 2, Opacity = .55, IsHitTestVisible = false };
    private List<BackgroundRange> backgroundRanges = [];
    private double backgroundFrom, backgroundTo, backgroundAnchor;
    private bool selectingBackground, backgroundSelectionFresh;
    private bool backgroundEnabled;
    private const double SnapSeconds = .05;
    private static readonly Brush InkBrush = Brush("#24443C");
    private static readonly Brush ClipBrush = Brush("#D8EBE1");
    private static readonly Brush SelectedBrush = Brush("#B0D9C5");
    private static readonly Brush AccentBrush = Brush("#177566");
    private readonly Canvas surface = new() { Background = Brush("#F4F7F3"), ClipToBounds = true };
    private readonly ScrollViewer scroll;
    private readonly Slider zoom;
    private readonly TextBlock positionLabel = new() { FontSize = 12, Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock selectionLabel = new() { FontSize = 11, Foreground = InkBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly RulerElement ruler = new();
    private readonly Line playhead = new() { Stroke = Brush("#D06538"), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Dictionary<VoiceTimelineClip, Thumb> thumbs = [];
    private List<VoiceTimelineClip> clips = [];
    private VoiceTimelineClip? selectedClip;
    private double videoDuration;
    private double position;
    private double pixelsPerSecond = 20;
    private double dragStart;
    private double dragMouseX;
    private bool dragging;
    private bool editingEnabled = true;
    private int laneCount = 2;

    public event Action<VoiceTimelineClip>? ClipSelected;
    public event Action? DragStarted;
    public event Action? PositionsChanged;
    public event Action<double>? SeekRequested;
    public event Action<VoiceTimelineClip>? EditRequested;
    public event Action<VoiceTimelineClip>? RegenerateRequested;
    public event Action<VoiceTimelineClip>? ChooseVoiceRequested;
    public event Action<VoiceTimelineClip>? DeleteRequested;
    public event Action<double>? AddRequested;
    public event Action<string, double, double, BackgroundRange?>? BackgroundAction;
    public event Action<string, double, int>? VideoAction;

    public void LoadVideoSections(IReadOnlyList<VideoSection> sections, bool canUndo)
    {
        videoSections = sections.ToList(); canUndoVideo = canUndo;
        splitVideoButton.IsEnabled = videoSections.Count > 0;
        undoVideoButton.IsEnabled = canUndo;
        RebuildSurface();
    }

    public void LoadBackground(IReadOnlyList<BackgroundRange> ranges, bool enabled)
    {
        backgroundRanges = ranges.ToList(); backgroundEnabled = enabled;
        backgroundSelectionFresh = false; backgroundFrom = backgroundTo = 0;
        RebuildSurface();
    }

    public IReadOnlyList<VoiceTimelineClip> Clips => clips;
    public VoiceTimelineClip? SelectedClip => selectedClip;

    public bool IsEditingEnabled
    {
        get => editingEnabled;
        set
        {
            editingEnabled = value;
            foreach (var thumb in thumbs.Values) thumb.IsEnabled = value;
        }
    }

    public TimelineEditorControl()
    {
        Focusable = true;
        MinHeight = 200;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(zoomPanel, Dock.Right);
        zoomPanel.Children.Add(new TextBlock
        {
            Text = "缩放", FontSize = 12, Foreground = InkBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
        });
        zoom = new Slider
        {
            Minimum = -8, Maximum = 4, Value = 0, Width = 115,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "向右放大时间轴；向左查看更长时间范围"
        };
        zoom.ValueChanged += ZoomChanged;
        zoomPanel.Children.Add(zoom);
        var fit = new Button { Content = "全览", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
        fit.Click += (_, _) => FitToWindow();
        zoomPanel.Children.Add(fit);
        toolbar.Children.Add(zoomPanel);
        splitVideoButton = new Button { Content = "切开视频", Padding = new Thickness(8, 4, 8, 4), FontSize = 11, Margin = new Thickness(0, 0, 6, 0), ToolTip = "在播放线位置切开视频，再右键视频片段删除。" };
        splitVideoButton.Click += (_, _) => VideoAction?.Invoke("split", position, 0);
        toolbar.Children.Add(splitVideoButton);
        undoVideoButton = new Button { Content = "撤销剪切", Padding = new Thickness(8, 4, 8, 4), FontSize = 11, Margin = new Thickness(0, 0, 8, 0) };
        undoVideoButton.Click += (_, _) => VideoAction?.Invoke("undo", 0, 0);
        toolbar.Children.Add(undoVideoButton);
        toolbar.Children.Add(positionLabel);
        root.Children.Add(toolbar);

        scroll = new ScrollViewer
        {
            Content = surface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false, MinHeight = 96,
            BorderBrush = Brush("#DCE5DF"), BorderThickness = new Thickness(1)
        };
        scroll.ScrollChanged += (_, _) => RefreshRuler();
        scroll.SizeChanged += (_, _) => { UpdateSurfaceBounds(); RefreshRuler(); };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        surface.MouseLeftButtonDown += SurfaceMouseDown;
        surface.MouseMove += (_, e) => {
            if (!selectingBackground) return;
            var at = Math.Clamp(e.GetPosition(surface).X / pixelsPerSecond, 0, videoDuration);
            backgroundFrom = Math.Min(backgroundAnchor, at); backgroundTo = Math.Max(backgroundAnchor, at);
            ShowBackgroundSelection();
        };
        surface.MouseLeftButtonUp += (_, e) => {
            if (!selectingBackground) return;
            selectingBackground = false; surface.ReleaseMouseCapture(); zoom.IsEnabled = true;
            backgroundSelectionFresh = backgroundTo - backgroundFrom > .01;
            selectionLabel.Text = $"背景选区 {backgroundFrom:F3}–{backgroundTo:F3} 秒 · 右键调整音量或静音";
            SeekRequested?.Invoke(backgroundFrom); e.Handled = true;
        };
        surface.LostMouseCapture += (_, _) => {
            if (!selectingBackground) return;
            selectingBackground = false; zoom.IsEnabled = true;
            backgroundSelectionFresh = backgroundTo - backgroundFrom > .01;
        };
        surface.MouseRightButtonDown += (_, e) => {
            if (!editingEnabled) return;
            var point = e.GetPosition(surface);
            if (point.Y >= RulerHeight && point.Y < RulerHeight + VideoHeight) {
                ShowVideoMenu(Math.Clamp(point.X / pixelsPerSecond, 0, videoDuration)); e.Handled = true; return;
            }
            if (point.Y >= RulerHeight + VideoHeight && point.Y < RulerHeight + VideoHeight + BackgroundHeight) {
                ShowBackgroundMenu(Math.Clamp(point.X / pixelsPerSecond, 0, videoDuration)); e.Handled = true; return;
            }
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source != surface) {
                if (source is Thumb) return;
                source = VisualTreeHelper.GetParent(source);
            }
            var at = Math.Max(0, e.GetPosition(surface).X / pixelsPerSecond);
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("在此位置新增中文配音", () => AddRequested?.Invoke(at)));
            menu.PlacementTarget = surface; menu.IsOpen = true; e.Handled = true;
        };

        var footer = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        footer.Children.Add(selectionLabel);
        footer.Children.Add(new TextBlock
        {
            Text = "右键片段可编辑中文、重新生成语音或删除；右键空白处新增。拖动调整位置，← → 微调，Shift 加大步长。",
            FontSize = 11, Foreground = Brush("#67777B"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        PreviewKeyDown += HandleKeyDown;
        RebuildSurface();
        UpdateSelectionLabel();
        SetPosition(0);
    }

    public void LoadClips(IReadOnlyList<VoiceTimelineClip> newClips, double duration)
    {
        var selectedId = selectedClip?.Id;
        clips = newClips.ToList();
        foreach (var clip in clips)
            clip.Start = double.IsFinite(clip.Start) ? Math.Max(0, clip.Start) : 0;
        videoDuration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        selectedClip = selectedId.HasValue ? clips.FirstOrDefault(c => c.Id == selectedId.Value) : null;
        dragging = false;
        zoom.IsEnabled = true;
        RebuildSurface();
        UpdateSelectionLabel();
    }

    public void SetPosition(double seconds)
    {
        position = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        positionLabel.Text = $"播放位置  {FormatTime(position)}";
        MovePlayhead();
    }

    public void SelectClip(int id, bool bringIntoView = false)
    {
        var clip = clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return;
        Select(clip);
        if (bringIntoView && thumbs.TryGetValue(clip, out var thumb))
        {
            scroll.ScrollToHorizontalOffset(Math.Max(0, clip.Start * pixelsPerSecond - scroll.ViewportWidth * .2));
            thumb.BringIntoView();
        }
    }

    private void Select(VoiceTimelineClip clip)
    {
        var changed = !ReferenceEquals(clip, selectedClip);
        selectedClip = clip;
        foreach (var pair in thumbs)
        {
            pair.Value.Background = ReferenceEquals(pair.Key, clip) ? SelectedBrush : ClipBrush;
            pair.Value.BorderBrush = ReferenceEquals(pair.Key, clip) ? AccentBrush : Brush("#A8CAB8");
        }
        UpdateSelectionLabel();
        if (changed) ClipSelected?.Invoke(clip);
    }

    private void RebuildSurface()
    {
        surface.Children.Clear();
        thumbs.Clear();
        var laneEnds = new List<double>();
        var placements = new List<(VoiceTimelineClip Clip, int Lane)>();
        foreach (var clip in clips.OrderBy(c => c.Start).ThenBy(c => c.Id))
        {
            var lane = laneEnds.FindIndex(end => end <= clip.Start + .001);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(0); }
            laneEnds[lane] = clip.Start + ClipDuration(clip);
            placements.Add((clip, lane));
        }
        laneCount = Math.Max(2, laneEnds.Count);
        UpdateSurfaceBounds();
        for (var lane = 0; lane < laneCount; lane++)
        {
            var stripe = new Rectangle
            {
                Width = surface.Width, Height = LaneHeight - 1,
                Tag = "voice-lane",
                Fill = lane % 2 == 0 ? Brush("#F0F5F0") : Brush("#E8F0E9"),
                IsHitTestVisible = false
            };
            Canvas.SetTop(stripe, RulerHeight + VideoHeight + BackgroundHeight + lane * LaneHeight);
            surface.Children.Add(stripe);
        }
        surface.Children.Add(ruler);
        foreach (var section in videoSections) {
            var block = new Border {
                Width = Math.Max(1, (section.End - section.Start) * pixelsPerSecond), Height = VideoHeight - 4,
                Background = Brush(section.Deleted ? "#D6D7D8" : "#DDD4EF"), BorderBrush = Brush(section.Deleted ? "#A7A7A7" : "#8B75AC"),
                BorderThickness = new Thickness(1), IsHitTestVisible = false,
                Child = new TextBlock { Text = section.Deleted ? "已删除 · 右键恢复" : $"视频 {section.Id} · {FormatTime(section.Start)}—{FormatTime(section.End)}",
                    FontSize = 11, Margin = new Thickness(5, 4, 5, 0), TextTrimming = TextTrimming.CharacterEllipsis }
            };
            Canvas.SetLeft(block, section.Start * pixelsPerSecond); Canvas.SetTop(block, RulerHeight + 2); surface.Children.Add(block);
        }
        backgroundLane.Width = surface.Width; backgroundLane.Height = BackgroundHeight - 2;
        Canvas.SetTop(backgroundLane, RulerHeight + VideoHeight); surface.Children.Add(backgroundLane);
        foreach (var range in backgroundRanges) {
            var block = new Border { Width = Math.Max(2, (range.End - range.Start) * pixelsPerSecond), Height = BackgroundHeight - 8,
                Background = Brush(range.Gain == 0 ? "#CEB6B6" : "#A8CCD5"), BorderBrush = Brush("#587E8A"), BorderThickness = new Thickness(1),
                IsHitTestVisible = false, Child = new TextBlock { Text = range.Label, FontSize = 11, Margin = new Thickness(5), TextTrimming = TextTrimming.CharacterEllipsis } };
            Canvas.SetLeft(block, range.Start * pixelsPerSecond); Canvas.SetTop(block, RulerHeight + VideoHeight + 3); surface.Children.Add(block);
        }
        var bgLabel = new TextBlock { Text = backgroundEnabled ? "背景音 · 拖选范围后右键编辑" : "背景音（未启用）· 右键编辑", FontSize = 11, Foreground = Brush("#315D70"), IsHitTestVisible = false };
        Canvas.SetTop(bgLabel, RulerHeight + VideoHeight + 10); Canvas.SetLeft(bgLabel, 8); surface.Children.Add(bgLabel);
        surface.Children.Add(backgroundSelection); ShowBackgroundSelection();
        foreach (var (clip, lane) in placements)
        {
            var thumb = CreateThumb(clip);
            Canvas.SetTop(thumb, RulerHeight + VideoHeight + BackgroundHeight + lane * LaneHeight + 4);
            Canvas.SetLeft(thumb, clip.Start * pixelsPerSecond);
            thumbs[clip] = thumb;
            surface.Children.Add(thumb);
        }
        if (clips.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "生成配音后，每句音频会显示在这里。", Foreground = Brush("#67777B"),
                FontSize = 12, IsHitTestVisible = false
            };
            Canvas.SetLeft(empty, 14);
            Canvas.SetTop(empty, RulerHeight + VideoHeight + BackgroundHeight + 19);
            surface.Children.Add(empty);
        }
        surface.Children.Add(playhead);
        foreach (var section in videoSections.Where(s => s.Deleted)) {
            var shade = new Rectangle { Width = (section.End - section.Start) * pixelsPerSecond,
                Height = Math.Max(0, surface.Height - RulerHeight - VideoHeight), Fill = Brush("#7A7777"), Opacity = .22, IsHitTestVisible = false };
            Canvas.SetLeft(shade, section.Start * pixelsPerSecond); Canvas.SetTop(shade, RulerHeight + VideoHeight);
            surface.Children.Add(shade);
        }
        Panel.SetZIndex(playhead, 20);
        RefreshRuler();
        MovePlayhead();
    }

    private Thumb CreateThumb(VoiceTimelineClip clip)
    {
        var thumb = new Thumb
        {
            Width = Math.Max(8, ClipDuration(clip) * pixelsPerSecond), Height = LaneHeight - 9,
            Background = ReferenceEquals(clip, selectedClip) ? SelectedBrush : ClipBrush,
            BorderBrush = ReferenceEquals(clip, selectedClip) ? AccentBrush : Brush("#A8CAB8"),
            Foreground = InkBrush, BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeWE, IsEnabled = editingEnabled, Focusable = true,
            Template = ClipTemplate()
        };
        SetClipText(thumb, clip);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("编辑中文…", () => EditRequested?.Invoke(clip)));
        menu.Items.Add(MenuItem("重新生成本段语音", () => RegenerateRequested?.Invoke(clip)));
        menu.Items.Add(MenuItem("选择人物音色并重新生成…", () => ChooseVoiceRequested?.Invoke(clip)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("在此段后新增片段…", () => AddRequested?.Invoke(clip.Start + clip.Duration)));
        menu.Items.Add(MenuItem("删除片段（可撤销）", () => DeleteRequested?.Invoke(clip)));
        thumb.ContextMenu = menu;
        thumb.PreviewMouseRightButtonDown += (_, _) => Select(clip);
        thumb.PreviewMouseLeftButtonDown += (_, _) => Select(clip);
        thumb.DragStarted += (_, _) =>
        {
            DragStarted?.Invoke();
            Select(clip);
            dragStart = clip.Start;
            dragMouseX = Mouse.GetPosition(surface).X;
            dragging = true;
            zoom.IsEnabled = false;
        };
        thumb.DragDelta += (_, _) =>
        {
            var mouse = Mouse.GetPosition(scroll);
            if (mouse.X < 24 && scroll.HorizontalOffset > 0)
                scroll.ScrollToHorizontalOffset(Math.Max(0, scroll.HorizontalOffset - 18));
            else if (mouse.X > scroll.ViewportWidth - 24)
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + 18);
            var seconds = dragStart + (Mouse.GetPosition(surface).X - dragMouseX) / pixelsPerSecond;
            clip.Start = Math.Max(0, Math.Round(seconds / SnapSeconds) * SnapSeconds);
            Canvas.SetLeft(thumb, clip.Start * pixelsPerSecond);
            SetClipText(thumb, clip);
            UpdateSelectionLabel();
            UpdateSurfaceBounds();
        };
        thumb.DragCompleted += (_, args) =>
        {
            if (args.Canceled) clip.Start = dragStart;
            var changed = Math.Abs(clip.Start - dragStart) > .00001;
            dragging = false;
            zoom.IsEnabled = true;
            RebuildSurface();
            UpdateSelectionLabel();
            Focus();
            if (changed) PositionsChanged?.Invoke();
        };
        return thumb;
    }

    private static ControlTemplate ClipTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        border.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        border.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, TemplateBinding("Tag"));
        text.SetBinding(TextBlock.ForegroundProperty, TemplateBinding("Foreground"));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.IsHitTestVisibleProperty, false);
        border.AppendChild(text);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    private static System.Windows.Data.Binding TemplateBinding(string path) => new(path)
    {
        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
    };

    private static void SetClipText(Thumb thumb, VoiceTimelineClip clip)
    {
        var text = clip.Text.Replace('\r', ' ').Replace('\n', ' ');
        thumb.Tag = $"{clip.Id}{(clip.Speaker.Length > 0 ? " · " + clip.Speaker : "")} · {FormatTime(clip.Start)}{(clip.NeedsGeneration ? " · 待生成" : "")}\n{text}";
        thumb.ToolTip = $"第 {clip.Id} 句  {FormatTime(clip.Start)} — {FormatTime(clip.Start + ClipDuration(clip))}\n"
            + $"时长 {ClipDuration(clip):0.00} 秒 · 人物：{(clip.Speaker.Length > 0 ? clip.Speaker : "未命名")} · 音色：{(clip.Voice.Length > 0 ? clip.Voice : "跟随制作偏好")}\n"
            + (clip.GeneratedModel.Length > 0 ? $"上次生成模型：{clip.GeneratedModel}\n" : "") + text;
    }

    private static MenuItem MenuItem(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowVideoMenu(double at)
    {
        var menu = new ContextMenu();
        var section = videoSections.FirstOrDefault(s => at >= s.Start && at < s.End);
        if (section != null) {
            menu.Items.Add(new MenuItem { Header = $"视频 {section.Id} · {FormatTime(section.Start)}—{FormatTime(section.End)}", IsEnabled = false });
            if (!section.Deleted) {
                menu.Items.Add(MenuItem("在右击位置切开", () => VideoAction?.Invoke("split", at, section.Id)));
                menu.Items.Add(MenuItem("在播放线位置切开", () => VideoAction?.Invoke("split", position, section.Id)));
                menu.Items.Add(MenuItem("删除此视频片段（连同音频）", () => VideoAction?.Invoke("delete", at, section.Id)));
            } else menu.Items.Add(MenuItem("恢复此视频片段", () => VideoAction?.Invoke("restore", at, section.Id)));
        }
        var undoItem = MenuItem("撤销上一步视频剪切", () => VideoAction?.Invoke("undo", 0, 0));
        undoItem.IsEnabled = canUndoVideo; menu.Items.Add(undoItem);
        menu.PlacementTarget = surface; menu.IsOpen = true;
    }

    private void SurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (dragging) return;
        var point = e.GetPosition(surface);
        if (point.Y >= RulerHeight + VideoHeight && point.Y < RulerHeight + VideoHeight + BackgroundHeight && editingEnabled) {
            DragStarted?.Invoke(); selectedClip = null;
            backgroundAnchor = Math.Clamp(point.X / pixelsPerSecond, 0, videoDuration);
            backgroundFrom = backgroundTo = backgroundAnchor;
            selectingBackground = true; zoom.IsEnabled = false; surface.CaptureMouse();
            ShowBackgroundSelection(); e.Handled = true; return;
        }
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != surface)
        {
            if (source is Thumb) return;
            source = VisualTreeHelper.GetParent(source);
        }
        var seconds = Math.Max(0, e.GetPosition(surface).X / pixelsPerSecond);
        var timelineEnd = Math.Max(videoDuration, clips.Select(c => c.Start + ClipDuration(c)).DefaultIfEmpty(0).Max());
        if (timelineEnd > 0) seconds = Math.Min(timelineEnd, seconds);
        SetPosition(seconds);
        SeekRequested?.Invoke(seconds);
        Focus();
        e.Handled = true;
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        if (!editingEnabled || dragging || zoom.IsKeyboardFocusWithin || selectedClip is null || (e.Key != Key.Left && e.Key != Key.Right)) return;
        var amount = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .5 : SnapSeconds;
        var start = Math.Max(0, Math.Round((selectedClip.Start + (e.Key == Key.Left ? -amount : amount)) * 100) / 100);
        if (Math.Abs(start - selectedClip.Start) > .00001)
        {
            selectedClip.Start = start;
            RebuildSurface();
            UpdateSelectionLabel();
            Focus();
            PositionsChanged?.Invoke();
        }
        e.Handled = true;
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider construction precedes the viewport; subsequent changes preserve its centre.
        if (scroll is null) return;
        var center = (scroll.HorizontalOffset + scroll.ViewportWidth / 2) / pixelsPerSecond;
        pixelsPerSecond = 20 * Math.Pow(2, zoom.Value);
        RebuildSurface();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scroll.ScrollToHorizontalOffset(Math.Max(0, center * pixelsPerSecond - scroll.ViewportWidth / 2));
        }));
    }

    private void FitToWindow()
    {
        if (dragging) return;
        var duration = Math.Max(videoDuration, clips.Select(c => c.Start + ClipDuration(c)).DefaultIfEmpty(0).Max());
        if (duration <= 0 || scroll.ViewportWidth <= 0) return;
        zoom.Value = Math.Clamp(Math.Log2(Math.Max(1, scroll.ViewportWidth - 20) / (duration + 1) / 20), zoom.Minimum, zoom.Maximum);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => scroll.ScrollToHorizontalOffset(0)));
    }

    private void UpdateSurfaceBounds()
    {
        var end = Math.Max(videoDuration, clips.Select(c => c.Start + ClipDuration(c)).DefaultIfEmpty(0).Max());
        surface.Width = Math.Max(Math.Max(300, scroll?.ViewportWidth ?? 0), (Math.Max(10, end) + 3) * pixelsPerSecond);
        surface.Height = RulerHeight + VideoHeight + BackgroundHeight + laneCount * LaneHeight;
        backgroundLane.Width = surface.Width;
        ruler.Width = surface.Width;
        ruler.Height = RulerHeight;
        foreach (var stripe in surface.Children.OfType<Rectangle>().Where(r => r.Tag?.ToString() == "voice-lane")) stripe.Width = surface.Width;
        playhead.Y2 = surface.Height;
    }

    private void RefreshRuler()
    {
        ruler.PixelsPerSecond = pixelsPerSecond;
        ruler.Offset = scroll.HorizontalOffset;
        ruler.ViewportWidth = scroll.ViewportWidth;
        ruler.InvalidateVisual();
    }

    private void MovePlayhead()
    {
        playhead.X1 = playhead.X2 = position * pixelsPerSecond;
        playhead.Y1 = 0;
        playhead.Y2 = surface.Height;
    }

    private void ShowBackgroundSelection()
    {
        Canvas.SetLeft(backgroundSelection, backgroundFrom * pixelsPerSecond);
        Canvas.SetTop(backgroundSelection, RulerHeight + VideoHeight + 1);
        backgroundSelection.Width = Math.Max(0, (backgroundTo - backgroundFrom) * pixelsPerSecond);
        backgroundSelection.Height = BackgroundHeight - 4;
        backgroundSelection.Visibility = backgroundTo > backgroundFrom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBackgroundMenu(double at)
    {
        if (videoDuration <= 0) return;
        selectedClip = null;
        BackgroundRange? range = null;
        if (!backgroundSelectionFresh || at < backgroundFrom || at > backgroundTo) {
            range = backgroundRanges.FirstOrDefault(r => at >= r.Start && at < r.End);
            backgroundFrom = range?.Start ?? Math.Min(at, Math.Max(0, videoDuration - .1));
            backgroundTo = range?.End ?? Math.Min(videoDuration, backgroundFrom + 3);
        } else range = backgroundRanges.FirstOrDefault(r => Math.Abs(r.Start - backgroundFrom) < .001 && Math.Abs(r.End - backgroundTo) < .001);
        backgroundSelectionFresh = false; ShowBackgroundSelection();
        var from = backgroundFrom; var to = backgroundTo; var selected = range;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("背景音量 / 时间范围…", () => BackgroundAction?.Invoke("edit", from, to, selected)));
        menu.Items.Add(MenuItem("静音此范围（保留中文）", () => BackgroundAction?.Invoke("mute", from, to, selected)));
        menu.Items.Add(MenuItem("强制使用分离背景", () => BackgroundAction?.Invoke("separated", from, to, selected)));
        menu.Items.Add(MenuItem("仅试听此范围背景", () => BackgroundAction?.Invoke("audition", from, to, selected)));
        if (selected != null) menu.Items.Add(MenuItem("删除此调整 / 恢复默认", () => BackgroundAction?.Invoke("delete", from, to, selected)));
        menu.PlacementTarget = surface; menu.IsOpen = true;
    }

    private void UpdateSelectionLabel()
    {
        selectionLabel.Text = selectedClip is null
            ? $"共 {clips.Count} 句配音 · 原视频 {FormatTime(videoDuration)}"
            : $"已选第 {selectedClip.Id} 句 · {(selectedClip.Speaker.Length > 0 ? selectedClip.Speaker + " · " : "")}音色 {(!string.IsNullOrEmpty(selectedClip.Voice) ? selectedClip.Voice : "跟随制作偏好")} · 开始 {FormatTime(selectedClip.Start)} · 时长 {ClipDuration(selectedClip):0.00} 秒 · {selectedClip.Text.Replace('\n', ' ')}";
    }

    private static double ClipDuration(VoiceTimelineClip clip) => double.IsFinite(clip.Duration) && clip.Duration > 0 ? clip.Duration : .05;

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}"
            : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed class RulerElement : FrameworkElement
    {
        public double PixelsPerSecond { get; set; } = 20;
        public double Offset { get; set; }
        public double ViewportWidth { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brush("#E0EAE1"), null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (PixelsPerSecond <= 0 || ViewportWidth <= 0) return;
            var minimumStep = 85 / PixelsPerSecond;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(minimumStep)));
            var step = new[] { 1.0, 2, 5, 10 }.Select(n => n * magnitude).First(n => n >= minimumStep);
            var first = Math.Max(0, Math.Floor(Offset / PixelsPerSecond / step) * step);
            var last = Math.Min(ActualWidth, Offset + ViewportWidth) / PixelsPerSecond + step;
            var pen = new Pen(Brush("#96AD9F"), 1);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var seconds = first; seconds <= last; seconds += step)
            {
                var x = seconds * PixelsPerSecond;
                dc.DrawLine(pen, new Point(x, ActualHeight - 7), new Point(x, ActualHeight));
                var text = new FormattedText(FormatTime(seconds), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10, InkBrush, dpi);
                dc.DrawText(text, new Point(x + 4, 3));
            }
        }
    }
}
