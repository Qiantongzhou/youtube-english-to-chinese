using System.Windows;
using System.Windows.Controls;

namespace VideoCN;

public sealed class RegenerateVoicesWindow : Window
{
    private readonly ComboBox model = new() { SelectedValuePath = "Tag" };
    private readonly ComboBox voice = new() { MaxDropDownHeight = 330 };
    private readonly CheckBox preserve = new() { Content = "保留每段已指定的音色（多人对话）", IsChecked = true, Margin = new Thickness(0, 14, 0, 8) };
    public string Model => model.SelectedValue?.ToString() ?? "auto";
    public string DefaultVoice => (voice.SelectedItem as ClipVoiceOption)?.Id ?? "Serena";
    public bool PreserveVoices => preserve.IsChecked == true;

    public RegenerateVoicesWindow(string selectedModel, string mode, string defaultVoice,
        IReadOnlyList<ClipVoiceOption> voices, int count)
    {
        Title = "切换模型并重新生成全部语音";
        Width = 620; Height = 520; MinWidth = 520; MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var start = new Button { Content = "开始重新生成全部语音", Margin = new Thickness(0, 0, 10, 0) };
        start.Click += (_, _) => { if (model.SelectedItem != null && voice.SelectedItem != null) DialogResult = true; };
        buttons.Children.Add(start); buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = $"重新生成 {count} 段中文语音", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        form.Children.Add(new TextBlock { Text = "本地语音模型", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        model.Items.Add(new ComboBoxItem { Content = $"跟随制作质量（当前 {(mode == "quick" ? "0.6B" : "1.7B")}）", Tag = "auto" });
        model.Items.Add(new ComboBoxItem { Content = "Qwen3-TTS 0.6B · 轻量模型", Tag = "0.6B" });
        model.Items.Add(new ComboBoxItem { Content = "Qwen3-TTS 1.7B · 较大模型", Tag = "1.7B" });
        model.SelectedValue = selectedModel;
        if (model.SelectedItem == null) model.SelectedIndex = 0;
        form.Children.Add(model);
        form.Children.Add(new TextBlock { Text = "默认音色", FontSize = 12, Margin = new Thickness(0, 14, 0, 6) });
        foreach (var option in voices.Where(v => v.Id.Length > 0)) voice.Items.Add(option);
        voice.SelectedItem = voice.Items.Cast<ClipVoiceOption>().FirstOrDefault(v => v.Id == defaultVoice);
        if (voice.SelectedItem == null && voice.Items.Count > 0) voice.SelectedIndex = 0;
        form.Children.Add(voice); form.Children.Add(preserve);
        var scope = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        void DescribeScope() => scope.Text = PreserveVoices
            ? "默认音色用于跟随制作偏好的片段；各人物已单独指定的音色保持不变。"
            : "全部片段统一改用上方音色，人物名称仍保留。";
        preserve.Checked += (_, _) => DescribeScope(); preserve.Unchecked += (_, _) => DescribeScope();
        DescribeScope(); form.Children.Add(scope);
        form.Children.Add(new TextBlock { Text = "保留中文文案、每段开始位置、视频剪切和背景音设置。新语音可能改变片段时长，仍允许重叠。生成完成后可试听，再按时间轴重新导出视频。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        form.Children.Add(new TextBlock { Text = "所有片段均重新生成，包括已剪掉的视频区间中的片段。旧录音保留，并备份本次操作前的时间轴；中断后未完成片段显示待生成。未缓存的本地模型会联网下载。微软在线音色由微软服务合成，文案会发送到微软，本地模型选择不影响这些音色。",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray });
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
    }
}
