using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet
{
    /// <summary>
    /// 桌面宠物主窗口：透明置顶、帧动画、拖拽、跟随/散步/休息行为。
    /// 右键菜单：散步/休息（同键切换）、跟随鼠标、设置（奔跑速度+大小滑动条）、退出。
    /// 单击=点头 / 双击=跳跃；退出先播放挥手。
    /// 软件名称：Gauss Yannin。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---------- 动画定义 ----------
        private sealed class Anim
        {
            public IReadOnlyList<BitmapImage> Frames = Array.Empty<BitmapImage>();
            public double Fps = 12;
            public bool Loop = true;
        }

        // 帧图像的“身体包围盒”（不透明像素范围，像素坐标）
        private readonly struct BodyBounds
        {
            public readonly int MinX, MinY, MaxX, MaxY;
            public BodyBounds(int minX, int minY, int maxX, int maxY)
            {
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }
        }

        private readonly Dictionary<string, Anim> _anims = new();

        private Anim _current = null!;
        private string _currentKey = "休息";
        private int _frameIndex;
        private bool _oneShot;

        // 当前帧 alpha 掩码（仅透明度，用于透明区域点击穿透）。按帧缓存，循环动画复用避免每帧全图拷贝
        private byte[]? _alphaMask;
        private bool _pPixelsReady;   // _alphaMask 与包围盒已就绪，命中测试可做身体预筛
        private int _pW = 1, _pH = 1;

        // 当前帧“宠物身体”（不透明像素）的包围盒（像素坐标），
        // 用于让屏幕边界的碰撞贴合宠物实际轮廓而非整张图片边缘。
        private int _bodyMinX = 0, _bodyMinY = 0, _bodyMaxX = 1, _bodyMaxY = 1;

        // 每帧图像的“身体包围盒”缓存：包围盒只取决于帧图像，解码一次后复用到，避免每帧全图扫描
        private readonly Dictionary<BitmapImage, BodyBounds> _bodyCache = new();
        // 每帧图像的 alpha 掩码缓存（与包围盒一起只提取一次）
        private readonly Dictionary<BitmapImage, byte[]> _alphaCache = new();

        // ---------- 计时器 ----------
        private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Normal);
        private readonly DispatcherTimer _moveTimer = new(DispatcherPriority.Normal);
        private readonly DispatcherTimer _idleTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _clickTimer = new(DispatcherPriority.Background);
        private readonly Random _rng = new();

        // ---------- 行为模式 ----------
        private enum Mode { Rest, Walk, Follow }
        private Mode _mode = Mode.Rest;
        private string _desiredAnim = "休息";

        private Point _target;            // 散步目标点
        private bool _moving;
        private bool _wanderRun;          // 本轮散步是否为跑步
        private DateTime _movePauseUntil = DateTime.MinValue;

        // 速度 / 距离阈值（逻辑像素）
        private const double WALK_SPEED = 2.2;
        private double _runSpeed = 5.0;          // 奔跑速度（可在「设置」中拖动调整）
        private const double FOLLOW_MAX_D = 280; // 跟随距离归一化上限（超过此距离取最大速度）
        private const double FOLLOW_NEAR = 30;   // 离鼠标小于此距离 -> 休息
        private const double FOLLOW_RUN_D = 120; // 跟随时光标距离超过此值 -> 显示奔跑动画
        private const double MOVE_EPS = 6;

        // 散步/休息行为概率
        private const double RUN_P = 0.2;   // 散步：跑步
        private const double WALK_P = 0.5;  // 散步：走步（跑步+走步=0.7，其余 0.3 休息）

        // 跳跳弹跳曲线高度（像素）：越大跳得越高
        private const double JUMP_HEIGHT = 32;

        // 缩放上下限（逻辑像素），与 XAML 滑动条 Min/Max 保持一致
        private const double MIN_SIZE = 100;
        private const double MAX_SIZE = 360;

        // ---------- 拖拽状态（delta 法，DPI 安全）----------
        private bool _isDragging;
        private bool _clickPending;
        private Point _downPoint;
        private double _grabOffX;   // 抓取时光标逻辑坐标与窗口左上角的固定偏移
        private double _grabOffY;
        private bool _facingLeft = true;   // true=朝左（素材默认朝左，不镜像）——启动即未镜像，故默认 true

        // 退出待挥手完成
        private bool _exiting;

        // 退出渐隐定时器：挥手播完后窗口透明度逐帧下降
        private System.Windows.Threading.DispatcherTimer? _fadeTimer;

        // 设置窗口（独立置顶，避免模态对话框被透明主窗遮蔽）
        private SettingsWindow? _settingsWindow;
        private readonly PetSettings.Data _settings = PetSettings.Load();

        // 面板窗口列表：退出时一并关闭
        private readonly List<NotePanelWindow> _notePanels = new();

        // 羊群模式：最多 50 只小羊伙伴（设置中可调，最少 1 只）
        public const int MAX_SHEEP = 50;
        private bool _herdMode;
        private bool _freeRoam;                       // 散养模式：小羊自由移动，不跟随主羊
        private int _herdCount = 3;
        private readonly List<SheepFollowerWindow> _herd = new();
        private readonly Point[] _herdTargets = new Point[MAX_SHEEP];

        // 整点报时
        private readonly DispatcherTimer _chimeTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _bubbleTimer = new(DispatcherPriority.Background);
        private int _lastChimeHour = -1;
        private DateTime _bubbleShownAt;
        private bool _bubbleFading;

        // 常驻时钟（菜单可开关注显示时间）
        private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background);
        private bool _showTime;
        private bool _showSeconds = true;   // 是否显示秒（设置中可调）
        private bool _use24Hour = true;     // true=24 小时制，false=12 小时制（设置中可调）

        // 番茄钟模式（工作 + 休息循环，时长可在右键二级菜单中设置）
        private readonly DispatcherTimer _pomoTimer = new(DispatcherPriority.Normal);
        private bool _pomodoro;            // 番茄钟开关
        private bool _pomoWorking = true;  // true=工作阶段，false=休息阶段
        private DateTime _pomoEndAt;       // 当前阶段结束时刻
        private int _pomoWorkMin = 15;     // 工作时长（分钟），默认 15
        private int _pomoBreakMin = 5;     // 休息时长（分钟），默认 5
        private bool _pomoPromptShowing;   // 番茄钟阶段切换时，正在显示提示语（此时隐藏计时器，等提示语渐隐后再显示）
        // 阶段结束提醒音：改用窗口内隐藏的 MediaElement（XAML RemindMedia），
        // 与主程序音乐播放器同机制，比独立 MediaPlayer 可靠（MediaPlayer 在繁忙 UI 下会静默不响）。
        private bool _remindReady;   // remind 音频 Source 已加载

        // 跳跳（一次性动画）的窗口弹跳：记录起跳前 Top，用抛物线在动画过程中临时修改 Top
        private double _jumpBaseTop;
        private bool _jumpActive;

        // 透明点击穿透
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        // 命中视为“宠物身体”的最小不透明度：
        // 高于此值才拦截点击（可拖拽），低于此值（透明/半透明描边）穿透到桌面，
        // 使可点边缘贴合宠物实际轮廓而非整张图片边缘。
        private const int ALPHA_HIT_THRESHOLD = 128;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // 命中测试用的 DPI 缩放（逻辑 DIP → 物理像素）。在 SourceInitialized 时缓存，
        // 使 WndProc 里能用原生 GetWindowRect 换算本地坐标，而不必调用会强制 WPF 布局的 PointFromScreen。
        private double _dpiScaleX = 1.0, _dpiScaleY = 1.0;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // 原生获取窗口屏幕矩形（物理像素）：比 PointFromScreen 便宜得多，且不触发 WPF 布局
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public MainWindow()
        {
            InitializeComponent();
            PetImage.RenderTransformOrigin = new Point(0.5, 0.5); // 以中心为锚点做左右镜像
            _ = LoadAnimationsAsync(); // 异步加载动画帧，避免阻塞窗口显示
            _playTimer.Tick += PlayTick;
            _moveTimer.Tick += MoveTick;
            _idleTimer.Tick += IdleTick;
            _clickTimer.Tick += ClickTimerTick;
            _chimeTimer.Tick += ChimeTick;
            _bubbleTimer.Tick += BubbleTick;
            _clockTimer.Tick += ClockTick;
            _pomoTimer.Tick += PomodoroTick;
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            ContextMenuOpening += MainWindow_ContextMenuOpening;
            Closed += (_, _) => { DespawnHerd(); SaveSettings(); };
        }

        // ---------- 素材加载（从嵌入资源解码，素材已打进 exe）----------

        // 动画定义清单：在后台线程读取帧字节，回到 UI 线程逐帧解码
        private static readonly (string Key, string Folder, double Fps, bool Loop)[] AnimDefs =
        {
            ("休息", "Idle", 11, true),
            ("走路", "Walk", 15, true),
            ("奔跑", "Run", 18, true),
            ("挥手", "Wave", 20, false),
            ("点头", "Nod", 14, false),
            ("跳跳", "Jump", 18, false),
            ("吃草", "Grazing", 15, true),
            // 「读书」帧数最多（241 帧），按需懒加载，避免拖慢启动
        };

        private async System.Threading.Tasks.Task LoadAnimationsAsync()
        {
            // 全部解码放到后台线程一次性完成：DecodeFrame 内部已 Freeze，位图可跨线程安全使用，
            // 不会阻塞 UI 线程（此前逐帧在 UI 线程解码 + Task.Yield，仍会持续抢占 UI 线程导致启动/右键菜单卡顿）。
            var loaded = await System.Threading.Tasks.Task.Run(() =>
            {
                var result = new Dictionary<string, Anim>(AnimDefs.Length);
                foreach (var d in AnimDefs)
                {
                    var bytes = AnimResources.ReadAnimBytes(d.Folder);
                    var frames = new List<BitmapImage>(bytes.Length);
                    foreach (var b in bytes)
                        if (b != null) frames.Add(AnimResources.DecodeFrame(b, 256));
                    result[d.Key] = new Anim { Frames = frames, Fps = d.Fps, Loop = d.Loop };
                }
                return result;
            });

            _anims.Clear();
            foreach (var kv in loaded)
                _anims[kv.Key] = kv.Value;
            _animsReady = true;

            // 若窗口已加载（异步加载可能晚于 Loaded），补上初始登场
            if (IsLoaded)
                StartInitialGreeting();
        }

        private bool _animsReady;
        private bool _readingLoading;
        private bool _greeted;

        // ---------- 启动 ----------
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 恢复用户上次调整过的设置
            Width = Height = Math.Max(MIN_SIZE, Math.Min(MAX_SIZE, _settings.Size));
            _runSpeed = Math.Max(2.5, Math.Min(8.0, _settings.RunSpeed));
            _herdCount = Math.Max(1, Math.Min(MAX_SHEEP, _settings.HerdCount));
            _showSeconds = _settings.ShowSeconds;
            _use24Hour = _settings.Use24Hour;
            _pomoWorkMin = Math.Max(1, Math.Min(120, _settings.PomoWorkMin));
            _pomoBreakMin = Math.Max(1, Math.Min(120, _settings.PomoBreakMin));

            Left = (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Height - Height - 40;

            _moveTimer.Interval = TimeSpan.FromMilliseconds(30);
            _moveTimer.Start();
            ScheduleIdle();

            // 初始登场：出现时先挥手欢迎一次（播完自动回到休息）。
            // 若动画还没加载完，等 LoadAnimationsAsync 完成后补播
            StartInitialGreeting();

            // 整点报时：每秒检查，避免刚启动就重复报当前整点
            _lastChimeHour = DateTime.Now.Hour;
            _chimeTimer.Interval = TimeSpan.FromSeconds(1);
            _chimeTimer.Start();

            // 预加载番茄钟提醒音，避免到点时 Open+Play 竞态导致无声
            InitRemindSound();

            PetSettings.Log(
                $"启动应用设置 -> Size={_settings.Size:0} RunSpeed={_settings.RunSpeed:0.0} " +
                $"HerdCount={_settings.HerdCount} 显示秒={_settings.ShowSeconds} 24小时={_settings.Use24Hour} " +
                $"工作={_settings.PomoWorkMin} 休息={_settings.PomoBreakMin}");
        }

        // 初始登场：先播放「休息」，再挥手欢迎一次（播完自动回到休息）。
        // 动画加载完成前调用不会真正播放，等加载完后再触发一次
        private void StartInitialGreeting()
        {
            if (_greeted) return;

            if (!_animsReady)
            {
                // 动画还没加载完，稍后由 LoadAnimationsAsync 再次调用
                return;
            }

            _greeted = true;
            Play("休息");
            if (_anims.ContainsKey("挥手"))
                SetOneShot("挥手");
        }

        // 保存当前用户设置到本地（大小/速度/羊数/时间选项/番茄钟时长）
        private void SaveSettings()
        {
            _settings.Size = Width;
            _settings.RunSpeed = _runSpeed;
            _settings.HerdCount = _herdCount;
            _settings.ShowSeconds = _showSeconds;
            _settings.Use24Hour = _use24Hour;
            _settings.PomoWorkMin = _pomoWorkMin;
            _settings.PomoBreakMin = _pomoBreakMin;
            PetSettings.Save(_settings);
        }

        // 菜单弹出前，切换「散步/休息」按钮上的文字（只显示当前状态，字体随之转换）
        private void MainWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            bool inWalk = _mode == Mode.Walk;
            Menu_WanderItem.Header = inWalk ? "休息" : "散步";
            Menu_WanderItem.Icon = inWalk
                ? (object)new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xB9, 0xA9, 0x8F)) }
                : (object)new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x8F)) };

            // 羊群模式开启时点亮图标
            if (HerdIcon != null)
                HerdIcon.Fill = _herdMode
                    ? new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x8F))
                    : new SolidColorBrush(Color.FromRgb(0xB9, 0xA9, 0x8F));

            // 羊群二级菜单：当前激活的模式点亮，另一个置灰
            Brush on = new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x8F));
            Brush off = new SolidColorBrush(Color.FromRgb(0xB9, 0xA9, 0x8F));
            if (HerdFollowIcon != null)
                HerdFollowIcon.Fill = _herdMode && !_freeRoam ? on : off;
            if (HerdFreeRoamIcon != null)
                HerdFreeRoamIcon.Fill = _herdMode && _freeRoam ? new SolidColorBrush(Color.FromRgb(0x9A, 0xC8, 0x5A)) : off;

            // 显示时间开启时点亮图标
            if (TimeIcon != null)
                TimeIcon.Fill = _showTime
                    ? new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0xE0))
                    : new SolidColorBrush(Color.FromRgb(0xB9, 0xA9, 0x8F));

            // 番茄钟开启时点亮图标，并同步「停止」按钮可用状态
            if (PomodoroIcon != null)
                PomodoroIcon.Fill = _pomodoro
                    ? new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x5A))
                    : new SolidColorBrush(Color.FromRgb(0xB9, 0xA9, 0x8F));
            if (PomoStopItem != null)
                PomoStopItem.IsEnabled = _pomodoro;

            // 番茄钟时长勾选：工作 15/30/45/自定义，休息 5/10
            UpdatePomoCheck(PomoWork15, 15, _pomoWorkMin);
            UpdatePomoCheck(PomoWork30, 30, _pomoWorkMin);
            UpdatePomoCheck(PomoWork45, 45, _pomoWorkMin);
            PomoWorkCustom.IsChecked = _pomoWorkMin != 15 && _pomoWorkMin != 30 && _pomoWorkMin != 45;
            UpdatePomoCheck(PomoBreak5, 5, _pomoBreakMin);
            UpdatePomoCheck(PomoBreak10, 10, _pomoBreakMin);
        }

        // 勾选“时长==value”的番茄钟时长菜单项
        private void UpdatePomoCheck(MenuItem? item, int value, int current)
        {
            if (item != null) item.IsChecked = current == value;
        }

        // ---------- 动画播放 ----------
        private void Play(string key, bool oneShot = false)
        {
            EnsureAnim(key); // 懒加载的动画（如「读书」）在真正需要时才解码
            if (!_anims.TryGetValue(key, out var anim) || anim.Frames.Count == 0)
                return;
            _currentKey = key;
            _current = anim;
            _frameIndex = 0;
            _oneShot = oneShot;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / anim.Fps);
            if (!_playTimer.IsEnabled) _playTimer.Start();
            ShowFrame();
        }

        // 按需加载「读书」等未在启动时预解码的动画（减少启动耗时与内存）。
        // 改为后台读取 + 逐帧解码，避免首次切到读书时卡死
        private void EnsureAnim(string key)
        {
            if (_anims.ContainsKey(key)) return;
            if (key == "读书" && !_readingLoading)
            {
                _readingLoading = true;
                _ = LoadReadingAsync();
            }
        }

        private async System.Threading.Tasks.Task LoadReadingAsync()
        {
            // 「读书」241 帧全部在后台线程解码（Freeze 后可跨线程使用），避免首次切到读书时卡死 UI
            var frames = await System.Threading.Tasks.Task.Run(() =>
            {
                var bytes = AnimResources.ReadAnimBytes("Reading");
                var list = new List<BitmapImage>(bytes.Length);
                foreach (var b in bytes)
                    if (b != null) list.Add(AnimResources.DecodeFrame(b, 256));
                return list;
            });

            _anims["读书"] = new Anim { Frames = frames, Fps = 25, Loop = true };

            // 若加载期间已在等待读书动画，加载完后立即切过去
            if (!_oneShot && _desiredAnim == "读书")
                Play("读书");
        }

        private void SetLoop(string key)
        {
            // 已在播放且为循环动画：避免重启抖动（动画未就绪时 _current 可能为 null）
            if (_current != null && _currentKey == key && _current.Loop) return;
            Play(key, oneShot: false);
        }

        private void SetOneShot(string key)
        {
            // 跳跳开始：记录起跳基准 Top，并启用弹跳（每帧 PlayTick 应用）
            if (key == "跳跳")
            {
                _jumpBaseTop = Top;
                _jumpActive = true;
            }
            Play(key, oneShot: true);
        }

        private void ShowFrame()
        {
            var frames = _current.Frames;
            if (_frameIndex >= frames.Count) _frameIndex = frames.Count - 1;
            var img = frames[_frameIndex];
            PetImage.Source = img;

            _pW = img.PixelWidth;
            _pH = img.PixelHeight;

            // 包围盒与 alpha 掩码只与帧图像有关：首次遇到该帧时提取一次并缓存。
            // 循环动画后续帧直接复用，避免每帧全图 CopyPixels + 扫描（是持续运行的主要 CPU 开销）。
            if (!_bodyCache.TryGetValue(img, out var bb))
            {
                var full = new byte[_pW * _pH * 4];
                img.CopyPixels(full, _pW * 4, 0);

                bb = ComputeBodyBounds(full);
                _bodyCache[img] = bb;

                var alpha = new byte[_pW * _pH];
                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = full[i * 4 + 3];
                _alphaCache[img] = alpha;
            }
            _alphaMask = _alphaCache[img];
            _bodyMinX = bb.MinX; _bodyMaxX = bb.MaxX;
            _bodyMinY = bb.MinY; _bodyMaxY = bb.MaxY;
            _pPixelsReady = true;

            // 跳跳动画：按帧应用抛物线弹跳，高度 JUMP_HEIGHT
            if (_jumpActive && _currentKey == "跳跳" && frames.Count > 1)
            {
                double p = (double)_frameIndex / (frames.Count - 1); // 0..1
                // 抛物线：y = 4 * H * p * (1 - p)，峰值 H 在 p=0.5
                double offset = -4.0 * JUMP_HEIGHT * p * (1.0 - p); // 负值=往上
                double newTop = _jumpBaseTop + offset;
                // 不要跑出屏幕上界
                if (newTop < 0) newTop = 0;
                Top = newTop;
            }
        }

        private void PlayTick(object? sender, EventArgs e)
        {
            var anim = _current;
            if (anim.Frames.Count <= 1) return;

            bool wasJumping = _jumpActive && _currentKey == "跳跳";

            _frameIndex++;
            if (_frameIndex >= anim.Frames.Count)
            {
                if (anim.Loop)
                {
                    _frameIndex = 0;
                }
                else
                {
                    // 动画结束：若是跳跳，把 Top 还原到起跳基准并关闭弹跳
                    if (wasJumping)
                    {
                        _jumpActive = false;
                        double x = Left;
                        double y = _jumpBaseTop;
                        ClampTopLeft(ref x, ref y);
                        Top = y;
                    }

                    _frameIndex = anim.Frames.Count - 1;
                    _oneShot = false;

                    // 退出：挥手播完 -> 开始渐隐后关闭
                    if (_exiting)
                    {
                        BeginFadeOut();
                        return;
                    }

                    if (_isDragging) SetLoop("走路");
                    else ApplyModeAnim();
                    return;
                }
            }
            ShowFrame();
        }

        // ---------- 行为模式动画应用 ----------
        private void ApplyModeAnim()
        {
            if (_oneShot || _isDragging || _exiting) return;
            SetLoop(_desiredAnim);
        }

        // ---------- 移动引擎 ----------
        private void MoveTick(object? sender, EventArgs e)
        {
            if (_isDragging || _oneShot || _exiting) return;

            // 番茄钟工作阶段：小羊保持「读书」状态，不移动不休息
            if (_pomodoro && _pomoWorking)
            {
                _moving = false;
                _desiredAnim = "读书";
                SetLoop("读书");
                return;
            }

            switch (_mode)
            {
                case Mode.Rest:
                    RestStep();
                    break;
                case Mode.Walk:
                    WanderStep();
                    break;
                case Mode.Follow:
                    FollowStep();
                    break;
            }
            ApplyModeAnim();
        }

        // 随机散步：每段行程按概率决定 跑步(0.2)/走步(0.5)/休息(0.3)
        private void WanderStep()
        {
            var now = DateTime.Now;
            if (now < _movePauseUntil) { _desiredAnim = "休息"; return; }

            // 开启新一段行程时按概率抽一次行为
            if (!_moving)
            {
                double r = _rng.NextDouble();
                if (r < RUN_P)          { _wanderRun = true; }          // 跑步 0.2
                else if (r < RUN_P + WALK_P) { _wanderRun = false; }    // 走步 0.5
                else
                {
                    // 休息 0.3：停一会儿，站立/微动，最短 5 秒
                    _movePauseUntil = now.AddMilliseconds(_rng.Next(5000, 9000));
                    _desiredAnim = "休息";
                    return;
                }
                _target = RandomPointOnScreen();
                _moving = true;
            }

            var center = PetCenter();
            double dx = _target.X - center.X;
            double dy = _target.Y - center.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < MOVE_EPS)
            {
                _moving = false; // 到点，下一帧重新抽行为
                return;
            }
            double speed = _wanderRun ? _runSpeed : WALK_SPEED;
            double step = Math.Min(speed, d);
            SetPetCenter(center.X + dx / d * step, center.Y + dy / d * step);
            SetFacing(dx);
            _desiredAnim = _wanderRun ? "奔跑" : "走路";
        }

        // 休息模式：在 读书 / 吃草 / 休息 之间轮流，读书最短 5 分钟、吃草最短 1 分钟、休息最短 30 秒
        private void RestStep()
        {
            var now = DateTime.Now;
            if (now < _movePauseUntil) return; // 当前动作未结束，保持 _desiredAnim

            double r = _rng.NextDouble();
            if (r < 0.4)
            {
                _desiredAnim = "读书";
                _movePauseUntil = now.AddMinutes(_rng.Next(5, 11));   // 读书 ≥5 分钟
            }
            else if (r < 0.7)
            {
                _desiredAnim = "吃草";
                _movePauseUntil = now.AddMinutes(_rng.Next(1, 4));    // 吃草 ≥1 分钟
            }
            else
            {
                _desiredAnim = "休息";
                _movePauseUntil = now.AddSeconds(_rng.Next(30, 61));  // 休息 ≥30 秒
            }
        }

        // 计算当前帧中不透明（身体）像素的包围盒
        private BodyBounds ComputeBodyBounds(byte[] buf)
        {
            int w = _pW, h = _pH;
            int minX = w, minY = h, maxX = 0, maxY = 0;
            bool any = false;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (buf[(row + x) * 4 + 3] >= ALPHA_HIT_THRESHOLD)
                    {
                        any = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (!any)
            {
                return new BodyBounds(0, 0, w, h);
            }
            return new BodyBounds(minX, minY, maxX, maxY);
        }

        // 跟随鼠标：速度使用二次曲线 —— 越远越快、越近越慢，有上下限；
        // 距离超过 FOLLOW_RUN_D 时显示奔跑动画，否则走路
        private void FollowStep()
        {
            var cursor = GetCursorLogical();
            var center = PetCenter();
            double dx = cursor.X - center.X;
            double dy = cursor.Y - center.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < FOLLOW_NEAR) { _desiredAnim = "休息"; return; }

            // 归一化距离到 [0,1] 区间（上限 FOLLOW_MAX_D）
            double t = Math.Min(1.0, (d - FOLLOW_NEAR) / Math.Max(1.0, FOLLOW_MAX_D - FOLLOW_NEAR));
            // 二次曲线：speed = WALK_SPEED + (MAX - WALK) * t^2
            // 距离近时接近 WALK_SPEED，距离远时平滑过渡到 _runSpeed
            double speed = WALK_SPEED + (_runSpeed - WALK_SPEED) * t * t;
            double step = Math.Min(speed, d);
            SetPetCenter(center.X + dx / d * step, center.Y + dy / d * step);
            SetFacing(dx);
            _desiredAnim = d > FOLLOW_RUN_D ? "奔跑" : "走路";
        }

        // ---------- 位置工具 ----------
        public Point PetCenter() => new(Left + Width / 2, Top + Height / 2);

        private void SetPetCenter(double cx, double cy)
        {
            double x = cx - Width / 2;
            double y = cy - Height / 2;
            ClampTopLeft(ref x, ref y);
            Left = x;
            Top = y;
        }

        // 朝向：素材原始为朝左；dirX<0 向左（不镜像），dirX>0 向右（水平翻转）
        private void SetFacing(double dirX)
        {
            bool left = dirX < 0;
            if (left == _facingLeft) return;
            _facingLeft = left;
            PetImage.RenderTransform = left ? new ScaleTransform(1, 1) : new ScaleTransform(-1, 1);
        }

        // 可见“宠物身体”在窗口逻辑坐标下距窗口四边的留白（含朝向镜像）
        private void GetBodyMargins(out double ml, out double mr, out double mt, out double mb)
        {
            double scale = Width / (double)_pW;
            mt = _bodyMinY * scale;
            mb = (_pH - _bodyMaxY) * scale; // 纵向不受镜像影响
            if (_facingLeft)
            {
                ml = _bodyMinX * scale;
                mr = (_pW - _bodyMaxX) * scale;
            }
            else
            {
                // 镜像后：图像右缘实际显示在左（视觉左边 = 图片右边），反之亦然
                ml = (_pW - _bodyMaxX) * scale;
                mr = _bodyMinX * scale;
            }
        }

        private void ClampTopLeft(ref double x, ref double y)
        {
            double w = SystemParameters.WorkArea.Width;
            double h = SystemParameters.WorkArea.Height;
            GetBodyMargins(out double ml, out double mr, out double mt, out double mb);
            // 让身体边缘贴合屏幕：身体左边 >= 0 ⇒ Left >= -ml；身体右边 <= w ⇒ Left <= w-(Width-mr)
            double xLo = -ml;
            double xHi = w - (Width - mr);
            double yLo = -mt;
            double yHi = h - (Height - mb);
            if (xHi < xLo) xLo = xHi;
            if (x < xLo) x = xLo; else if (x > xHi) x = xHi;
            if (yHi < yLo) yLo = yHi;
            if (y < yLo) y = yLo; else if (y > yHi) y = yHi;
        }

        // 设置大小：按滑动条值（或 delta）直接修改，保持中心不变
        private void SetSize(double newSize)
        {
            double size = Math.Max(MIN_SIZE, Math.Min(MAX_SIZE, newSize));
            if (Math.Abs(size - Width) < 0.5) return;
            var c = PetCenter();
            Width = size;
            Height = size;
            SetPetCenter(c.X, c.Y);
        }

        private Point RandomPointOnScreen()
        {
            double w = SystemParameters.WorkArea.Width;
            double h = SystemParameters.WorkArea.Height;
            return new Point(_rng.NextDouble() * (w - Width) + Width / 2,
                             _rng.NextDouble() * (h - Height) + Height / 2);
        }

        private Point GetCursorLogical()
        {
            if (GetCursorPos(out POINT p))
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                    return src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));
            }
            return new Point(SystemParameters.WorkArea.Width / 2, SystemParameters.WorkArea.Height / 2);
        }

        // ---------- 羊群模式 ----------
        // 供伙伴读取主羊朝向
        public bool FacingLeft => _facingLeft;

        // 主羊当前的移动状态（奔跑 / 走路 / 休息），供伙伴跟随相同节奏
        public string MainMode => _desiredAnim;

        // 供伙伴读取各自的跟随目标点
        public Point HerdTarget(int index)
            => index >= 0 && index < _herd.Count ? _herdTargets[index] : PetCenter();

        // 供伙伴读取当前实际在位的伙伴数量与中心位置（用于互斥避免重叠）
        public int FollowerCount => _herd.Count;
        public Point FollowerCenter(int i)
        {
            if (i >= 0 && i < _herd.Count) return _herd[i].Center();
            return new Point(double.NaN, double.NaN);
        }

        // 更新所有伙伴的生成/出发目标点：围绕主羊环形分布，覆盖当前 _herdCount 只
        public void UpdateHerdTargets()
        {
            double m = Width;
            var c = PetCenter();
            double r = m * 0.6;
            double ws = m * 0.25;                 // 伙伴窗口边长
            double w = SystemParameters.WorkArea.Width;
            double h = SystemParameters.WorkArea.Height;
            for (int i = 0; i < _herdCount; i++)
            {
                double ang = i * (Math.PI * 2 / _herdCount) + (i % 2) * 0.35;
                double x = c.X + Math.Cos(ang) * r;
                double y = c.Y + Math.Sin(ang) * r;
                x = Math.Clamp(x, ws / 2, w - ws / 2);
                y = Math.Clamp(y, ws / 2, h - ws / 2);
                _herdTargets[i] = new Point(x, y);
            }
        }

        // 手动调整羊群数量（设置滑动条），1~50
        public void SetHerdCount(int count)
        {
            int c = Math.Clamp(count, 1, MAX_SHEEP);
            if (c == _herdCount) return;
            _herdCount = c;
            if (_herdMode) SyncHerd();
        }

        // 生成/同步羊群：把伙伴数量对齐到 _herdCount（多退少补）
        private void SyncHerd()
        {
            UpdateHerdTargets();
            while (_herd.Count < _herdCount)
            {
                var f = new SheepFollowerWindow(this, _herd.Count);
                f.FreeRoam = _freeRoam;
                _herd.Add(f);
                f.Show();
            }
            while (_herd.Count > _herdCount)
            {
                var last = _herd[_herd.Count - 1];
                _herd.RemoveAt(_herd.Count - 1);
                last.Despawn();
            }
        }

        // 消散羊群：伙伴渐隐后关闭
        private void DespawnHerd()
        {
            var list = _herd.ToArray();
            _herd.Clear();
            foreach (var f in list) f.Despawn();
        }

        // 关闭所有面板窗口
        private void CloseNotePanels()
        {
            var list = _notePanels.ToArray();
            _notePanels.Clear();
            foreach (var p in list) p.Close();
        }

        // 休息模式只保持休息（无自发跳跃）
        private void ScheduleIdle()
        {
            _idleTimer.Interval = TimeSpan.FromMilliseconds(_rng.Next(5000, 12000));
            _idleTimer.Start();
        }

        // ---------- 整点报时 ----------
        private void ChimeTick(object? sender, EventArgs e)
        {
            if (_exiting) return;
            var now = DateTime.Now;
            // 每小时第一分钟内仅触发一次
            if (now.Minute == 0 && now.Hour != _lastChimeHour)
            {
                _lastChimeHour = now.Hour;
                Chime(now.Hour);
            }
        }

        private void Chime(int hour)
        {
            // 挥手报时（若当前空闲，无拖拽/无其它一次性动画）
            if (!_oneShot && !_isDragging &&
                _anims.TryGetValue("挥手", out var a) && a.Frames.Count > 0)
            {
                SetOneShot("挥手");
            }

            ChimeText.Text = $"{hour:D2}:00"; // 例：20:00
            ChimeBubble.Opacity = 1;
            ChimeBubble.Visibility = Visibility.Visible;
            _bubbleShownAt = DateTime.Now;
            _bubbleFading = false;
            _bubbleTimer.Interval = TimeSpan.FromMilliseconds(30);
            _bubbleTimer.Stop();
            _bubbleTimer.Start();
        }

        // 报时气泡：至少保留 5 秒，随后渐隐消失
        private void BubbleTick(object? sender, EventArgs e)
        {
            _bubbleTimer.Stop();

            if (!_bubbleFading)
            {
                if ((DateTime.Now - _bubbleShownAt).TotalSeconds < 5.0)
                {
                    _bubbleTimer.Start(); // 未到 5 秒，继续保留
                    return;
                }
                _bubbleFading = true;
                // 提示语开始渐隐 → 如果是番茄钟阶段切换，此时显示计时器
                if (_pomoPromptShowing)
                {
                    _pomoPromptShowing = false;
                    UpdatePomodoroBubble();
                    ShowPomodoroBubble();
                }
            }

            ChimeBubble.Opacity -= 0.1;
            if (ChimeBubble.Opacity <= 0)
            {
                ChimeBubble.Opacity = 0;
                ChimeBubble.Visibility = Visibility.Collapsed;
                return;
            }
            _bubbleTimer.Start();
        }

        // 常驻时钟：每秒刷新当前时间
        private void ClockTick(object? sender, EventArgs e)
        {
            if (!_showTime) return;
            ClockText.Text = FormatClock(DateTime.Now);
        }

        // 按设置拼装时钟文本：24/12 小时制、是否显示秒
        private string FormatClock(DateTime now)
        {
            string fmt = _use24Hour
                ? (_showSeconds ? "HH:mm:ss" : "HH:mm")
                : (_showSeconds ? "h:mm:ss tt" : "h:mm tt");
            return now.ToString(fmt);
        }

        // ---------- 番茄钟 ----------

        // 启动番茄钟（从工作阶段开始）
        private void PomodoroStart()
        {
            _pomoWorking = true;
            _pomoEndAt = DateTime.Now.AddMinutes(_pomoWorkMin);
            _pomoTimer.Interval = TimeSpan.FromSeconds(1);
            _pomoTimer.Stop();
            _pomoTimer.Start();
            UpdatePomodoroBubble();
            ShowPomodoroBubble();
        }

        // 停止番茄钟并隐藏气泡
        private void PomodoroStop()
        {
            _pomoTimer.Stop();
            _pomoPromptShowing = false;
            PomodoroBubble.Visibility = Visibility.Collapsed;
            ChimeBubble.Visibility = Visibility.Collapsed;
        }

        // 预加载提醒音：窗口加载时设置一次 Source（MediaElement 常驻视觉树，加载稳定），
        // 到点时只需 Position=0 再 Play 即可，彻底规避 MediaPlayer Open/Play 竞态导致无声。
        private void InitRemindSound()
        {
            if (_remindReady || _exiting) return;

            try
            {
                var path = AnimResources.RingtoneTempPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    PetSettings.Log("[提醒音] remind.mp3 未找到");
                    return;
                }
                RemindMedia.Source = new Uri(path, UriKind.Absolute);
                _remindReady = true;
            }
            catch (Exception ex)
            {
                _remindReady = false;
                PetSettings.Log($"[提醒音初始化失败] {ex.Message}");
            }
        }

        // 播放阶段结束提醒音（remind.mp3）
        private void PlayRemindSound()
        {
            if (_exiting) return;
            try
            {
                InitRemindSound(); // 幂等：已加载则直接跳过
                if (!_remindReady || RemindMedia.Source == null) return;

                RemindMedia.Stop();
                RemindMedia.Position = TimeSpan.Zero;
                RemindMedia.Play();
            }
            catch (Exception ex)
            {
                PetSettings.Log($"[提醒音播放失败] {ex.Message}");
            }
        }

        // 播放完一次后归零，便于下次重播
        private void RemindMedia_Ended(object sender, RoutedEventArgs e)
        {
            RemindMedia.Stop();
            RemindMedia.Position = TimeSpan.Zero;
        }

        private void RemindMedia_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            _remindReady = false;
            PetSettings.Log($"[提醒音加载失败] {e.ErrorException?.Message}");
        }

        // 每秒刷新倒计时；阶段结束时切换工作/休息并提醒
        private void PomodoroTick(object? sender, EventArgs e)
        {
            if (!_pomodoro || _exiting) return;
            if (DateTime.Now >= _pomoEndAt)
            {
                // 阶段切换
                _pomoWorking = !_pomoWorking;
                _pomoEndAt = DateTime.Now.AddMinutes(_pomoWorking ? _pomoWorkMin : _pomoBreakMin);
                // 提醒：播放提醒音 + 挥手 + 提示气泡
                PlayRemindSound();
                if (!_oneShot && !_isDragging &&
                    _anims.TryGetValue("挥手", out var a) && a.Frames.Count > 0)
                {
                    SetOneShot("挥手");
                }
                // 先显示提示语（隐藏计时器），等提示语渐隐后再显示计时器
                _pomoPromptShowing = true;
                PomodoroBubble.Visibility = Visibility.Collapsed;
                ChimeText.Text = _pomoWorking ? "开始工作啦！" : "休息时间到！";
                ChimeBubble.Opacity = 1;
                ChimeBubble.Visibility = Visibility.Visible;
                _bubbleShownAt = DateTime.Now;
                _bubbleFading = false;
                _bubbleTimer.Interval = TimeSpan.FromMilliseconds(30);
                _bubbleTimer.Stop();
                _bubbleTimer.Start();
                return;
            }
            UpdatePomodoroBubble();
        }

        // 刷新番茄钟气泡：显示阶段 + 剩余时间（四舍五入到整秒，避免刚设 1 分钟就显示 00:59 被误读为 59 分钟）
        private void UpdatePomodoroBubble()
        {
            if (!_pomodoro) return;
            var remain = _pomoEndAt - DateTime.Now;
            if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;
            // 向上取整到整秒：设置 1 分钟时显示「01:00」，过半秒后才变 00:59
            int totalSec = (int)Math.Ceiling(remain.TotalSeconds);
            PomodoroText.Text = (_pomoWorking ? "工作 " : "休息 ")
                                + $"{totalSec / 60:D2}:{totalSec % 60:D2}";
        }

        private void ShowPomodoroBubble()
        {
            PomodoroBubble.Opacity = 1;
            PomodoroBubble.Visibility = Visibility.Visible;
        }

        private void IdleTick(object? sender, EventArgs e)
        {
            // 休息模式下保持静止：不触发自发跳跃（双击跳跃仍保留为主动交互）
            ScheduleIdle();
        }

        // ---------- 鼠标交互（单击点头/双击跳跃，左键拖拽移动）----------
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _downPoint = e.GetPosition(this);
            var cur = GetCursorLogical();
            _grabOffX = Left - cur.X;
            _grabOffY = Top - cur.Y;
            _isDragging = false;
            CaptureMouse();
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured) return;

            var p = e.GetPosition(this);
            if (!_isDragging &&
                (Math.Abs(p.X - _downPoint.X) > 3 || Math.Abs(p.Y - _downPoint.Y) > 3))
            {
                // 一旦判定拖动：取消单击/双击待执行的互动
                _clickPending = false;
                _clickTimer.Stop();
                _isDragging = true;
            }

            if (_isDragging)
            {
                var cur = GetCursorLogical();
                double prevX = Left, prevY = Top;
                Left = cur.X + _grabOffX;     // 抓取点始终精确贴合光标
                Top = cur.Y + _grabOffY;
                double moved = Math.Abs(Left - prevX) + Math.Abs(Top - prevY);
                if (Math.Abs(Left - prevX) > 0.5) SetFacing(Left - prevX);

                if (!_oneShot)
                    SetLoop(moved > 2.4 ? "奔跑" : "走路");
            }
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_isDragging)
            {
                _isDragging = false;
                _desiredAnim = "休息";
                ApplyModeAnim();
                return;
            }

            // 单击点头 / 双击跳跃
            if (_clickPending)
            {
                _clickTimer.Stop();
                _clickPending = false;
                SetOneShot("跳跳");
            }
            else
            {
                _clickPending = true;
                _clickTimer.Interval = TimeSpan.FromMilliseconds(260);
                _clickTimer.Start();
            }
        }

        // 单/双击计时器到期：没有第二次点击 → 视为单击 → 点头
        private void ClickTimerTick(object? sender, EventArgs e)
        {
            _clickTimer.Stop();
            _clickPending = false;
            SetOneShot("点头");
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                BeginExit();
                e.Handled = true;
            }
        }

        // ---------- 右键菜单事件 ----------
        // 音乐播放 / 暂停（交由主程序实现）
        private void Menu_MusicToggle(object sender, RoutedEventArgs e) => PetActions.ToggleMusic?.Invoke();

        // AI 生成图片（交由主程序实现）
        private void Menu_AiImage(object sender, RoutedEventArgs e) => PetActions.OpenGenerateImage?.Invoke();

        // AI 生成视频（交由主程序实现）
        private void Menu_AiVideo(object sender, RoutedEventArgs e) => PetActions.OpenGenerateVideo?.Invoke();

        // AI 对话（交由主程序实现）
        private void Menu_AiChat(object sender, RoutedEventArgs e) => PetActions.OpenChat?.Invoke();

        // 查看 AI 任务队列（交由主程序实现）
        private void Menu_Queue(object sender, RoutedEventArgs e) => PetActions.OpenQueue?.Invoke();

        // 打开宠物资源管理（交由主程序实现）
        private void Menu_Resources(object sender, RoutedEventArgs e) => PetActions.OpenResources?.Invoke();

        // 散步/休息 同键切换
        private void Menu_Wander_Toggle(object sender, RoutedEventArgs e)
        {
            if (_mode == Mode.Walk)
            {
                _mode = Mode.Rest;
                _moving = false;
                _desiredAnim = "休息";
                SetLoop("休息");
            }
            else
            {
                _mode = Mode.Walk;
                // 进入散步：先走一段“走路”，之后再随机抽 跑步/走步/休息 等其它动作。
                // 清掉残留的休息暂停时间，避免一进入就被旧动作卡着先去“休息”。
                _movePauseUntil = DateTime.MinValue;
                _moving = true;
                _wanderRun = false;
                _target = RandomPointOnScreen();
            }
        }

        private void Menu_Follow(object sender, RoutedEventArgs e) { _mode = Mode.Follow; }

        // 羊群模式二级菜单：跟随（小羊跟随大羊）
        private void Menu_Herd_Follow(object sender, RoutedEventArgs e)
        {
            // 再次点击当前已激活的“跟随”则关闭羊群
            if (_herdMode && !_freeRoam) { _herdMode = false; DespawnHerd(); return; }
            _herdMode = true;
            _freeRoam = false;
            foreach (var f in _herd) f.FreeRoam = false;
            SyncHerd();
        }

        // 羊群模式二级菜单：散养（小羊自由移动）
        private void Menu_Herd_FreeRoam(object sender, RoutedEventArgs e)
        {
            // 再次点击当前已激活的“散养”则关闭羊群
            if (_herdMode && _freeRoam) { _herdMode = false; DespawnHerd(); return; }
            _herdMode = true;
            _freeRoam = true;
            foreach (var f in _herd) f.FreeRoam = true;
            SyncHerd();
        }

        // 显示时间开关：常驻时钟
        private void Menu_Time_Toggle(object sender, RoutedEventArgs e)
        {
            _showTime = !_showTime;
            if (_showTime)
            {
                ClockText.Text = FormatClock(DateTime.Now);
                ClockBubble.Visibility = Visibility.Visible;
                _clockTimer.Interval = TimeSpan.FromSeconds(1);
                _clockTimer.Stop();
                _clockTimer.Start();
            }
            else
            {
                _clockTimer.Stop();
                ClockBubble.Visibility = Visibility.Collapsed;
            }
        }

        // 面板：打开文字编辑面板窗口（可打开多个实例）
        private void Menu_NotePanel(object sender, RoutedEventArgs e)
        {
            var panel = new NotePanelWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Owner = this // 面板依赖主程序：作为主窗口的附属窗口，不显示在任务栏
            };
            _notePanels.Add(panel);
            panel.Closed += (_, _) => _notePanels.Remove(panel);
            panel.Show();
        }

        // 番茄钟开关：开启从工作阶段倒计时，关闭隐藏
        private void Menu_Pomodoro_Toggle(object sender, RoutedEventArgs e)
        {
            _pomodoro = !_pomodoro;
            if (_pomodoro) PomodoroStart();
            else PomodoroStop();
        }

        // 工作时长预设（15/30/45 分钟）：点击即设置并开始番茄钟
        private void PomoWork_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string s || !int.TryParse(s, out var min)) return;
            SetPomoWork(min);
            EnsurePomodoroRunning();
        }

        // 自定义工作时长：弹出输入框（1~120 分钟），点击即设置并开始番茄钟
        private void PomoWorkCustom_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DurationInputDialog(_pomoWorkMin);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true && dlg.ResultMinutes.HasValue)
            {
                SetPomoWork(dlg.ResultMinutes.Value);
                EnsurePomodoroRunning();
            }
        }

        // 应用新的工作时长：更新值；若正在运行则按新时长重启当前工作阶段
        private void SetPomoWork(int minutes)
        {
            _pomoWorkMin = Math.Clamp(minutes, 1, 120);
            if (_pomodoro && _pomoWorking)
            {
                _pomoEndAt = DateTime.Now.AddMinutes(_pomoWorkMin);
                UpdatePomodoroBubble();
            }
            SaveSettings();
        }

        // 休息时长（5/10 分钟）：点击即设置并开始番茄钟
        private void PomoBreak_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string s || !int.TryParse(s, out var min)) return;
            _pomoBreakMin = Math.Clamp(min, 1, 120);
            if (_pomodoro && !_pomoWorking)
            {
                _pomoEndAt = DateTime.Now.AddMinutes(_pomoBreakMin);
                UpdatePomodoroBubble();
            }
            EnsurePomodoroRunning();
            SaveSettings();
        }

        // 点击任意时长选项即开始：未运行则启动番茄钟
        private void EnsurePomodoroRunning()
        {
            if (!_pomodoro)
            {
                _pomodoro = true;
                PomodoroStart();
            }
        }

        // 打开设置窗口：大小 + 速度 两个滑动条实时生效（独立置顶窗口，保证能正常弹出）
        private void Menu_Settings(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            var win = _settingsWindow;
            _settingsWindow.ResizeApplied += size => { SetSize(size); SaveSettings(); };
            _settingsWindow.SpeedChanged += speed => { _runSpeed = speed; SaveSettings(); };
            _settingsWindow.HerdCountChanged += count => { SetHerdCount(count); SaveSettings(); };
            _settingsWindow.TimeOptionsChanged += (showSeconds, use24Hour) =>
            {
                _showSeconds = showSeconds;
                _use24Hour = use24Hour;
                if (_showTime) ClockText.Text = FormatClock(DateTime.Now);
                SaveSettings();
            };
            _settingsWindow.AutoOpenPetChanged += autoOpenPet =>
            {
                _settings.AutoOpenPet = autoOpenPet;
                SaveSettings();
            };
            // 关闭设置窗口时兜底：把窗口最终生效值写回并持久化，
            // 防止手动输入/最后一次拖动等未触发事件导致的值丢失。
            _settingsWindow.Closed += (_, _) =>
            {
                PetSettings.Log(
                    $"SettingsWindow.Closed 回写 -> Size={win.CurrentSize:0} Speed={win.CurrentSpeed:0.0} " +
                    $"HerdCount={win.CurrentHerdCount} 显示秒={win.CurrentShowSeconds} 24小时={win.CurrentUse24Hour}");
                SetSize(win.CurrentSize);
                _runSpeed = win.CurrentSpeed;
                _herdCount = win.CurrentHerdCount;
                _showSeconds = win.CurrentShowSeconds;
                _use24Hour = win.CurrentUse24Hour;
                SaveSettings();
                _settingsWindow = null;
            };
            _settingsWindow.InitValues(Width, _runSpeed, _herdCount, _showSeconds, _use24Hour, _settings.AutoOpenPet);
            _settingsWindow.Show();
        }

        private void Menu_Exit(object sender, RoutedEventArgs e) => BeginExit();

        /// <summary>供主程序（托盘菜单）打开宠物设置窗口</summary>
        public void OpenSettings() => Menu_Settings(this, new RoutedEventArgs());

        // ---------- 退出：先挥手再渐隐关闭 ----------
        private void BeginExit()
        {
            if (_exiting) return;
            _exiting = true;
            _mode = Mode.Rest;
            _moving = false;
            _desiredAnim = "休息";
            Opacity = 1.0;
            PomodoroStop();  // 退出时停止番茄钟
            DespawnHerd(); // 退出时让伙伴一起消散，避免残留窗口
            CloseNotePanels(); // 退出时关闭所有面板窗口

            // 若挥手动画存在且可播，则播完在 PlayTick 中渐隐关闭；否则直接关
            if (_anims.TryGetValue("挥手", out var a) && a.Frames.Count > 0)
            {
                SetOneShot("挥手");
            }
            else
            {
                BeginFadeOut();
            }
        }

        // 挥手播完后：透明度逐帧下降（约 0.3s），降到 0 再关闭
        private void BeginFadeOut()
        {
            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(30)
                };
                _fadeTimer.Tick += (s, e) =>
                {
                    Opacity -= 0.1;
                    if (Opacity <= 0)
                    {
                        Opacity = 0;
                        _fadeTimer.Stop();
                        Close();
                    }
                };
            }
            _fadeTimer.Start();
        }

        // ---------- 滚轮缩放（可直接调整大小）----------
        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            SetSize(Width + (e.Delta > 0 ? +24 : -24));
            e.Handled = true;
        }

        // ---------- 透明区域点击穿透 ----------
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var scale = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = scale.DpiScaleX > 0 ? scale.DpiScaleX : 1.0;
            _dpiScaleY = scale.DpiScaleY > 0 ? scale.DpiScaleY : 1.0;

            var hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;

            // 拖拽期间不做透明穿透，避免窗口在底层窗口间抖动/重绘闪烁
            if (_isDragging) return IntPtr.Zero;

            var lp = lParam.ToInt32();
            var sx = lp & 0xFFFF;
            var sy = (lp >> 16) & 0xFFFF;

            // 用原生 GetWindowRect 换成本地坐标（物理像素相减再除以 DPI），
            // 避免 PointFromScreen 每次命中都强制 WPF 布局，从而不再拖慢悬停。
            if (!GetWindowRect(hwnd, out RECT wr)) return IntPtr.Zero;
            double localX = (sx - wr.Left) / _dpiScaleX;
            double localY = (sy - wr.Top) / _dpiScaleY;

            if (localX < 0 || localY < 0 || localX >= Width || localY >= Height)
                return IntPtr.Zero;

            // 身体包围盒预筛：光标落在透明边距上则直接穿透，省去逐像素读取与镜像映射。
            // 悬停时这是在命中测试之外的瘦身，进一步降低高频命中的开销。
            if (_pPixelsReady)
            {
                double scale = Width / (double)_pW;
                double bml = _bodyMinX * scale, bmt = _bodyMinY * scale;
                double bmr = (Math.Max(1, _pW) - _bodyMaxX) * scale;
                double bmb = (Math.Max(1, _pH) - _bodyMaxY) * scale;
                if (_facingLeft)
                {
                    bml = _bodyMinX * scale;
                    bmr = (Math.Max(1, _pW) - _bodyMaxX) * scale;
                }
                else
                {
                    bml = (Math.Max(1, _pW) - _bodyMaxX) * scale;
                    bmr = _bodyMinX * scale;
                }
                // 完全在身体盒之外 → 透明，直接穿透
                if (localX < bml || localX > Width - bmr ||
                    localY < bmt || localY > Height - bmb)
                {
                    if (!RightButtonDown())
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }
                }
            }

            if (_alphaMask != null)
            {
                // 朝向镜像时，点击坐标需按显示方向相反对应，保证透明区与可见身体一致
                double lx = _facingLeft ? localX : (Width - localX);
                int px = (int)(lx * _pW / Width);
                int py = (int)(localY * _pH / Height);
                if (px < 0) px = 0; else if (px >= _pW) px = _pW - 1;
                if (py < 0) py = 0; else if (py >= _pH) py = _pH - 1;
                int alpha = _alphaMask[py * _pW + px];
                if (alpha < ALPHA_HIT_THRESHOLD)
                {
                    // 该像素判定为透明/边缘：穿透到桌面（右键仍命中本窗口以弹出菜单）
                    if (!RightButtonDown())
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }
                }
            }
            return IntPtr.Zero;
        }

        private static bool RightButtonDown() => (GetAsyncKeyState(0x02) & 0x8000) != 0;
    }
}
