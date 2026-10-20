using System.Windows;
using VpnUs.App.Services;
using VpnUs.App.ViewModels;
using VpnUs.Core.Storage;

namespace VpnUs.App;

public partial class App : Application
{
    private const string ShowWindowEventName = @"Local\VpnUs.ShowWindow";
    private Mutex? _mutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;

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
            try
            {
                EventWaitHandle.OpenExisting(ShowWindowEventName).Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
            }

            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        VpnUsPaths.EnsureUserData();

        string? lastErrorMessage = null;
        DateTimeOffset lastErrorTime = DateTimeOffset.MinValue;

        DispatcherUnhandledException += (_, args) =>
        {
            UiLog.Write("unhandled", args.Exception.ToString());

            var message = args.Exception is UnauthorizedAccessException
                ? $"Нет доступа к файлу или папке:\n{args.Exception.Message}\n\n" +
                  $"Проверьте права на {VpnUsPaths.ProgramDataRoot}. Для работы VPN установите службу " +
                  "(«Настройки» → «Установить / починить службу (UAC)») — она работает от LocalSystem.\n\n" +
                  $"Подробности записаны в {VpnUsPaths.UiLogFile}"
                : "Ошибка: " + args.Exception.Message;

            args.Handled = true;

            var now = DateTimeOffset.Now;
            if (string.Equals(lastErrorMessage, message, StringComparison.Ordinal) && (now - lastErrorTime).TotalSeconds < 2)
            {
                return;
            }

            lastErrorMessage = message;
            lastErrorTime = now;

            MessageBox.Show(message, "VpnUs", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        Client = new ServiceClient();
        Shell = new MainViewModel(Client);

        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;

        Tray = new TrayIconManager(Shell);
        window.Show();

        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) ||
                          (Shell.UiPreferences.StartMinimized && !e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase));

        if (startHidden)
        {
            window.Hide();
            Tray.Notify("VpnUs запущен в трее");
        }

        _ = InitializeShellAsync();
    }

    private async Task InitializeShellAsync()
    {
        try
        {
            await Shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            UiLog.Write("error", "Ошибка инициализации: " + ex);
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
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
        Shell?.Dispose();
        _showWindowRegistration?.Unregister(null);
        _showWindowEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
