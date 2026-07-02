using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            // 恢复目标节点高亮环为默认颜色（PrimaryBrush），然后隐藏
            _hoveredTarget.Highlight.Stroke = (Brush)Application.Current.Resources["PrimaryBrush"];
            _hoveredTarget.Highlight.Visibility = Visibility.Collapsed;
        }
        _hoveredTarget = null;
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
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xEF, 0xE6)) };
        var d = new MenuItem { Header = "默认背景" };
        d.Click += (_, _) => { _bgImagePath = null; _mapState.BackgroundPath = null; BgImage.Source = null; SaveState(); };
        menu.Items.Add(d);
        var u = new MenuItem { Header = "📁 上传图片…" }; u.Click += (_, _) => UploadBg(); menu.Items.Add(u);
        if (_bgImagePath != null)
        {
            menu.Items.Add(new Separator());
            var c = new MenuItem { Header = "移除" };
            c.Click += (_, _) => { _bgImagePath = null; _mapState.BackgroundPath = null; BgImage.Source = null; SaveState(); };
            menu.Items.Add(c);
        }
        menu.IsOpen = true;
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

        // 名称 —— 使用主题色
        var nt = new TextBlock { Text = ch.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetRow(nt, 1); root.Children.Add(nt);

        // 高亮环（聚焦时显示）
        var hl = new Ellipse { Width = 52, Height = 52, Stroke = (Brush)Application.Current.Resources["PrimaryBrush"],
            StrokeThickness = 2.2, Fill = Brushes.Transparent, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0) };
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
                // 选中目标节点 → 双方都高亮 + 弹出关系对话框
                _connectFirst.Highlight.Visibility = Visibility.Visible;
                node.Highlight.Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x7C, 0x3C)); // 橙色 = 被动方
                node.Highlight.Visibility = Visibility.Visible;
                ModeHint.Text = $"🔗 「{_connectFirst.Character.Name}」→「{node.Character.Name}」，输入关系名称。";
                DragLine.Visibility = Visibility.Collapsed;

                AskRelationship(_connectFirst, node);

                // 对话框关闭后恢复
                _connectFirst.Highlight.Visibility = Visibility.Collapsed;
                node.Highlight.Stroke = (Brush)Application.Current.Resources["PrimaryBrush"];
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
                    _hoveredTarget.Highlight.Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x7C, 0x3C)); // 橙色
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
        double ux = dx / len, uy = dy / len, tm = 32;
        var line = new Line
        {
            X1 = cx1 + ux * tm, Y1 = cy1 + uy * tm,
            X2 = cx2 - ux * tm, Y2 = cy2 - uy * tm,
            Stroke = hl ? (Brush)Application.Current.Resources["PrimaryBrush"] : new SolidColorBrush(Color.FromRgb(0x90, 0x86, 0x76)),
            StrokeThickness = hl ? 3 : 1.8,
            StrokeDashArray = hl ? null : new DoubleCollection(new[] { 5.0, 4.0 })
        };
        LinesLayer.Children.Add(line);

        double mx = (line.X1 + line.X2) / 2, my = (line.Y1 + line.Y2) / 2 - 16;
        var lb = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFA, 0xF6, 0xED)),
            Child = new TextBlock { Text = label, FontSize = 9, Foreground = hl ? (Brush)Application.Current.Resources["PrimaryBrush"] : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), FontWeight = hl ? FontWeights.Bold : FontWeights.Normal } };
        lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(lb, mx - lb.DesiredSize.Width / 2); Canvas.SetTop(lb, my);
        LinesLayer.Children.Add(lb);
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

        var dlg = new Window { Title = "创建人物关系", Width = 380, Height = 270, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false, Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"] } };
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
        if (e.OriginalSource is not Canvas) return; // 只处理画布空白处右键
        e.Handled = true;
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xEF, 0xE6))
        };
        var create = new MenuItem { Header = "➕ 新建角色…" };
        create.Click += (_, _) => DoCreateCharacter();
        menu.Items.Add(create);
        menu.IsOpen = true;
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
        foreach (var n in _nodes) n.Root.Opacity = f == null || ids.Contains(n.Character.Id) ? 1.0 : 0.12;
        // 高亮环
        foreach (var n in _nodes) n.Highlight.Visibility = n == f && !_connectMode ? Visibility.Visible : Visibility.Collapsed;
        RedrawLines();
    }

    // ==================== 右键菜单 ====================

    private void ShowNodeContextMenu(MapNode node)
    {
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xEF, 0xE6)) };

        var edit = new MenuItem { Header = "✏️ 修改名称…" };
        edit.Click += (_, _) => RenameCharacter(node); menu.Items.Add(edit);

        var av = new MenuItem { Header = "🖼 更换头像…" };
        av.Click += (_, _) => ChangeAvatar(node); menu.Items.Add(av);

        menu.Items.Add(new Separator());

        var addRel = new MenuItem { Header = "➕ 添加关系…" };
        addRel.Click += (_, _) => AddRelDialog(node); menu.Items.Add(addRel);

        var rs = node.Character.Relationships ?? new();
        if (rs.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var r in rs)
            {
                var tn = _nodeDict.TryGetValue(r.TargetId, out var t) ? t.Character.Name : r.TargetName;
                if (string.IsNullOrEmpty(tn)) tn = "(已删除)";
                var d = new MenuItem { Header = $"🗑 删除关系「{r.Relation} → {tn}」" };
                var cap = r;
                d.Click += (_, _) => { node.Character.Relationships?.Remove(cap); RedrawLines(); SaveState(); };
                menu.Items.Add(d);
            }
        }

        menu.Items.Add(new Separator());
        var del = new MenuItem { Header = "❌ 删除角色", Foreground = Brushes.Red };
        del.Click += (_, _) => DeleteCharacter(node); menu.Items.Add(del);

        menu.IsOpen = true;
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
        var dlg = new Window { Title = "添加关系", Width = 340, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false, Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"] } };
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
        var nc = new CharacterInfo
        {
            Id = "新角色_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            Name = "新角色",
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

        var dlg = new Window { Title = "修改角色名称", Width = 340, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = new Border { Child = sp, Background = (Brush)Application.Current.Resources["WindowBackgroundBrush"] } };
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
        if (e.Key == Key.Space) { DoCreateCharacter(); e.Handled = true; }
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
