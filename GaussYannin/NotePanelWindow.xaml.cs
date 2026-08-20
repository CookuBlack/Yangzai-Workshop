using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;

namespace DesktopPet
{
    /// <summary>
    /// 面板窗口：RichTextBox 富文本编辑，支持选中文字后右键设置字号、加粗、斜体、颜色。
    /// 可打开多个实例，始终置顶显示。
    /// </summary>
    public partial class NotePanelWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private bool _midDragging;
        private double _dpiScale = 1.0;
        private int _cursorStartX, _cursorStartY;
        private double _winStartX, _winStartY;

        public NotePanelWindow()
        {
            InitializeComponent();
        }

        // ---------- 鼠标中键拖动窗口 ----------
        // 用原始屏幕坐标(GetCursorPos)计算位移，避开“窗口移动→窗口内相对坐标变化”的
        // 反馈回路，避免拖动时窗口晃动。
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                GetCursorPos(out POINT p);
                _cursorStartX = p.X;
                _cursorStartY = p.Y;
                _winStartX = Left;
                _winStartY = Top;
                _midDragging = true;
                e.Handled = true;
                CaptureMouse();
            }
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_midDragging && e.MiddleButton == MouseButtonState.Pressed)
            {
                GetCursorPos(out POINT p);
                // 物理像素位移 → 逻辑像素(DIP)
                var dx = (p.X - _cursorStartX) / _dpiScale;
                var dy = (p.Y - _cursorStartY) / _dpiScale;
                Left = _winStartX + dx;
                Top = _winStartY + dy;
            }
            else if (_midDragging)
            {
                _midDragging = false;
                ReleaseMouseCapture();
            }
        }

        // ---------- 字号 ----------
        private void FontSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string s ||
                !double.TryParse(s, out var size)) return;
            ApplyToSelection(TextElement.FontSizeProperty, size);
        }

        // ---------- 加粗 ----------
        private void Bold_Click(object sender, RoutedEventArgs e)
        {
            var weight = FontWeightFromSelection();
            ApplyToSelection(TextElement.FontWeightProperty,
                weight == FontWeights.Bold ? FontWeights.Normal : FontWeights.Bold);
        }

        // ---------- 斜体 ----------
        private void Italic_Click(object sender, RoutedEventArgs e)
        {
            var style = FontStyleFromSelection();
            ApplyToSelection(TextElement.FontStyleProperty,
                style == FontStyles.Italic ? FontStyles.Normal : FontStyles.Italic);
        }

        // ---------- 对齐方式 ----------
        private void Align_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string align) return;
            var ta = align switch
            {
                "Left" => TextAlignment.Left,
                "Center" => TextAlignment.Center,
                "Right" => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            var sel = NoteBox.Selection;
            if (sel.IsEmpty) return;
            // 从选中起始段落开始，遍历到选中结束的所有段落，设置对齐方式
            var p = sel.Start.Paragraph;
            while (p != null)
            {
                if (p.ContentEnd.CompareTo(sel.Start) >= 0) p.TextAlignment = ta;
                if (p.ContentEnd.CompareTo(sel.End) >= 0) break;
                p = p.NextBlock as Paragraph;
            }
        }

        // ---------- 颜色 ----------
        private void Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string colorStr) return;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                ApplyToSelection(TextElement.ForegroundProperty, new SolidColorBrush(color));
            }
            catch { }
        }

        // ---------- 公共：对选中文本应用依赖属性值 ----------
        private void ApplyToSelection(DependencyProperty dp, object value)
        {
            var sel = NoteBox.Selection;
            if (sel.IsEmpty) return;
            // 若选中文本已应用了该属性值，则清除（复原）
            object? current = GetSelectionPropertyValue(dp);
            if (current != null && current.Equals(value))
            {
                sel.ApplyPropertyValue(dp, DependencyProperty.UnsetValue);
                return;
            }
            sel.ApplyPropertyValue(dp, value);
        }

        // 读取当前选中文本的某个属性值（取第一个文字run的值）
        private object? GetSelectionPropertyValue(DependencyProperty dp)
        {
            var sel = NoteBox.Selection;
            if (sel.IsEmpty) return null;
            var start = sel.Start;
            if (start == null) return null;
            var parent = start.Parent as TextElement;
            return parent?.GetValue(dp);
        }

        private FontWeight FontWeightFromSelection()
        {
            var v = GetSelectionPropertyValue(TextElement.FontWeightProperty);
            if (v is FontWeight fw) return fw;
            return FontWeights.Normal;
        }

        private FontStyle FontStyleFromSelection()
        {
            var v = GetSelectionPropertyValue(TextElement.FontStyleProperty);
            if (v is FontStyle fs) return fs;
            return FontStyles.Normal;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}