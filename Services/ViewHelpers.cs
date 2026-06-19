using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YangzaiWorkshop.Services;

/// <summary>跨页面复用工具方法</summary>
public static class ViewHelpers
{
    /// <summary>对 UIElement 应用圆角矩形裁切</summary>
    public static void ApplyRoundedClip(UIElement el, double radius = 6)
    {
        double w = (el is FrameworkElement fe) ? fe.ActualWidth : el.RenderSize.Width;
        double h = (el is FrameworkElement fe2) ? fe2.ActualHeight : el.RenderSize.Height;
        if (w > 0 && h > 0)
            el.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    /// <summary>安全解析色值字符串为 SolidColorBrush</summary>
    public static SolidColorBrush ParseColor(string hex, string fallback = "#4A90E2")
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }

    /// <summary>数值友好格式化（万/k）</summary>
    public static string FormatNumber(long n)
    {
        if (n >= 10000) return $"{n / 10000.0:F1}万";
        if (n >= 1000) return $"{n / 1000.0:F1}k";
        return n.ToString();
    }

    /// <summary>封面占位文字</summary>
    public static TextBlock CoverPlaceholder(string name, int maxChars = 2, double fontSize = 22)
    {
        var clipped = name.Length > 0 ? name[..Math.Min(maxChars, name.Length)] : "?";
        return new TextBlock
        {
            Text = clipped,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>图片查看器（支持滚轮缩放 + 左键拖动平移 + 圆角裁切）</summary>
    public static void ShowImageViewer(string path, Window? owner = null)
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            var win = new Window
            {
                Title = System.IO.Path.GetFileName(path),
                Width = Math.Min(wa.Width * 0.85, 1400),
                Height = Math.Min(wa.Height * 0.85, 900),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Background = Brushes.Black,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var zoom = new ScaleTransform(1, 1);
            var pan = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(zoom);
            group.Children.Add(pan);
            img.RenderTransform = group;
            img.RenderTransformOrigin = new Point(0.5, 0.5);

            var outer = new Border();
            outer.SizeChanged += (s, _) =>
            {
                var b = (Border)s;
                if (b.ActualWidth > 0 && b.ActualHeight > 0)
                    b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 12, 12);
            };
            outer.Child = img;
            win.Content = outer;

            outer.MouseWheel += (_, e) =>
            {
                double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
                double ns = Math.Max(0.2, Math.Min(8, zoom.ScaleX * f));
                zoom.ScaleX = ns; zoom.ScaleY = ns;
            };

            Point? panStart = null;
            double stx = 0, sty = 0;
            img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 1)
                { panStart = e.GetPosition(outer); stx = pan.X; sty = pan.Y; img.CaptureMouse(); }
                else win.Close();
            };
            img.MouseMove += (_, e) =>
            {
                if (panStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
                {
                    var cur = e.GetPosition(outer);
                    pan.X = stx + (cur.X - panStart.Value.X);
                    pan.Y = sty + (cur.Y - panStart.Value.Y);
                }
            };
            img.MouseLeftButtonUp += (_, _) => { panStart = null; img.ReleaseMouseCapture(); };
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
            win.Show();
        }
        catch { }
    }
}
