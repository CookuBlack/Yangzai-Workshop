using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopPet
{
    /// <summary>
    /// 设置窗口：宠物大小、奔跑速度、小羊数量（滑动条 + 可手动输入并钳制上下限），
    /// 以及时间显示选项（是否显示秒、24/12 小时制）。通过事件把新值回传给主窗口实时应用。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private bool _ready;

        /// <summary>大小改变事件（参数为新尺寸，逻辑像素）。</summary>
        public event Action<double>? ResizeApplied;
        /// <summary>速度改变事件（参数为新奔跑速度）。</summary>
        public event Action<double>? SpeedChanged;
        /// <summary>小羊数量改变事件（参数为新的小羊数量，1~50）。</summary>
        public event Action<int>? HerdCountChanged;
        /// <summary>时间显示选项改变事件（是否显示秒、是否 24 小时制）。</summary>
        public event Action<bool, bool>? TimeOptionsChanged;
        /// <summary>打开软件时自动打开宠物选项改变事件（参数为是否开启）。</summary>
        public event Action<bool>? AutoOpenPetChanged;

        public SettingsWindow()
        {
            InitializeComponent();
            _ready = true;
        }

        // 批量初始化所有滑条/复选框值，抑制事件回传，避免逐个 Set 时触发其它滑条的默认值回传
        public void InitValues(double size, double speed, int herdCount, bool showSeconds, bool use24Hour, bool autoOpenPet)
        {
            _ready = false;
            SizeSlider.Value = size;
            SpeedSlider.Value = speed;
            HerdSlider.Value = herdCount;
            ShowSecondsCheck.IsChecked = showSeconds;
            Use24hRadio.IsChecked = use24Hour;
            Use12hRadio.IsChecked = !use24Hour;
            AutoOpenPetCheck.IsChecked = autoOpenPet;
            _ready = true;
            UpdateSizeLabel();
            UpdateSpeedLabel();
            UpdateHerdLabel();
        }

        // 读取窗口当前生效值：供关闭时回传主窗口并持久化
        public double CurrentSize => SizeSlider.Value;
        public double CurrentSpeed => SpeedSlider.Value;
        public int CurrentHerdCount => (int)Math.Round(HerdSlider.Value);
        public bool CurrentShowSeconds => ShowSecondsCheck.IsChecked == true;
        public bool CurrentUse24Hour => Use24hRadio.IsChecked == true;
        public bool CurrentAutoOpenPet => AutoOpenPetCheck.IsChecked == true;

        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSizeLabel();
            if (_ready) ResizeApplied?.Invoke(SizeSlider.Value);
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSpeedLabel();
            if (_ready) SpeedChanged?.Invoke(SpeedSlider.Value);
        }

        private void HerdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateHerdLabel();
            if (_ready) HerdCountChanged?.Invoke((int)HerdSlider.Value);
        }

        private void UpdateSizeLabel()
        {
            if (SizeInput == null || SizeSlider == null) return;
            SizeInput.Text = ((int)SizeSlider.Value).ToString() + " px";
        }

        private void UpdateSpeedLabel()
        {
            if (SpeedInput == null || SpeedSlider == null) return;
            SpeedInput.Text = SpeedSlider.Value.ToString("0.0");
        }

        private void UpdateHerdLabel()
        {
            if (HerdInput == null || HerdSlider == null) return;
            HerdInput.Text = ((int)HerdSlider.Value).ToString();
        }

        // ---------- 手动输入：失焦 / 回车时解析并钳制到 [Minimum, Maximum] ----------

        private void SizeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            double v = ParseClamp(SizeInput.Text, SizeSlider.Minimum, SizeSlider.Maximum, SizeSlider.Value);
            SizeSlider.Value = v;
            SizeInput.Text = ((int)v).ToString() + " px";
        }

        private void SpeedInput_LostFocus(object sender, RoutedEventArgs e)
        {
            double v = ParseClamp(SpeedInput.Text, SpeedSlider.Minimum, SpeedSlider.Maximum, SpeedSlider.Value);
            SpeedSlider.Value = v;
            SpeedInput.Text = v.ToString("0.0");
        }

        private void HerdInput_LostFocus(object sender, RoutedEventArgs e)
        {
            int v = (int)Math.Round(ParseClamp(HerdInput.Text, HerdSlider.Minimum, HerdSlider.Maximum, HerdSlider.Value));
            HerdSlider.Value = v;
            HerdInput.Text = v.ToString();
        }

        private void NumInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus(); // 触发对应 LostFocus，完成解析与钳制
                e.Handled = true;
            }
        }

        // 提取文本中的数值（忽略单位/多余字符），越界则钳制到上下限
        private static double ParseClamp(string text, double min, double max, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var sb = new System.Text.StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+') sb.Append(ch);
            }
            if (!double.TryParse(sb.ToString(), out var v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        private void TimeOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            bool showSeconds = ShowSecondsCheck.IsChecked == true;
            bool use24 = Use24hRadio.IsChecked == true;
            TimeOptionsChanged?.Invoke(showSeconds, use24);
        }

        private void AutoOpenPet_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) AutoOpenPetChanged?.Invoke(CurrentAutoOpenPet);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // 窗口关闭前：把尚未提交的手动输入（文本框）解析并写入滑动条，
        // 触发 ValueChanged → 回传主窗口，确保最终值不会被丢弃。
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            CommitInputs();
            PetSettings.Log(
                $"SettingsWindow 关闭提交 -> Size={CurrentSize:0} Speed={CurrentSpeed:0.0} " +
                $"HerdCount={CurrentHerdCount} 显示秒={CurrentShowSeconds} 24小时={CurrentUse24Hour}");
        }

        // 将三个数值输入框的当前文本提交到对应滑动条（含上下限钳制）
        public void CommitInputs()
        {
            if (SizeInput != null && SizeSlider != null)
            {
                string raw = SizeInput.Text;
                SizeInput_LostFocus(SizeInput, new RoutedEventArgs());
                PetSettings.Log($"提交大小输入 \"{raw}\" -> 滑动条={SizeSlider.Value:0}");
            }
            if (SpeedInput != null && SpeedSlider != null)
            {
                string raw = SpeedInput.Text;
                SpeedInput_LostFocus(SpeedInput, new RoutedEventArgs());
                PetSettings.Log($"提交速度输入 \"{raw}\" -> 滑动条={SpeedSlider.Value:0.0}");
            }
            if (HerdInput != null && HerdSlider != null)
            {
                string raw = HerdInput.Text;
                HerdInput_LostFocus(HerdInput, new RoutedEventArgs());
                PetSettings.Log($"提交数量输入 \"{raw}\" -> 滑动条={HerdSlider.Value:0}");
            }
        }
    }
}
