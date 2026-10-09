using System.Windows;

namespace VideoCN;

public partial class CookieImportWindow : Window
{
    private readonly Func<string, int> import;
    internal CookieImportWindow(Func<string, int> import)
    {
        InitializeComponent(); this.import = import;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Closed += (_, _) => ClearInput();
    }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) RawText.Text = Clipboard.GetText(); else Feedback.Text = "剪贴板没有文本，请先复制 Cookie 表格。"; }
        catch { Feedback.Text = "暂时无法读取剪贴板，请直接在文本框按 Ctrl+V。"; }
    }
    private void ClearInput() { RawText.IsUndoEnabled = false; RawText.Clear(); RawText.IsUndoEnabled = true; }
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearInput();
    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        try { import(RawText.Text); Close(); }
        catch (FormatException ex) { Feedback.Text = ex.Message; }
        catch { Feedback.Text = "无法保存 Cookie 文件，请检查本机文件夹是否可写后重试。"; }
    }
}
