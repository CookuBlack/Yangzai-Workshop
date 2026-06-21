using System;
using System.Linq;
using System.Windows;

namespace YangzaiWorkshop.Views;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string title, string initialStatus)
    {
        InitializeComponent();
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        StatusText.Text = initialStatus;
        PercentText.Text = "0%";
    }

    /// <summary>更新进度（0-100）和状态文字</summary>
    public void Report(int percent, string? status = null)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProgressBar.Value = percent;
            PercentText.Text = $"{percent}%";
            if (status != null) StatusText.Text = status;
        });
    }
}
