using System.Windows;
using System.Windows.Controls;

namespace VideoCN;

public sealed class BackgroundRangeWindow : Window
{
    public BackgroundRange Range { get; }
    public BackgroundRangeWindow(BackgroundRange range)
    {
        Range = range.Copy(); Title = "调整背景音轨"; Width = 480; Height = Math.Min(540, SystemParameters.WorkArea.Height - 60); MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(20) };
        TextBox Number(string label, double value) {
            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3) });
            var box = new TextBox { Text = value.ToString("0.###") }; root.Children.Add(box); return box;
        }
        var start = Number("开始 / 秒", Range.Start); var end = Number("结束 / 秒", Range.End);
        var gain = Number("背景音量 / %（0 为静音，中文配音不受影响）", Range.Gain * 100);
        var fade = Number("边界淡入淡出 / 秒（0–0.5）", Range.Fade);
        var mode = new ComboBox { Margin = new Thickness(0, 12, 0, 8), SelectedIndex = Range.Mode == "separated" ? 1 : 0 };
        mode.Items.Add("沿用原背景策略（空挡保留原声）"); mode.Items.Add("强制使用分离背景（空挡也不恢复原声）");
        root.Children.Add(mode);
        var hint = new TextBlock { Text = "音量会同时影响该范围内的音乐与环境声。", TextWrapping = TextWrapping.Wrap }; root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "保存调整" };
        save.Click += (_, _) => {
            if (!double.TryParse(start.Text, out var s) || !double.TryParse(end.Text, out var e) || !double.TryParse(gain.Text, out var g) || !double.TryParse(fade.Text, out var f)
                || !new[] { s, e, g, f }.All(double.IsFinite) || s < 0 || e <= s || g < 0 || g > 100 || f < 0 || f > .5) {
                hint.Text = "请检查起止秒数、0–100% 音量和 0–0.5 秒淡入淡出。"; return;
            }
            Range.Start = s; Range.End = e; Range.Gain = g / 100; Range.Fade = f; Range.Mode = mode.SelectedIndex == 1 ? "separated" : "auto";
            DialogResult = true;
        };
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) });
        root.Children.Add(buttons); Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
