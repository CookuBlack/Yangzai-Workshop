using System.Windows;
using System.Windows.Input;

namespace YangzaiWorkshop.Views;

public partial class InputDialog : Window
{
    public string InputText => InputBox.Text;

    public InputDialog(string title, string message, string defaultText = "")
    {
        InitializeComponent();
        TitleText.Text = message;
        InputBox.Text = defaultText;
        InputBox.SelectAll();
        InputBox.Focus();
        InputBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; Close(); }
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
