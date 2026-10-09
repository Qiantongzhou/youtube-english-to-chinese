using System.IO;
using System.Text.Json.Nodes;

namespace VideoCN;

public sealed record VideoSection(int Id, double Start, double End, bool Deleted = false);

/// <summary>Non-destructive cuts in source coordinates, shared by all media tracks.</summary>
public sealed class VideoEditSession
{
    public List<VideoSection> Sections { get; private set; } = [];
    public bool CanUndo => undo.Count > 0;
    public bool HasCuts => Sections.Any(s => s.Deleted);
    public bool Valid { get; private set; } = true;
    private readonly Stack<List<VideoSection>> undo = new();
    private string? path;
    private double sourceDuration;
    private bool dirty;
    public event Action? Changed;
    public event Action<string>? Message;

    public void Load(string? projectPath, double duration)
    {
        path = projectPath == null ? null : Path.Combine(Path.GetDirectoryName(projectPath)!, "video_edit.json");
        sourceDuration = duration; dirty = false; undo.Clear(); Valid = true;
        Sections = duration > 0 ? [new VideoSection(1, 0, duration)] : [];
        if (path == null || !File.Exists(path)) return;
        try {
            var data = JsonNode.Parse(File.ReadAllText(path))!;
            var storedDuration = data["source_duration"]!.GetValue<double>();
            if (data["version"]?.GetValue<int>() != 1 || !double.IsFinite(storedDuration) || Math.Abs(storedDuration - duration) > .05)
                throw new InvalidDataException("剪切记录与原视频时长不匹配。");
            var sections = data["segments"]!.AsArray().Select(n => new VideoSection(n!["id"]!.GetValue<int>(),
                n["start"]!.GetValue<double>(), n["end"]!.GetValue<double>(), n["deleted"]!.GetValue<bool>())).ToList();
            var end = 0.0; var ids = new HashSet<int>();
            foreach (var s in sections) {
                if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.Id <= 0 || !ids.Add(s.Id)
                    || Math.Abs(s.Start - end) > .00001 || s.End <= s.Start || s.End > duration + .00001)
                    throw new InvalidDataException("视频剪切记录的区间无效。");
                end = s.End;
            }
            if (Math.Abs(end - duration) > .00001 || !sections.Any(s => !s.Deleted))
                throw new InvalidDataException("视频剪切记录必须覆盖原视频并保留至少一段。");
            Sections = sections;
        }
        catch { Valid = false; Sections = []; throw; }
    }

    public void Split(double at)
    {
        if (!Valid || path == null) return;
        at = Math.Round(at, 3);
        var index = Sections.FindIndex(s => !s.Deleted && at >= s.Start + .05 && at <= s.End - .05);
        if (index < 0) { Message?.Invoke("请在保留的视频片段内部切开，距离边缘至少 0.05 秒。"); return; }
        var copy = Sections.ToList(); var section = copy[index];
        copy[index] = section with { End = at };
        copy.Insert(index + 1, new VideoSection(copy.Max(s => s.Id) + 1, at, section.End));
        Commit(copy, "已切开视频；右键紫色视频片段可以删除。");
    }

    public void SetDeleted(int id, bool deleted)
    {
        if (!Valid || path == null) return;
        if (!Sections.Any(s => s.Id == id && s.Deleted != deleted)) return;
        var copy = Sections.Select(s => s.Id == id ? s with { Deleted = deleted } : s).ToList();
        if (copy.All(s => s.Deleted)) { Message?.Invoke("至少需要保留一个视频片段；可先切开再删除其中一段。"); return; }
        Commit(copy, deleted ? "已删除视频区间。预览跳过、导出拼接；配音和背景同步裁剪。时间轴保留原片时间，灰色区间可右键恢复。" : "已恢复视频片段及该区间的音频。");
    }

    private void Commit(List<VideoSection> next, string message)
    {
        var previous = Sections; Sections = next; dirty = true;
        if (!Save()) { Sections = previous; dirty = false; return; }
        undo.Push(previous); Changed?.Invoke(); Message?.Invoke(message);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var previous = Sections; Sections = undo.Peek(); dirty = true;
        if (!Save()) { Sections = previous; dirty = false; return; }
        undo.Pop(); Changed?.Invoke(); Message?.Invoke("已撤销上一步视频剪切。");
    }

    public bool Save()
    {
        if (!Valid) { Message?.Invoke("剪切记录读取失败，请修复 video_edit.json 后重新打开项目，避免覆盖记录。"); return false; }
        if (!dirty || path == null) return true;
        try {
            var rows = new JsonArray();
            foreach (var s in Sections) rows.Add(new JsonObject { ["id"] = s.Id, ["start"] = s.Start, ["end"] = s.End, ["deleted"] = s.Deleted });
            var document = new JsonObject { ["version"] = 1, ["source_duration"] = sourceDuration, ["segments"] = rows };
            File.WriteAllText(path + ".tmp", document.ToJsonString(new() { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".previous", true);
            File.Move(path + ".tmp", path, true); dirty = false; return true;
        }
        catch (Exception ex) { Message?.Invoke("剪切保存失败：" + ex.Message); return false; }
    }

    public IEnumerable<(double Start, double End)> Kept(double total)
    {
        if (Sections.Count == 0) { if (Valid && total > 0) yield return (0, total); yield break; }
        foreach (var s in Sections.Where(s => !s.Deleted))
            yield return (s.Start, s.End == sourceDuration ? Math.Max(s.End, total) : s.End);
    }

    public double OutputTime(double source, double total) => Kept(total).Sum(s => Math.Clamp(source - s.Start, 0, s.End - s.Start));
    public double OutputDuration(double total) => Kept(total).Sum(s => s.End - s.Start);
    public bool IntersectsKept(double start, double end, double total) => Kept(total).Any(s => start < s.End && end > s.Start);
    public double SourceTime(double output, double total)
    {
        foreach (var s in Kept(total)) {
            if (output < s.End - s.Start) return s.Start + Math.Max(0, output);
            output -= s.End - s.Start;
        }
        return total;
    }
    public double Advance(double start, double elapsed, double total) => SourceTime(OutputTime(start, total) + elapsed, total);
    public bool CrossesCut(double from, double to) => Sections.Any(s => s.Deleted && from < s.End && to >= s.End && to > s.Start);
}
