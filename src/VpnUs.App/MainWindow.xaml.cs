using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using VpnUs.App.Services;
using VpnUs.App.ViewModels;
using VpnUs.Core.Storage;

namespace VpnUs.App;

public partial class MainWindow : Window
{
    private MainViewModel? _shell;
    private bool _placementLoaded;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            RestoreWindowPlacement();
            AnimatePage();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = e.NewValue as MainViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Current))
        {
            AnimatePage();
        }
    }

    private void AnimatePage()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        PageHost.BeginAnimation(OpacityProperty, fade);
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    private void RestoreWindowPlacement()
    {
        if (_placementLoaded || _shell is null)
        {
            return;
        }

        _placementLoaded = true;
        var prefs = _shell.UiPreferences;

        Width = Math.Max(MinWidth, prefs.WindowWidth);
        Height = Math.Max(MinHeight, prefs.WindowHeight);

        if (prefs.WindowLeft is { } left && prefs.WindowTop is { } top)
        {
            var windowRect = new Rect(left, top, Width, Height);
            var virtualRect = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            if (windowRect.IntersectsWith(virtualRect) && windowRect.Width > 0 && windowRect.Height > 0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (prefs.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (_shell is null)
        {
            return;
        }

        var prefs = _shell.UiPreferences;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            prefs.WindowWidth = bounds.Width;
            prefs.WindowHeight = bounds.Height;
            prefs.WindowLeft = bounds.Left;
            prefs.WindowTop = bounds.Top;
        }

        prefs.WindowMaximized = WindowState == WindowState.Maximized;
        _shell.SaveUiPreferences();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (App.Instance.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            App.Instance.Tray?.Notify("VpnUs свёрнут в трей — откройте двойным кликом по иконке");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            SaveWindowPlacement();
            Hide();
        }
    }
}
