using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YangzaiWorkshop.Views;

public partial class MessageDialog : Window
{
    public bool Confirmed { get; private set; }
    public bool CheckBoxChecked { get; private set; }

    public MessageDialog(string title, string message, bool showCancel = false,
        string? checkBoxText = null)
    {
        InitializeComponent();
        // 以最前台窗口为 Owner，确保弹窗在最前
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
             ?? Application.Current.MainWindow;
        TitleBlock.Text = title;
        MessageBlock.Text = message;

        if (showCancel)
        {
            CancelBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            OkBtn.Margin = new Thickness(0);
        }

        // 可选勾选框
        if (!string.IsNullOrEmpty(checkBoxText))
        {
            RemindCheckBox.Content = checkBoxText;
            RemindCheckBox.Visibility = Visibility.Visible;
            RemindCheckBox.Checked += (_, _) => CheckBoxChecked = true;
            RemindCheckBox.Unchecked += (_, _) => CheckBoxChecked = false;
        }

        OkBtn.Click += (_, _) => { Confirmed = true; Close(); };
        CancelBtn.Click += (_, _) => { Confirmed = false; Close(); };
        Loaded += OnLoaded;
    }

    /// <summary>弹窗出现时播放闪动动画，吸引用户注意</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口整体淡入
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        // 弹窗缩放弹跳：小 → 大 → 正常
        var scaleAnim = new DoubleAnimationUsingKeyFrames();
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, TimeSpan.FromMilliseconds(0)));
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, TimeSpan.FromMilliseconds(200),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, TimeSpan.FromMilliseconds(300),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, TimeSpan.FromMilliseconds(400),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        // 阴影闪动：从浅到深
        var shadowAnim = new DoubleAnimation(0.1, 0.35, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowAnim);
    }

    void OkBtn_Click(object sender, RoutedEventArgs e) { }
    void CancelBtn_Click(object sender, RoutedEventArgs e) { }

    /// <summary>显示纯提示</summary>
    public static void Show(string title, string message)
    {
        new MessageDialog(title, message).ShowDialog();
    }

    /// <summary>显示确认对话框，返回 true=确定</summary>
    public static bool Confirm(string title, string message)
    {
        var dlg = new MessageDialog(title, message, showCancel: true);
        dlg.ShowDialog();
        return dlg.Confirmed;
    }

    /// <summary>
    /// 显示带勾选框的确认对话框
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">提示内容</param>
    /// <param name="checkBoxText">勾选框文案（如"7 天内不再提醒"）</param>
    /// <param name="skipChecked">勾选框是否被勾选</param>
    /// <returns>true=用户点了确定，false=点了取消</returns>
    public static bool ConfirmWithCheck(string title, string message,
        string checkBoxText, out bool skipChecked)
    {
        var dlg = new MessageDialog(title, message, showCancel: true,
            checkBoxText: checkBoxText);
        dlg.ShowDialog();
        skipChecked = dlg.CheckBoxChecked;
        return dlg.Confirmed;
    }
}
