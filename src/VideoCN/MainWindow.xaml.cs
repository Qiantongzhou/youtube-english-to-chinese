using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoCN;

public sealed class Segment : INotifyPropertyChanged
{
    public int Id { get; set; }
    private double start, end, targetRate = 4;
    private string zh = "";
    public double Start { get => start; set { start = value; Changed(); } }
    public double End { get => end; set { end = value; Changed(); } }
    public string En { get; set; } = "";
    public string Zh { get => zh; set { zh = value; Changed(); } }
    public double? AudioStart { get; set; }
    public int? SpeechBlock { get; set; }
    public double? BlockStart { get; set; }
    public double? BlockEnd { get; set; }
    private double? speechDuration;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? SpeechDuration {
        get => SpeechSpans == null ? speechDuration : SpeechSpans.Sum(s => Math.Max(0, Math.Min(End, s.End) - Math.Max(Start, s.Start)));
        set => speechDuration = value;
    }
    [System.Text.Json.Serialization.JsonIgnore] public List<(double Start, double End)>? SpeechSpans { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string AudioTiming { get; set; } = "尚未导出";
    [System.Text.Json.Serialization.JsonIgnore] public double TargetRate { get => targetRate; set { targetRate = value; Changed(); } }
    [System.Text.Json.Serialization.JsonIgnore] public string TextBudget {
        get {
            var count = System.Text.RegularExpressions.Regex.Matches(Zh, @"[\u3400-\u9fff]|[A-Za-z]+|\d+(?:\.\d+)?").Count;
            var target = Math.Max(1, Math.Round((SpeechDuration ?? End - Start) * TargetRate));
            var low = Math.Max(1, Math.Floor(target * .8)); var high = Math.Ceiling(target * 1.2);
            return $"{count} / {low:0}–{high:0} 字" + (count < low ? " 偏少" : count > high ? " 偏多" : "") +
                (SpeechBlock.HasValue ? $"\n段{SpeechBlock} · 有声 {SpeechDuration:F2} 秒" : "");
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(nameof(TextBudget)));
}

public sealed class Stage(string key, string name) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    private string mark = "○";
    public string Mark { get => mark; set { mark = value; PropertyChanged?.Invoke(this, new(nameof(Mark))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private string voiceModel = "auto";
    private const string DefaultVoiceStyle = "整条视频保持同一位中文讲解员的播音状态。语气温和、稳定、清晰、自然，保持中等语速和稳定音高，不夸张表演，不突然提高或降低情绪，不拖长句尾；按中文标点做轻微停顿，专有名词和英文名保持平稳读法。";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private readonly string root;
    private readonly VoiceEditorWorkspace voiceEditor = new();
    private readonly ObservableCollection<Stage> stages = [new("download", "获取视频"), new("extract", "提取原声音轨"), new("transcribe", "识别英文与时间轴"), new("translate", "翻译成自然中文"), new("separate", "分离背景音乐与环境声"), new("tts", "生成中文配音"), new("compose", "合成 MP4 与字幕"), new("publish_text", "生成标题与简介")];
    private ObservableCollection<Segment> segments = [];
    private string? projectPath;
    private Process? active;
    private ProcessJob? activeJob;
    private bool busy;
    private bool cancelling;
    private bool closing;
    private bool reportedError;
    private bool stoppingEdge;
    private string? lastError;
    private string Python => Path.Combine(root, ".runtime", "ai", "Scripts", "python.exe");
    private string Engine => Path.Combine(root, "engine", "engine.py");

    public MainWindow()
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        root = FindRoot();
        VoiceEditorHost.Content = voiceEditor;
        Pages.SelectionChanged += (_, e) => {
            if (e.Source == Pages && Pages.SelectedIndex == 1) LogPanel.IsExpanded = false;
        };
        EditorViews.SelectionChanged += (_, e) => {
            if (e.Source == EditorViews && EditorViews.SelectedIndex == 0) LogPanel.IsExpanded = false;
        };
        voiceEditor.ExportRequested += async () => await ExportTimeline();
        voiceEditor.RegenerateRequested += async id => await GenerateTimelineVoice(id);
        voiceEditor.BeforeEdit = SaveCurrentEditor;
        voiceEditor.VoiceOptions = () => {
            var options = VoiceBox.Items.Cast<ComboBoxItem>()
                .Select(item => new ClipVoiceOption(item.Tag?.ToString() ?? "", item.Content?.ToString() ?? "")).ToList();
            var current = (VoiceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "默认音色";
            options.Insert(0, new ClipVoiceOption("", "跟随制作偏好（" + current + "）"));
            return options;
        };
        voiceEditor.EditorChanged += UpdateControls;
        voiceEditor.ClipSelected += clip => {
            var row = segments.FirstOrDefault(s => clip.SourceIds.Contains(s.Id));
            if (row != null) SubtitleGrid.SelectedItem = row;
        };
        OutputBox.Text = Path.Combine(root, "output");
        StageList.ItemsSource = stages;
        SubtitleGrid.ItemsSource = segments;
        var edgeFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
        if (Directory.Exists(edgeFolder)) {
            foreach (var path in Directory.EnumerateDirectories(edgeFolder, "Profile *")) {
                var name = Path.GetFileName(path);
                if (System.Text.RegularExpressions.Regex.IsMatch(name, "^Profile [0-9]+$")) EdgeProfileBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
        }
        LoadSettings();
        UpdateControls();
    }
    private static string FindRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("VIDEOCN_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "engine", "engine.py"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private void VoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        // Initial XAML selection can fire before the other controls exist.
        if (VoiceProviderHint == null || VoiceRepetitionSlider == null) return;
        var online = Selected(VoiceBox).StartsWith("edge:", StringComparison.Ordinal);
        VoiceProviderHint.Text = online
            ? "微软在线配音：需联网，待配音文本发送给微软服务。无需本机 GPU；统一语速有效，风格提示、种子和采样参数不适用。"
            : "本地 Qwen 配音：使用所选计算设备，支持风格提示和生成参数；双卡自动分配任务。";
        VoiceStyleBox.IsEnabled = VoiceConsistencyBox.IsEnabled = VoiceSeedBox.IsEnabled = !online;
        VoiceTemperatureSlider.IsEnabled = VoiceTopPSlider.IsEnabled = VoiceRepetitionSlider.IsEnabled = !online;
    }
    private static void Select(ComboBox box, string? value)
    {
        foreach (ComboBoxItem item in box.Items) if (item.Tag?.ToString() == value) { box.SelectedItem = item; break; }
    }
    private JsonObject Settings() => new() {
        ["source"] = SourceBox.Text.Trim(), ["output_dir"] = OutputBox.Text, ["mode"] = Selected(ModeBox),
        ["voice"] = Selected(VoiceBox), ["resolution"] = Selected(ResolutionBox), ["device"] = Selected(DeviceBox),
        ["dub"] = DubBox.IsChecked == true, ["background"] = BackgroundBox.IsChecked == true,
        ["burn_subtitles"] = SubtitleBox.IsChecked == true, ["review_first"] = ReviewBox.IsChecked == true,
        ["glossary"] = GlossaryBox.Text.Trim(), ["cookies"] = CookiesBox.Text,
        ["voice_style"] = string.IsNullOrWhiteSpace(VoiceStyleBox.Text) ? DefaultVoiceStyle : VoiceStyleBox.Text.Trim(),
        ["voice_consistency"] = VoiceConsistencyBox.IsChecked == true,
        ["voice_seed"] = long.TryParse(VoiceSeedBox.Text.Trim(), out var seed) ? seed : 20260915L,
        ["voice_temperature"] = VoiceTemperatureSlider.Value,
        ["voice_top_p"] = VoiceTopPSlider.Value,
        ["voice_repetition_penalty"] = VoiceRepetitionSlider.Value,
        ["voice_speed"] = VoicePlaybackSpeedSlider.Value,
        ["voice_volume"] = voiceEditor.VoiceVolume,
        ["voice_model"] = voiceModel,
        ["chars_per_second"] = RateSlider.Value,
        ["subtitle_max_chars"] = (int)Math.Round(SubtitleLengthSlider.Value),
        ["sentence_alignment"] = SentenceAlignmentBox.IsChecked == true,
        ["alignment_tolerance"] = double.Parse(Selected(ToleranceBox), System.Globalization.CultureInfo.InvariantCulture),
        ["cookie_source"] = Selected(CookieSourceBox), ["edge_profile"] = Selected(EdgeProfileBox)
    };
    private void ApplySettings(JsonObject s, bool includeLogin = true)
    {
        SourceBox.Text = s["source"]?.ToString() ?? "";
        OutputBox.Text = s["output_dir"]?.ToString() ?? Path.Combine(root, "output");
        Select(ModeBox, s["mode"]?.ToString()); Select(VoiceBox, s["voice"]?.ToString());
        Select(ResolutionBox, s["resolution"]?.ToString()); Select(DeviceBox, s["device"]?.ToString());
        DubBox.IsChecked = s["dub"]?.GetValue<bool>() ?? true;
        BackgroundBox.IsChecked = s["background"]?.GetValue<bool>() ?? true;
        SubtitleBox.IsChecked = s["burn_subtitles"]?.GetValue<bool>() ?? true;
        ReviewBox.IsChecked = s["review_first"]?.GetValue<bool>() ?? true;
        GlossaryBox.Text = s["glossary"]?.ToString() ?? "";
        VoiceStyleBox.Text = s["voice_style"]?.ToString() ?? DefaultVoiceStyle;
        VoiceConsistencyBox.IsChecked = s["voice_consistency"]?.GetValue<bool>() ?? true;
        try { VoiceSeedBox.Text = s["voice_seed"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "20260915"; }
        catch { VoiceSeedBox.Text = "20260915"; }
        VoiceTemperatureSlider.Value = SettingDouble(s, "voice_temperature", .65, .1, 1.2);
        VoiceTopPSlider.Value = SettingDouble(s, "voice_top_p", .9, .5, 1);
        VoiceRepetitionSlider.Value = SettingDouble(s, "voice_repetition_penalty", 1.05, .9, 1.3);
        VoicePlaybackSpeedSlider.Value = SettingDouble(s, "voice_speed", 1.0, .75, 1.5);
        voiceEditor.VoiceVolume = SettingDouble(s, "voice_volume", 1, 0, 2);
        voiceModel = s["voice_model"]?.ToString() is "0.6B" or "1.7B" ? s["voice_model"]!.ToString() : "auto";
        RateSlider.Value = Math.Clamp(s["chars_per_second"]?.GetValue<double>() ?? 4, 2, 8);
        SubtitleLengthSlider.Value = SettingDouble(s, "subtitle_max_chars", 20, 10, 40);
        SentenceAlignmentBox.IsChecked = s["sentence_alignment"]?.GetValue<bool>() ?? true;
        Select(ToleranceBox, (s["alignment_tolerance"]?.GetValue<double>() ?? 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!includeLogin) return;
        CookiesBox.Text = s["cookies"]?.ToString() ?? "";
        Select(CookieSourceBox, s["cookie_source"]?.ToString() ?? (string.IsNullOrWhiteSpace(CookiesBox.Text) ? "none" : "file"));
        var profile = s["edge_profile"]?.ToString() ?? "Default";
        if (!EdgeProfileBox.Items.Cast<ComboBoxItem>().Any(i => i.Tag?.ToString() == profile) && System.Text.RegularExpressions.Regex.IsMatch(profile, "^(Default|Profile [0-9]+)$"))
            EdgeProfileBox.Items.Add(new ComboBoxItem { Content = profile, Tag = profile });
        Select(EdgeProfileBox, profile);
    }
    private static double SettingDouble(JsonObject settings, string key, double fallback, double min, double max)
    {
        try { return Math.Clamp(settings[key]?.GetValue<double>() ?? fallback, min, max); }
        catch { return fallback; }
    }
    private void LoadSettings()
    {
        try { var path = Path.Combine(root, "settings.json"); if (File.Exists(path)) ApplySettings(JsonNode.Parse(File.ReadAllText(path))!.AsObject()); }
        catch (Exception ex) { Log("设置加载失败，已使用默认值：" + ex.Message); }
    }
    private static void AtomicWrite(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
    private JsonObject Project() => JsonNode.Parse(File.ReadAllText(projectPath!))!.AsObject();
    private bool ReadyToRender()
    {
        if (projectPath == null) return false;
        try { return Project()["completed"]!.AsArray().Any(s => s?.ToString() == "translate"); }
        catch { return false; }
    }
    private void UpdateControls()
    {
        if (closing || App.IsShuttingDown) return;
        StartButton.IsEnabled = DownloadButton.IsEnabled = OpenProjectButton.IsEnabled = !busy;
        SourceBox.IsEnabled = OptionsPanel.IsEnabled = GlossaryBox.IsEnabled = !busy;
        LoginPanel.IsEnabled = !busy;
        CookieSourceBox.IsEnabled = QuickLoginButton.IsEnabled = StopEdgeButton.IsEnabled = ImportCookiesButton.IsEnabled = !busy;
        InstallButton.IsEnabled = InstallCoreButton.IsEnabled = CheckButton.IsEnabled = ModelsButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && !cancelling && !stoppingEdge;
        RenderButton.IsEnabled = RetimeButton.IsEnabled = ResegmentButton.IsEnabled = !busy && ReadyToRender();
        if (voiceEditor.HasEditableTimeline) {
            RetimeButton.IsEnabled = ResegmentButton.IsEnabled = false;
            EditorHint.Text = "时间轴编辑已启用：请在配音片段上右键修改中文，本表保留原文对照。生成中文视频将使用时间轴配音稿。";
        }
        EditorRatePanel.IsEnabled = !busy;
        SaveButton.IsEnabled = PreviewButton.IsEnabled = !busy && segments.Count > 0;
        ResumeButton.IsEnabled = !busy && projectPath != null;
        SubtitleGrid.IsReadOnly = busy || voiceEditor.HasEditableTimeline;
        EditorOpenButton.IsEnabled = !busy;
        EditorRegenerateAllButton.IsEnabled = !busy && projectPath != null && voiceEditor.HasClips;
        voiceEditor.SetBusy(busy);
    }
    private void Log(string line)
    {
        if (closing || App.IsShuttingDown) return;
        if (LogBox.Text.Length > 100_000) LogBox.Text = LogBox.Text[^60_000..];
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n"); LogBox.ScrollToEnd();
    }
    private async Task<bool> Run(string executable, IEnumerable<string> arguments, bool protocol)
    {
        if (busy || closing || App.IsShuttingDown) return false;
        if (!SaveCurrentEditor()) return false;
        voiceEditor.ReleaseMedia();
        busy = true; cancelling = false; reportedError = false; lastError = null; UpdateControls();
        Progress.IsIndeterminate = !protocol;
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        var logPath = Path.Combine(root, "artifacts", $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        try {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            start.Environment["PYTHONUTF8"] = "1"; start.Environment["PYTHONUNBUFFERED"] = "1";
            start.Environment["VIDEOCN_ROOT"] = root;
            active = new Process { StartInfo = start };
            using var job = ProcessJob.Start(active);
            activeJob = job;
            using var diskLog = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
            async Task Read(StreamReader reader, bool parse)
            {
                while (await reader.ReadLineAsync() is { } line) {
                    // Both readers resume on WPF's dispatcher, serializing log and UI access.
                    diskLog.WriteLine(line);
                    if (parse) Receive(line); else Log(line);
                }
            }
            await Task.WhenAll(Read(active.StandardOutput, protocol), Read(active.StandardError, false), active.WaitForExitAsync());
            if (closing || App.IsShuttingDown) return false;
            if (cancelling) {
                StatusText.Text = "已停止。已完成的阶段和字幕保留在项目中。";
                return false;
            }
            if (active.ExitCode != 0 || reportedError) {
                if (projectPath != null && File.Exists(projectPath)) LoadSegments();
                StatusText.Text = "处理未完成，请查看错误详情。可修改字幕或修复环境后继续。";
                var message = lastError ?? "进程执行失败，请查看运行日志。";
                if (message.Length > 1200) message = message[..1200] + "\n…更多详情见日志。";
                StatusText.Text = message.Length > 180 ? message[..180] + "…" : message;
                if (!Environment.GetCommandLineArgs().Contains("--smoke-test"))
                    MessageBox.Show(message + "\n\n完整日志：" + logPath, "处理未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }
        catch (Exception ex) {
            if (closing || App.IsShuttingDown) return false;
            Log(ex.ToString()); StatusText.Text = "未能启动或完成处理。";
            if (Environment.GetCommandLineArgs().Contains("--smoke-test")) throw;
            MessageBox.Show(ex.Message, "VideoCN"); return false;
        }
        finally {
            activeJob?.Dispose(); activeJob = null; active?.Dispose(); active = null; busy = false;
            // Closing cancels this task too; never reopen media from its continuation.
            if (!closing && !App.IsShuttingDown) {
                Progress.IsIndeterminate = false; voiceEditor.SetBusy(false); voiceEditor.LoadProject(projectPath); UpdateControls();
            }
        }
    }
    private void Receive(string line)
    {
        if (closing || App.IsShuttingDown) return;
        try {
            var item = JsonNode.Parse(line)!.AsObject();
            var type = item["type"]?.ToString();
            switch (type) {
                case "progress":
                    Progress.IsIndeterminate = false;
                    Progress.Value = item["percent"]?.GetValue<double>() ?? Progress.Value;
                    StatusText.Text = item["message"]?.ToString() ?? "处理中…";
                    var key = item["stage"]?.ToString();
                    var current = stages.FirstOrDefault(s => s.Key == key);
                    if (current != null) {
                        foreach (var s in stages) { if (s == current) { s.Mark = "●"; break; } s.Mark = "✓"; }
                    }
                    break;
                case "project": projectPath = item["path"]!.ToString(); Log("项目：" + projectPath); break;
                case "title": VideoTitle.Text = item["message"]!.ToString(); break;
                case "auth": LoginStatusText.Text = item["message"]!.ToString(); Log(LoginStatusText.Text); break;
                case "review": LoadSegments(); Pages.SelectedIndex = 1; EditorViews.SelectedIndex = 1; StatusText.Text = "翻译已完成。校对字幕后生成中文视频。"; break;
                case "done": LoadSegments(); stages.ToList().ForEach(s => s.Mark = "✓"); Progress.Value = 100; Pages.SelectedIndex = 1; EditorViews.SelectedIndex = 0; LogPanel.IsExpanded = false; StatusText.Text = "中文视频已导出，可在时间轴继续调整配音位置。"; break;
                case "voice_ready": Progress.Value = 100; Pages.SelectedIndex = 1; EditorViews.SelectedIndex = 0; StatusText.Text = "所选配音已更新，可直接试听；点击按时间轴重新导出更新成品。"; break;
                case "downloaded": StatusText.Text = "视频已保存到项目文件夹。"; Progress.Value = 100; break;
                case "models_ready": EnvironmentText.Text = item["message"]!.ToString(); StatusText.Text = "模型下载完成。"; break;
                case "login_checked": LoginStatusText.Text = item["message"]!.ToString(); StatusText.Text = LoginStatusText.Text; break;
                case "check":
                    EnvironmentText.Text = string.Join("\n", item["checks"]!.AsArray().Select(c => $"{(c!["ok"]!.GetValue<bool>() ? "✓" : "○")}  {c["name"]}    {c["detail"]}"));
                    StatusText.Text = "环境检查完成。"; break;
                case "error": reportedError = true; lastError = item["message"]?.ToString(); Log(lastError ?? line); break;
                default: Log(item["message"]?.ToString() ?? line); break;
            }
        }
        catch (JsonException) { Log(line); }
        catch (Exception ex) { Log("读取进度失败：" + ex.Message + "\n" + line); }
    }
    private bool EnsurePython()
    {
        if (File.Exists(Python)) return true;
        Pages.SelectedIndex = 2; EnvironmentText.Text = "尚未安装本地环境。请点击「安装 / 修复完整环境」，仅下载视频可选择「只安装下载工具」。";
        return false;
    }
    private async Task StartPipeline(bool downloadOnly)
    {
        if (busy || !EnsurePython()) return;
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) { SourceBox.Focus(); StatusText.Text = "请先粘贴网址或导入视频。"; return; }
        if (!SaveCurrentEditor()) return;
        AtomicWrite(Path.Combine(root, "settings.json"), Settings().ToJsonString(Json));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        var request = Path.Combine(root, "artifacts", "request-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(request, Settings().ToJsonString(Json));
        projectPath = null; segments.Clear(); foreach (var s in stages) s.Mark = "○";
        voiceEditor.LoadProject(null);
        var autoRender = ReviewBox.IsChecked != true && !downloadOnly;
        var ok = await Run(Python, ["-u", Engine, downloadOnly ? "download" : "prepare", request], true);
        if (ok && autoRender) await Render();
    }
    private void LoadSegments()
    {
        if (closing || App.IsShuttingDown || projectPath == null) return;
        var path = Path.Combine(Path.GetDirectoryName(projectPath)!, "segments.json");
        if (!File.Exists(path)) return;
        segments = new(JsonSerializer.Deserialize<List<Segment>>(File.ReadAllText(path), Json) ?? []);
        var layoutPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "speech_regions.json");
        if (File.Exists(layoutPath)) {
            var blocks = JsonNode.Parse(File.ReadAllText(layoutPath))!["blocks"]!.AsArray();
            foreach (var segment in segments) {
                var block = blocks.FirstOrDefault(b => b!["id"]!.GetValue<int>() == segment.SpeechBlock);
                if (block != null) segment.SpeechSpans = block["speech_spans"]!.AsArray().Select(s =>
                    (s!["start"]!.GetValue<double>(), s["end"]!.GetValue<double>())).ToList();
            }
        }
        var timingPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "dub_timing.json");
        if (File.Exists(timingPath) && Project()["completed"]!.AsArray().Any(s => s?.ToString() == "tts")) {
            var timing = JsonNode.Parse(File.ReadAllText(timingPath))!.AsArray();
            foreach (var segment in segments) {
                var matches = timing.Where(entry => entry!["source_ids"] is JsonArray ids ? ids.Any(id => id!.GetValue<int>() == segment.Id) : entry!["id"]!.GetValue<int>() == segment.Id).ToList();
                if (matches.Count > 0) segment.AudioTiming = string.Join("；", matches.Select(entry => {
                    var rate = entry!["chars_per_second"]?.GetValue<double>();
                    return $"句{entry["id"]} {entry["start"]!.GetValue<double>():F2}–{entry["end"]!.GetValue<double>():F2} 秒" +
                        (rate.HasValue ? $" · {rate:F1} 字/秒" : "") + $" {entry["note"]}";
                }));
            }
        }
        foreach (var segment in segments) segment.TargetRate = RateSlider.Value;
        SubtitleGrid.ItemsSource = segments;
        EditorHint.Text = $"共 {segments.Count} 句 · 修改会保存在项目中：{Path.GetDirectoryName(projectPath)}";
        UpdateControls();
    }
    private bool SaveSegments(bool allowEmptyText = false)
    {
        if (projectPath == null || segments.Count == 0) return false;
        if (!SubtitleGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !SubtitleGrid.CommitEdit(DataGridEditingUnit.Row, true)) {
            MessageBox.Show("请先修正表格中无效的数字。"); return false;
        }
        var duration = Project()["duration"]!.GetValue<double>(); double end = 0;
        foreach (var s in segments) {
            if (s.AudioStart is double audioStart && (!double.IsFinite(audioStart) || audioStart < 0)) {
                MessageBox.Show($"第 {s.Id} 句配音开始时间必须是非负秒数，留空则自动排列。"); return false;
            }
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.Start < end - 0.001 || s.End <= s.Start || s.End > duration + 0.05 || (!allowEmptyText && string.IsNullOrWhiteSpace(s.Zh))) {
                MessageBox.Show($"第 {s.Id} 句无效：中文不能为空，时间不能重叠或超出视频时长。"); return false;
            }
            end = s.End;
        }
        AtomicWrite(Path.Combine(Path.GetDirectoryName(projectPath)!, "segments.json"), JsonSerializer.Serialize(segments, Json));
        EditorHint.Text = $"已保存 {segments.Count} 句 · {DateTime.Now:HH:mm:ss}";
        return true;
    }
    private async Task Render()
    {
        if (voiceEditor.HasEditableTimeline) { await GenerateTimelineVoice(null, export: true); return; }
        if (busy || !ReadyToRender() || !EnsurePython() || !SaveSegments()) return;
        if (!voiceEditor.SavePositions()) return;
        var p = Project();
        // Keep source and output location fixed for the open project; update only rendering options.
        foreach (var key in new[] { "voice", "mode", "device", "dub", "background", "burn_subtitles", "chars_per_second", "subtitle_max_chars", "sentence_alignment", "alignment_tolerance", "voice_style", "voice_consistency", "voice_seed", "voice_temperature", "voice_top_p", "voice_repetition_penalty", "voice_speed", "voice_volume", "voice_model" })
            p["settings"]![key] = Settings()[key]?.DeepClone();
        AtomicWrite(projectPath!, p.ToJsonString(Json));
        await Run(Python, ["-u", Engine, "render", projectPath!], true);
    }
    private async Task ExportTimeline()
    {
        if (busy || projectPath == null || (!voiceEditor.HasVideo && !voiceEditor.HasClips && !voiceEditor.HasEditableTimeline) || !EnsurePython()) return;
        var useVoice = (voiceEditor.HasClips || voiceEditor.HasEditableTimeline) && (Project()["settings"]?["dub"]?.GetValue<bool>() ?? true);
        if (useVoice && voiceEditor.PendingClips > 0) { StatusText.Text = $"保留区间还有 {voiceEditor.PendingClips} 段待生成，请右键片段生成语音后再导出。"; return; }
        if ((segments.Count > 0 && !SaveSegments(allowEmptyText: !useVoice)) || !voiceEditor.SavePositions()) return;
        var p = Project();
        foreach (var key in new[] { "burn_subtitles", "subtitle_max_chars", "voice_volume" })
            p["settings"]![key] = Settings()[key]?.DeepClone();
        AtomicWrite(projectPath, p.ToJsonString(Json));
        var command = useVoice ? "remix" : "cut-export";
        await Run(Python, ["-u", Engine, command, projectPath], true);
    }
    private async void RegenerateAllVoices_Click(object sender, RoutedEventArgs e)
    {
        if (busy || projectPath == null || !voiceEditor.HasClips || !EnsurePython() || !SaveCurrentEditor()) return;
        voiceEditor.PauseForEditing();
        var options = VoiceBox.Items.Cast<ComboBoxItem>()
            .Select(item => new ClipVoiceOption(item.Tag?.ToString() ?? "", item.Content?.ToString() ?? "")).ToList();
        var dialog = new RegenerateVoicesWindow(voiceModel, Selected(ModeBox), Selected(VoiceBox), options, voiceEditor.ClipCount) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try {
            if (!voiceEditor.PrepareAllVoiceRegeneration(dialog.PreserveVoices ? null : dialog.DefaultVoice)) return;
            voiceModel = dialog.Model; Select(VoiceBox, dialog.DefaultVoice); DubBox.IsChecked = true;
            await GenerateTimelineVoice(null);
        }
        catch (Exception ex) { StatusText.Text = "无法开始批量配音：" + ex.Message; }
    }

    private async Task GenerateTimelineVoice(int? id, bool export = false)
    {
        if (busy || projectPath == null || !EnsurePython() || !SaveCurrentEditor()) return;
        var p = Project();
        foreach (var key in new[] { "voice", "mode", "device", "burn_subtitles", "subtitle_max_chars", "voice_style", "voice_consistency", "voice_seed", "voice_temperature", "voice_top_p", "voice_repetition_penalty", "voice_speed", "voice_volume", "voice_model" })
            p["settings"]![key] = Settings()[key]?.DeepClone();
        p["settings"]!["dub"] = true;
        AtomicWrite(projectPath, p.ToJsonString(Json));
        List<string> args = ["-u", Engine, export ? "voice-render" : "voice-generate", projectPath];
        if (id.HasValue) args.AddRange(["--clip-id", id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var success = await Run(Python, args, true);
        if (success && id.HasValue) voiceEditor.SelectAndSeek(id.Value);
    }
    private bool SaveCurrentEditor()
    {
        if (!voiceEditor.SavePositions()) return false;
        if (projectPath == null || segments.Count == 0) return true;
        try { return SaveSegments(allowEmptyText: true); }
        catch (Exception ex) { MessageBox.Show("保存当前项目失败：" + ex.Message); return false; }
    }
    private void RateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        foreach (var segment in segments) segment.TargetRate = e.NewValue;
    }
    private void RateSlower_Click(object sender, RoutedEventArgs e) { if (!busy) RateSlider.Value -= .5; }
    private void RateFaster_Click(object sender, RoutedEventArgs e) { if (!busy) RateSlider.Value += .5; }
    private async void Retime_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ReadyToRender() || !EnsurePython() || !SaveSegments()) return;
        var p = Project();
        foreach (var key in new[] { "chars_per_second", "subtitle_max_chars", "mode", "device", "glossary", "voice_style", "voice_consistency", "voice_seed", "voice_temperature", "voice_top_p", "voice_repetition_penalty", "voice_speed", "voice_model" }) p["settings"]![key] = Settings()[key]?.DeepClone();
        AtomicWrite(projectPath!, p.ToJsonString(Json));
        AtomicWrite(Path.Combine(root, "settings.json"), Settings().ToJsonString(Json));
        await Run(Python, ["-u", Engine, "retime", projectPath!], true);
        LoadSegments();
    }
    private async void Resegment_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ReadyToRender() || !EnsurePython() || !SaveSegments()) return;
        var p = Project();
        foreach (var key in new[] { "chars_per_second", "subtitle_max_chars", "mode", "device", "glossary", "voice_style", "voice_consistency", "voice_seed", "voice_temperature", "voice_top_p", "voice_repetition_penalty", "voice_speed", "voice_model" }) p["settings"]![key] = Settings()[key]?.DeepClone();
        AtomicWrite(projectPath!, p.ToJsonString(Json));
        await Run(Python, ["-u", Engine, "resegment", projectPath!], true);
        LoadSegments();
    }
    private async Task Install(string mode)
    {
        StatusText.Text = "正在安装环境，可在运行日志中查看下载进度。";
        EnvironmentText.Text = "安装进行中。首次下载较大，关闭窗口会停止安装；再次安装可复用已下载组件。";
        if (await Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "setup.ps1"), "-Mode", mode, "-Root", root], false)) {
            StatusText.Text = "环境已安装。"; await CheckEnvironment();
        }
    }
    private async Task CheckEnvironment() { if (!busy && EnsurePython()) await Run(Python, ["-u", Engine, "check"], true); }
    private void Cancel()
    {
        if (!busy || active == null) return;
        cancelling = true; StatusText.Text = "正在停止进程并释放显存…"; UpdateControls();
        activeJob?.Dispose(); activeJob = null;
        try { active.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } catch (Win32Exception) { }
    }
    private void Studio_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    private void Editor_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 1;
    private void Environment_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    private async void LoginSettings_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 2;
        await Dispatcher.InvokeAsync(() => EnvironmentCookieSourceBox.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void Paste_Click(object sender, RoutedEventArgs e) { if (!busy && Clipboard.ContainsText()) SourceBox.Text = Clipboard.GetText().Trim(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "视频文件|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.FileName;
    }
    private void Output_Click(object sender, RoutedEventArgs e) { if (busy) return; var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName; }
    private async void Start_Click(object sender, RoutedEventArgs e) => await StartPipeline(false);
    private async void Download_Click(object sender, RoutedEventArgs e) => await StartPipeline(true);
    private async void Render_Click(object sender, RoutedEventArgs e) => await Render();
    private void Save_Click(object sender, RoutedEventArgs e) { if (!busy) SaveCurrentEditor(); }
    private async void Install_Click(object sender, RoutedEventArgs e) => await Install("Full");
    private async void InstallCore_Click(object sender, RoutedEventArgs e) => await Install("Core");
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckEnvironment();
    private async void Models_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !EnsurePython()) return;
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        var path = Path.Combine(root, "artifacts", "models-request.json");
        AtomicWrite(path, Settings().ToJsonString(Json));
        await Run(Python, ["-u", Engine, "models", path], true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Cookies_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "Cookie 文本文件|*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { CookiesBox.Text = dialog.FileName; Select(CookieSourceBox, "file"); }
    }
    private void ClearCookies_Click(object sender, RoutedEventArgs e) { if (!busy) { CookiesBox.Text = ""; if (Selected(CookieSourceBox) == "file") Select(CookieSourceBox, "none"); } }
    private void ImportCookies_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        new CookieImportWindow(text => ImportCookieText(text)) { Owner = this }.ShowDialog();
    }
    internal int ImportCookieText(string text, string? directory = null)
    {
        if (busy) throw new InvalidOperationException("请等待当前处理结束。");
        var converted = CookieConverter.Convert(text);
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCN", "cookies");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "imported.txt");
        AtomicWrite(path, converted.Text);
        CookiesBox.Text = path;
        Select(CookieSourceBox, "file");
        AtomicWrite(Path.Combine(root, "settings.json"), Settings().ToJsonString(Json));
        StatusText.Text = LoginStatusText.Text = $"已转换 {converted.Count} 条 Cookie，并切换为 Cookie 文件模式。可以测试登录或重新下载。";
        Log(StatusText.Text);
        return converted.Count;
    }
    private async void TestLogin_Click(object sender, RoutedEventArgs e)
        => await TestLogin();
    private async Task<bool> TestLogin()
    {
        if (closing || App.IsShuttingDown) return false;
        if (busy || !EnsurePython()) return false;
        if (Selected(CookieSourceBox) is not ("edge" or "file")) { StatusText.Text = LoginStatusText.Text = "请先选择 Edge 登录状态或 Cookie 文件。"; return false; }
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) { StatusText.Text = LoginStatusText.Text = "请先在工作台粘贴要下载的 YouTube 视频网址。"; return false; }
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        var settings = Settings().ToJsonString(Json);
        AtomicWrite(Path.Combine(root, "settings.json"), settings);
        var path = Path.Combine(root, "artifacts", "login-request.json");
        AtomicWrite(path, settings);
        LoginStatusText.Text = "正在使用所选登录方式测试视频访问…";
        var ok = await Run(Python, ["-u", Engine, "check-login", path], true);
        if (!ok) StatusText.Text = LoginStatusText.Text = lastError ?? "登录测试已停止，请查看日志。";
        return ok;
    }
    private async void StopEdge_Click(object sender, RoutedEventArgs e) => await StopEdgeAndTest();
    internal async Task<object> StopEdgeAndTest()
    {
        if (busy) return new { Skipped = true };
        Select(CookieSourceBox, "edge");
        int stopped;
        busy = stoppingEdge = true; UpdateControls();
        StatusText.Text = "正在结束 Edge 后台进程…";
        try {
            stopped = await EdgeProcesses.StopBackgroundAsync();
            Log($"已结束 {stopped} 个 Edge 后台进程，即将重新测试登录。");
        } catch (Exception ex) {
            StatusText.Text = LoginStatusText.Text = ex.Message; Log(ex.Message);
            return new { Stopped = false, Message = ex.Message };
        } finally { busy = stoppingEdge = false; UpdateControls(); }
        var ok = await TestLogin();
        return new { Stopped = true, ProcessCount = stopped, LoginPassed = ok, Message = LoginStatusText.Text };
    }
    private async void UpdateDownloader_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var exe = Path.Combine(root, "tools", "yt-dlp.exe");
        if (!File.Exists(exe)) { EnvironmentText.Text = "请先安装下载工具。"; return; }
        if (await Run(exe, ["--ignore-config", "-U"], false)) StatusText.Text = "下载器更新检查完成。";
    }
    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (busy || projectPath == null || !EnsurePython()) return;
        if (ReadyToRender()) { Pages.SelectedIndex = 1; StatusText.Text = "已恢复编辑页，可继续调整字幕或配音。"; return; }
        var p = Project();
        foreach (var key in new[] { "cookies", "cookie_source", "edge_profile", "device", "glossary", "chars_per_second", "subtitle_max_chars", "voice_style", "voice_consistency", "voice_seed", "voice_temperature", "voice_top_p", "voice_repetition_penalty", "voice_speed", "voice_model" }) p["settings"]![key] = Settings()[key]?.DeepClone();
        AtomicWrite(projectPath, p.ToJsonString(Json));
        await Run(Python, ["-u", Engine, "resume", projectPath], true);
    }
    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "VideoCN 项目 (project.json)|project.json", InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : root };
        if (dialog.ShowDialog(this) != true) return;
        try { if (SaveCurrentEditor()) LoadProjectFile(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show("无法打开项目：" + ex.Message); }
    }
    private void LoadProjectFile(string path)
    {
        var p = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (p["version"]?.GetValue<int>() != 1 || p["settings"] is not JsonObject || p["completed"] is not JsonArray) throw new InvalidDataException("不是受支持的 VideoCN 项目。");
        var previousPath = projectPath;
        var previousSegments = segments;
        var previousSettings = Settings();
        var previousView = EditorViews.SelectedIndex;
        try {
            ApplySettings(p["settings"]!.AsObject(), includeLogin: false);
            projectPath = path; segments = []; SubtitleGrid.ItemsSource = segments;
            voiceEditor.LoadProject(projectPath);
            LoadSegments();
            EditorViews.SelectedIndex = voiceEditor.HasClips || voiceEditor.HasVideo ? 0 : 1;
            foreach (var stage in stages) stage.Mark = p["completed"]!.AsArray().Any(c => c?.ToString() == stage.Key) ? "✓" : "○";
            VideoTitle.Text = p["title"]?.ToString() ?? "项目已打开";
            StatusText.Text = "已打开项目，可继续处理或校对字幕。";
            if (ReadyToRender() || voiceEditor.HasVideo) Pages.SelectedIndex = 1;
            UpdateControls();
        }
        catch {
            // A malformed old project must never receive the previous project's subtitles.
            projectPath = previousPath; segments = previousSegments; SubtitleGrid.ItemsSource = segments;
            ApplySettings(previousSettings); voiceEditor.LoadProject(previousPath);
            EditorViews.SelectedIndex = previousView; UpdateControls();
            throw;
        }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = projectPath == null ? OutputBox.Text : Path.GetDirectoryName(projectPath)!;
        Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (busy || projectPath == null || SubtitleGrid.SelectedItem is not Segment selected) { EditorHint.Text = "请先选中一句字幕。"; return; }
        var source = VoiceEditorWorkspace.ResolveMedia(Path.GetDirectoryName(projectPath)!, Project()["video"]?.ToString());
        if (source == null || !File.Exists(source)) { MessageBox.Show("找不到原视频，请确认它没有被移动。"); return; }
        new PreviewWindow(source, selected.Start, selected.End, Path.GetDirectoryName(projectPath), Project()["duration"]?.GetValue<double>() ?? 0) { Owner = this }.Show();
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Python)) EnvironmentText.Text = "首次使用：请安装完整环境，然后下载工作台所选档位的模型。";
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 2 && args[1] == "--smoke-test") {
            await Task.Delay(350);
            await SmokeTest.Run(this, args[2]);
            Close();
        } else {
            if (args.Length > 2 && args[1] == "--project") {
                try { LoadProjectFile(args[2]); }
                catch (Exception ex) { MessageBox.Show("无法打开项目：" + ex.Message); }
            }
            if (File.Exists(Python)) await CheckEnvironment();
        }
    }
    internal async Task<object> VerifyProcessBoundary(string directory)
    {
        if (!File.Exists(Python)) return new { Skipped = true, Reason = "Python unavailable" };
        var checkedOk = await Run(Python, ["-u", Engine, "check"], true);
        var pidPath = Path.Combine(directory, "fixture-child.pid");
        var fixture = Path.Combine(root, "tests", "child_process_fixture.py");
        var cancellationTask = Run(Python, ["-u", fixture, pidPath], false);
        for (int i = 0; i < 100 && !File.Exists(pidPath); i++) await Task.Delay(50);
        if (!File.Exists(pidPath)) throw new InvalidOperationException("Cancellation test child did not start.");
        var pid = int.Parse(File.ReadAllText(pidPath));
        Cancel();
        var cancelledResult = await cancellationTask;
        await Task.Delay(200);
        bool childStopped;
        try { using var child = Process.GetProcessById(pid); childStopped = child.HasExited; }
        catch (ArgumentException) { childStopped = true; }
        if (!checkedOk || !childStopped || cancelledResult) throw new InvalidOperationException($"Process boundary smoke test failed: check={checkedOk}, childStopped={childStopped}, cancelledResult={cancelledResult}.");
        return new { EnvironmentCheck = checkedOk, CancelReturnedFalse = !cancelledResult, ChildProcessStopped = childStopped };
    }
    internal object VerifySubtitleEditor(string inputProject, string directory)
    {
        var previousSettings = Settings();
        var copyFolder = Path.Combine(directory, "editor-project");
        Directory.CreateDirectory(copyFolder);
        var copy = Path.Combine(copyFolder, "project.json");
        File.Copy(inputProject, copy, true);
        File.Copy(Path.Combine(Path.GetDirectoryName(inputProject)!, "segments.json"), Path.Combine(copyFolder, "segments.json"), true);
        var inputLayout = Path.Combine(Path.GetDirectoryName(inputProject)!, "speech_regions.json");
        if (File.Exists(inputLayout)) File.Copy(inputLayout, Path.Combine(copyFolder, "speech_regions.json"), true);
        var inputTiming = Path.Combine(Path.GetDirectoryName(inputProject)!, "dub_timing.json");
        if (File.Exists(inputTiming)) File.Copy(inputTiming, Path.Combine(copyFolder, "dub_timing.json"), true);
        var oldProject = JsonNode.Parse(File.ReadAllText(copy))!.AsObject();
        oldProject["settings"]!["cookie_source"] = "none";
        AtomicWrite(copy, oldProject.ToJsonString(Json));
        Select(CookieSourceBox, "edge");
        LoadProjectFile(copy);
        if (Selected(CookieSourceBox) != "edge" || Selected(EnvironmentCookieSourceBox) != "edge")
            throw new InvalidOperationException("Opening an old project changed the login mode or selectors did not synchronize.");
        Select(EnvironmentCookieSourceBox, "file");
        if (Selected(CookieSourceBox) != "file") throw new InvalidOperationException("Login selectors did not synchronize back to the workbench.");
        Select(CookieSourceBox, "edge");
        if (!RenderButton.IsEnabled || segments.Count == 0) throw new InvalidOperationException("Editor did not restore a translated project.");
        var expected = segments[0].Zh + " 测试修改。";
        var expectedBlock = segments[0].SpeechBlock;
        var expectedDuration = segments[0].SpeechDuration;
        var expectedWords = segments[0].Extra?.GetValueOrDefault("words").GetRawText();
        segments[0].Zh = expected;
        segments[0].AudioStart = 0.25;
        RateSlider.Value = 5;
        if (Settings()["chars_per_second"]!.GetValue<double>() != 5 || segments[0].TargetRate != 5)
            throw new InvalidOperationException("Speech rate did not update settings and row budget.");
        SentenceAlignmentBox.IsChecked = false;
        Select(ToleranceBox, "1.5");
        if (Settings()["sentence_alignment"]!.GetValue<bool>() || Settings()["alignment_tolerance"]!.GetValue<double>() != 1.5)
            throw new InvalidOperationException("Sentence alignment settings were not captured.");
        SentenceAlignmentBox.IsChecked = true;
        if (!SaveSegments()) throw new InvalidOperationException("Editor save failed.");
        LoadProjectFile(copy);
        if (segments[0].Zh != expected) throw new InvalidOperationException("Subtitle changes did not survive reopening.");
        if (segments[0].AudioStart != 0.25) throw new InvalidOperationException("Audio position did not survive reopening.");
        if (segments[0].SpeechBlock != expectedBlock || segments[0].SpeechDuration != expectedDuration ||
            !JsonNode.DeepEquals(JsonNode.Parse(segments[0].Extra?.GetValueOrDefault("words").GetRawText() ?? "null"), JsonNode.Parse(expectedWords ?? "null")))
            throw new InvalidOperationException("Speech metadata did not survive editor saving.");
        var count = segments.Count;
        ApplySettings(previousSettings);
        return new { RestoredRows = count, SaveAndReopen = true, SpeechMetadataPreserved = expectedBlock.HasValue,
            LatestTimingVisible = File.Exists(inputTiming) && segments.Any(s => s.AudioTiming != "尚未导出"),
            LoginPreserved = true, LoginSelectorsSynchronized = true, RenderEnabledAfterRestore = RenderButton.IsEnabled };
    }
    internal async Task<object> VerifyCookieImport(string directory)
    {
        var previous = Settings();
        var target = Path.Combine(directory, "synthetic-cookies");
        var dialog = new CookieImportWindow(text => ImportCookieText(text, target)) { Owner = this };
        try {
            dialog.Show();
            var input = (TextBox)dialog.FindName("RawText");
            var convert = (Button)dialog.FindName("ConvertButton");
            input.Text = "Name\tValue\tDomain\tPath\tExpires / Max-Age\tSize\tHttpOnly\tSecure\nSID\tSYNTHETIC_TEST_ONLY\t.youtube.com\t/\tSession\t20\t✓\t✓";
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            SmokeTest.Capture(dialog, Path.Combine(directory, "cookie-import.png"));
            convert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var file = Path.Combine(target, "imported.txt");
            if (dialog.IsVisible || !File.Exists(file) || Selected(CookieSourceBox) != "file" || Selected(EnvironmentCookieSourceBox) != "file")
                throw new InvalidOperationException("Cookie import did not save and select file mode.");
            var saved = File.ReadAllText(file);
            if (!saved.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tSYNTHETIC_TEST_ONLY") || LogBox.Text.Contains("SYNTHETIC_TEST_ONLY"))
                throw new InvalidOperationException("Cookie output or log privacy test failed.");
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(root, "settings.json")))!["cookies"]!.ToString() != file)
                throw new InvalidOperationException("Imported Cookie selection was not persisted.");
            try { ImportCookieText("not a cookie table", target); throw new InvalidOperationException("Invalid import succeeded."); }
            catch (FormatException) { }
            if (File.ReadAllText(file) != saved) throw new InvalidOperationException("Invalid input overwrote the usable Cookie file.");
            return new { DialogButtonSavedFile = true, SelectedFileMode = true, SelectionPersisted = true, InvalidInputPreservedFile = true, CookieValuesAbsentFromLogs = true };
        } finally {
            dialog.Close(); ApplySettings(previous);
            AtomicWrite(Path.Combine(root, "settings.json"), previous.ToJsonString(Json));
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (!busy && !SaveCurrentEditor()) { e.Cancel = true; return; }
        closing = true;
        try { AtomicWrite(Path.Combine(root, "settings.json"), Settings().ToJsonString(Json)); } catch { }
        try {
            voiceEditor.Shutdown();
            if (busy) Cancel();
        }
        catch (Exception ex) { Trace.WriteLine("VideoCN shutdown cleanup: " + ex.Message); }
        finally {
            // This also stops probe/transcode/volume preview jobs outside the main worker.
            App.BeginShutdown();
        }
    }
}
