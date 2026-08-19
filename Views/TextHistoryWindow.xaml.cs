using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 文本历史窗口：展示历史快照列表，支持撤销/重做/回退到任意历史版本/清空历史。
/// </summary>
public partial class TextHistoryWindow : Window
{
    private readonly TextHistoryService _history = TextHistoryService.Instance;

    public TextHistoryWindow()
    {
        InitializeComponent();
        _history.HistoryChanged += OnHistoryChanged;
        Refresh();

        // 快捷键：Ctrl+Z 撤销 / Ctrl+Y 重做
        KeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo_Click(this, e); e.Handled = true; }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _history.HistoryChanged -= OnHistoryChanged;
        base.OnClosed(e);
    }

    private void OnHistoryChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        SnapshotList.Children.Clear();
        var snapshots = _history.Snapshots;

        CountText.Text = $"共 {snapshots.Count} 条历史记录（最多 {_history.MaxHistory} 条）";
        HintText.Text = "撤销/重做作用于当前正在编辑的文本；点击下方任意历史版本可将该文本回退到对应时刻。";

        if (snapshots.Count == 0)
        {
            SnapshotList.Children.Add(new TextBlock
            {
                Text = "暂无历史记录\n编辑剧本、提示词或小说原文后会自动记录",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        // 倒序展示（最新的在最上面）
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            var snap = snapshots[i];
            int index = i;
            SnapshotList.Children.Add(CreateSnapshotCard(snap, index));
        }
    }

    private Border CreateSnapshotCard(HistorySnapshot snap, int index)
    {
        var display = ResolveDisplayName(snap.Key);
        var preview = snap.Content.Length > 60 ? snap.Content.Substring(0, 60) + "…" : snap.Content;

        var card = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Tag = index
        };

        var stack = new StackPanel();

        // 第一行：字段名 + 时间
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = display,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var timeText = new TextBlock
        {
            Text = snap.Time.ToString("MM-dd HH:mm:ss"),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeText, 1);
        header.Children.Add(timeText);

        stack.Children.Add(header);

        // 第二行：内容预览
        stack.Children.Add(new TextBlock
        {
            Text = preview,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        });

        card.Child = stack;

        // 悬停高亮
        card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("PrimaryBrush");
        card.MouseLeave += (_, _) => card.BorderBrush = (Brush)FindResource("BorderBrush");

        // 点击回退
        card.MouseLeftButtonDown += (_, _) => RestoreSnapshot(index);

        return card;
    }

    /// <summary>将 key 解析为可读的字段名</summary>
    private static string ResolveDisplayName(string key)
    {
        var parts = key.Split('|');
        string field = parts.Length >= 3 ? parts[2] : key;
        string chapter = parts.Length >= 2 ? parts[1] : "";
        string prefix = string.IsNullOrEmpty(chapter) ? "" : $"章节[{chapter}] · ";

        string fieldName = field switch
        {
            "script" => "剧本",
            "prompt" => "提示词",
            "original" => "小说原文",
            _ => field
        };
        return prefix + fieldName;
    }

    /// <summary>回退到指定历史快照</summary>
    private void RestoreSnapshot(int index)
    {
        var snap = _history.GetSnapshot(index);
        if (snap == null) return;

        var result = MessageBox.Show(
            this,
            $"确定将「{ResolveDisplayName(snap.Key)}」回退到 {snap.Time:MM-dd HH:mm:ss} 的版本吗？\n\n当前内容将被替换为该历史版本。",
            "回退确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // 通知 ScriptPage 应用回退
        var scriptPage = NavigationService.Instance.GetPage<ScriptPage>("Script");
        if (scriptPage != null)
        {
            scriptPage.RestoreHistorySnapshot(snap.Key, snap.Content);
        }
        else
        {
            MessageBox.Show("当前无法定位到剧本编辑页面，请先打开剧本管理页面。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var scriptPage = NavigationService.Instance.GetPage<ScriptPage>("Script");
        scriptPage?.UndoCurrentEditor();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var scriptPage = NavigationService.Instance.GetPage<ScriptPage>("Script");
        scriptPage?.RedoCurrentEditor();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "确定清空全部文本历史记录吗？此操作不可撤销。",
            "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        _history.ClearSnapshots();
        _history.ClearUndoRedo();
        Refresh();
    }
}
