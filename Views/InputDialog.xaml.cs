using System;
using System.Windows;
using System.Windows.Input;

namespace YangzaiWorkshop.Views;

public partial class InputDialog : Window
{
    public string InputText => InputBox.Text;
    public event Action<string>? Confirmed;

    public InputDialog(string title, string message, string defaultText = "")
    {
        InitializeComponent();
        TitleText.Text = message;
        InputBox.Text = defaultText;
        InputBox.SelectAll();
        InputBox.Focus();
        InputBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { Confirmed?.Invoke(InputText); Close(); }
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Confirmed?.Invoke(InputText);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
