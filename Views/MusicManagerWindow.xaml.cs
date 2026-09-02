using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>音乐管理窗口：集中展示/删除全部音乐，随目录实时刷新。</summary>
public partial class MusicManagerWindow : Window
{
    public MusicManagerWindow(Window owner)
    {
        Owner = owner;
        InitializeComponent();
        RefreshList();
        // 音乐目录被监听实时刷新时，同步更新本窗口列表
        MusicPlayerService.Instance.PlaylistChanged += RefreshList;
        Closed += (_, _) => MusicPlayerService.Instance.PlaylistChanged -= RefreshList;
    }

    private void RefreshList()
    {
        var svc = MusicPlayerService.Instance;
        var items = svc.Playlist.Select(f => new { Path = f, Name = Path.GetFileName(f) }).ToList();
        MusicList.ItemsSource = items;
        MusicCountText.Text = items.Count > 0 ? $"共 {items.Count} 首曲目" : "暂无音乐文件";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext == null) return;
        var prop = btn.DataContext.GetType().GetProperty("Path");
        if (prop == null) return;
        var value = prop.GetValue(btn.DataContext);
        if (value is not string path || string.IsNullOrEmpty(path)) return;
        var name = Path.GetFileName(path);

        if (MessageBox.Show(this, $"确定删除音乐文件 \"{name}\" 吗？", "删除音乐",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        MusicPlayerService.Instance.DeleteFile(path);
        // DeleteFile 内部触发 LoadPlaylist → PlaylistChanged → RefreshList
    }

    private void ListScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}