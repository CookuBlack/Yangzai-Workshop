using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopPet
{
    /// <summary>输入自定义工作时长（分钟）的小对话框，返回 1~120 的整数；取消返回 null。</summary>
    public partial class DurationInputDialog : Window
    {
        public int? ResultMinutes { get; private set; }

        public DurationInputDialog(int defaultMinutes = 25)
        {
            InitializeComponent();
            MinuteBox.Text = defaultMinutes.ToString();
            MinuteBox.Focus();
            MinuteBox.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(MinuteBox.Text.Trim(), out var v) && v >= 1 && v <= 120)
            {
                ResultMinutes = v;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(this, "请输入 1~120 之间的整数分钟。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Ok_Click(sender, e);
            else if (e.Key == Key.Escape) DialogResult = false;
        }
    }
}
