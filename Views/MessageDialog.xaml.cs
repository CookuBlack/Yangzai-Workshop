using System.Linq;
using System.Windows;

namespace YangzaiWorkshop.Views;

public partial class MessageDialog : Window
{
    public bool Confirmed { get; private set; }

    public MessageDialog(string title, string message, bool showCancel = false)
    {
        InitializeComponent();
        // 以最前台窗口为 Owner，确保弹窗在最前
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
             ?? Application.Current.MainWindow;
        TitleBlock.Text = title;
        MessageBlock.Text = message;

        if (showCancel)
        {
            CancelBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            OkBtn.Margin = new Thickness(0);
        }

        OkBtn.Click += (_, _) => { Confirmed = true; Close(); };
        CancelBtn.Click += (_, _) => { Confirmed = false; Close(); };
    }

    void OkBtn_Click(object sender, RoutedEventArgs e) { }
    void CancelBtn_Click(object sender, RoutedEventArgs e) { }

    /// <summary>显示纯提示</summary>
    public static void Show(string title, string message)
    {
        new MessageDialog(title, message).ShowDialog();
    }

    /// <summary>显示确认对话框，返回 true=确定</summary>
    public static bool Confirm(string title, string message)
    {
        var dlg = new MessageDialog(title, message, showCancel: true);
        dlg.ShowDialog();
        return dlg.Confirmed;
    }
}
