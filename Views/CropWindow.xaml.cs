using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YangzaiWorkshop.Views;

public partial class CropWindow : Window
{
    public BitmapSource? CroppedImage { get; private set; }
    public event Action<BitmapSource>? Cropped;

    private readonly BitmapImage _src;

    // 拖拽状态
    private bool _dragging;
    private Point _dragStart;
    private double _dragX0, _dragY0, _dragW0, _dragH0;
    private enum DragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private DragMode _dragMode;

    // 锁定比例（null = 自由裁剪）
    private double? _ratioW, _ratioH;
    private bool _suppressInput;

    public CropWindow(string imagePath, bool square = true)
    {
        InitializeComponent();
        _src = new BitmapImage();
        _src.BeginInit();
        _src.UriSource = new Uri(imagePath);
        _src.CacheOption = BitmapCacheOption.OnLoad;
        _src.EndInit();
        _src.Freeze();
        Img.Source = _src;

        // 初始比例 + 高亮对应按钮
        if (square)
        {
            SetRatio(1, 1);
            HighlightActiveBtn(Btn1x1);
        }
        else
        {
            SetRatio(3, 4);
            HighlightActiveBtn(Btn3x4);
        }

        Loaded += (_, _) => Dispatcher.BeginInvoke(Init, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(RePosition, DispatcherPriority.Loaded);
    }

    // ==================== 按钮高亮 ====================

    private void HighlightActiveBtn(Button active)
    {
        foreach (var b in new Button[] { Btn1x1, Btn3x4, Btn4x5, Btn9x16, Btn16x9, BtnFree })
        {
            if (b == active)
            {
                b.Background = (Brush)FindResource("PrimaryBrush");
                b.Foreground = Brushes.White;
                b.BorderBrush = (Brush)FindResource("PrimaryBrush");
                b.BorderThickness = new Thickness(1);
            }
            else
            {
                b.Background = (Brush)FindResource("CardBackgroundBrush");
                b.Foreground = (Brush)FindResource("TextSecondaryBrush");
                b.BorderBrush = (Brush)FindResource("BorderBrush");
                b.BorderThickness = new Thickness(1);
            }
            b.Cursor = Cursors.Hand;
            b.Padding = new Thickness(0);
        }
    }

    // ==================== 比例 ====================

    private void SetRatio(double w, double h)
    {
        _ratioW = w; _ratioH = h;
        RatioLabel.Text = $"{w}:{h}";
        RatioBadge.Visibility = Visibility.Visible;
        SyncSizeInputs();
    }

    private void ClearRatio()
    {
        _ratioW = null; _ratioH = null;
        RatioLabel.Text = "自由";
        RatioBadge.Visibility = Visibility.Collapsed;
        SyncSizeInputs();
    }

    // ==================== 按钮事件 ====================

    private void RatioBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        HighlightActiveBtn(btn);

        if (tag == "free") { ClearRatio(); return; }
        var parts = tag.Split(',');
        if (parts.Length == 2 && double.TryParse(parts[0], out var rw) && double.TryParse(parts[1], out var rh))
        {
            SetRatio(rw, rh);
            AdjustToRatio();
        }
    }

    private void AdjustToRatio()
    {
        if (_ratioW == null || _ratioH == null) return;
        double rw = _ratioW.Value, rh = _ratioH.Value;
        if (rw <= 0 || rh <= 0) return;
        double newH = Sel.Width * rh / rw;
        var (r, _) = RenderRect();
        newH = Math.Max(30, Math.Min(newH, r.Height * 0.95));
        double cx = Canvas.GetLeft(Sel) + Sel.Width / 2;
        double cy = Canvas.GetTop(Sel) + Sel.Height / 2;
        Place(Sel.Width, newH, cx - Sel.Width / 2, cy - newH / 2);
    }

    // ==================== 尺寸输入 ====================

    private void SyncSizeInputs()
    {
        _suppressInput = true;
        WInput.Text = ((int)Math.Round(Sel.Width)).ToString();
        HInput.Text = ((int)Math.Round(Sel.Height)).ToString();
        _suppressInput = false;
    }

    private void SizeInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressInput) return;
        if (int.TryParse(WInput.Text, out int w) && int.TryParse(HInput.Text, out int h) && w > 5 && h > 5)
        {
            if (_ratioW != null && _ratioH != null)
                h = (int)(w * _ratioH.Value / _ratioW.Value);
            var (r, _) = RenderRect();
            w = Math.Max(10, Math.Min(w, (int)r.Width));
            h = Math.Max(10, Math.Min(h, (int)r.Height));
            double cx = Canvas.GetLeft(Sel) + Sel.Width / 2;
            double cy = Canvas.GetTop(Sel) + Sel.Height / 2;
            Place(w, h, cx - w / 2, cy - h / 2);
        }
    }

    // ==================== 渲染 ====================

    private (Rect r, double s) RenderRect()
    {
        double gw = CropGrid.ActualWidth, gh = CropGrid.ActualHeight;
        double iw = _src.PixelWidth, ih = _src.PixelHeight;
        if (iw <= 0 || ih <= 0 || gw < 5 || gh < 5)
            return (new Rect(0, 0, gw, gh), 1);
        double s = Math.Min(gw / iw, gh / ih);
        double rw = iw * s, rh = ih * s;
        double ox = (gw - rw) / 2, oy = (gh - rh) / 2;
        return (new Rect(ox, oy, rw, rh), s);
    }

    private void Init()
    {
        var (r, _) = RenderRect();
        if (r.Width < 10 || r.Height < 10) return;

        if (_ratioW != null && _ratioH != null)
        {
            double ratio = _ratioW.Value / _ratioH.Value;
            double sz = Math.Min(r.Width, r.Height * ratio) * 0.65;
            double w0 = Math.Max(30, sz);
            double h0 = Math.Max(30, w0 / ratio);
            w0 = Math.Min(w0, r.Width * 0.9);
            h0 = Math.Min(h0, r.Height * 0.9);
            Sel.Width = w0; Sel.Height = h0;
            Place(w0, h0, r.Left + (r.Width - w0) / 2, r.Top + (r.Height - h0) / 2);
        }
        else
        {
            double sz = Math.Min(r.Width, r.Height) * 0.65;
            sz = Math.Max(20, sz);
            Sel.Width = sz; Sel.Height = sz;
            Place(sz, sz, r.Left + (r.Width - sz) / 2, r.Top + (r.Height - sz) / 2);
        }
        SyncSizeInputs();
    }

    private void RePosition()
    {
        var (r, _) = RenderRect();
        if (r.Width < 10) return;
        double w = Math.Max(20, Math.Min(Sel.Width, r.Width * 0.98));
        double h = Math.Max(20, Math.Min(Sel.Height, r.Height * 0.98));
        double cx = Canvas.GetLeft(Sel) + Sel.Width / 2;
        double cy = Canvas.GetTop(Sel) + Sel.Height / 2;
        cx = Math.Max(r.Left + w / 2, Math.Min(r.Right - w / 2, cx));
        cy = Math.Max(r.Top + h / 2, Math.Min(r.Bottom - h / 2, cy));
        Place(w, h, cx - w / 2, cy - h / 2);
    }

    private void Place(double w, double h, double x, double y)
    {
        var (r, _) = RenderRect();
        x = Math.Max(r.Left, Math.Min(r.Right - w, x));
        y = Math.Max(r.Top, Math.Min(r.Bottom - h, y));
        Canvas.SetLeft(Sel, x); Canvas.SetTop(Sel, y);
        Sel.Width = w; Sel.Height = h;
        UpdateHandles(x, y, w, h);
        UpdateMasks(x, y, w, h);
        SyncSizeInputs();
    }

    private void UpdateHandles(double sx, double sy, double w, double h)
    {
        double hs = 5;
        Canvas.SetLeft(HandleTL, sx - hs); Canvas.SetTop(HandleTL, sy - hs);
        Canvas.SetLeft(HandleTR, sx + w - hs); Canvas.SetTop(HandleTR, sy - hs);
        Canvas.SetLeft(HandleBL, sx - hs); Canvas.SetTop(HandleBL, sy + h - hs);
        Canvas.SetLeft(HandleBR, sx + w - hs); Canvas.SetTop(HandleBR, sy + h - hs);
    }

    private void UpdateMasks(double sx, double sy, double w, double h)
    {
        double gw = CropGrid.ActualWidth, gh = CropGrid.ActualHeight;
        Canvas.SetLeft(MskTop, 0); Canvas.SetTop(MskTop, 0);
        MskTop.Width = gw; MskTop.Height = sy;
        Canvas.SetLeft(MskBot, 0); Canvas.SetTop(MskBot, sy + h);
        MskBot.Width = gw; MskBot.Height = Math.Max(0, gh - sy - h);
        Canvas.SetLeft(MskLeft, 0); Canvas.SetTop(MskLeft, sy);
        MskLeft.Width = sx; MskLeft.Height = h;
        Canvas.SetLeft(MskRight, sx + w); Canvas.SetTop(MskRight, sy);
        MskRight.Width = Math.Max(0, gw - sx - w); MskRight.Height = h;
    }

    // ==================== 鼠标事件 ====================

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(EventCanvas);
        _dragX0 = Canvas.GetLeft(Sel); _dragY0 = Canvas.GetTop(Sel);
        _dragW0 = Sel.Width; _dragH0 = Sel.Height;

        if (sender == HandleTL) _dragMode = DragMode.ResizeTL;
        else if (sender == HandleTR) _dragMode = DragMode.ResizeTR;
        else if (sender == HandleBL) _dragMode = DragMode.ResizeBL;
        else _dragMode = DragMode.ResizeBR;

        EventCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        var pt = e.GetPosition(EventCanvas);
        double sx = Canvas.GetLeft(Sel), sy = Canvas.GetTop(Sel);
        if (pt.X >= sx - 8 && pt.X <= sx + Sel.Width + 8 &&
            pt.Y >= sy - 8 && pt.Y <= sy + Sel.Height + 8)
        {
            _dragging = true;
            _dragMode = DragMode.Move;
            _dragStart = pt;
            _dragX0 = sx; _dragY0 = sy;
            _dragW0 = Sel.Width; _dragH0 = Sel.Height;
            EventCanvas.CaptureMouse();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pt = e.GetPosition(EventCanvas);
        double dx = pt.X - _dragStart.X;
        double dy = pt.Y - _dragStart.Y;

        if (_dragMode == DragMode.Move)
        {
            Place(_dragW0, _dragH0, _dragX0 + dx, _dragY0 + dy);
        }
        else
        {
            double nx = _dragX0, ny = _dragY0, nw = _dragW0, nh = _dragH0;
            switch (_dragMode)
            {
                case DragMode.ResizeTL: nx += dx; ny += dy; nw -= dx; nh -= dy; break;
                case DragMode.ResizeTR: ny += dy; nw += dx; nh -= dy; break;
                case DragMode.ResizeBL: nx += dx; nw -= dx; nh += dy; break;
                case DragMode.ResizeBR: nw += dx; nh += dy; break;
            }
            nw = Math.Max(20, nw); nh = Math.Max(20, nh);
            var (r, _) = RenderRect();

            if (_ratioW != null && _ratioH != null)
            {
                double ratio = _ratioW.Value / _ratioH.Value;
                if (Math.Abs(dx) > Math.Abs(dy))
                { nw = Math.Max(20, nw); nh = nw / ratio; }
                else
                { nh = Math.Max(20, nh); nw = nh * ratio; }

                if (_dragMode == DragMode.ResizeTL || _dragMode == DragMode.ResizeBL)
                    nx = _dragX0 + _dragW0 - nw;
                if (_dragMode == DragMode.ResizeTL || _dragMode == DragMode.ResizeTR)
                    ny = _dragY0 + _dragH0 - nh;
            }

            if (nx < r.Left) { nw -= (r.Left - nx); nx = r.Left; }
            if (ny < r.Top) { nh -= (r.Top - ny); ny = r.Top; }
            if (nx + nw > r.Right) nw = r.Right - nx;
            if (ny + nh > r.Bottom) nh = r.Bottom - ny;
            nw = Math.Max(20, nw); nh = Math.Max(20, nh);

            Place(nw, nh, nx, ny);
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragMode = DragMode.None;
        EventCanvas.ReleaseMouseCapture();
    }

    private void CropGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double d = e.Delta > 0 ? 12 : -12;
        double w = Math.Max(30, Sel.Width + d);
        double h = _ratioW != null && _ratioH != null
            ? w * _ratioH.Value / _ratioW.Value
            : Math.Max(30, Sel.Height + d);
        h = Math.Max(20, h);
        double cx = Canvas.GetLeft(Sel) + Sel.Width / 2;
        double cy = Canvas.GetTop(Sel) + Sel.Height / 2;
        var (r, _) = RenderRect();
        w = Math.Min(w, r.Width * 0.95); h = Math.Min(h, r.Height * 0.95);
        Place(w, h, cx - w / 2, cy - h / 2);
    }

    // ==================== 确认 / 取消 ====================

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (r, s) = RenderRect();
            double sx = Canvas.GetLeft(Sel), sy = Canvas.GetTop(Sel);
            double sw = Sel.Width, sh = Sel.Height;
            int px = (int)Math.Round((sx - r.Left) / s);
            int py = (int)Math.Round((sy - r.Top) / s);
            int pw = (int)Math.Round(sw / s);
            int ph = (int)Math.Round(sh / s);
            px = Math.Max(0, Math.Min(_src.PixelWidth - 1, px));
            py = Math.Max(0, Math.Min(_src.PixelHeight - 1, py));
            pw = Math.Max(1, Math.Min(pw, _src.PixelWidth - px));
            ph = Math.Max(1, Math.Min(ph, _src.PixelHeight - py));
            CroppedImage = new CroppedBitmap(_src, new Int32Rect(px, py, pw, ph));
            DialogResult = true;
            Cropped?.Invoke(CroppedImage);
            Close();
        }
        catch { MessageDialog.Show("错误", "裁剪失败"); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
