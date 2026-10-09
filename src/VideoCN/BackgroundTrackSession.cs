using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace VideoCN;

public sealed class BackgroundRange
{
    public int Id { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Gain { get; set; } = 1;
    public double Fade { get; set; } = .08;
    public string Mode { get; set; } = "auto";
    public BackgroundRange Copy() => (BackgroundRange)MemberwiseClone();
    public string Label => Gain == 0 ? "背景静音" : $"{(Mode == "separated" ? "强制分离背景" : "背景音量")} {Gain:P0}";
}

public sealed class BackgroundTrackSession
{
    private string? folder, originalPath, stemPath;
    private bool hasDocument;
    private double duration;
    private List<(double Start, double End)> gaps = [];
    private readonly Stack<(bool Enabled, List<BackgroundRange> Ranges)> undo = new();
    private readonly List<(MediaPlayer Player, bool Original, TaskCompletionSource<bool> Ready)> players = [];
    private readonly HashSet<MediaPlayer> started = [];
    public List<BackgroundRange> Ranges { get; private set; } = [];
    public bool Enabled { get; private set; }
    public bool Available => stemPath != null;
    public bool CanUndo => undo.Count > 0;
    public event Action? Changed;
    public event Action<string>? Message;

    public void Load(string? projectPath)
    {
        Close(); folder = null; duration = 0; stemPath = originalPath = null; Ranges = []; gaps = []; undo.Clear(); hasDocument = false; Enabled = false;
        if (projectPath == null) return;
        folder = Path.GetDirectoryName(projectPath)!;
        var p = JsonNode.Parse(File.ReadAllText(projectPath))!;
        duration = p["duration"]?.GetValue<double>() ?? 0;
        originalPath = VoiceEditorWorkspace.ResolveMedia(folder, p["audio"]?.ToString(), "");
        stemPath = VoiceEditorWorkspace.ResolveMedia(folder, p["background"]?.ToString() ?? "no_vocals.wav", Path.Combine("stems", "htdemucs", "original"));
        Enabled = (p["settings"]?["background"]?.GetValue<bool>() ?? true) && p["background"] != null && Available;
        var layout = Path.Combine(folder, "speech_regions.json");
        if (p["speech_layout_version"]?.GetValue<int>() == 1 && File.Exists(layout))
            gaps = JsonNode.Parse(File.ReadAllText(layout))!["gaps"]!.AsArray().Select(n => (n!["start"]!.GetValue<double>(), n["end"]!.GetValue<double>())).ToList();
        var path = Path.Combine(folder, "background_edit.json");
        if (!File.Exists(path)) return;
        var data = JsonNode.Parse(File.ReadAllText(path))!;
        if (data["version"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的背景音编辑版本。");
        Enabled = data["enabled"]!.GetValue<bool>(); hasDocument = true;
        foreach (var n in data["ranges"]!.AsArray()) Ranges.Add(new BackgroundRange {
            Id = n!["id"]!.GetValue<int>(), Start = n["start"]!.GetValue<double>(), End = n["end"]!.GetValue<double>(),
            Gain = n["gain"]!.GetValue<double>(), Fade = n["fade"]!.GetValue<double>(), Mode = n["mode"]!.ToString()
        });
        foreach (var range in Ranges) Validate(range);
    }

    private void Snapshot() => undo.Push((Enabled, Ranges.Select(r => r.Copy()).ToList()));
    public void SetEnabled(bool value) { if (folder == null || value == Enabled) return; Snapshot(); Enabled = value; Commit(); }
    public bool Apply(BackgroundRange range)
    {
        try {
            Validate(range);
            Snapshot();
            if (range.Id == 0) range.Id = Ranges.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;
            Ranges.RemoveAll(r => r.Id == range.Id); Ranges.Add(range.Copy());
            Ranges = Ranges.OrderBy(r => r.Start).ToList(); Enabled = true;
            return Commit();
        }
        catch (Exception ex) { Message?.Invoke(ex.Message); return false; }
    }
    private void Validate(BackgroundRange range)
    {
        if (!new[] { range.Start, range.End, range.Gain, range.Fade }.All(double.IsFinite) || range.Start < 0 || range.End <= range.Start || range.End > duration + .001 || range.Gain < 0 || range.Gain > 1 || range.Fade < 0 || range.Fade > .5 || range.Mode is not ("auto" or "separated"))
            throw new InvalidDataException("背景音范围必须在原视频内，结束大于开始；音量为 0–100%，淡入淡出为 0–0.5 秒。");
        if (Ranges.Any(r => r.Id != range.Id && range.Start < r.End - .0001 && range.End > r.Start + .0001))
            throw new InvalidDataException("选区与已有背景调整重叠，请右键编辑原区域，或缩小选区。");
    }
    public void Delete(int id) { Snapshot(); Ranges.RemoveAll(r => r.Id == id); Commit(); }
    public void Undo() { if (undo.Count == 0) return; var old = undo.Pop(); Enabled = old.Enabled; Ranges = old.Ranges; Commit(); }
    private bool Commit() { hasDocument = true; Close(); var ok = Save(); Changed?.Invoke(); return ok; }

    public bool Save()
    {
        if (folder == null || !hasDocument) return true;
        try {
            var entries = new JsonArray();
            foreach (var r in Ranges) entries.Add(new JsonObject { ["id"] = r.Id, ["start"] = r.Start, ["end"] = r.End, ["gain"] = r.Gain, ["fade"] = r.Fade, ["mode"] = r.Mode });
            var json = new JsonObject { ["version"] = 1, ["enabled"] = Enabled, ["ranges"] = entries }.ToJsonString(new() { WriteIndented = true });
            var path = Path.Combine(folder, "background_edit.json");
            if (File.Exists(path) && File.ReadAllText(path) == json) return true;
            File.WriteAllText(path + ".tmp", json, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".previous", true);
            File.Move(path + ".tmp", path, true);
            Message?.Invoke("背景音轨调整已保存，重新导出后更新成品。"); return true;
        }
        catch (Exception ex) { Message?.Invoke("背景音调整保存失败：" + ex.Message); return false; }
    }

    public async Task PrepareAsync()
    {
        if (!Enabled) return;
        if (!Available) { Message?.Invoke("项目缺少分离背景，请先生成包含背景声的中文视频。"); return; }
        if (gaps.Count > 0 && originalPath == null) Message?.Invoke("原音轨 original.wav 缺失，空挡中的原背景暂时无法试听。");
        foreach (var (path, original) in new[] { (stemPath, false), (originalPath, true) }) {
            if (path == null || (original && gaps.Count == 0)) continue;
            var player = new MediaPlayer { Volume = 0 };
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            players.Add((player, original, ready));
            player.MediaOpened += (_, _) => ready.TrySetResult(true);
            player.MediaFailed += (_, e) => { if (ready.TrySetResult(false)) Message?.Invoke("背景音试听失败：" + e.ErrorException.Message); };
            try { player.Open(new Uri(path)); } catch (Exception ex) { ready.TrySetResult(false); Message?.Invoke("背景音试听失败：" + ex.Message); }
        }
        try { await Task.WhenAll(players.Select(p => p.Ready.Task)).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { Message?.Invoke("背景音加载超时，可暂停后重试。"); }
    }

    public void Update(double position, bool playing)
    {
        var raw = gaps.Any(g => position >= g.Start && position < g.End) ? 1.0 : 0;
        var stem = (1 - raw) * .65;
        foreach (var r in Ranges.Where(r => position >= r.Start && position < r.End)) {
            var fade = Math.Min(r.Fade, (r.End - r.Start) / 2);
            var blend = fade > 0 ? Math.Clamp(Math.Min((position - r.Start) / fade, (r.End - position) / fade), 0, 1) : 1;
            var wantedRaw = r.Mode == "auto" ? raw * r.Gain : 0;
            var wantedStem = r.Mode == "auto" ? stem * r.Gain : .65 * r.Gain;
            raw = raw * (1 - blend) + wantedRaw * blend; stem = stem * (1 - blend) + wantedStem * blend;
        }
        foreach (var (player, original, ready) in players) {
            if (!ready.Task.IsCompletedSuccessfully || !ready.Task.Result) continue;
            player.Volume = Enabled && position < duration ? (original ? raw : stem) : 0;
            if (playing && position < duration) {
                if (started.Add(player)) { player.Position = TimeSpan.FromSeconds(position); player.Play(); }
                else if (Math.Abs(player.Position.TotalSeconds - position) > .25) player.Position = TimeSpan.FromSeconds(position);
            } else player.Pause();
        }
    }
    public void Close()
    {
        foreach (var (player, _, ready) in players) { ready.TrySetResult(false); player.Close(); }
        players.Clear(); started.Clear();
    }
}
