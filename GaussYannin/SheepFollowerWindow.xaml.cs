using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet
{
    /// <summary>
    /// 羊群模式下的“小羊伙伴”：体型为主羊的 1/4，
    /// 跟随主羊移动（速度稍慢且各不相同），渐显出现，始终点击穿透（仅装饰）。
    /// </summary>
    public partial class SheepFollowerWindow : Window
    {
        private sealed class Anim
        {
            public IReadOnlyList<BitmapImage> Frames = Array.Empty<BitmapImage>();
            public double Fps = 12;
            public bool Loop = true;
        }

        private readonly MainWindow _owner;
        private readonly int _index;
        private readonly double _speedFactor;      // 速度乘子（0.9~1.25），使各小羊速度不同
        private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _moveTimer = new(DispatcherPriority.Background);
        private readonly Random _rng = new();
        private DispatcherTimer? _fadeTimer;

        // 所有小羊共享同一套动画帧（素材完全相同，只按需解码一次），
        // 避免 N 只小羊各自重复解码整份素材导致内存/解码开销随数量线性膨胀。
        private static readonly Dictionary<string, Anim> _anims = new();

        private Anim _current = null!;
        private string _currentKey = "休息";
        private int _frameIndex;
        private bool _facingLeft = true;
        private bool _fadingIn = true;
        private bool _closing;
        private string? _restAction;           // 休息时的子动作："吃草" / "休息"
        private DateTime _restUntil;

        // 散养模式：小羊不跟随大羊，自由漫游
        private bool _freeRoam;
        private Point? _roamTarget;
        private DateTime _roamPauseUntil;

        /// <summary>散养开关：为 true 时自由移动，关闭后恢复跟随大羊。</summary>
        public bool FreeRoam
        {
            get => _freeRoam;
            set { _freeRoam = value; _roamTarget = null; _roamPauseUntil = default; }
        }

        public SheepFollowerWindow(MainWindow owner, int index)
        {
            InitializeComponent();
            _owner = owner;
            _index = index;
            // 每只小羊的速度乘子各不相同（稍大一些，且差异明显），使三只速度不同
            _speedFactor = 0.9 + _rng.NextDouble() * 0.35;

            PetImage.RenderTransformOrigin = new Point(0.5, 0.5);
            LoadAnimations();
            SourceInitialized += OnSourceInitialized;

            _playTimer.Tick += PlayTick;
            _moveTimer.Tick += MoveTick;
            _playTimer.Interval = TimeSpan.FromMilliseconds(33);
            _moveTimer.Interval = TimeSpan.FromMilliseconds(30);

            // 从主羊身边露出（比目标位略低，渐显上浮）
            var t = owner.HerdTarget(index);
            Width = Math.Max(20.0, owner.Width / 3.0);
            Height = Width;
            SetCenter(t.X, t.Y + Width * 0.7);

            Play("休息");
            _playTimer.Start();
            _moveTimer.Start();
        }

        // ---------- 素材加载（与主羊同一套立绘，从嵌入资源解码）----------
        private void LoadAnimations()
        {
            AddAnim("休息", "Idle", 11);
            AddAnim("走路", "Walk", 15);
            AddAnim("奔跑", "Run", 18);
            AddAnim("吃草", "Grazing", 15); // 小羊：只加吃草，不读书
        }

        private void AddAnim(string key, string folder, double fps)
        {
            if (_anims.ContainsKey(key)) return; // 静态共享：已加载则跳过重复解码
            var frames = AnimResources.LoadAnim(folder, 160); // 小羊窗口小，用较低解码尺寸省内存
            _anims[key] = new Anim { Frames = frames, Fps = fps, Loop = true };
        }

        // 命中/激活风格位（见 OnSourceInitialized）——伙伴为纯装饰，不回应用户鼠标
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // ---------- 动画播放 ----------
        private void Play(string key)
        {
            if (!_anims.TryGetValue(key, out var a) || a.Frames.Count == 0) return;
            _currentKey = key;
            _current = a;
            _frameIndex = 0;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / a.Fps);
            if (!_playTimer.IsEnabled) _playTimer.Start();
            ShowFrame();
        }

        private void SetLoop(string key)
        {
            if (_currentKey == key && _current.Loop) return;
            Play(key);
        }

        private void ShowFrame()
        {
            var fs = _current.Frames;
            if (_frameIndex >= fs.Count) _frameIndex = fs.Count - 1;
            PetImage.Source = fs[_frameIndex];
        }

        private void PlayTick(object? sender, EventArgs e)
        {
            var a = _current;
            if (a.Frames.Count <= 1) return;
            _frameIndex = (_frameIndex + 1) % a.Frames.Count;
            ShowFrame();
        }

        // ---------- 跟随主羊 ----------
        private void MoveTick(object? sender, EventArgs e)
        {
            if (_closing) return;

            // 渐显
            if (_fadingIn)
            {
                Opacity = Math.Min(1.0, Opacity + 0.10);
                if (Opacity >= 1.0) _fadingIn = false;
            }

            // 体型始终为主羊的 1/3
            double tw = Math.Max(20.0, _owner.Width / 3.0);
            if (Math.Abs(tw - Width) > 0.5)
            {
                double cx = Left + Width / 2;
                double cy = Top + Height / 2;
                Width = tw;
                Height = tw;
                SetCenter(cx, cy);
            }

            // 散养模式：小羊自由移动，不跟随大羊
            if (_freeRoam) { RoamStep(); return; }

            // 跟随大羊：持续朝大羊位置移动，不设停靠边界（不“卡”住）；
            // 允许与大羊重叠，但小羊之间保持互斥、互不重叠。靠近过程每帧限速，不瞬移。
            var mainC = _owner.PetCenter();
            var c = Center();
            double dm = Math.Sqrt((mainC.X - c.X) * (mainC.X - c.X) + (mainC.Y - c.Y) * (mainC.Y - c.Y));

            // 小羊自身也可奔跑：距离大羊越远速度越快（二次曲线），越近越慢；
            // 距离超过 RunDist 时切换为奔跑，否则走路。各小羊 maxSpeed 不同（乘 _speedFactor）。
            double Near = _owner.Width * 0.35;                    // 很接近 → 慢走
            double RunDist = _owner.Width * 0.60;                 // 超过此距离 → 奔跑
            double MaxD = _owner.Width * 1.30;                    // 速度上限对应距离
            double t = Math.Min(1.0, (dm - Near) / Math.Max(1.0, MaxD - Near));
            double maxS = 6.2 * _speedFactor;                     // 各小羊最高速度不同（整体提速）
            double speed = 1.5 + (maxS - 1.5) * t * t;            // 就近慢、远快（二次曲线）

            double mvx = 0, mvy = 0;
            if (dm > 1e-3)                                        // 始终朝大羊位置跟随，不设停靠边界
            {
                mvx = (mainC.X - c.X) / dm;
                mvy = (mainC.Y - c.Y) / dm;
            }

            // 小羊之间互斥：保证彼此不重叠（允许与大羊重叠）
            double sepDist = Width;                              // 一只小羊的边长作为最小间距
            double sepDist2 = sepDist * sepDist;
            for (int j = 0; j < _owner.FollowerCount; j++)
            {
                if (j == _index) continue;
                var pj = _owner.FollowerCenter(j);
                if (double.IsNaN(pj.X)) continue;
                double ox = c.X - pj.X, oy = c.Y - pj.Y;
                double od2 = ox * ox + oy * oy;
                if (od2 > 1e-6 && od2 < sepDist2)               // 平方距离快速排除远处，省 sqrt
                {
                    double od = Math.Sqrt(od2);
                    double k = (sepDist - od) / sepDist * 1.6;   // 错开即停，不硬挤
                    mvx += ox / od * k;
                    mvy += oy / od * k;
                }
            }

            // 与大羊保持一个最小间距：太贴近（几乎叠在中心）时温和外推，
            // 避免初始几只小羊全堆在中央盖住大羊 / 在中心来回抖动
            double standoff = _owner.Width * 0.45;
            if (dm < standoff && dm > 1e-3)
            {
                double k = (standoff - dm) / standoff * 1.0;
                mvx += (c.X - mainC.X) / dm * k;
                mvy += (c.Y - mainC.Y) / dm * k;
            }

            double dMoved = 0;
            if (mvx != 0 || mvy != 0)
            {
                double len = Math.Sqrt(mvx * mvx + mvy * mvy);
                double step = Math.Min(speed, len);
                if (step >= 0.5)          // 位移过小则不移动窗口，避免无谓重绘
                {
                    double nx = c.X + mvx / len * step;
                    double ny = c.Y + mvy / len * step;
                    ClampInScreen(ref nx, ref ny);
                    SetCenter(nx, ny);
                    dMoved = step;
                }
            }

            // 朝向独立：按小羊自身相对大羊的水平方向决定左右镜像。
            // 加水平死区，防止小羊位于大羊正下方/附近时水平位移在正负间抖动导致左右“摇摆”
            double faceX = mainC.X - c.X;
            if (Math.Abs(faceX) > Math.Max(4.0, Width * 0.15))
            {
                bool leftF = faceX < 0;
                if (leftF != _facingLeft)
                {
                    _facingLeft = leftF;
                    PetImage.RenderTransform = leftF ? new ScaleTransform(1, 1) : new ScaleTransform(-1, 1);
                }
            }

            // 休息时偶尔吃草（小羊不读书）
            if (dMoved < 0.4)
            {
                var now = DateTime.Now;
                if (now >= _restUntil)
                {
                    _restAction = _rng.NextDouble() < 0.55 ? "吃草" : "休息";
                    _restUntil = now.AddMilliseconds(_rng.Next(1500, 4000));
                }
                SetLoop(_restAction ?? "休息");
            }
            else
            {
                _restAction = null;
                SetLoop(dm >= RunDist ? "奔跑" : "走路");
            }
        }

        public Point Center() => new(Left + Width / 2, Top + Height / 2);

        // 散养：小羊在屏幕内自由漫游；走步速度慢，目标较远时用奔跑素材快速冲刺；
        // 远方/到点后停下吃草（≥1 分钟）或休息（≥30 秒）。各小羊速度因 _speedFactor 而异。
        private void RoamStep()
        {
            var now = DateTime.Now;
            var c = Center();

            // 停顿吃草/休息：吃草 ≥1 分钟，休息 ≥30 秒
            if (now < _roamPauseUntil)
            {
                SetLoop(_restAction ?? "休息");
                return;
            }
            _restAction = null; // 停顿结束，准备去下一个目标

            // 需要新目标：先看是否原地坐下吃草/休息
            if (_roamTarget == null)
            {
                if (_rng.NextDouble() < 0.35)
                {
                    bool eat = _rng.NextDouble() < 0.5;
                    _restAction = eat ? "吃草" : "休息";
                    _roamPauseUntil = now.AddSeconds(eat ? _rng.Next(60, 121)   // 吃草 ≥1 分钟
                                                          : _rng.Next(30, 61)); // 休息 ≥30 秒
                    return;
                }
                var wa = SystemParameters.WorkArea;
                double m = Width;
                _roamTarget = new Point(
                    _rng.NextDouble() * (wa.Width - m) + m / 2,
                    _rng.NextDouble() * (wa.Height - m) + m / 2);
                return;
            }

            double dx = _roamTarget.Value.X - c.X;
            double dy = _roamTarget.Value.Y - c.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);

            // 到达目标：停下吃草/休息一段时间
            if (d < 12)
            {
                _roamTarget = null;
                bool eat = _rng.NextDouble() < 0.5;
                _restAction = eat ? "吃草" : "休息";
                _roamPauseUntil = now.AddSeconds(eat ? _rng.Next(60, 121)
                                                      : _rng.Next(30, 61));
                return;
            }

            // 距离驱动速度：远 → 奔跑（快，用奔跑素材）；近 → 走路（慢）
            double runDist = Width * 8.0;
            double walkSpeed = 1.1 * _speedFactor;   // 走步慢
            double runSpeed = 5.5 * _speedFactor;    // 奔跑
            double spd = d >= runDist ? runSpeed : walkSpeed;
            double step = Math.Min(spd, d);

            double nx = c.X + dx / d * step;
            double ny = c.Y + dy / d * step;
            ClampInScreen(ref nx, ref ny);
            SetCenter(nx, ny);

            // 散养朝向独立，按水平移动方向决定左右
            if (Math.Abs(dx) > 4)
            {
                bool leftF = dx < 0;
                if (leftF != _facingLeft)
                {
                    _facingLeft = leftF;
                    PetImage.RenderTransform = leftF ? new ScaleTransform(1, 1) : new ScaleTransform(-1, 1);
                }
            }
            SetLoop(d >= runDist ? "奔跑" : "走路");
        }

        private void SetCenter(double cx, double cy)
        {
            Left = cx - Width / 2;
            Top = cy - Height / 2;
        }

        private void ClampInScreen(ref double nx, ref double ny)
        {
            double w = SystemParameters.WorkArea.Width;
            double h = SystemParameters.WorkArea.Height;
            double x = nx - Width / 2;
            double y = ny - Height / 2;
            if (x < 0) x = 0; else if (x > w - Width) x = w - Width;
            if (y < 0) y = 0; else if (y > h - Height) y = h - Height;
            nx = x + Width / 2;
            ny = y + Height / 2;
        }

        // ---------- 消散：渐隐后关闭 ----------
        public void Despawn()
        {
            if (_closing) return;
            _closing = true;
            _moveTimer.Stop();
            if (_fadeTimer == null)
            {
                _fadeTimer = new DispatcherTimer();
                _fadeTimer.Interval = TimeSpan.FromMilliseconds(30);
                _fadeTimer.Tick += (s, ev) =>
                {
                    Opacity -= 0.12;
                    if (Opacity <= 0)
                    {
                        _fadeTimer.Stop();
                        Close();
                    }
                };
            }
            _fadeTimer.Start();
        }

        // ---------- 始终点击穿透（伙伴为纯装饰，不拦截鼠标）----------
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // WS_EX_NOACTIVATE + WS_EX_TRANSPARENT：小羊伙伴纯装饰、完全不参与
            // 鼠标命中/激活，避免大量置顶分层窗口在鼠标悬停时触发系统级重绘与命中链，
            // 从而消除“悬停在小羊上导致整体卡顿”。
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

            var src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            if (msg == WM_NCHITTEST)
            {
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            }
            return IntPtr.Zero;
        }
    }
}