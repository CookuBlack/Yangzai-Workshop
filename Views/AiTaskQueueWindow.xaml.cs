using System;
using System.Windows;
using System.Windows.Controls;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class AiTaskQueueWindow : Window
{
    public AiTaskQueueWindow()
    {
        InitializeComponent();
        TaskList.ItemsSource = AiTaskManager.Tasks;
        AiTaskManager.Changed += OnTasksChanged;
        Loaded += (_, _) => RefreshCount();
    }

    /// <summary>取消指定任务（点击行内“取消”按钮）</summary>
    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AiTask task })
            AiTaskManager.Cancel(task.Id);
    }

    /// <summary>重新生成指定任务（点击行内“🔄 重新生成”按钮）</summary>
    private void RetryTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AiTask task })
            AiTaskManager.Retry(task.Id);
    }

    /// <summary>清空所有已结束任务</summary>
    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        AiTaskManager.ClearFinished();
        RefreshCount();
    }

    private void OnTasksChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshCount);
            return;
        }
        RefreshCount();
    }

    private void RefreshCount()
    {
        CountText.Text = $"共 {AiTaskManager.Tasks.Count} 个任务";
    }

    protected override void OnClosed(EventArgs e)
    {
        AiTaskManager.Changed -= OnTasksChanged;
        base.OnClosed(e);
    }
}
