using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoCN;

internal sealed record CookieConversion(string Text, int Count);

// Adapted from the user's Edge table converter; values never appear in diagnostics.
internal static class CookieConverter
{
    internal static CookieConversion Convert(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("请先粘贴 Edge Cookie 表格。");
        if (text.Length > 4_000_000) throw new FormatException("文本过大，请只复制需要的 Cookie 表格。");
        var output = new StringBuilder("# Netscape HTTP Cookie File\n# Converted locally by VideoCN\n\n");
        int count = 0, lineNumber = 0;
        // Rich-text copies mark trailing tabs as entities and may escape Markdown characters.
        // Detect only trailing tab entities: never HTML-decode a Cookie value.
        var richText = Regex.IsMatch(text, @"&#(?:x0*9|0*9);[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        bool wrappedPriorityAllowed = false;
        Dictionary<string, int>? header = null;
        foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n')) {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = Regex.Replace(raw.TrimEnd('\r'), @"(?:&#(?:x0*9|0*9);[ \t]*)+$", "\t", RegexOptions.IgnoreCase);
            if (wrappedPriorityAllowed && line.Trim() is "Low" or "Medium" or "High" or "低" or "中" or "高") {
                wrappedPriorityAllowed = false;
                continue;
            }
            wrappedPriorityAllowed = false;
            var markdown = line.TrimStart().StartsWith('|');
            var columns = markdown ? MarkdownColumns(line) : line.Split('\t');
            if (richText && !markdown) columns = columns.Select(c => Regex.Replace(c, @"\\([_:*|])", "$1")).ToArray();
            if (markdown && columns.All(c => Regex.IsMatch(c, @"^\s*:?-+:?\s*$"))) continue;
            string At(int index) => index < columns.Length ? columns[index] : "";
            if (columns.Any(c => Key(c) == "name") && columns.Any(c => Key(c) == "domain") && columns.Any(c => Key(c) == "value")) {
                header = new();
                for (int i = 0; i < columns.Length; i++) { var key = Key(columns[i]); if (key.Length > 0) header[key] = i; }
                if (new[] { "name", "value", "domain", "path", "expires" }.Any(k => !header.ContainsKey(k)))
                    throw Error(lineNumber, "表头需要包含名称、值、域、路径和过期时间列。");
                continue;
            }
            if (columns.Length < 5) throw Error(lineNumber, "未识别为表格行。请复制整个 Cookie 表格，保留列之间的制表符；也支持 Markdown 表格。");
            if (header != null && header.Values.Any(i => i >= columns.Length)) throw Error(lineNumber, "数据列少于表头，请保留末尾的空列并完整复制这一行。");
            string Cell(string key, int fallback) => header == null ? At(fallback) : header.TryGetValue(key, out var index) ? At(index) : "";
            var name = Cell("name", 0).Trim();
            var value = Cell("value", 1);
            var domain = Cell("domain", 2).Trim();
            var path = Cell("path", 3).Trim();
            if (path.Length == 0) path = "/";
            if (!Regex.IsMatch(domain, @"^\.?[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$")) throw Error(lineNumber, "域名无效，请检查是否完整复制了各列。");
            if (name.Length == 0 || name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '=' or ';')) throw Error(lineNumber, "Cookie 名称为空或格式无效。");
            if (!path.StartsWith('/') || new[] { value, path }.Any(s => s.Any(char.IsControl))) throw Error(lineNumber, "值或路径包含无效字符。");
            var expiry = Expiry(Cell("expires", 4), lineNumber);
            var httpOnly = Checked(Cell("httponly", 6), lineNumber);
            var secure = Checked(Cell("secure", 7), lineNumber);
            output.AppendJoin('\t', (httpOnly ? "#HttpOnly_" : "") + domain,
                domain.StartsWith('.') ? "TRUE" : "FALSE", path, secure ? "TRUE" : "FALSE",
                expiry.ToString(CultureInfo.InvariantCulture), name, value).Append('\n');
            count++;
            // Edge's Priority column is not part of Netscape. Accept a wrapped label only
            // after a complete row whose trailing optional columns are empty.
            wrappedPriorityAllowed = richText && !markdown && header == null && columns.Length >= 8 && columns.Skip(8).All(string.IsNullOrWhiteSpace);
        }
        if (count == 0) throw new FormatException("没有识别到 Cookie 数据行，请完整复制表格后重试。");
        return new(output.ToString(), count);
    }

    private static string[] MarkdownColumns(string raw)
    {
        var line = raw.Trim();
        if (line.EndsWith('|') && (line.Length < 2 || line[^2] != '\\')) line = line[..^1];
        line = line[1..];
        var cells = new List<string>(); var cell = new StringBuilder();
        for (int i = 0; i < line.Length; i++) {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && "_:*|\\".Contains(line[i + 1])) { cell.Append(line[++i]); continue; }
            if (c == '|') { cells.Add(cell.ToString().Trim()); cell.Clear(); } else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells.ToArray();
    }
    private static string Key(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"[\s/_-]", "") switch {
        "name" or "名称" => "name", "value" or "值" => "value",
        "domain" or "域" or "域名" => "domain", "path" or "路径" => "path",
        "expires" or "expiresmaxage" or "过期时间" or "过期最大存在时间" or "过期最大有效期" => "expires",
        "httponly" => "httponly", "secure" or "安全" => "secure", _ => ""
    };
    private static long Expiry(string value, int row)
    {
        value = value.Trim();
        if (value.ToLowerInvariant() is "" or "session" or "session cookie" or "会话" or "会话期" or "会话 cookie" or "会话cookie") return 0;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && unix <= 253402300799) return unix;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return Math.Max(1, date.ToUnixTimeSeconds());
        throw Error(row, "无法识别过期时间，请使用 ISO 日期、Unix 秒数或 Session / 会话。");
    }
    private static bool Checked(string value, int row) => value.Trim().ToLowerInvariant() switch {
        "✓" or "✔" or "true" or "yes" or "1" => true,
        "" or "false" or "no" or "0" or "-" => false,
        _ => throw Error(row, "HttpOnly 或 Secure 列无法识别，请检查列顺序。")
    };
    private static FormatException Error(int row, string message) => new($"第 {row} 行：{message}");
}
