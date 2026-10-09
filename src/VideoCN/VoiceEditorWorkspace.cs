using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VideoCN;

/// <summary>Video preview and live audition of the existing fitted voice clips.</summary>
public sealed class VoiceEditorWorkspace : UserControl
{
    private readonly MediaElement video = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, Volume = 0 };
    private readonly TimelineEditorControl timeline = new();
    private readonly BackgroundTrackSession backgroundTrack = new();
    private readonly VideoEditSession videoEdits = new();
    private readonly ComboBox audioMode = new() { Width = 130, Margin = new Thickness(6, 0, 8, 0) };
    private readonly CheckBox backgroundEnabled = new() { Content = "保留背景音", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button undoBackgroundButton;
    private bool loadingBackground;
    private readonly Slider voiceVolume = new() { Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 20, Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), ToolTip = "所有中文配音的整体音量：0% 静音，100% 原音量，最高 200%。试听与导出均生效。" };
    private readonly TextBlock voiceVolumeText = new() { Text = "100%", MinWidth = 42, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button resetVoiceVolumeButton;
    private readonly DispatcherTimer volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool loadingVolume, volumeDirty, resumeAfterVolume;
    private CancellationTokenSource voiceCancellation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch clock = new();
    private readonly Dictionary<VoiceTimelineClip, MediaPlayer> players = [];
    private readonly Dictionary<VoiceTimelineClip, TaskCompletionSource<bool>> voiceReady = [];
    private readonly HashSet<VoiceTimelineClip> startedVoices = [];
    private readonly HashSet<VoiceTimelineClip> failedClips = [];
    private readonly HashSet<VoiceTimelineClip> positionedClips = [];
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly TextBlock previewStatus = new() { Text = "打开项目后显示视频预览", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 16, MaxWidth = 650, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock selection = new() { Text = "选择下方配音片段，然后左右拖动调整位置。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock counter = new() { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock caption = new() { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(165, 0, 0, 0)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 17, MaxHeight = 66, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Slider scrubber = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox startBox = new() { Width = 90, Padding = new Thickness(6, 3, 6, 3) };
    private readonly Button playButton;
    private readonly Button exportButton;
    private readonly Button undoButton;
    private readonly Button saveButton;
    private readonly MenuItem locateButton;
    private readonly Button cancelPreviewButton;
    private readonly Stack<(List<VoiceTimelineClip> Clips, int[] ManualIds)> undo = new();
    private List<VoiceTimelineClip> clips = [];
    private List<VoiceTimelineClip> lastClips = [];
    private JsonObject? editingDocument;
    private string? projectPath, videoPath;
    private double sourceDuration, position, clockStart, auditionEnd = double.PositiveInfinity;
    private bool playing, preparing, videoReady, updatingScrubber, busy;
    private int playbackVersion;
    private int previewVersion;
    private bool creatingPreview, usingCompatiblePreview;
    private bool closed;
    private CancellationTokenSource? previewCancellation;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = true };

    public event Action? ExportRequested;
    public event Action<VoiceTimelineClip>? ClipSelected;
    public event Action<int>? RegenerateRequested;
    public event Action? EditorChanged;
    public Func<bool>? BeforeEdit { get; set; }
    public Func<IReadOnlyList<ClipVoiceOption>>? VoiceOptions { get; set; }
    public bool HasClips => clips.Count > 0;
    public int ClipCount => clips.Count;
    public void PauseForEditing() => Pause();
    public bool HasEditableTimeline => editingDocument != null;
    public bool HasVideo => videoPath != null && sourceDuration > 0 && videoEdits.Valid;
    public int PendingClips => clips.Count(c => c.NeedsGeneration && videoEdits.IntersectsKept(c.Start, c.Start + c.Duration, Duration));
    public double VoiceVolume {
        get => voiceVolume.Value / 100;
        set {
            loadingVolume = true;
            try { voiceVolume.Value = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 1, 0, 2) * 100); }
            finally { loadingVolume = false; }
        }
    }
    public void SelectAndSeek(int id)
    {
        if (clips.FirstOrDefault(c => c.Id == id) is not VoiceTimelineClip clip) return;
        timeline.SelectClip(id, true); Seek(clip.Start);
    }

    public VoiceEditorWorkspace()
    {
        var root = new Grid();
        var previewRow = new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 180 };
        var dividerRow = new RowDefinition { Height = new GridLength(8) };
        var editorRow = new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 300 };
        root.RowDefinitions.Add(previewRow); root.RowDefinitions.Add(dividerRow); root.RowDefinitions.Add(editorRow);
        var previewPane = new Grid();
        previewPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(previewPane);
        var editorPane = new Grid();
        editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(editorPane, 2); root.Children.Add(editorPane);
        var divider = new GridSplitter {
            Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = new SolidColorBrush(Color.FromRgb(206, 221, 214)), ShowsPreview = true,
            ToolTip = "上下拖动，调整视频和时间轴的大小；双击恢复默认比例。"
        };
        Grid.SetRow(divider, 1); root.Children.Add(divider);
        divider.MouseDoubleClick += (_, _) => {
            previewRow.Height = new GridLength(3, GridUnitType.Star); editorRow.Height = new GridLength(2, GridUnitType.Star);
        };
        var screen = new Grid { Background = Brushes.Black, ClipToBounds = true };
        screen.Children.Add(video); screen.Children.Add(previewStatus); screen.Children.Add(caption); previewPane.Children.Add(screen);

        var transport = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        playButton = MakeButton("播放", (_, _) => TogglePlayback());
        transport.Children.Add(playButton);
        transport.Children.Add(MakeButton("回到开头", (_, _) => Seek(0)));
        audioMode.Items.Add("中文＋背景"); audioMode.Items.Add("仅背景"); audioMode.Items.Add("原片声音"); audioMode.SelectedIndex = 0;
        audioMode.SelectionChanged += (_, _) => ChangeAudio();
        transport.Children.Add(audioMode);
        var expandedPreview = false;
        var savedPreviewHeight = previewRow.Height; var savedEditorHeight = editorRow.Height;
        var expandButton = MakeButton("放大预览", (_, _) => { });
        expandButton.Click += (_, _) => {
            expandedPreview = !expandedPreview;
            if (expandedPreview) {
                savedPreviewHeight = previewRow.Height; savedEditorHeight = editorRow.Height;
                editorPane.Visibility = divider.Visibility = Visibility.Collapsed;
                editorRow.MinHeight = 0; editorRow.Height = new GridLength(0); dividerRow.Height = new GridLength(0);
                previewRow.Height = new GridLength(1, GridUnitType.Star);
            } else {
                editorRow.MinHeight = 300; editorRow.Height = savedEditorHeight; previewRow.Height = savedPreviewHeight;
                dividerRow.Height = new GridLength(8); editorPane.Visibility = divider.Visibility = Visibility.Visible;
            }
            expandButton.Content = expandedPreview ? "返回编辑" : "放大预览";
        };
        transport.Children.Add(expandButton);
        cancelPreviewButton = MakeButton("停止预览准备", (_, _) => previewCancellation?.Cancel());
        cancelPreviewButton.Visibility = Visibility.Collapsed;
        transport.Children.Add(cancelPreviewButton);
        DockPanel.SetDock(counter, Dock.Right); transport.Children.Add(counter);
        transport.Children.Add(scrubber);
        Grid.SetRow(transport, 1); previewPane.Children.Add(transport);

        var toolRows = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        toolRows.Children.Add(actions);
        actions.Children.Add(new TextBlock { Text = "开始 / 秒", ToolTip = "选中配音的开始时间", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        actions.Children.Add(startBox);
        actions.Children.Add(MakeButton("应用", (_, _) => ApplyStart()));
        actions.Children.Add(MakeButton("试听此段", (_, _) => AuditionSelected()));
        undoButton = MakeButton("撤销编辑", (_, _) => UndoMove()); actions.Children.Add(undoButton);
        saveButton = MakeButton("保存位置", (_, _) => SavePositions()); actions.Children.Add(saveButton);
        exportButton = MakeButton("按时间轴重新导出", (_, _) => ExportRequested?.Invoke()); actions.Children.Add(exportButton);
        var toolsMenu = new ContextMenu();
        locateButton = new MenuItem { Header = "重新关联原视频" };
        locateButton.Click += (_, _) => LocateVideo(); toolsMenu.Items.Add(locateButton);
        var compatibleItem = new MenuItem { Header = "重新准备兼容预览" };
        compatibleItem.Click += async (_, _) => await PrepareVideoPreview(force: true); toolsMenu.Items.Add(compatibleItem);
        var moreButton = MakeButton("更多工具 ▾", (_, _) => { });
        moreButton.Click += (_, _) => { compatibleItem.IsEnabled = !busy; toolsMenu.PlacementTarget = moreButton; toolsMenu.IsOpen = true; };
        actions.Children.Add(moreButton);
        var audioActions = new WrapPanel(); toolRows.Children.Add(audioActions);
        backgroundEnabled.Margin = new Thickness(8, 0, 8, 0);
        undoBackgroundButton = MakeButton("撤销背景调整", (_, _) => { if (!busy) { Pause(); backgroundTrack.Undo(); } });
        var volumeControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 0, 4) };
        volumeControls.Children.Add(new TextBlock { Text = "中文人声音量", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        volumeControls.Children.Add(voiceVolume); volumeControls.Children.Add(voiceVolumeText);
        resetVoiceVolumeButton = MakeButton("恢复 100%", (_, _) => voiceVolume.Value = 100);
        volumeControls.Children.Add(resetVoiceVolumeButton); audioActions.Children.Add(volumeControls);
        audioActions.Children.Add(backgroundEnabled); audioActions.Children.Add(undoBackgroundButton);
        voiceVolume.ValueChanged += (_, _) => ChangeVoiceVolume();
        volumeTimer.Tick += (_, _) => {
            volumeTimer.Stop(); var resume = resumeAfterVolume; resumeAfterVolume = false;
            SaveVoiceVolume();
            if (resume && !busy) Play();
        };
        backgroundEnabled.Checked += (_, _) => { if (!loadingBackground && !busy) backgroundTrack.SetEnabled(true); };
        backgroundEnabled.Unchecked += (_, _) => { if (!loadingBackground && !busy) backgroundTrack.SetEnabled(false); };
        backgroundTrack.Message += message => status.Text = message;
        backgroundTrack.Changed += () => { Pause(); RefreshBackground(); };
        editorPane.Children.Add(toolRows);
        Grid.SetRow(timeline, 1); editorPane.Children.Add(timeline);
        status.Margin = new Thickness(0, 4, 0, 0);
        status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status });
        Grid.SetRow(status, 2); editorPane.Children.Add(status);
        Content = root;

        timeline.SeekRequested += Seek;
        timeline.ClipSelected += clip => { ShowSelection(clip); ClipSelected?.Invoke(clip); };
        timeline.DragStarted += Pause;
        timeline.PositionsChanged += PositionsChanged;
        timeline.EditRequested += clip => EditClip(clip);
        timeline.ChooseVoiceRequested += clip => EditClip(clip, chooseVoice: true);
        timeline.AddRequested += at => EditClip(null, at);
        timeline.BackgroundAction += EditBackground;
        timeline.VideoAction += (action, at, id) => {
            if (busy || !HasVideo) return;
            Pause();
            if (action == "split") videoEdits.Split(at);
            else if (action == "delete") videoEdits.SetDeleted(id, true);
            else if (action == "restore") videoEdits.SetDeleted(id, false);
            else if (action == "undo") videoEdits.Undo();
        };
        videoEdits.Message += message => status.Text = message;
        videoEdits.Changed += () => {
            timeline.LoadVideoSections(videoEdits.Sections, videoEdits.CanUndo); UpdatePosition(); EditorChanged?.Invoke();
        };
        timeline.DeleteRequested += DeleteClip;
        timeline.RegenerateRequested += clip => {
            if (busy) return;
            Pause();
            if (EnsureEditingDocument() && SavePositions()) RegenerateRequested?.Invoke(clip.Id);
        };
        scrubber.ValueChanged += (_, e) => { if (!updatingScrubber) Seek(e.NewValue); };
        video.MediaOpened += async (_, _) => {
            if (closed || App.IsShuttingDown) return;
            if (!video.HasVideo || video.NaturalVideoWidth <= 0) {
                await RecoverVideoPreview(new InvalidDataException("播放器未能解码视频画面。")); return;
            }
            videoReady = true;
            previewStatus.Visibility = Visibility.Collapsed;
            if (sourceDuration <= 0 && video.NaturalDuration.HasTimeSpan) sourceDuration = video.NaturalDuration.TimeSpan.TotalSeconds;
            video.Position = TimeSpan.FromSeconds(Math.Min(Math.Max(.001, position), sourceDuration));
            if (playing && position < sourceDuration) video.Play(); else video.Pause();
        };
        video.MediaFailed += async (_, e) => await RecoverVideoPreview(e.ErrorException);
        timer.Tick += (_, _) => Tick();
        Unloaded += (_, _) => Pause();
        SetBusy(false);
        UpdatePosition();
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
        button.Click += click; return button;
    }

    private double Duration => Math.Max(sourceDuration, clips.Select(c => c.Start + c.Duration).DefaultIfEmpty(0).Max());
    private static string TimeText(double seconds) => $"{(int)seconds / 60:00}:{seconds % 60:00.00}";

    public static string? ResolveMedia(string folder, string? stored, string? subfolder = null)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        // A user may explicitly relink an external original video with the same name.
        if (subfolder == null && Path.IsPathRooted(stored) && File.Exists(stored)) return Path.GetFullPath(stored);
        var name = Path.GetFileName(stored.Replace('\\', '/'));
        // Prefer the files in this project over stale absolute paths after a move/copy.
        var candidates = new List<string>();
        if (subfolder != null) candidates.Add(Path.Combine(folder, subfolder, name));
        candidates.Add(Path.Combine(folder, name));
        if (!Path.IsPathRooted(stored)) candidates.Add(Path.Combine(folder, stored));
        candidates.Add(stored);
        return candidates.FirstOrDefault(File.Exists) is string path ? Path.GetFullPath(path) : null;
    }

    public void LoadProject(string? path)
    {
        if (closed || App.IsShuttingDown) return;
        ReleaseMedia();
        volumeDirty = false;
        backgroundTrack.Load(null);
        videoEdits.Load(null, 0); timeline.LoadVideoSections(videoEdits.Sections, false);
        projectPath = path; clips = []; lastClips = []; editingDocument = null; positionedClips.Clear(); undo.Clear(); position = sourceDuration = 0;
        selection.Text = "选择下方配音片段，然后左右拖动调整位置。";
        if (path == null) { status.Text = "打开已有项目，或先完成视频识别与配音。"; startBox.Clear(); timeline.LoadClips(clips, 0); RefreshBackground(); SetBusy(busy); UpdatePosition(); return; }
        try {
            var folder = Path.GetDirectoryName(path)!;
            var project = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            VoiceVolume = project["settings"]?["voice_volume"]?.GetValue<double>() ?? 1;
            sourceDuration = project["duration"]?.GetValue<double>() ?? 0;
            if (!double.IsFinite(sourceDuration) || sourceDuration < 0) sourceDuration = 0;
            videoPath = ResolveMedia(folder, project["video"]?.ToString());
            videoEdits.Load(path, sourceDuration); timeline.LoadVideoSections(videoEdits.Sections, false);
            var timingPath = Path.Combine(folder, "dub_timing.json");
            var editPath = Path.Combine(folder, "voice_edit.json");
            JsonArray? entries = null;
            if (File.Exists(editPath)) {
                editingDocument = JsonNode.Parse(File.ReadAllText(editPath))!.AsObject();
                if (editingDocument["version"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的配音编辑版本。");
                entries = editingDocument["clips"]!.AsArray();
            } else if (File.Exists(timingPath)) entries = JsonNode.Parse(File.ReadAllText(timingPath))!.AsArray();
            if (entries != null) {
                foreach (var node in entries) {
                    var start = node!["start"]!.GetValue<double>();
                    var duration = editingDocument != null ? node["duration"]!.GetValue<double>() : node["end"]!.GetValue<double>() - start;
                    if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration <= 0) continue;
                    var file = node["file"]?.ToString() ?? "";
                    var id = node["id"]!.GetValue<int>();
                    clips.Add(new VoiceTimelineClip {
                        Id = id, Text = node["zh"]?.ToString() ?? "", Start = start, Duration = duration,
                        FilePath = ResolveMedia(folder, file, "配音片段") ?? file,
                        GeneratedText = editingDocument != null ? node["generated_zh"]?.ToString() ?? "" : node["zh"]?.ToString() ?? "",
                        Voice = node["voice"]?.ToString() ?? "",
                        GeneratedVoice = node["generated_voice"]?.ToString() ?? (string.IsNullOrEmpty(node["voice"]?.ToString()) ? project["settings"]?["voice"]?.ToString() ?? "Serena" : ""),
                        GeneratedModel = node["generated_model"]?.ToString() ?? "",
                        Speaker = node["speaker"]?.ToString() ?? "",
                        Speed = node["speed"]?.GetValue<double>() ?? 1,
                        RawDuration = node["raw_duration"]?.GetValue<double>() ?? duration,
                        SourceIds = node["source_ids"] is JsonArray ids ? ids.Select(n => n!.GetValue<int>()).ToArray() : [id]
                    });
                }
            }
            var positionsPath = Path.Combine(folder, "voice_positions.json");
            var ignored = 0;
            if (editingDocument == null && File.Exists(positionsPath)) {
                var positions = JsonNode.Parse(File.ReadAllText(positionsPath))!.AsObject();
                if (positions["version"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的时间轴位置文件版本");
                foreach (var node in positions["clips"]!.AsArray()) {
                    var clip = clips.FirstOrDefault(c => c.Id == node!["id"]!.GetValue<int>() && c.Text == node["zh"]?.ToString()
                        && Path.GetFileName(c.FilePath) == Path.GetFileName(node["file"]?.ToString()));
                    var start = node!["start"]!.GetValue<double>();
                    if (clip != null && double.IsFinite(start) && start >= 0) { clip.Start = start; positionedClips.Add(clip); } else ignored++;
                }
            }
            lastClips = clips.Select(c => c.Copy()).ToList();
            audioMode.SelectedIndex = clips.Count == 0 && editingDocument == null ? 2 : 0;
            timeline.LoadClips(clips, sourceDuration);
            backgroundTrack.Load(path); RefreshBackground();
            var missing = clips.Count(c => !c.NeedsGeneration && !File.Exists(c.FilePath));
            status.Text = clips.Count == 0 ? "暂无配音片段，可右键时间轴空白处新增中文配音。" :
                $"已加载 {clips.Count} 段配音。拖动后自动保存；导出使用现有音频，不重新生成语音。";
            if (missing > 0) status.Text += $" {missing} 段音频缺失，请找回项目的“配音片段”文件夹。";
            if (ignored > 0) status.Text += $" {ignored} 个旧位置与当前音频不匹配，已忽略。";
            if (editingDocument != null) status.Text += $" 时间轴配音稿已恢复，{clips.Count(c => c.NeedsGeneration)} 段待生成。";
            if (videoPath == null) status.Text += " 原视频未找到，请重新关联原视频。";
            else { _ = PrepareVideoPreview(force: false); }
        }
        catch (Exception ex) {
            // Never leave the previous project's clips attached to the new project.
            clips = []; lastClips = []; editingDocument = null; timeline.LoadClips(clips, sourceDuration);
            status.Text = "时间轴读取失败：" + ex.Message;
        }
        SetBusy(busy); UpdatePosition();
    }

    public void SetBusy(bool value)
    {
        if (closed || App.IsShuttingDown) return;
        busy = value;
        timeline.IsEnabled = !busy;
        playButton.IsEnabled = !busy && !creatingPreview && (videoPath != null || clips.Count > 0);
        startBox.IsEnabled = !busy && clips.Count > 0;
        exportButton.IsEnabled = saveButton.IsEnabled = !busy && videoEdits.Valid && (HasVideo || clips.Count > 0 || editingDocument != null);
        locateButton.IsEnabled = !busy && projectPath != null;
        undoButton.IsEnabled = !busy && undo.Count > 0;
        scrubber.IsEnabled = !busy;
        audioMode.IsEnabled = !busy;
        backgroundEnabled.IsEnabled = !busy && projectPath != null;
        undoBackgroundButton.IsEnabled = !busy && backgroundTrack.CanUndo;
        voiceVolume.IsEnabled = resetVoiceVolumeButton.IsEnabled = !busy;
    }

    public bool SavePositions()
    {
        if (!videoEdits.Save()) return false;
        if (!SaveVoiceVolume()) return false;
        if (!backgroundTrack.Save()) return false;
        if (editingDocument != null) return SaveEditingDocument();
        if (projectPath == null || clips.Count == 0) return true;
        try {
            var entries = new JsonArray();
            foreach (var clip in clips.Where(positionedClips.Contains)) {
                if (!double.IsFinite(clip.Start) || clip.Start < 0) throw new InvalidDataException("配音位置必须是非负秒数");
                entries.Add(new JsonObject { ["id"] = clip.Id, ["zh"] = clip.Text, ["file"] = clip.FilePath, ["start"] = clip.Start });
            }
            var file = Path.Combine(Path.GetDirectoryName(projectPath)!, "voice_positions.json");
            if (entries.Count == 0 && !File.Exists(file)) return true;
            var json = new JsonObject { ["version"] = 1, ["clips"] = entries }.ToJsonString(Json);
            if (File.Exists(file) && File.ReadAllText(file) == json) return true;
            File.WriteAllText(file + ".tmp", json, new System.Text.UTF8Encoding(false));
            if (File.Exists(file)) File.Copy(file, file + ".previous", overwrite: true);
            File.Move(file + ".tmp", file, overwrite: true);
            status.Text = $"配音位置已保存 · {DateTime.Now:HH:mm:ss}。点击“按时间轴重新导出”更新成品。";
            return true;
        }
        catch (Exception ex) { status.Text = "位置保存失败，请保留窗口并重试：" + ex.Message; return false; }
    }

    private void ChangeVoiceVolume()
    {
        voiceVolumeText.Text = $"{voiceVolume.Value:0}%";
        if (loadingVolume) return;
        var resume = resumeAfterVolume || playing || preparing;
        Pause(); resumeAfterVolume = resume;
        volumeDirty = true;
        volumeTimer.Stop();
        volumeTimer.Start();
    }

    private bool SaveVoiceVolume()
    {
        if (!volumeDirty || projectPath == null) return true;
        try {
            var project = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
            if (project["settings"] is not JsonObject) throw new InvalidDataException("项目缺少制作设置。");
            project["settings"]!["voice_volume"] = VoiceVolume;
            File.WriteAllText(projectPath + ".volume.tmp", project.ToJsonString(Json), new System.Text.UTF8Encoding(false));
            File.Move(projectPath + ".volume.tmp", projectPath, overwrite: true);
            volumeDirty = false;
            status.Text = $"中文人声音量 {voiceVolume.Value:0}% 已保存，点击“按时间轴重新导出”更新成品。";
            return true;
        }
        catch (Exception ex) { status.Text = "人声音量保存失败，请保留窗口并重试：" + ex.Message; return false; }
    }

    private void PositionsChanged()
    {
        Pause();
        undo.Push((lastClips.Select(c => c.Copy()).ToList(), positionedClips.Select(c => c.Id).ToArray()));
        foreach (var clip in clips)
            if (lastClips.FirstOrDefault(c => c.Id == clip.Id) is not VoiceTimelineClip previous || Math.Abs(clip.Start - previous.Start) > .00001) positionedClips.Add(clip);
        lastClips = clips.Select(c => c.Copy()).ToList();
        ShowSelection(timeline.SelectedClip);
        SavePositions(); SetBusy(busy); UpdatePosition();
    }

    private void UndoMove()
    {
        if (busy || undo.Count == 0) return;
        Pause(); var previous = undo.Pop();
        var selectedId = timeline.SelectedClip?.Id;
        clips = previous.Clips;
        positionedClips.Clear();
        foreach (var clip in clips.Where(c => previous.ManualIds.Contains(c.Id))) positionedClips.Add(clip);
        lastClips = clips.Select(c => c.Copy()).ToList();
        timeline.LoadClips(clips, sourceDuration);
        if (selectedId.HasValue) timeline.SelectClip(selectedId.Value);
        ShowSelection(timeline.SelectedClip);
        SavePositions(); SetBusy(busy); UpdatePosition();
    }

    private void ShowSelection(VoiceTimelineClip? clip)
    {
        if (clip == null) { startBox.Clear(); selection.Text = "右键片段编辑中文，右键空白处新增片段。"; return; }
        startBox.Text = clip.Start.ToString("F3", CultureInfo.CurrentCulture);
        selection.Text = $"配音 {clip.Id} · {clip.Start:F3}–{clip.Start + clip.Duration:F3} 秒 · {(clip.NeedsGeneration ? "待生成，时长暂为估计 · " : "")}{clip.Text}";
    }

    private bool EnsureEditingDocument()
    {
        if (projectPath == null) return false;
        if (editingDocument != null) return true;
        if (BeforeEdit?.Invoke() == false) return false;
        try {
            var sourcePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "segments.json");
            var source = new JsonArray();
            foreach (var row in JsonNode.Parse(File.ReadAllText(sourcePath))!.AsArray())
                source.Add(new JsonObject { ["id"] = row!["id"]!.DeepClone(), ["zh"] = row["zh"]?.ToString() ?? "" });
            editingDocument = new JsonObject { ["version"] = 1, ["source_text"] = source, ["next_id"] = clips.Select(c => c.Id).DefaultIfEmpty(0).Max() + 1 };
            EditorChanged?.Invoke();
            return true;
        }
        catch (Exception ex) { status.Text = "无法打开配音编辑：" + ex.Message; return false; }
    }

    private bool SaveEditingDocument()
    {
        if (projectPath == null || editingDocument == null) return false;
        try {
            var entries = new JsonArray();
            foreach (var clip in clips) entries.Add(new JsonObject {
                ["id"] = clip.Id, ["zh"] = clip.Text, ["generated_zh"] = clip.GeneratedText,
                ["voice"] = clip.Voice, ["generated_voice"] = clip.GeneratedVoice, ["generated_model"] = clip.GeneratedModel, ["speaker"] = clip.Speaker,
                ["start"] = clip.Start, ["duration"] = clip.Duration, ["file"] = clip.FilePath,
                ["speed"] = clip.Speed, ["raw_duration"] = clip.RawDuration,
                ["source_ids"] = new JsonArray(clip.SourceIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
            });
            editingDocument["clips"] = entries;
            var path = Path.Combine(Path.GetDirectoryName(projectPath)!, "voice_edit.json");
            var json = editingDocument.ToJsonString(Json);
            if (File.Exists(path) && File.ReadAllText(path) == json) return true;
            File.WriteAllText(path + ".tmp", json, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".previous", true);
            File.Move(path + ".tmp", path, true);
            status.Text = $"时间轴已保存 · {clips.Count(c => c.NeedsGeneration)} 段待生成。原字幕表格保留作对照，配音以时间轴文案为准。";
            return true;
        }
        catch (Exception ex) { status.Text = "时间轴保存失败：" + ex.Message; return false; }
    }

    public bool PrepareAllVoiceRegeneration(string? uniformVoice)
    {
        if (busy || projectPath == null || clips.Count == 0) return false;
        Pause();
        if (!EnsureEditingDocument() || !SavePositions()) return false;
        try {
            var folder = Path.GetDirectoryName(projectPath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            File.Copy(Path.Combine(folder, "voice_edit.json"), Path.Combine(folder, "voice_edit.before-bulk-" + stamp + ".json"));
            File.Copy(projectPath, Path.Combine(folder, "project.before-bulk-" + stamp + ".json"));
        }
        catch (Exception ex) { status.Text = "生成前备份失败：" + ex.Message; return false; }
        var previous = clips.Select(c => c.Copy()).ToList();
        var manualIds = positionedClips.Select(c => c.Id).ToArray();
        foreach (var clip in clips) {
            if (uniformVoice != null) clip.Voice = uniformVoice;
            // Never present unfinished clips from a cancelled batch as newly generated.
            clip.GeneratedText = "";
        }
        if (!SavePositions()) {
            clips = previous; positionedClips.Clear();
            foreach (var clip in clips.Where(c => manualIds.Contains(c.Id))) positionedClips.Add(clip);
            timeline.LoadClips(clips, sourceDuration); return false;
        }
        undo.Push((previous, manualIds)); lastClips = clips.Select(c => c.Copy()).ToList();
        timeline.LoadClips(clips, sourceDuration); UpdatePosition(); EditorChanged?.Invoke();
        return true;
    }

    private void EditClip(VoiceTimelineClip? clip, double at = 0, bool chooseVoice = false)
    {
        if (busy || projectPath == null) return;
        Pause();
        var dialog = new ClipTextWindow(clip?.Text ?? "", clip?.Start ?? at, clip == null,
            VoiceOptions?.Invoke() ?? [], clip?.Voice ?? "", clip?.Speaker ?? "", chooseVoice) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || !EnsureEditingDocument()) return;
        if (clip == null) {
            var id = editingDocument!["next_id"]!.GetValue<int>();
            editingDocument["next_id"] = id + 1;
            clip = new VoiceTimelineClip { Id = id, Duration = Math.Max(.5, dialog.ChineseText.Length / 4.0) };
            clips.Add(clip);
        }
        // Switching back to the project voice also needs regeneration; do not audition stale audio.
        if (clip.Voice != dialog.Voice) clip.GeneratedText = "";
        clip.Text = dialog.ChineseText; clip.Start = dialog.StartSeconds; clip.Voice = dialog.Voice; clip.Speaker = dialog.Speaker;
        PositionsChanged(); timeline.LoadClips(clips, sourceDuration); timeline.SelectClip(clip.Id, true);
        if (SavePositions() && dialog.GenerateSpeech) RegenerateRequested?.Invoke(clip.Id);
    }

    private void DeleteClip(VoiceTimelineClip clip)
    {
        if (busy || !EnsureEditingDocument()) return;
        Pause(); clips.Remove(clip); positionedClips.Remove(clip);
        PositionsChanged(); timeline.LoadClips(clips, sourceDuration); ShowSelection(null);
    }

    private void RefreshBackground()
    {
        loadingBackground = true; backgroundEnabled.IsChecked = backgroundTrack.Enabled; loadingBackground = false;
        timeline.LoadBackground(backgroundTrack.Ranges, backgroundTrack.Enabled); SetBusy(busy);
    }

    private void EditBackground(string action, double from, double to, BackgroundRange? existing)
    {
        if (busy || projectPath == null) return;
        Pause();
        if (action == "audition") {
            if (!backgroundTrack.Enabled) { status.Text = "请先勾选保留背景音，再单独试听背景。"; return; }
            audioMode.SelectedIndex = 1; Seek(from); auditionEnd = to; Play(); return;
        }
        if (action == "delete" && existing != null) { backgroundTrack.Delete(existing.Id); return; }
        var range = existing?.Copy() ?? new BackgroundRange { Start = from, End = to };
        if (action == "edit") {
            var dialog = new BackgroundRangeWindow(range) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            range = dialog.Range;
        } else if (action == "mute") range.Gain = 0;
        else if (action == "separated") { range.Mode = "separated"; if (range.Gain == 0) range.Gain = 1; }
        else return;
        backgroundTrack.Apply(range);
    }

    private void ApplyStart()
    {
        if (busy || timeline.SelectedClip is not VoiceTimelineClip clip) return;
        if (!double.TryParse(startBox.Text, out var start) || !double.IsFinite(start) || start < 0) {
            status.Text = "请输入非负的开始秒数。"; return;
        }
        clip.Start = Math.Round(start, 3); PositionsChanged();
        timeline.LoadClips(clips, sourceDuration); timeline.SelectClip(clip.Id); UpdatePosition();
    }

    private void LocateVideo()
    {
        if (busy || projectPath == null) return;
        var dialog = new OpenFileDialog { Title = "重新关联本项目的原视频", Filter = "视频|*.mp4;*.mkv;*.mov;*.webm;*.avi|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try {
            if (!SavePositions()) return;
            var p = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
            p["video"] = dialog.FileName;
            var tmp = projectPath + ".tmp"; File.WriteAllText(tmp, p.ToJsonString(Json)); File.Move(tmp, projectPath, true);
            LoadProject(projectPath);
        }
        catch (Exception ex) { status.Text = "关联视频失败：" + ex.Message; }
    }

    private void TogglePlayback()
    {
        if (busy) return;
        if (playing || preparing || resumeAfterVolume) Pause(); else { auditionEnd = double.PositiveInfinity; Play(); }
    }

    private void AuditionSelected()
    {
        if (busy || timeline.SelectedClip is not VoiceTimelineClip clip) return;
        Pause(); Seek(clip.Start); auditionEnd = clip.Start + clip.Duration; Play();
    }

    private async void Play()
    {
        if (closed || App.IsShuttingDown || busy || creatingPreview || Duration <= 0 || !videoEdits.Valid) return;
        volumeTimer.Stop(); resumeAfterVolume = false;
        SaveVoiceVolume();
        if (position >= Duration) position = 0;
        position = videoEdits.Advance(position, 0, Duration);
        if (position >= Math.Min(Duration, auditionEnd)) { UpdatePosition(); status.Text = "此位置之后没有保留的视频区间，请回到开头播放。"; return; }
        var version = ++playbackVersion;
        preparing = true; playButton.Content = "加载中 / 暂停";
        SyncVoices();
        var backgroundLoad = audioMode.SelectedIndex != 2 ? backgroundTrack.PrepareAsync() : Task.CompletedTask;
        var currentLoads = voiceReady.Where(p => position >= p.Key.Start && position < p.Key.Start + p.Key.Duration).Select(p => p.Value.Task).ToArray();
        try { await Task.WhenAll(currentLoads.Cast<Task>().Append(backgroundLoad)).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException) {
            if (version == playbackVersion) { Pause(); status.Text = "音频加载超时，请确认片段文件可用后重试。"; }
            return;
        }
        if (closed || App.IsShuttingDown || version != playbackVersion || busy) return;
        preparing = false;
        clockStart = position; clock.Restart(); playing = true;
        if (videoReady) { video.Position = TimeSpan.FromSeconds(Math.Min(position, sourceDuration)); if (position < sourceDuration) video.Play(); }
        playButton.Content = "暂停"; timer.Start(); SyncVoices();
        backgroundTrack.Update(position, playing);
    }

    private void Pause()
    {
        resumeAfterVolume = false;
        if (playing) position = videoEdits.Advance(clockStart, clock.Elapsed.TotalSeconds, Duration);
        playbackVersion++; preparing = playing = false; clock.Stop(); timer.Stop();
        if (videoReady) video.Pause();
        CloseVoices(); playButton.Content = "播放"; UpdatePosition();
        backgroundTrack.Close();
    }

    public void ReleaseMedia()
    {
        volumeTimer.Stop();
        previewVersion++;
        previewCancellation?.Cancel(); previewCancellation = null;
        creatingPreview = usingCompatiblePreview = false;
        cancelPreviewButton.Visibility = Visibility.Collapsed;
        Pause(); video.Close(); video.Source = null; videoReady = false; videoPath = null;
        previewStatus.Text = "打开项目后显示视频预览"; previewStatus.Visibility = Visibility.Visible;
    }

    public void Shutdown()
    {
        if (closed) return;
        closed = true;
        ReleaseMedia();
    }

    private async Task RecoverVideoPreview(Exception error)
    {
        videoReady = false;
        if (closed || App.IsShuttingDown || creatingPreview || videoPath == null || projectPath == null || busy) return;
        Pause();
        if (usingCompatiblePreview) {
            video.Close();
            SetPreviewStatus("兼容预览仍无法播放，请检查 Windows 媒体组件。错误：" + error.Message);
            return;
        }
        await PrepareVideoPreview(force: true);
    }

    private void SetPreviewStatus(string message)
    {
        status.Text = previewStatus.Text = message;
        previewStatus.Visibility = Visibility.Visible;
    }

    private async Task PrepareVideoPreview(bool force)
    {
        if (closed || App.IsShuttingDown || creatingPreview || videoPath == null || projectPath == null || busy) return;
        Pause(); videoReady = false;
        var version = previewVersion;
        var source = videoPath;
        var folder = Path.GetDirectoryName(projectPath)!;
        using var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        creatingPreview = true; cancelPreviewButton.Visibility = Visibility.Visible; SetBusy(busy);
        video.Close(); video.Source = null;
        var progress = new Progress<string>(message => {
            if (version == previewVersion && !cancellation.IsCancellationRequested && creatingPreview) SetPreviewStatus(message);
        });
        try {
            SetPreviewStatus("正在准备视频预览…");
            var proxy = force
                ? await CompatibleVideoPreview.GetAsync(source, folder, sourceDuration, progress, cancellation.Token)
                : await CompatibleVideoPreview.GetPlaybackPathAsync(source, folder, sourceDuration, progress, cancellation.Token);
            if (closed || App.IsShuttingDown || version != previewVersion || cancellation.IsCancellationRequested) return;
            usingCompatiblePreview = !string.Equals(proxy, source, StringComparison.OrdinalIgnoreCase);
            creatingPreview = false;
            video.Source = new Uri(proxy); video.Play();
            status.Text = usingCompatiblePreview ? "兼容预览已就绪。预览使用缩小画面缓存，成品仍使用原视频画质。" : "视频已加载，可播放或点击时间轴定位。";
        }
        catch (OperationCanceledException) {
            if (version == previewVersion) SetPreviewStatus("已停止预览准备，点击“兼容预览”可重试。");
        }
        catch (Exception ex) {
            if (version == previewVersion) SetPreviewStatus(ex.Message);
        }
        finally {
            if (version == previewVersion) {
                previewCancellation = null; creatingPreview = false;
                cancelPreviewButton.Visibility = Visibility.Collapsed; SetBusy(busy);
            }
        }
    }

    private void Seek(double seconds)
    {
        if (busy) return;
        var resume = playing || preparing;
        Pause();
        position = Math.Clamp(seconds, 0, Duration);
        auditionEnd = double.PositiveInfinity;
        if (videoReady) {
            video.Position = TimeSpan.FromSeconds(Math.Min(position, Math.Max(0, sourceDuration - .04)));
        }
        if (resume) Play(); UpdatePosition();
    }

    private void ChangeAudio()
    {
        var resume = playing || preparing;
        Pause(); video.Volume = audioMode.SelectedIndex == 2 ? 1 : 0;
        if (resume) Play();
    }

    private void Tick()
    {
        if (closed || App.IsShuttingDown) return;
        var previous = position;
        position = videoEdits.Advance(clockStart, clock.Elapsed.TotalSeconds, Duration);
        if (position >= Math.Min(Duration, auditionEnd)) { Pause(); return; }
        if (videoEdits.CrossesCut(previous, position)) {
            var next = position; Pause(); position = next;
            if (videoReady) video.Position = TimeSpan.FromSeconds(Math.Min(position, Math.Max(0, sourceDuration - .04)));
            Play(); return;
        }
        if (videoReady && position < sourceDuration && Math.Abs(video.Position.TotalSeconds - position) > .35)
            video.Position = TimeSpan.FromSeconds(position);
        if (videoReady && position >= sourceDuration) video.Pause();
        SyncVoices(); UpdatePosition();
        backgroundTrack.Update(position, playing);
    }

    private void SyncVoices()
    {
        if (closed || App.IsShuttingDown) return;
        if (audioMode.SelectedIndex != 0) return;
        // Open upcoming WAVs before their start, preserving sentence beginnings.
        var nearby = clips.Where(c => !c.NeedsGeneration && position + 3 >= c.Start && position < c.Start + c.Duration).ToHashSet();
        foreach (var old in players.Keys.Where(c => !nearby.Contains(c)).ToArray()) {
            voiceReady[old].TrySetResult(false); players[old].Close(); players.Remove(old);
            voiceReady.Remove(old); startedVoices.Remove(old);
        }
        foreach (var clip in nearby) {
            if (failedClips.Contains(clip)) continue;
            if (players.TryGetValue(clip, out var loaded)) {
                if (playing && position >= clip.Start && voiceReady[clip].Task.IsCompletedSuccessfully && voiceReady[clip].Task.Result && startedVoices.Add(clip)) {
                    loaded.Position = TimeSpan.FromSeconds(Math.Max(0, position - clip.Start)); loaded.Play();
                }
                continue;
            }
            if (!File.Exists(clip.FilePath)) { failedClips.Add(clip); status.Text = $"配音 {clip.Id} 音频文件缺失，无法试听。"; continue; }
            var gain = VoiceVolume;
            var player = new MediaPlayer { Volume = Math.Min(1, gain) };
            players[clip] = player;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            voiceReady[clip] = ready;
            player.MediaOpened += (_, _) => {
                if (!players.TryGetValue(clip, out var current) || current != player) return;
                player.Pause(); ready.TrySetResult(true);
            };
            player.MediaFailed += (_, _) => {
                ready.TrySetResult(false);
                if (!players.TryGetValue(clip, out var current) || current != player) return;
                failedClips.Add(clip); status.Text = $"配音 {clip.Id} 试听失败，请确认音频文件完整。";
            };
            _ = OpenVoiceAsync(clip, player, ready, gain, voiceCancellation.Token);
        }
    }

    private async Task OpenVoiceAsync(VoiceTimelineClip clip, MediaPlayer player, TaskCompletionSource<bool> ready, double gain, CancellationToken cancellation)
    {
        try {
            var path = await VoiceVolumePreview.GetAsync(clip.FilePath, Path.GetDirectoryName(projectPath!)!, gain, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!players.TryGetValue(clip, out var current) || current != player) return;
            player.Open(new Uri(Path.GetFullPath(path)));
        }
        catch (OperationCanceledException) { ready.TrySetResult(false); }
        catch (Exception ex) {
            ready.TrySetResult(false);
            if (!players.TryGetValue(clip, out var current) || current != player) return;
            failedClips.Add(clip); status.Text = $"配音 {clip.Id} 无法试听：{ex.Message}";
        }
    }

    private void CloseVoices()
    {
        voiceCancellation.Cancel(); voiceCancellation.Dispose(); voiceCancellation = new();
        foreach (var player in players.Values) player.Close();
        foreach (var ready in voiceReady.Values) ready.TrySetResult(false);
        players.Clear(); voiceReady.Clear(); startedVoices.Clear(); failedClips.Clear();
    }

    private void UpdatePosition()
    {
        updatingScrubber = true;
        scrubber.Maximum = Math.Max(1, Duration); scrubber.Value = position;
        updatingScrubber = false;
        counter.Text = $"{TimeText(videoEdits.OutputTime(position, Duration))} / {TimeText(videoEdits.OutputDuration(Duration))}";
        counter.ToolTip = $"剪辑后播放时间；原片位置 {TimeText(position)}。时间轴以原片时间定位，灰色区间在导出时删除。";
        timeline.SetPosition(position);
        caption.Text = string.Join("\n", clips.Where(c => position >= c.Start && position < c.Start + c.Duration).Select(c => c.Text));
    }
}
