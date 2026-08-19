using System;
using System.Collections.Generic;
using System.Linq;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

/// <summary>
/// 文本编辑历史服务：提供撤销/重做（类 Word 的停顿合并）与历史快照记录。
/// 只记录文本内容（剧本/提示词/小说原文），不涉及图片/视频等资源。
/// 历史快照仅在应用关闭时持久化，重新打开后可继续浏览/回退。
/// </summary>
public sealed class TextHistoryService
{
    /// <summary>单例</summary>
    public static TextHistoryService Instance { get; } = new();

    /// <summary>默认最大历史快照数（可在设置中调整）</summary>
    public const int DefaultMaxHistory = 50;

    private readonly object _lock = new();

    // ===== 撤销/重做栈 =====
    private readonly LinkedList<EditState> _undoStack = new();
    private readonly LinkedList<EditState> _redoStack = new();

    // ===== 每个编辑目标最近一次已知文本（用于推导变动前后的差异） =====
    private readonly Dictionary<string, string> _lastKnownText = new();

    // ===== 历史快照（持久化用） =====
    private readonly List<HistorySnapshot> _snapshots = new();
    private int _maxHistory = DefaultMaxHistory;

    private TextHistoryService() { }

    /// <summary>历史快照最大数量（设置中可调，1~500）。降低上限时立即裁剪多余记录。</summary>
    public int MaxHistory
    {
        get => _maxHistory;
        set
        {
            int clamped = Math.Clamp(value, 1, 500);
            lock (_lock)
            {
                _maxHistory = clamped;
                TrimToLimitLocked();
            }
        }
    }

    /// <summary>（须在锁内调用）按当前上限裁剪快照与撤销栈</summary>
    private void TrimToLimitLocked()
    {
        while (_snapshots.Count > _maxHistory)
            _snapshots.RemoveAt(0);
        while (_undoStack.Count > _maxHistory)
            _undoStack.RemoveLast();
    }

    /// <summary>撤销栈是否可撤销</summary>
    public bool CanUndo { get { lock (_lock) return _undoStack.Count > 0; } }

    /// <summary>重做栈是否可重做</summary>
    public bool CanRedo { get { lock (_lock) return _redoStack.Count > 0; } }

    /// <summary>历史快照数量</summary>
    public int SnapshotCount { get { lock (_lock) return _snapshots.Count; } }

    /// <summary>历史快照变化事件（刷新历史窗口 UI）</summary>
    public event Action? HistoryChanged;

    /// <summary>当前快照列表（供历史窗口展示）</summary>
    public IReadOnlyList<HistorySnapshot> Snapshots
    {
        get { lock (_lock) return _snapshots.ToList(); }
    }

    /// <summary>初始化某个编辑目标的基准文本（加载文档时调用，作为第一次编辑的 before）</summary>
    public void RegisterBaseline(string key, string baselineText)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            _lastKnownText[key] = baselineText;
        }
    }

    /// <summary>
    /// 提交一次文本变动。根据该 key 上次已知文本推导 before，与 after 比较。
    /// 若内容无变化则忽略；否则压入撤销栈并记录历史快照。
    /// </summary>
    public void CommitEdit(string key, string afterText)
    {
        if (string.IsNullOrEmpty(key)) return;

        lock (_lock)
        {
            // 推导 before
            _lastKnownText.TryGetValue(key, out var before);
            before ??= "";

            // 内容无变化则不记录
            if (before == afterText) return;

            var now = DateTime.UtcNow;

            // 合并策略：若撤销栈顶部是同一 key，且时间接近（由调用方控制停顿），
            // 这里仅更新终点，不再重复叠加（保证"连续输入"合并为一次变动）
            bool merge = _undoStack.Count > 0 && _undoStack.First!.Value.Key == key;

            if (merge)
            {
                var node = _undoStack.First!;
                node.Value = new EditState(key, node.Value.Before, afterText, now);
            }
            else
            {
                _undoStack.AddFirst(new EditState(key, before, afterText, now));
                _redoStack.Clear();
                TrimToLimitLocked();
            }

            // 更新最近已知文本
            _lastKnownText[key] = afterText;

            // 记录历史快照
            RecordSnapshot(key, afterText);

            HistoryChanged?.Invoke();
        }
    }

    /// <summary>执行撤销，返回需要回填的文本；无操作返回 null</summary>
    public string? Undo(string key)
    {
        lock (_lock)
        {
            if (_undoStack.Count == 0) return null;
            var node = _undoStack.First!;
            if (node.Value.Key != key) return null;

            _undoStack.RemoveFirst();
            _redoStack.AddFirst(node.Value);

            // 更新最近已知文本为撤销后的状态
            _lastKnownText[key] = node.Value.Before;

            HistoryChanged?.Invoke();
            return node.Value.Before;
        }
    }

    /// <summary>执行重做，返回需要回填的文本；无操作返回 null</summary>
    public string? Redo(string key)
    {
        lock (_lock)
        {
            if (_redoStack.Count == 0) return null;
            var node = _redoStack.First!;
            if (node.Value.Key != key) return null;

            _redoStack.RemoveFirst();
            _undoStack.AddFirst(node.Value);

            _lastKnownText[key] = node.Value.After;

            HistoryChanged?.Invoke();
            return node.Value.After;
        }
    }

    /// <summary>清空指定编辑目标的撤销/重做栈（切换章节/关闭文档时）</summary>
    public void ClearUndoRedo(string? key = null)
    {
        lock (_lock)
        {
            if (key == null)
            {
                _undoStack.Clear();
                _redoStack.Clear();
                _lastKnownText.Clear();
            }
            else
            {
                var u = _undoStack.First;
                while (u != null)
                {
                    var next = u.Next;
                    if (u.Value.Key == key) _undoStack.Remove(u);
                    u = next;
                }
                var r = _redoStack.First;
                while (r != null)
                {
                    var next = r.Next;
                    if (r.Value.Key == key) _redoStack.Remove(r);
                    r = next;
                }
                _lastKnownText.Remove(key);
            }
        }
    }

    // ==================== 历史快照 ====================

    private void RecordSnapshot(string key, string afterText)
    {
        // 去重：若最后一条快照内容相同，跳过
        if (_snapshots.Count > 0 && _snapshots[^1].Key == key && _snapshots[^1].Content == afterText)
            return;

        _snapshots.Add(new HistorySnapshot
        {
            Key = key,
            Content = afterText,
            Time = DateTime.Now
        });

        TrimToLimitLocked();
    }

    /// <summary>获取指定索引的历史快照</summary>
    public HistorySnapshot? GetSnapshot(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _snapshots.Count) return null;
            return _snapshots[index];
        }
    }

    /// <summary>清空全部历史快照</summary>
    public void ClearSnapshots()
    {
        lock (_lock)
        {
            _snapshots.Clear();
            HistoryChanged?.Invoke();
        }
    }

    // ==================== 持久化 ====================

    /// <summary>从文件加载历史快照（应用启动时调用）</summary>
    public void LoadSnapshots(string workRoot)
    {
        lock (_lock)
        {
            _snapshots.Clear();
            var loaded = FileService.LoadTextHistory(workRoot);
            _snapshots.AddRange(loaded);
            while (_snapshots.Count > MaxHistory)
                _snapshots.RemoveAt(0);
        }
    }

    /// <summary>将历史快照持久化到文件（仅应用关闭时调用）</summary>
    public void SaveSnapshots(string workRoot)
    {
        List<HistorySnapshot> copy;
        lock (_lock) copy = _snapshots.ToList();
        FileService.SaveTextHistory(workRoot, copy);
    }

    /// <summary>单个编辑状态（撤销栈节点）</summary>
    private readonly struct EditState
    {
        public string Key { get; }
        public string Before { get; }
        public string After { get; }
        public DateTime Time { get; }

        public EditState(string key, string before, string after, DateTime time)
        {
            Key = key;
            Before = before;
            After = after;
            Time = time;
        }
    }
}
