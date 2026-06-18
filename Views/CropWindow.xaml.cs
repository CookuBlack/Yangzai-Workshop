using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YangzaiWorkshop.Views;

public partial class CropWindow : Window
{
    public BitmapSource? CroppedImage { get; private set; }

    private readonly BitmapImage _sourceImage;
    private Point _dragStart;
    private bool _isDragging;
    private double _cropSize;

    public CropWindow(string imagePath)
    {
        InitializeComponent();
        _sourceImage = new BitmapImage();
        _sourceImage.BeginInit();
        _sourceImage.UriSource = new Uri(imagePath);
        _sourceImage.CacheOption = BitmapCacheOption.OnLoad;
        _sourceImage.EndInit();
        SourceImage.Source = _sourceImage;

        Loaded += (_, _) => InitCrop();
    }

    private void InitCrop()
    {
        // 默认正方形选区（取较短边）
        double imgW = SourceImage.ActualWidth;
        double imgH = SourceImage.ActualHeight;
        _cropSize = Math.Min(imgW, imgH) * 0.7;
        double x = (imgW - _cropSize) / 2;
        double y = (imgH - _cropSize) / 2;
        Canvas.SetLeft(CropRect, x);
        Canvas.SetTop(CropRect, y);
        CropRect.Width = _cropSize;
        CropRect.Height = _cropSize;
    }

    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(SourceImage);
        // 检查是否点在选区内部
        double rx = Canvas.GetLeft(CropRect);
        double ry = Canvas.GetTop(CropRect);
        if (_dragStart.X >= rx - 10 && _dragStart.X <= rx + _cropSize + 10 &&
            _dragStart.Y >= ry - 10 && _dragStart.Y <= ry + _cropSize + 10)
        {
            _isDragging = true;
            SourceImage.CaptureMouse();
        }
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(SourceImage);
        double imgW = SourceImage.ActualWidth;
        double imgH = SourceImage.ActualHeight;
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;

        double newX = Canvas.GetLeft(CropRect) + dx;
        double newY = Canvas.GetTop(CropRect) + dy;

        newX = Math.Max(0, Math.Min(imgW - _cropSize, newX));
        newY = Math.Max(0, Math.Min(imgH - _cropSize, newY));

        Canvas.SetLeft(CropRect, newX);
        Canvas.SetTop(CropRect, newY);
        _dragStart = pos;
    }

    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        SourceImage.ReleaseMouseCapture();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            double imgW = SourceImage.ActualWidth;
            double imgH = SourceImage.ActualHeight;
            int srcW = _sourceImage.PixelWidth;
            int srcH = _sourceImage.PixelHeight;

            double scaleX = srcW / imgW;
            double scaleY = srcH / imgH;

            int x = (int)(Canvas.GetLeft(CropRect) * scaleX);
            int y = (int)(Canvas.GetTop(CropRect) * scaleY);
            int size = (int)(_cropSize * Math.Min(scaleX, scaleY));

            x = Math.Max(0, Math.Min(srcW - size, x));
            y = Math.Max(0, Math.Min(srcH - size, y));
            size = Math.Min(size, srcW - x);
            size = Math.Min(size, srcH - y);

            CroppedImage = new CroppedBitmap(_sourceImage, new Int32Rect(x, y, size, size));
            DialogResult = true;
            Close();
        }
        catch
        {
            MessageBox.Show("裁剪失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
