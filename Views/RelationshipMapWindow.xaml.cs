using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;
using PathIO = System.IO.Path;

namespace YangzaiWorkshop.Views;

public partial class RelationshipMapWindow : Window
{
    private readonly string _workRoot, _novelId, _novelName, _mediaFolder;
    private readonly string _stateFile;
    private readonly List<CharacterInfo> _characters;
    private readonly List<MapNode> _nodes = new();
    private readonly Dictionary<string, MapNode> _nodeDict = new();
    private MapNode? _focusedNode;
    private bool _isDragging;
    private Point _dragStart;
    private MapNode? _dragTarget;
    private bool _connectMode;
    private MapNode? _connectFirst;
    private MapNode? _hoveredTarget;
    private double _scale = 1.0;
    private double _panX, _panY;
    private Point _panStart;
    private bool _isPanning;
    private bool _isCanvasClick; // 区分点击和拖拽平移
    private string? _bgImagePath;
    private MapState _mapState = new();
    private const double NodeW = 66, NodeH = 82;

    // 全屏状态
    private bool _isFullscreen;
    private double _normalLeft, _normalTop, _normalWidth, _normalHeight;
    private int _normalCornerRadius = 12; // 正常圆角

    public RelationshipMapWindow(string workRoot, string novelId, string novelName,
        string mediaFolder, List<CharacterInfo> characters)
    {
        _workRoot = workRoot; _novelId = novelId;
        _novelName = novelName; _mediaFolder = mediaFolder;
        _characters = characters;
        _stateFile = PathIO.Combine(FileService.NovelPath(workRoot, novelId), "relationship_map_state.json");
        InitializeComponent();
        RefreshTitle();
        ModeHint.Text = "💡 拖拽节点移动 · 点击聚焦 · 右键编辑 · 空格+新建 · Del=删除选中 · 滚轮缩放 · Esc 关闭";
        Loaded += (_, _) => { LoadState(); BuildGraph(); };
        Activated += (_, _) => SyncNames();
        Closing += (_, _) => SaveState();
    }

    private string MapStateFile => _stateFile;
    private string NovelCharsDir => FileService.NovelCharactersPath(_workRoot, _novelId);

    private void RefreshTitle()
        => MapTitle.Text = $"《{_novelName}》· {_characters.Count} 个角色";

    // ==================== 连线模式 ====================

    private void ConnectModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _connectMode = !_connectMode;
        _connectFirst = null;
        _hoveredTarget = null;
        foreach (var n in _nodes) n.Highlight.Visibility = Visibility.Collapsed;
        DragLine.Visibility = Visibility.Collapsed;

        if (_connectMode)
        {
            ConnectModeBtn.Style = (Style)FindResource("PrimaryButtonStyle");
            ConnectModeBtn.Content = "🔗 连线中…";
            ModeHint.Text = "🔗 连线模式 — 点击第一个节点，再点击第二个节点完成连线。再次点击按钮退出。";
            MainCanvas.Cursor = Cursors.Cross;
        }
        else
        {
            ConnectModeBtn.Style = (Style)FindResource("SecondaryButtonStyle");
            ConnectModeBtn.Content = "🔗 连线模式";
            ModeHint.Text = "💡 拖拽节点移动 · 点击聚焦 · 右键编辑 · 滚轮缩放(跟随指针) · 空白平移 · Esc 关闭";
            MainCanvas.Cursor = Cursors.Arrow;
        }
    }

    private void ClearHoverHighlights()
    {
        if (_hoveredTarget != null && _hoveredTarget != _connectFirst)
        {
            // hover 结束：仅隐藏发光环（颜色由 DropShadowEffect 统一管理）
            _hoveredTarget.Highlight.Visibility = Visibility.Collapsed;
        }
        _hoveredTarget = null;
    }

    // ==================== 全屏切换 ====================

    private void FullScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFullscreen)
        {
            // 进入全屏：保存当前状态
            _normalLeft = Left;
            _normalTop = Top;
            _normalWidth = Width;
            _normalHeight = Height;
            // 最大化并去掉圆角
            var mainBorder = Content as Border;
            if (mainBorder != null)
            {
                mainBorder.CornerRadius = new CornerRadius(0);
                // 标题栏也去圆角
                foreach (var child in LogicalTreeHelper.GetChildren(mainBorder))
                    if (child is Grid g && g.RowDefinitions.Count > 0)
                        foreach (var gc in g.Children)
                            if (gc is Border tbBorder && tbBorder.CornerRadius.TopLeft > 0)
                                tbBorder.CornerRadius = new CornerRadius(0);
            }
            WindowState = WindowState.Maximized;
            FullScreenBtn.Content = "⛶";
            _isFullscreen = true;
        }
        else
        {
            // 退出全屏
            WindowState = WindowState.Normal;
            var mainBorder = Content as Border;
            if (mainBorder != null)
            {
                mainBorder.CornerRadius = new CornerRadius(_normalCornerRadius);
                // 恢复圆角
                foreach (var child in LogicalTreeHelper.GetChildren(mainBorder))
                    if (child is Grid g && g.RowDefinitions.Count > 0)
                        foreach (var gc in g.Children)
                            if (gc is Border tbBorder && tbBorder.CornerRadius.TopLeft == 0)
                                tbBorder.CornerRadius = new CornerRadius(_normalCornerRadius, _normalCornerRadius, 0, 0);
            }
            Left = _normalLeft; Top = _normalTop;
            Width = _normalWidth; Height = _normalHeight;
            FullScreenBtn.Content = "⛶";
            _isFullscreen = false;
        }
    }

    // ==================== 名称同步 ====================

    private void SyncNames()
    {
        foreach (var node in _nodes)
        {
            if (node.NameText.Text != node.Character.Name)
            {
                node.NameText.Text = node.Character.Name;
                foreach (var rel in node.Character.Relationships ?? new())
                    if (_nodeDict.TryGetValue(rel.TargetId, out var t))
                        rel.TargetName = t.Character.Name;
            }
        }
        if (_focusedNode != null) UpdateOpacity(_focusedNode);
    }

    // ==================== 背景 (自动持久化) ====================

    private void ChangeBackground_Click(object sender, RoutedEventArgs e)
    {
        var btn = (FrameworkElement)sender;
        var popup = ShowStyledMenu(btn);
        var sp = popup?.Child is Border bd ? bd.Child as StackPanel : null;
        if (popup == null || sp == null) return;

        AddMenuItem(sp, "默认背景", (_, _) =>
            { _bgImagePath = null; _mapState.BackgroundPath = null; BgImage.Source = null; SaveState(); });
        AddMenuItem(sp, "📁 上传图片…", (_, _) => UploadBg());

        if (_bgImagePath != null)
        {
            AddMenuSeparator(sp);
            AddMenuItem(sp, "移除", (_, _) =>
                { _bgImagePath = null; _mapState.BackgroundPath = null; BgImage.Source = null; SaveState(); });
        }
        popup.IsOpen = true;
    }

    private void UploadBg()
    {
        var dlg = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var b = new BitmapImage(); b.BeginInit(); b.UriSource = new Uri(dlg.FileName);
            b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
            BgImage.Source = b; _bgImagePath = dlg.FileName;
            _mapState.BackgroundPath = dlg.FileName; SaveState();
        }
        catch { }
    }

    // ==================== 状态持久化 ====================

    private void LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                _mapState = JsonSerializer.Deserialize<MapState>(json) ?? new();
                if (!string.IsNullOrEmpty(_mapState.BackgroundPath))
                {
                    _bgImagePath = _mapState.BackgroundPath;
                    if (File.Exists(_bgImagePath))
                    {
                        var b = new BitmapImage(); b.BeginInit();
                        b.UriSource = new Uri(_bgImagePath); b.CacheOption = BitmapCacheOption.OnLoad;
                        b.EndInit(); b.Freeze(); BgImage.Source = b;
                    }
                }
                if (_mapState.Nodes.TryGetValue("__view__", out var vs))
                    { _panX = vs.X; _panY = vs.Y; _scale = vs.Scale; ApplyTransform(); }
            }
        }
        catch { _mapState = new MapState(); }
    }

    private void SaveState()
    {
        // 保存所有节点位置
        _mapState.Nodes.Clear();
        foreach (var n in _nodes)
            _mapState.Nodes[n.Character.Id] = new NodePosState { X = n.X, Y = n.Y };
        // 保存画面状态
        _mapState.Nodes["__view__"] = new NodePosState { X = _panX, Y = _panY, Scale = _scale };
        try
        {
            FileService.EnsureDirectory(PathIO.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_mapState, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }

    // ==================== 构建节点（无框背景，纯头像+名称） ====================

    private void BuildGraph()
    {
        NodesLayer.Children.Clear(); LinesLayer.Children.Clear();
        _nodes.Clear(); _nodeDict.Clear();
        if (_characters.Count == 0) return;

        int n = _characters.Count;
        double cx = 500, cy = 300, r = Math.Min(260, n * 35);

        for (int i = 0; i < n; i++)
        {
            var ch = _characters[i];
            // 优先使用保存位置，否则自动布局
            double x, y;
            if (_mapState.Nodes.TryGetValue(ch.Id, out var saved))
            { x = saved.X; y = saved.Y; }
            else
            { double a = (2 * Math.PI * i / n) - Math.PI / 2;
              x = cx + r * Math.Cos(a) - NodeW / 2; y = cy + r * Math.Sin(a) - NodeH / 2; }

            var node = CreateNode(ch, x, y);
            _nodes.Add(node); _nodeDict[ch.Id] = node;
        }
        DrawAllLines(); UpdateOpacity(null);
    }

    private MapNode CreateNode(CharacterInfo ch, double x, double y)
    {
        // 纯透明容器 Grid
        var root = new Grid { Width = NodeW, Tag = ch, Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition());

        // 头像圆形 48（用 Ellipse + ImageBrush 确保完美圆形）
        var avatarEllipse = new Ellipse { Width = 48, Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
            StrokeThickness = 0, Fill = Brushes.Transparent };
        var af = FileService.CharacterAvatarFile(_workRoot, _novelId, ch.Id);
        if (System.IO.File.Exists(af))
        {
            try
            {
                var d = System.IO.File.ReadAllBytes(af);
                var b = new BitmapImage(); b.BeginInit(); b.StreamSource = new System.IO.MemoryStream(d);
                b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                avatarEllipse.Fill = new ImageBrush(b) { Stretch = Stretch.UniformToFill };
            }
            catch { AddInitialEllipse(avatarEllipse, ch); }
        }
        else AddInitialEllipse(avatarEllipse, ch);
        Grid.SetRow(avatarEllipse, 0); root.Children.Add(avatarEllipse);

        // 名称 —— 使用主题色 + 字体优化（缩放后清晰）
        // 使用 Microsoft YaHei UI 字体并启用 Display 模式 + ClearType，避免缩放后模糊
        var nt = new TextBlock
        {
            Text = ch.Name,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(nt, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(nt, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(nt, TextHintingMode.Fixed);
        Grid.SetRow(nt, 1); root.Children.Add(nt);

        // 聚焦发光层：使用外发光 DropShadowEffect，不再使用棕色实心圈
        // 半径稍大于头像(48→62)，营造"悬浮+边缘发亮"效果
        var hl = new Ellipse
        {
            Width = 62,
            Height = 62,
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, 0, 0),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)Application.Current.Resources["PrimaryColor"],
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.95
            }
        };
        Grid.SetRow(hl, 0); root.Children.Add(hl);

        var node = new MapNode { Character = ch, X = x, Y = y, Root = root, NameText = nt, Highlight = hl };
        Canvas.SetLeft(root, x); Canvas.SetTop(root, y);
        NodesLayer.Children.Add(root);

        root.MouseLeftButtonDown += Node_Click;
        root.MouseMove += Node_MouseMove;
        root.MouseLeave += Node_MouseLeave;
        root.MouseLeftButtonUp += Node_MouseUp;
        root.MouseRightButtonDown += (s, e2) => { e2.Handled = true; UpdateOpacity(node); ShowNodeContextMenu(node); };
        return node;
    }

    private static void AddInitialEllipse(Ellipse ellipse, CharacterInfo ch)
    {
        ellipse.Fill = (Brush)Application.Current.Resources["PrimaryBrush"];
        ellipse.StrokeThickness = 0;
        // 在椭圆下方叠加文字
        var parent = ellipse.Parent as Grid;
        if (parent != null)
        {
            var tb = new TextBlock { Text = ch.Name.Length > 0 ? ch.Name[..1] : "?",
                FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false };
            tb.Margin = ellipse.Margin; tb.Width = 48; tb.Height = 48;
            Grid.SetRow(tb, 0); parent.Children.Add(tb);
        }
    }

    // ==================== 节点交互 ====================

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid g || g.Tag is not CharacterInfo ch || !_nodeDict.TryGetValue(ch.Id, out var node)) return;

        if (_connectMode)
        {
            // 连线模式：两次点击配对
            if (_connectFirst == null)
            {
                // 选中第一个节点 → 点亮主动角色 + 开始拉线
                _connectFirst = node;
                _hoveredTarget = null;
                ClearHoverHighlights();
                node.Highlight.Visibility = Visibility.Visible;
                DragLine.Visibility = Visibility.Visible;
                double cx = node.X + NodeW / 2, cy = node.Y + 40;
                DragLine.X1 = cx; DragLine.Y1 = cy;
                DragLine.X2 = cx; DragLine.Y2 = cy;
                ModeHint.Text = $"🔗 已选「{node.Character.Name}」，请点击目标角色完成连线（不能与自己建立关系）。";
            }
            else if (_connectFirst == node)
            {
                // 点击自己 → 取消选择
                _connectFirst.Highlight.Visibility = Visibility.Collapsed;
                _connectFirst = null;
                _hoveredTarget = null;
                DragLine.Visibility = Visibility.Collapsed;
                ClearHoverHighlights();
                ModeHint.Text = "🔗 连线模式 — 点击第一个节点，再点击第二个节点完成连线。";
            }
            else
            {
                // 选中目标节点 → 双方都高亮（统一发光样式，无棕色实圈）+ 弹出关系对话框
                _connectFirst.Highlight.Visibility = Visibility.Visible;
                node.Highlight.Visibility = Visibility.Visible;
                ModeHint.Text = $"🔗 「{_connectFirst.Character.Name}」→「{node.Character.Name}」，输入关系名称。";
                DragLine.Visibility = Visibility.Collapsed;

                AskRelationship(_connectFirst, node);

                // 对话框关闭后恢复
                _connectFirst.Highlight.Visibility = Visibility.Collapsed;
                node.Highlight.Visibility = Visibility.Collapsed;
                _connectFirst = null;
                _hoveredTarget = null;
                ClearHoverHighlights();
                ModeHint.Text = "🔗 连线模式 — 点击第一个节点，再点击第二个节点完成连线。";
            }
        }
        else
        {
            // 普通模式：准备拖拽或聚焦
            _dragTarget = node; _isDragging = false;
            _dragStart = e.GetPosition(MainCanvas);
            g.CaptureMouse();
        }
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Grid g || g.Tag is not CharacterInfo ch || !_nodeDict.TryGetValue(ch.Id, out var node)) return;

        // 连线模式下检测悬停节点
        if (_connectMode)
        {
            if (_connectFirst != null && DragLine.Visibility == Visibility.Visible && node != _connectFirst)
            {
                // 悬停在目标节点上 → 双方都高亮
                if (node != _hoveredTarget)
                {
                    ClearHoverHighlights();
                    _hoveredTarget = node;
                    _connectFirst.Highlight.Visibility = Visibility.Visible;
                    _hoveredTarget.Highlight.Visibility = Visibility.Visible;
                    ModeHint.Text = $"🔗 「{_connectFirst.Character.Name}」→「{_hoveredTarget.Character.Name}」，点击确认连线。";
                }
            }
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        var cur = e.GetPosition(MainCanvas);
        double dx = cur.X - _dragStart.X, dy = cur.Y - _dragStart.Y;
        if (!_isDragging) { if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return; _isDragging = true; }

        double nx = node.X + dx, ny = node.Y + dy;
        _dragStart = cur;
        if (nx < 0) nx = 0; if (ny < 0) ny = 0;
        node.X = nx; node.Y = ny;
        Canvas.SetLeft(g, nx); Canvas.SetTop(g, ny);
        RedrawLines();
    }

    private void Node_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid g) return;
        if (_connectMode) return;

        if (!_isDragging && _dragTarget != null)
        {
            // 普通点击 → 聚焦
            if (_focusedNode == _dragTarget) UpdateOpacity(null);
            else UpdateOpacity(_dragTarget);
        }
        if (_isDragging) SaveState(); // 拖拽结束保存位置
        _isDragging = false; _dragTarget = null;
        g.ReleaseMouseCapture();
    }

    /// <summary>鼠标离开节点 → 清除连线模式悬停</summary>
    private void Node_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_connectMode || _connectFirst == null) return;
        if (sender is not Grid g || g.Tag is not CharacterInfo ch) return;
        if (!_nodeDict.TryGetValue(ch.Id, out var node)) return;

        if (node == _hoveredTarget)
        {
            ClearHoverHighlights();
            _connectFirst.Highlight.Visibility = Visibility.Visible;
            ModeHint.Text = $"🔗 已选「{_connectFirst.Character.Name}」，请点击目标角色完成连线。";
        }
    }

    // ==================== 连线绘制 ====================

    private void DrawAllLines() { LinesLayer.Children.Clear(); DrawLines(); }
    private void RedrawLines() { LinesLayer.Children.Clear(); DrawLines(); }

    private void DrawLines()
    {
        var drawn = new HashSet<string>();
        foreach (var node in _nodes)
        {
            if (node.Character.Relationships == null) continue;
            foreach (var rel in node.Character.Relationships)
            {
                if (!_nodeDict.TryGetValue(rel.TargetId, out var t)) continue;
                var key = string.CompareOrdinal(node.Character.Id, rel.TargetId) < 0
                    ? $"{node.Character.Id}|{rel.TargetId}|{rel.Relation}"
                    : $"{rel.TargetId}|{node.Character.Id}|{rel.Relation}";
                if (drawn.Contains(key)) continue; drawn.Add(key);
                bool hl = _focusedNode != null && (node == _focusedNode || t == _focusedNode);
                DrawLine(node, t, rel.Relation, hl);
            }
        }
    }

    private void DrawLine(MapNode a, MapNode b, string label, bool hl)
    {
        double cx1 = a.X + NodeW / 2, cy1 = a.Y + 40;
        double cx2 = b.X + NodeW / 2, cy2 = b.Y + 40;
        double dx = cx2 - cx1, dy = cy2 - cy1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01) return;
        // 端点内缩：避免线段刺入节点圆形
        double tm = 30;
        double startX = cx1 + dx / len * tm, startY = cy1 + dy / len * tm;
        double endX = cx2 - dx / len * tm, endY = cy2 - dy / len * tm;

        // 直线（取代之前的贝塞尔曲线）：使用简单的 Line 几何
        var lineBrush = hl
            ? (Brush)Application.Current.Resources["PrimaryBrush"]
            : new SolidColorBrush(Color.FromArgb(0xB0, 0x9A, 0x8B, 0x7A));
        var line = new System.Windows.Shapes.Line
        {
            X1 = startX, Y1 = startY,
            X2 = endX, Y2 = endY,
            Stroke = lineBrush,
            StrokeThickness = hl ? 2.6 : 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
        };
        // 聚焦时附加柔和发光（外阴影）让连线更醒目
        if (hl)
        {
            line.Effect = new DropShadowEffect
            {
                Color = (Color)Application.Current.Resources["PrimaryColor"],
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.55
            };
        }
        LinesLayer.Children.Add(line);

        // 隐形加粗 Path 用于点击检测（用 Path 复盖 Line，便于事件触发）：
        // 为了点击区域，使用一个透明 Path 沿直线段展开
        var hitGeom = new LineGeometry(new Point(startX, startY), new Point(endX, endY));
        var hitPath = new System.Windows.Shapes.Path
        {
            Stroke = Brushes.Transparent,
            StrokeThickness = 14,
            Cursor = Cursors.Hand,
            Data = hitGeom,
            Tag = new LineHitInfo { FromNode = a, ToNode = b, RelationLabel = label }
        };
        hitPath.MouseLeftButtonUp += Line_Click;
        hitPath.MouseRightButtonDown += Line_RightClick;
        hitPath.MouseEnter += Line_MouseEnter;
        hitPath.MouseLeave += Line_MouseLeave;
        LinesLayer.Children.Add(hitPath);

        // 关系标签：玻璃质感背景 + 阴影 + 优化字体（位于线段中点）
        double mx = (startX + endX) / 2;
        double my = (startY + endY) / 2;
        var lbText = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 11,
            FontWeight = hl ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = hl
                ? (Brush)Application.Current.Resources["PrimaryBrush"]
                : new SolidColorBrush(Color.FromRgb(0x35, 0x2A, 0x22)),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        TextOptions.SetTextRenderingMode(lbText, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(lbText, TextFormattingMode.Display);
        var lb = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFB, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xC0, 0x70, 0x40)),
            BorderThickness = new Thickness(0.8),
            Child = lbText,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Color.FromArgb(0x30, 0, 0, 0), BlurRadius = 6, ShadowDepth = 0, Opacity = 0.22 }
        };
        lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 标签放置在线段中点（略微上移，避免压在直线上）
        Canvas.SetLeft(lb, mx - lb.DesiredSize.Width / 2); Canvas.SetTop(lb, my - lb.DesiredSize.Height - 4);
        LinesLayer.Children.Add(lb);
    }

    private MapNode? _hoveredLineFrom, _hoveredLineTo;
    private string? _hoveredLineRelation;

    private void Line_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Path p || p.Tag is not LineHitInfo info) return;
        _hoveredLineFrom = info.FromNode;
        _hoveredLineTo = info.ToNode;
        _hoveredLineRelation = info.RelationLabel;
        // 悬停这条线：找到对应的可见 Line 并加粗 + 加发光
        int idx = LinesLayer.Children.IndexOf(p);
        if (idx > 0 && LinesLayer.Children[idx - 1] is System.Windows.Shapes.Line visLine)
        {
            visLine.Stroke = (Brush)Application.Current.Resources["PrimaryBrush"];
            visLine.StrokeThickness = 3.0;
            visLine.Effect = new DropShadowEffect
            {
                Color = (Color)Application.Current.Resources["PrimaryColor"],
                BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7
            };
        }
    }

    private void Line_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Path p || p.Tag is not LineHitInfo info) return;
        // 恢复线样式
        int idx = LinesLayer.Children.IndexOf(p);
        if (idx > 0 && LinesLayer.Children[idx - 1] is System.Windows.Shapes.Line visLine)
        {
            bool hl = _focusedNode != null && (info.FromNode == _focusedNode || info.ToNode == _focusedNode);
            visLine.Stroke = hl
                ? (Brush)Application.Current.Resources["PrimaryBrush"]
                : new SolidColorBrush(Color.FromArgb(0xB0, 0x9A, 0x8B, 0x7A));
            visLine.StrokeThickness = hl ? 2.6 : 1.8;
            // 聚焦时仍保留发光
            visLine.Effect = hl
                ? new DropShadowEffect
                {
                    Color = (Color)Application.Current.Resources["PrimaryColor"],
                    BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55
                }
                : null;
        }
        _hoveredLineFrom = null; _hoveredLineTo = null; _hoveredLineRelation = null;
    }

    private void Line_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Line l || l.Tag is not LineHitInfo info) return;
        // 左键点击连线 → 聚焦两端的节点，高亮显示关系
        UpdateOpacity(info.FromNode);
        ModeHint.Text = $"🔗 「{info.FromNode.Character.Name}」—「{info.RelationLabel}」—「{info.ToNode.Character.Name}」（右键编辑）";
    }

    private void Line_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Line l || l.Tag is not LineHitInfo info) return;
        e.Handled = true;

        var popup = ShowStyledMenu();
        var sp = popup?.Child is Border bd ? bd.Child as StackPanel : null;
        if (popup == null || sp == null) return;

        AddMenuItem(sp, $"✏️ 编辑关系：「{info.RelationLabel}」", (_, _) => EditRelationship(info));
        AddMenuSeparator(sp);
        AddMenuItem(sp,
            $"🗑 删除关系：「{info.FromNode.Character.Name}」→「{info.ToNode.Character.Name}」",
            (_, _) =>
            {
                info.FromNode.Character.Relationships?.RemoveAll(r => r.TargetId == info.ToNode.Character.Id && r.Relation == info.RelationLabel);
                info.ToNode.Character.Relationships?.RemoveAll(r => r.TargetId == info.FromNode.Character.Id && r.Relation == info.RelationLabel);
                RedrawLines(); SaveState();
            }, Brushes.Red);

        popup.IsOpen = true;
    }

    /// <summary>编辑已有关系的名称</summary>
    private void EditRelationship(LineHitInfo info)
    {
        var from = info.FromNode;
        var to = info.ToNode;
        var oldRel = info.RelationLabel;

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = $"编辑关系：「{from.Character.Name}」→「{to.Character.Name}」", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(new TextBlock { Text = "关系名称：", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 0, 0, 4) });
        var tb = new TextBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = oldRel }; sp.Children.Add(tb);
        var tags = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var t in new[] { "父亲", "母亲", "兄弟", "姐妹", "朋友", "恋人", "敌人", "师徒", "同事", "青梅竹马" })
        { var b = new Button { Content = t, FontSize = 10, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4), Style = (Style)FindResource("SecondaryButtonStyle") }; b.Click += (_, _) => tb.Text = t; tags.Children.Add(b); }
        sp.Children.Add(tags);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var cancel = new Button { Content = "取消", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "确定", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("PrimaryButtonStyle") };
        btnRow.Children.Add(cancel); btnRow.Children.Add(ok); sp.Children.Add(btnRow);
        var dlg = new Window { Title = "编辑人物关系", Width = 380,
            SizeToContent = SizeToContent.Height, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"], Padding = new Thickness(0, 0, 0, 12) } };
        tb.Focus(); tb.SelectAll();
        ok.Click += (_, _) => dlg.DialogResult = true; cancel.Click += (_, _) => dlg.DialogResult = false;
        if (dlg.ShowDialog() != true) return;
        var rn = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(rn) || rn == oldRel) return;
        // 更新双方的关系名称
        foreach (var r in from.Character.Relationships ?? new())
            if (r.TargetId == to.Character.Id && r.Relation == oldRel) r.Relation = rn;
        foreach (var r in to.Character.Relationships ?? new())
            if (r.TargetId == from.Character.Id && r.Relation == oldRel) r.Relation = rn;
        RedrawLines(); SaveState();
    }

    // ==================== 关系对话框（自定义名称 + 快捷标签） ====================

    private void AskRelationship(MapNode from, MapNode to)
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = $"「{from.Character.Name}」→「{to.Character.Name}」", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "关系名称：", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 0, 0, 4) });
        var tb = new TextBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(tb);
        var tags = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var t in new[] { "父亲", "母亲", "兄弟", "姐妹", "朋友", "恋人", "敌人", "师徒", "同事", "青梅竹马" })
        { var b = new Button { Content = t, FontSize = 10, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4), Style = (Style)FindResource("SecondaryButtonStyle") }; b.Click += (_, _) => tb.Text = t; tags.Children.Add(b); }
        sp.Children.Add(tags);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var cancel = new Button { Content = "取消", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "确定", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("PrimaryButtonStyle") };
        btnRow.Children.Add(cancel); btnRow.Children.Add(ok); sp.Children.Add(btnRow);

        var dlg = new Window { Title = "创建人物关系", Width = 380,
            SizeToContent = SizeToContent.Height, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"], Padding = new Thickness(0, 0, 0, 12) } };
        tb.Focus();
        ok.Click += (_, _) => dlg.DialogResult = true; cancel.Click += (_, _) => dlg.DialogResult = false;
        if (dlg.ShowDialog() != true) return;
        var rn = tb.Text.Trim(); if (string.IsNullOrWhiteSpace(rn)) return;
        if (from.Character.Relationships.Any(r => r.TargetId == to.Character.Id && r.Relation == rn)) return;
        from.Character.Relationships.Add(new CharacterRelationship { TargetId = to.Character.Id, TargetName = to.Character.Name, Relation = rn });
        RedrawLines(); UpdateOpacity(from);
    }

    // ==================== 画布（跟随指针缩放） ====================

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Canvas c && (c == MainCanvas || c == LinesLayer || c == NodesLayer))
        { _isPanning = true; _isCanvasClick = true; _panStart = e.GetPosition(this); MainCanvas.Cursor = Cursors.ScrollAll; MainCanvas.CaptureMouse(); }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        // 连线模式：虚线跟随鼠标
        if (_connectMode && _connectFirst != null && DragLine.Visibility == Visibility.Visible)
        {
            var cur = e.GetPosition(MainCanvas);
            double cx = _connectFirst.X + NodeW / 2, cy = _connectFirst.Y + 40;
            DragLine.X1 = cx; DragLine.Y1 = cy;
            DragLine.X2 = cur.X; DragLine.Y2 = cur.Y;

            // 鼠标在画布空白区域 → 清除悬停高亮
            if (_hoveredTarget != null)
            {
                ClearHoverHighlights();
                _connectFirst.Highlight.Visibility = Visibility.Visible;
                ModeHint.Text = $"🔗 已选「{_connectFirst.Character.Name}」，请点击目标角色完成连线。";
            }
        }

        // 平移模式
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed) return;
        var cur2 = e.GetPosition(this);
        _panX += cur2.X - _panStart.X; _panY += cur2.Y - _panStart.Y;
        _panStart = cur2; ApplyTransform();
        _isCanvasClick = false; // 发生了实际移动 → 不是点击
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 画布空白处点击 → 退出聚焦状态
        if (_isCanvasClick && !_connectMode && _focusedNode != null)
            UpdateOpacity(null);
        _isPanning = false; _isCanvasClick = false; MainCanvas.Cursor = Cursors.Arrow; MainCanvas.ReleaseMouseCapture();
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 跟随指针缩放
        var ptr = e.GetPosition(MainCanvas);
        double oldScale = _scale;
        _scale += e.Delta > 0 ? 0.06 : -0.06;
        _scale = Math.Clamp(_scale, 0.25, 3.0);

        // 调整平移量使指针位置不变
        double ratio = _scale / oldScale;
        _panX = ptr.X - ratio * (ptr.X - _panX);
        _panY = ptr.Y - ratio * (ptr.Y - _panY);
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(_scale, _scale));
        g.Children.Add(new TranslateTransform(_panX, _panY));
        MainCanvas.RenderTransform = g;
    }

    private void ResetView_Click(object sender, RoutedEventArgs e)
    { _panX = _panY = 0; _scale = 1.0; MainCanvas.RenderTransform = Transform.Identity; BuildGraph(); }

    /// <summary>画布右键 → 新建角色</summary>
    private void Canvas_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Canvas) return;
        e.Handled = true;

        var popup = ShowStyledMenu();
        var sp = popup?.Child is Border bd ? bd.Child as StackPanel : null;
        if (popup == null || sp == null) return;

        AddMenuItem(sp, "➕ 新建角色…", (_, _) => DoCreateCharacter());

        popup.IsOpen = true;
    }

    // ==================== 聚焦 ====================

    private void UpdateOpacity(MapNode? f)
    {
        _focusedNode = f;
        var ids = new HashSet<string>();
        if (f != null)
        {
            ids.Add(f.Character.Id);
            // 被聚焦角色指向的目标
            if (f.Character.Relationships != null)
                foreach (var r in f.Character.Relationships)
                    ids.Add(r.TargetId);
            // 指向被聚焦角色的节点（反向关系）
            foreach (var node in _nodes)
                if (node.Character.Relationships != null)
                    foreach (var r in node.Character.Relationships)
                        if (r.TargetId == f.Character.Id)
                            ids.Add(node.Character.Id);
        }
        // 其他角色：0.62 透明度（柔和暗化，便于聚焦主要关系，但不会太暗）
        foreach (var n in _nodes) n.Root.Opacity = f == null || ids.Contains(n.Character.Id) ? 1.0 : 0.62;
        // 聚焦节点：发光环（替代棕色实圈）+ 轻微放大产生悬浮感
        foreach (var n in _nodes)
        {
            bool isFocused = n == f && !_connectMode;
            n.Highlight.Visibility = isFocused ? Visibility.Visible : Visibility.Collapsed;
            // 缩放：聚焦 1.08，其他 1.0（中心放大，向四周轻微"悬浮"）
            n.Root.LayoutTransform = isFocused
                ? new ScaleTransform(1.08, 1.08, NodeW / 2, NodeH / 2)
                : Transform.Identity;
        }
        RedrawLines();
    }

    // ==================== 右键菜单（自建 Popup，100% 可控） ====================

    private Popup? _activeMenuPopup;

    /// <summary>在指定位置显示自定义右键菜单</summary>
    /// <param name="placementTarget">非null时弹窗相对于此元素定位（如按钮点击）；null时使用鼠标当前位置（右键菜单）</param>
    private Popup ShowStyledMenu(UIElement? placementTarget = null)
    {
        // 先关掉之前的
        CloseMenu();

        var app = Application.Current;
        var bgBrush = (Brush)app.Resources["WindowBackgroundBrush"];
        var borderBrush = (Brush)app.Resources["BorderBrush"];

        var sp = new StackPanel { MinWidth = 160 };

        var border = new Border
        {
            Child = sp,
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Effect = new DropShadowEffect { Color = Color.FromArgb(0x60, 0, 0, 0), BlurRadius = 16, ShadowDepth = 4, Opacity = 0.45 },
            SnapsToDevicePixels = true,
        };

        var popup = new Popup
        {
            Child = border,
            PlacementTarget = placementTarget ?? this,
            Placement = placementTarget != null ? PlacementMode.Bottom : PlacementMode.MousePoint,
            AllowsTransparency = true,
            StaysOpen = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        _activeMenuPopup = popup;

        // 在下一个输入事件中注册全局点击关闭
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_activeMenuPopup == popup)
                AddHandler(PreviewMouseDownEvent, (MouseButtonEventHandler)OnWindowPreviewMouseDown, true);
        }), System.Windows.Threading.DispatcherPriority.Input);

        return popup;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击在 Popup 外部 → 关闭
        CloseMenu();
    }

    private void CloseMenu()
    {
        if (_activeMenuPopup != null)
        {
            RemoveHandler(PreviewMouseDownEvent, (MouseButtonEventHandler)OnWindowPreviewMouseDown);
            _activeMenuPopup.IsOpen = false;
            _activeMenuPopup = null;
        }
    }

    /// <summary>添加一个菜单项到 Popup 菜单中</summary>
    private Border AddMenuItem(StackPanel menuPanel, string text, RoutedEventHandler? onClick, Brush? foreground = null)
    {
        var app = Application.Current;
        var textBrush = foreground ?? (Brush)app.Resources["TextPrimaryBrush"];
        var primaryBrush = (Brush)app.Resources["PrimaryBrush"];

        var itemBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(3, 2, 3, 2),
            Padding = new Thickness(14, 8, 14, 8),
            Cursor = Cursors.Hand,
            Tag = textBrush,
        };

        var tb = new TextBlock { Text = text, FontSize = 13, Foreground = textBrush };
        itemBorder.Child = tb;
        menuPanel.Children.Add(itemBorder);

        if (onClick != null)
        {
            itemBorder.PreviewMouseLeftButtonUp += (_, e2) =>
            {
                CloseMenu();
                onClick(itemBorder, new RoutedEventArgs());
                e2.Handled = true;
            };
        }

        itemBorder.MouseEnter += (s, _) =>
        {
            if (s is Border b)
            {
                b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4A, 0x90, 0x79));
                ((TextBlock)b.Child).Foreground = primaryBrush;
            }
        };
        itemBorder.MouseLeave += (s, _) =>
        {
            if (s is Border b && b.Tag is Brush originalBrush)
            {
                b.Background = Brushes.Transparent;
                ((TextBlock)b.Child).Foreground = originalBrush;
            }
        };

        return itemBorder;
    }

    /// <summary>添加分隔线</summary>
    private static void AddMenuSeparator(StackPanel menuPanel)
    {
        menuPanel.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 10, Y1 = 0, X2 = 150, Y2 = 0,
            Stroke = (Brush)Application.Current.Resources["BorderBrush"],
            StrokeThickness = 1,
            Margin = new Thickness(6, 3, 6, 3),
            SnapsToDevicePixels = true,
        });
    }

    private void ShowNodeContextMenu(MapNode node)
    {
        var popup = ShowStyledMenu();
        var sp = popup?.Child is Border bd ? bd.Child as StackPanel : null;
        if (popup == null || sp == null) return;

        AddMenuItem(sp, "✏️ 修改名称…", (_, _) => RenameCharacter(node));
        AddMenuItem(sp, "🖼 更换头像…", (_, _) => ChangeAvatar(node));
        AddMenuSeparator(sp);
        AddMenuItem(sp, "➕ 添加关系…", (_, _) => AddRelDialog(node));

        foreach (var r in node.Character.Relationships ?? new())
        {
            var tn = _nodeDict.TryGetValue(r.TargetId, out var t) ? t.Character.Name : r.TargetName;
            if (string.IsNullOrEmpty(tn)) tn = "(已删除)";
            var cap = r;
            AddMenuSeparator(sp);
            AddMenuItem(sp, $"🗑 删除关系「{r.Relation} → {tn}」", (_, _) =>
            {
                node.Character.Relationships?.Remove(cap); RedrawLines(); SaveState();
            });
        }

        AddMenuSeparator(sp);
        AddMenuItem(sp, "❌ 删除角色", (_, _) => DeleteCharacter(node), Brushes.Red);

        popup.IsOpen = true;
    }

    private void AddRelDialog(MapNode from)
    {
        var others = _characters.Where(c => c.Id != from.Character.Id).ToList(); if (others.Count == 0) return;
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = $"为「{from.Character.Name}」添加关系", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(new TextBlock { Text = "目标角色：", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 0, 0, 4) });
        var cb = new ComboBox { ItemsSource = others.Select(c => c.Name).ToList(), SelectedIndex = 0, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(cb);
        sp.Children.Add(new TextBlock { Text = "关系名称：", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 0, 0, 4) });
        var tb = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8) }; sp.Children.Add(tb);
        var br = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var can = new Button { Content = "取消", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "确定", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("PrimaryButtonStyle") };
        br.Children.Add(can); br.Children.Add(ok); sp.Children.Add(br);
        var dlg = new Window { Title = "添加关系", Width = 340,
            SizeToContent = SizeToContent.Height, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"], Padding = new Thickness(0, 0, 0, 12) } };
        ok.Click += (_, _) => dlg.DialogResult = true; can.Click += (_, _) => dlg.DialogResult = false;
        if (dlg.ShowDialog() != true) return;
        var rn = tb.Text.Trim(); if (string.IsNullOrWhiteSpace(rn)) return;
        var tgt = others[cb.SelectedIndex];
        if (from.Character.Relationships.Any(r => r.TargetId == tgt.Id && r.Relation == rn)) return;
        from.Character.Relationships.Add(new CharacterRelationship { TargetId = tgt.Id, TargetName = tgt.Name, Relation = rn });
        RedrawLines();
    }

    // ==================== 角色 CRUD ====================

    private void CreateCharacter_Click(object sender, RoutedEventArgs e) => DoCreateCharacter();

    private void DoCreateCharacter()
    {
        // 自动生成唯一名称：新角色、新角色2、新角色3… 避免重名
        var existing = _characters.Select(c => c.Name).ToHashSet();
        string name = "新角色";
        int idx = 2; // 第一次出现"新角色"直接用，再后续的为"新角色2"开始
        while (existing.Contains(name)) { name = $"新角色{idx}"; idx++; }

        var nc = new CharacterInfo
        {
            Id = "新角色_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString()[..4],
            Name = name,
            Personality = ""
        };
        var cp = FileService.CharacterPath(_workRoot, _novelId, nc.Id);
        if (Directory.Exists(cp))
        { nc.Id += "_" + Guid.NewGuid().ToString()[..4]; cp = FileService.CharacterPath(_workRoot, _novelId, nc.Id); }
        FileService.EnsureDirectory(cp);
        FileService.EnsureDirectory(PathIO.Combine(cp, "images"));
        FileService.WriteJson(PathIO.Combine(cp, "info.json"), nc);

        _characters.Add(nc);
        RefreshTitle();

        // 自动布局到画布中心附近
        double cx = 500 + new Random().Next(-100, 100);
        double cy = 300 + new Random().Next(-80, 80);
        var node = CreateNode(nc, cx, cy);
        _nodes.Add(node); _nodeDict[nc.Id] = node;
        UpdateOpacity(_focusedNode); // 保持原有焦点
    }

    private void DeleteCharacter(MapNode node)
    {
        if (!MessageDialog.Confirm("确认删除", $"确定删除角色「{node.Character.Name}」吗？\n该角色的所有关系也将被清除。")) return;

        // 清理所有与此角色相关的关系
        foreach (var n in _nodes)
        {
            n.Character.Relationships?.RemoveAll(r => r.TargetId == node.Character.Id);
        }

        // 从列表中移除
        _characters.Remove(node.Character);

        // 移到回收站
        var p = FileService.CharacterPath(_workRoot, _novelId, node.Character.Id);
        if (Directory.Exists(p)) FileService.MoveToTrash(p);

        // 从画布移除节点
        NodesLayer.Children.Remove(node.Root);
        _nodes.Remove(node);
        _nodeDict.Remove(node.Character.Id);

        // 清除聚焦
        if (_focusedNode == node) _focusedNode = null;
        UpdateOpacity(null);
        RefreshTitle();
        SaveState();
    }

    private void RenameCharacter(MapNode node)
    {
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = $"修改「{node.Character.Name}」的名称：", FontSize = 13,
            FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Margin = new Thickness(0, 0, 0, 10) });
        var tb = new TextBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = node.Character.Name };
        sp.Children.Add(tb);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "确定", Height = 28, FontSize = 12,
            Padding = new Thickness(14,0,14,0), MinWidth = 62,
            Style = (Style)FindResource("PrimaryButtonStyle") };
        btnRow.Children.Add(cancel); btnRow.Children.Add(ok); sp.Children.Add(btnRow);

        var dlg = new Window { Title = "修改角色名称", Width = 340,
            SizeToContent = SizeToContent.Height, MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"], Padding = new Thickness(0, 0, 0, 12) } };
        tb.Focus(); tb.SelectAll();
        ok.Click += (_, _) => dlg.DialogResult = true; cancel.Click += (_, _) => dlg.DialogResult = false;
        if (dlg.ShowDialog() != true) return;

        var newName = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Character.Name) return;

        node.Character.Name = newName;
        node.NameText.Text = newName;
        // 同步关系中的 TargetName
        foreach (var n in _nodes)
            foreach (var r in n.Character.Relationships ?? new())
                if (r.TargetId == node.Character.Id) r.TargetName = newName;
        // 写回 info.json
        var ip = FileService.CharacterInfoFile(_workRoot, _novelId, node.Character.Id);
        FileService.WriteJson(ip, node.Character);
        SaveState();
    }

    private void ChangeAvatar(MapNode node)
    {
        var dlg = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 使用裁剪窗口
            var crop = new CropWindow(dlg.FileName, square: true);
            crop.Owner = this;
            if (crop.ShowDialog() != true || crop.CroppedImage == null) return;

            // 保存裁剪后的图片
            var targetPath = FileService.CharacterAvatarFile(_workRoot, _novelId, node.Character.Id);
            var dir = PathIO.GetDirectoryName(targetPath);
            if (dir != null) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(targetPath, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(crop.CroppedImage));
                encoder.Save(fs);
            }

            // 更新节点头像：加载裁剪后的图片
            var b = new BitmapImage(); b.BeginInit();
            b.UriSource = new Uri(targetPath); b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit(); b.Freeze();
            var brush = new ImageBrush(b) { Stretch = Stretch.UniformToFill };

            // 更新 Ellipse 并把占位文字移除
            var toRemove = new List<UIElement>();
            foreach (var child in node.Root.Children)
            {
                if (child is Ellipse e && Math.Abs(e.Width - 48) < 0.1 && Math.Abs(e.Height - 48) < 0.1)
                    e.Fill = brush;
                else if (child is TextBlock tb && Math.Abs(tb.Width - 48) < 0.1 && Math.Abs(tb.Height - 48) < 0.1)
                    toRemove.Add(tb); // 占位字母
            }
            foreach (var r in toRemove) node.Root.Children.Remove(r);
        }
        catch { }
    }

    // ==================== 窗口 ====================
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        if (e.Key == Key.R) UpdateOpacity(null);
        // 空格创建角色：仅在焦点不在可输入控件（TextBox/PasswordBox/ComboBox）时触发，
        // 避免误吞用户在重命名/编辑关系对话框中输入的空格
        if (e.Key == Key.Space)
        {
            var focus = Keyboard.FocusedElement;
            if (focus is TextBox || focus is PasswordBox || focus is ComboBox) return;
            DoCreateCharacter();
            e.Handled = true;
        }
        if (e.Key == Key.Delete && _focusedNode != null) { DeleteCharacter(_focusedNode); e.Handled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal class MapNode
{
    public CharacterInfo Character { get; init; } = null!;
    public double X { get; set; }
    public double Y { get; set; }
    public Grid Root { get; init; } = null!;
    public TextBlock NameText { get; init; } = null!;
    public Ellipse Highlight { get; init; } = null!;
}

internal class MapState
{
    public string? BackgroundPath { get; set; }
    public Dictionary<string, NodePosState> Nodes { get; set; } = new();
}

internal class NodePosState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;
}

/// <summary>关系线点击检测数据</summary>
internal class LineHitInfo
{
    public MapNode FromNode { get; init; } = null!;
    public MapNode ToNode { get; init; } = null!;
    public string RelationLabel { get; init; } = null!;
}
