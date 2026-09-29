using Avalonia.Controls;

namespace SptModManager.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
        : this("Message", string.Empty, "OK", null, false)
    {
    }

    public MessageDialog(string title, string message, string confirmText, string? cancelText, bool danger)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => Close(true);
        if (danger)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("danger");
        }

        CancelButton.IsVisible = cancelText is not null;
        CancelButton.Content = cancelText;
        CancelButton.IsCancel = true;
        CancelButton.Click += (_, _) => Close(false);
    }
}
