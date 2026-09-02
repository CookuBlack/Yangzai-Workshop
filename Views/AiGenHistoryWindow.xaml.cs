using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 历史记录选择窗口：列出 AI 生成历史（图片/视频），
/// 点击一条后返回该条目，供生成窗口把提示词、参数、参考素材一并回填。
/// </summary>
public partial class AiGenHistoryWindow : Window
{
    private readonly List<AiGenHistoryEntry> _items = new();
    private readonly List<HistoryRow> _rows = new();
    private AiGenHistoryEntry? _selected;

    public AiGenHistoryWindow(IReadOnlyList<AiGenHistoryEntry> entries)
    {
        InitializeComponent();
        foreach (var e in entries) _items.Add(e);
        foreach (var e in _items) _rows.Add(new HistoryRow(e));

        CountText.Text = _items.Count == 0
            ? "暂无历史记录（生成后会在此展示）。"
            : $"共 {_items.Count} 条：点击选中，双击或点击“使用该记录填充”回填到生成窗口。";

        HistoryList.ItemsSource = _rows;
    }

    /// <summary>当前选中的历史条目；未选中返回 null</summary>
    public AiGenHistoryEntry? SelectedEntry => DialogResult == true ? _selected : null;

    private sealed class HistoryRow
    {
        private readonly AiGenHistoryEntry _e;
        public HistoryRow(AiGenHistoryEntry e) => _e = e;
        public string TypeText => _e.Type == AiGenType.Video ? "🎬 视频" : "🖼️ 图片";
        public string TimeText => _e.CreatedAt.ToString("MM-dd HH:mm");
        public string Prompt => string.IsNullOrWhiteSpace(_e.Prompt) ? "（无提示词）" : _e.Prompt;
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(_e.Level)) parts.Add(_e.Level);
                if (!string.IsNullOrWhiteSpace(_e.Ratio)) parts.Add(_e.Ratio);
                if (_e.Seconds > 0) parts.Add($"{_e.Seconds}s");
                if (_e.RefImagePaths.Count > 0) parts.Add($"图×{_e.RefImagePaths.Count}");
                if (!string.IsNullOrWhiteSpace(_e.RefVideoPath)) parts.Add("视频参考");
                return parts.Count > 0 ? string.Join("·", parts) : "—";
            }
        }
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => TrySelect();

    private bool TrySelect()
    {
        if (HistoryList?.SelectedItem is not HistoryRow row) return false;
        var idx = _rows.IndexOf(row);
        if (idx >= 0 && idx < _items.Count)
        {
            _selected = _items[idx];
            SelectedText.Text = $"已选：{row.TypeText} · {row.TimeText}";
            return true;
        }
        return false;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrySelect()) DialogResult = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (TrySelect()) DialogResult = true;
        else SelectedText.Text = "请先点击选择一条历史记录";
    }

    /// <summary>清除全部历史记录（确认后删除历史文件并清空列表）。</summary>
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner ?? this;
        if (!MessageDialog.Confirm("清除历史", "确定要删除全部 AI 生成历史记录吗？\n此操作不可恢复。"))
            return;
        AiGenHistory.Clear(App.WorkRoot);
        _items.Clear();
        _rows.Clear();
        HistoryList.ItemsSource = null;
        _selected = null;
        CountText.Text = "暂无历史记录（生成后会在此展示）。";
        SelectedText.Text = "历史记录已清除";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}