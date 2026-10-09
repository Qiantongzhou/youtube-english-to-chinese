using System.Windows;
using System.Windows.Controls;

namespace VideoCN;

public sealed record ClipVoiceOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class ClipTextWindow : Window
{
    private readonly TextBox text;
    private readonly TextBox start;
    private readonly TextBox speaker;
    private readonly ComboBox voice;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    public string ChineseText => text.Text.Trim();
    public double StartSeconds { get; private set; }
    public bool GenerateSpeech { get; private set; }
    public string Voice => (voice.SelectedItem as ClipVoiceOption)?.Id ?? "";
    public string Speaker => speaker.Text.Trim();

    public ClipTextWindow(string initialText, double startSeconds, bool adding,
        IReadOnlyList<ClipVoiceOption> voices, string selectedVoice = "", string speakerName = "", bool chooseVoice = false)
    {
        Title = adding ? "新增中文配音片段" : chooseVoice ? "选择本段人物与音色" : "编辑中文配音片段";
        Width = 680; Height = 490; MinWidth = 540; MinHeight = 440;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var position = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        position.Children.Add(new TextBlock { Text = "开始 / 秒", VerticalAlignment = VerticalAlignment.Center });
        start = new TextBox { Text = startSeconds.ToString("F3"), Width = 110, Margin = new Thickness(12, 0, 0, 0) };
        position.Children.Add(start); root.Children.Add(position);
        position.Children.Add(new TextBlock { Text = "人物名（可选）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) });
        speaker = new TextBox { Text = speakerName, Width = 155, MaxLength = 40, ToolTip = "例如：主持人、嘉宾 A。用于时间轴标记，不会自动识别说话人。" };
        position.Children.Add(speaker);
        var voiceRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        voiceRow.Children.Add(new TextBlock { Text = "本段音色", FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
        voice = new ComboBox { MaxDropDownHeight = 330 };
        foreach (var option in voices) voice.Items.Add(option);
        if (voice.Items.Count == 0) voice.Items.Add(new ClipVoiceOption("", "跟随制作偏好"));
        voice.SelectedItem = voice.Items.Cast<ClipVoiceOption>().FirstOrDefault(v => v.Id == selectedVoice);
        if (voice.SelectedItem == null) {
            var existing = new ClipVoiceOption(selectedVoice, "已有音色：" + selectedVoice);
            voice.Items.Add(existing); voice.SelectedItem = existing;
        }
        voiceRow.Children.Add(voice);
        voiceRow.Children.Add(new TextBlock { Text = "音色只应用于本段；为同一人物选择相同音色。微软在线音色需要联网，文案会发送到微软服务。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetRow(voiceRow, 1); root.Children.Add(voiceRow);
        text = new TextBox { Text = initialText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16 };
        Grid.SetRow(text, 2); root.Children.Add(text);
        error.Text = "修改文案或音色后需重新生成本段，开始位置保持不变。人物名只作标记。";
        error.Margin = new Thickness(0, 10, 0, 12); Grid.SetRow(error, 3); root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, generate) in new[] { ("仅保存", false), ("保存并生成本段", true) }) {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
            button.Click += (_, _) => {
                if (ChineseText.Length == 0) { error.Text = "请输入中文配音文案。"; return; }
                if (!double.TryParse(start.Text, out var seconds) || !double.IsFinite(seconds) || seconds < 0) { error.Text = "开始时间必须是非负秒数。"; return; }
                StartSeconds = Math.Round(seconds, 3); GenerateSpeech = generate; DialogResult = true;
            };
            buttons.Children.Add(button);
        }
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        Grid.SetRow(buttons, 4); root.Children.Add(buttons); Content = root;
        Loaded += (_, _) => { if (chooseVoice) voice.Focus(); else { text.Focus(); text.SelectAll(); } };
    }
}
