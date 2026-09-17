using System.Windows;
using Microsoft.Win32;

namespace VpnUs.App.Views;

public partial class TextWindow : Window
{
    private TextWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.WindowBackdrop.Apply(this);
    }

    public static void Show(Window? owner, string title, string text)
    {
        var window = new TextWindow
        {
            Title = title,
        };

        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
        }

        window.HeaderText.Text = title;
        window.BodyText.Text = text;

        if (owner is not null && owner.IsVisible)
        {
            window.ShowDialog();
        }
        else
        {
            window.Show();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить",
            FileName = "vpnus-config.json",
            Filter = "JSON (*.json)|*.json|Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, BodyText.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "VpnUs", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
