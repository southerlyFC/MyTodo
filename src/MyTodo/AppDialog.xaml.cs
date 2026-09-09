using System.Windows;
using System.Windows.Input;

namespace MyTodo;

public partial class AppDialog : Window
{
    private AppDialog(string title, string message, bool showCancel, string confirmText)
    {
        InitializeComponent();
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirmText;
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText = "确定")
    {
        var dialog = Create(owner, title, message, true, confirmText);
        return dialog.ShowDialog() == true;
    }

    public static void Notify(Window? owner, string title, string message, string confirmText = "知道了")
    {
        var dialog = Create(owner, title, message, false, confirmText);
        dialog.ShowDialog();
    }

    private static AppDialog Create(Window? owner, string title, string message, bool showCancel, string confirmText)
    {
        var dialog = new AppDialog(title, message, showCancel, confirmText);
        if (owner?.IsVisible == true)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
