using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace YangzaiWorkshop.Services;

/// <summary>
/// 浮动小窗口管理器：统一管理 AI 生图/生视频等浮动窗口的最小化行为。
/// 当这些窗口被最小化时，直接隐藏（Hide）而非最小化到任务栏，
/// 用户可通过快捷键或主窗口按钮重新显示。
/// </summary>
public sealed class FloatingWindowManager
{
    public static FloatingWindowManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly List<WeakReference<Window>> _windows = new();

    /// <summary>被隐藏（最小化）的窗口数量，供主窗口角标显示</summary>
    public int HiddenCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var wr in _windows)
                {
                    if (wr.TryGetTarget(out var w) && !w.IsVisible)
                        count++;
                }
                return count;
            }
        }
    }

    /// <summary>隐藏窗口数量变化事件（更新主窗口角标）</summary>
    public event Action? HiddenCountChanged;

    private FloatingWindowManager() { }

    /// <summary>
    /// 注册一个浮动小窗口：拦截其最小化事件，最小化时改为隐藏。
    /// 调用一次即可，管理器会自动跟踪窗口生命周期。
    /// </summary>
    public void Register(Window window)
    {
        if (window == null) return;

        lock (_lock)
        {
            // 去重：同一窗口不重复注册
            foreach (var wr in _windows)
            {
                if (wr.TryGetTarget(out var existing) && ReferenceEquals(existing, window))
                    return;
            }
            _windows.Add(new WeakReference<Window>(window));
        }

        // 防重入标志：Hide/State 复位过程会再次触发 StateChanged，需避免死循环
        bool handling = false;

        // 最小化 → 隐藏（不在任务栏、不占位）
        window.StateChanged += (_, e) =>
        {
            if (handling) return;

            if (window.WindowState == WindowState.Minimized)
            {
                handling = true;
                try
                {
                    // 先复位到 Normal（避免 Show 后仍是 Minimized），再隐藏
                    window.WindowState = WindowState.Normal;
                    window.Hide();
                }
                finally
                {
                    handling = false;
                }
                NotifyHiddenCountChanged();
            }
        };
    }

    /// <summary>
    /// 恢复最近一个被隐藏的浮动窗口。返回是否成功恢复。
    /// </summary>
    public bool RestoreLastHidden()
    {
        lock (_lock)
        {
            // 从后往前找第一个隐藏的窗口（最近隐藏的）
            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                if (!_windows[i].TryGetTarget(out var w)) continue;
                if (!w.IsVisible)
                {
                    // 恢复前确保状态不是 Minimized，避免 Show 后立即又被最小化
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    NotifyHiddenCountChanged();
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 循环切换：若所有窗口都可见则无操作；否则恢复最近隐藏的窗口。
    /// 供快捷键调用。
    /// </summary>
    public void CycleRestore()
    {
        RestoreLastHidden();
    }

    private void NotifyHiddenCountChanged()
    {
        // 事件可能在非 UI 线程触发，交给 Dispatcher 派发
        var app = Application.Current;
        if (app == null) return;
        var dispatcher = app.Dispatcher;
        if (dispatcher.CheckAccess())
            HiddenCountChanged?.Invoke();
        else
            dispatcher.BeginInvoke(() => HiddenCountChanged?.Invoke());
    }
}
