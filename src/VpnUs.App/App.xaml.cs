using System.Windows;
using VpnUs.App.Services;
using VpnUs.App.ViewModels;
using VpnUs.Core.Storage;

namespace VpnUs.App;

public partial class App : Application
{
    private Mutex? _mutex;

    public static App Instance => (App)Current;

    public ServiceClient Client { get; private set; } = null!;

    public MainViewModel Shell { get; private set; } = null!;

    public TrayIconManager? Tray { get; private set; }

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\VpnUs.Client.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VpnUs уже запущен — окно можно открыть из трея.", "VpnUs", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        VpnUsPaths.EnsureUserData();

        DispatcherUnhandledException += (_, args) =>
        {
            UiLog.Write("unhandled", args.Exception.ToString());

            var message = args.Exception is UnauthorizedAccessException
                ? $"Нет доступа к файлу или папке:\n{args.Exception.Message}\n\n" +
                  $"Проверьте права на {VpnUsPaths.ProgramDataRoot}. Для работы VPN установите службу " +
                  "(«Настройки» → «Установить / починить службу (UAC)») — она работает от LocalSystem.\n\n" +
                  $"Подробности записаны в {VpnUsPaths.UiLogFile}"
                : "Ошибка: " + args.Exception.Message;

            MessageBox.Show(message, "VpnUs", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        Client = new ServiceClient();
        Shell = new MainViewModel(Client);

        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;

        Tray = new TrayIconManager(Shell, window);
        window.Show();

        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) ||
                          (Shell.UiPreferences.StartMinimized && !e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase));

        if (startHidden)
        {
            window.Hide();
            Tray.Notify("VpnUs запущен в трее");
        }

        _ = Shell.InitializeAsync();
    }

    public void ToggleMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        if (window.IsVisible)
        {
            window.Hide();
        }
        else
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    public void ExitApp()
    {
        IsExiting = true;
        UiLog.Write("info", "Выход из приложения");
        Tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
