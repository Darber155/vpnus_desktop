using System.ComponentModel;
using System.Windows;
using VpnUs.App.Services;

namespace VpnUs.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Instance.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }
}
