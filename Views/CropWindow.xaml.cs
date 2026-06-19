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
    private readonly bool _square;
    private Point _dragOrigin;
    private double _dragX0, _dragY0;
    private bool _dragging;

    public CropWindow(string imagePath, bool square = true)
    {
        InitializeComponent();
        _square = square;
        _src = new BitmapImage();
        _src.BeginInit();
        _src.UriSource = new Uri(imagePath);
        _src.CacheOption = BitmapCacheOption.OnLoad;
        _src.EndInit();
        _src.Freeze();
        Img.Source = _src;

        Loaded += (_, _) => Dispatcher.BeginInvoke(Init, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(RePosition, DispatcherPriority.Loaded);
    }

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
        if (_square)
        {
            double sz = Math.Min(r.Width, r.Height) * 0.65;
            sz = Math.Max(20, sz);
            Sel.Width = sz; Sel.Height = sz;
            Place(sz, sz, r.Left + (r.Width - sz) / 2, r.Top + (r.Height - sz) / 2);
        }
        else
        {
            double h = r.Height * 0.85;
            double w = h * 0.75;
            h = Math.Max(30, Math.Min(h, r.Height));
            w = Math.Max(20, Math.Min(w, r.Width));
            Sel.Width = w; Sel.Height = h;
            Place(w, h, r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2);
        }
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
        UpdateMasks(x, y, w, h);
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

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(EventCanvas);
        double sx = Canvas.GetLeft(Sel), sy = Canvas.GetTop(Sel);
        if (pt.X >= sx - 12 && pt.X <= sx + Sel.Width + 12 &&
            pt.Y >= sy - 12 && pt.Y <= sy + Sel.Height + 12)
        {
            _dragging = true;
            _dragOrigin = pt;
            _dragX0 = sx; _dragY0 = sy;
            EventCanvas.CaptureMouse();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pt = e.GetPosition(EventCanvas);
        Place(Sel.Width, Sel.Height,
              _dragX0 + (pt.X - _dragOrigin.X),
              _dragY0 + (pt.Y - _dragOrigin.Y));
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        EventCanvas.ReleaseMouseCapture();
    }

    private void CropGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double d = e.Delta > 0 ? 12 : -12;
        double w = Math.Max(30, Sel.Width + d);
        double h = _square ? w : Math.Max(30, Sel.Height + d);
        h = Math.Max(20, h);
        double cx = Canvas.GetLeft(Sel) + Sel.Width / 2;
        double cy = Canvas.GetTop(Sel) + Sel.Height / 2;
        Place(w, h, cx - w / 2, cy - h / 2);
    }

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
            Cropped?.Invoke(CroppedImage);
            Close();
        }
        catch { MessageBox.Show("裁剪失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
