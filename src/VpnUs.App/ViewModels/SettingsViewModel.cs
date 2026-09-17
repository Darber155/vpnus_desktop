using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.App.Services;
using VpnUs.App.Views;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;

namespace VpnUs.App.ViewModels;

public sealed class ModeOption
{
    public ModeOption(RoutingMode mode)
    {
        Mode = mode;
        Title = RoutingModeInfo.DisplayName(mode);
        Description = mode switch
        {
            RoutingMode.BypassBlocked => "Напрямую всё, через VPN — только заблокированное в РФ (Re-filter списки).",
            RoutingMode.Selective => "Через VPN только выбранные ресурсы (YouTube, Telegram и т.д.), остальное напрямую.",
            RoutingMode.BypassRu => "Всё через VPN, кроме российских сайтов и IP.",
            RoutingMode.Global => "Весь трафик через VPN.",
            RoutingMode.PerApp => "VPN только для выбранных приложений (per-app split tunneling).",
            _ => "",
        };
    }

    public RoutingMode Mode { get; }

    public string Title { get; }

    public string Description { get; }
}

public sealed partial class ResourceOption : ObservableObject
{
    public ResourceOption(string name)
    {
        Name = name;
        Title = RoutingModeInfo.SelectiveRuleSetTitles.TryGetValue(name, out var title) ? title : name;
    }

    public string Name { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditable = true;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly MainViewModel _main;

    public SettingsViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;
    }

    /// <summary>Для биндингов карточки «Обновления».</summary>
    public MainViewModel Main => _main;

    public bool CheckUpdatesOnStart
    {
        get => _main.UiPreferences.CheckUpdatesOnStart;
        set
        {
            if (_main.UiPreferences.CheckUpdatesOnStart == value)
            {
                return;
            }

            _main.UiPreferences.CheckUpdatesOnStart = value;
            _main.SaveUiPreferences();
            OnPropertyChanged();
        }
    }

    public ObservableCollection<int> UpdateHoursOptions { get; } = [1, 3, 6, 12, 24, 48, 72];

    public ObservableCollection<string> DnsStrategies { get; } = ["ipv4_only", "ipv6_only", "prefer_ipv4", "prefer_ipv6"];

    public ObservableCollection<string> TunStacks { get; } = ["gvisor", "system", "mixed"];

    public ObservableCollection<string> LogLevels { get; } = ["trace", "debug", "info", "warn", "error", "fatal"];

    [ObservableProperty]
    private string _subscriptionUrl = "";

    [ObservableProperty]
    private bool _subscriptionAutoUpdate = true;

    [ObservableProperty]
    private int _subscriptionUpdateHours = 24;
    [ObservableProperty]
    private bool _bypassGames = true;

    [ObservableProperty]
    private bool _blockAds;

    [ObservableProperty]
    private string _dnsDirect = "77.88.8.8";

    [ObservableProperty]
    private string _dnsProxy = "1.1.1.1";

    [ObservableProperty]
    private string _dnsStrategy = "ipv4_only";

    [ObservableProperty]
    private string _tunStackName = "mixed";

    [ObservableProperty]
    private string _tunInterface = "happwrt0";

    [ObservableProperty]
    private int _tunMtu = 9000;

    [ObservableProperty]
    private int _clashApiPort = 9090;

    [ObservableProperty]
    private int _localProxyPort = 2081;

    [ObservableProperty]
    private string _urlTestUrl = "http://cp.cloudflare.com/generate_204";

    [ObservableProperty]
    private string _urlTestInterval = "3m";

    [ObservableProperty]
    private string _logLevel = "warn";

    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    private bool _startMinimized = true;

    [ObservableProperty]
    private string _serviceState = "неизвестно";

    [ObservableProperty]
    private string _runningServicePath = "—";

    [ObservableProperty]
    private string _coreInfo = "";

    [ObservableProperty]
    private string _nodesInfo = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _serviceInstalled;

    [ObservableProperty]
    private bool _isElevated;

    public string DataFolder => VpnUsPaths.ProgramDataRoot;

    public string ServiceExe => AdminLauncher.ServiceExePath;

    public void Load(AppSettings settings, int nodeCount)
    {
        try
        {
            SubscriptionUrl = settings.SubscriptionUrl;
            SubscriptionAutoUpdate = settings.SubscriptionAutoUpdate;
            SubscriptionUpdateHours = Math.Clamp(settings.SubscriptionUpdateHours, 1, 720);
            BypassGames = settings.BypassGames;
            BlockAds = settings.BlockAds;
            DnsDirect = settings.DnsDirect;
            DnsProxy = settings.DnsProxy;
            DnsStrategy = settings.DnsStrategy;
            TunStackName = settings.TunStack.ToString().ToLowerInvariant();
            TunInterface = settings.TunInterface;
            TunMtu = settings.TunMtu;
            ClashApiPort = settings.ClashApiPort;
            LocalProxyPort = settings.LocalProxyPort;
            UrlTestUrl = settings.UrlTestUrl;
            UrlTestInterval = settings.UrlTestInterval;
            LogLevel = settings.LogLevel;
            AutoConnect = settings.AutoConnect;
            StartMinimized = _main.UiPreferences.StartMinimized;
            LaunchAtLogin = AppAutoStart.IsEnabled();
            NodesInfo = $"Серверов в подписке: {nodeCount}";
            Message = "";
        }
        finally
        {
        }
    }

    public void ApplyStatus(ServiceStatusDto status)
    {
        ServiceState = status.State switch
        {
            "running" => "работает",
            "starting" => "запускается",
            "stopping" => "останавливается",
            "error" => "ошибка",
            _ => "остановлена",
        };

        CoreInfo = status.CorePresent
            ? $"sing-box {status.CoreVersion}, серверов {status.NodeCount}"
            : "ядро не установлено";

        NodesInfo = $"Серверов в подписке: {status.NodeCount}";
        ServiceInstalled = true;
        RunningServicePath = string.IsNullOrWhiteSpace(status.ServicePath) ? "путь неизвестен" : status.ServicePath;
    }

    public Task EnsureLoadedAsync()
    {
        IsElevated = AdminLauncher.IsElevated;
        ServiceInstalled = _client.IsServiceInstalled;

        if (SubscriptionUrl.Length == 0 && NodesInfo.Length == 0)
        {
            _ = RefreshAsync();
        }

        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is null)
        {
            Message = _client.LastError ?? "Служба недоступна";
            return;
        }

        Load(state.Settings, state.Nodes.Count);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is null)
        {
            Message = _client.LastError ?? "Служба недоступна";
            return;
        }

        var settings = state.Settings;
        settings.SubscriptionUrl = SubscriptionUrl.Trim();
        settings.SubscriptionAutoUpdate = SubscriptionAutoUpdate;
        settings.SubscriptionUpdateHours = Math.Clamp(SubscriptionUpdateHours, 1, 720);
        settings.BypassGames = BypassGames;
        settings.BlockAds = BlockAds;
        settings.DnsDirect = DnsDirect.Trim();
        settings.DnsProxy = DnsProxy.Trim();
        settings.DnsStrategy = DnsStrategy;
        settings.TunStack = TunStackName.Equals("system", StringComparison.OrdinalIgnoreCase) ? TunStack.System
            : TunStackName.Equals("gvisor", StringComparison.OrdinalIgnoreCase) ? TunStack.Gvisor : TunStack.Mixed;
        settings.TunInterface = TunInterface.Trim();
        settings.TunMtu = TunMtu;
        settings.ClashApiPort = ClashApiPort;
        settings.LocalProxyPort = LocalProxyPort;
        settings.UrlTestUrl = UrlTestUrl.Trim();
        settings.UrlTestInterval = UrlTestInterval.Trim();
        settings.LogLevel = LogLevel;
        settings.AutoConnect = AutoConnect;
        settings.LaunchAtLogin = LaunchAtLogin;
        settings.StartMinimized = StartMinimized;

        var ok = await _client.ExecuteAsync(IpcCommands.SaveSettings, new { settings });
        if (!ok)
        {
            Message = _client.LastError ?? "Не удалось сохранить";
            return;
        }

        _main.UiPreferences.StartMinimized = StartMinimized;
        _main.SaveUiPreferences();
        AppAutoStart.Set(LaunchAtLogin);

        Message = "Настройки сохранены";
        UiLog.Write("info", "Настройки сохранены из UI");
        await _main.ReloadStateAsync();
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync()
    {
        IsBusy = true;
        Message = "Обновляю подписку...";
        try
        {
            var result = await _client.GetAsync<SubscriptionRefreshDto>(IpcCommands.RefreshSubscription);
            if (result is { Ok: true })
            {
                Message = $"Подписка обновлена: {result.NodeCount} серверов (HTTP {result.StatusCode}, редиректов {result.Redirects})";
                await RefreshAsync();
            }
            else
            {
                Message = _client.LastError ?? "Не удалось обновить подписку";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateCoreAsync()
    {
        IsBusy = true;
        Message = "Скачиваю sing-box с GitHub...";
        try
        {
            var info = await _client.GetAsync<CoreInfoDto>(IpcCommands.UpdateCore);
            Message = info is null
                ? _client.LastError ?? "Не удалось обновить ядро"
                : $"Ядро обновлено: sing-box {info.Version} ({info.Arch}), wintun: {WintunText(info)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string WintunText(CoreInfoDto info)
        => info.WintunPresent ? "файл wintun.dll" : info.WintunEmbedded ? "встроен в ядро" : "нет";

    [RelayCommand]
    private async Task PreviewConfigAsync()
    {
        var preview = await _client.GetAsync<ConfigPreviewDto>(IpcCommands.PreviewConfig);
        if (preview is null)
        {
            Message = _client.LastError ?? "Служба недоступна";
            return;
        }

        TextWindow.Show(App.Current.MainWindow, "Сгенерированный config.json", preview.Json);
    }

    [RelayCommand]
    private async Task ValidateConfigAsync()
    {
        IsBusy = true;
        try
        {
            var validation = await _client.GetAsync<ValidationDto>(IpcCommands.ValidateConfig);
            if (validation is null)
            {
                Message = _client.LastError ?? "Служба недоступна";
                return;
            }

            Message = validation.Ok ? "Конфиг корректен" : "Ошибки конфига — см. вывод";
            TextWindow.Show(App.Current.MainWindow, validation.Ok ? "sing-box check: OK" : "sing-box check: ошибки", validation.Output);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void InstallService()
    {
        var (started, error) = AdminLauncher.RunServiceCommand("install");
        var logPath = Path.Combine(VpnUsPaths.LogsDir, "install.log");

        Message = started
            ? $"Запущен установщик службы (UAC). Подождите ~10 секунд и нажмите «Проверить». Результат: {logPath}"
            : error ?? "Не удалось запустить установщик";
        UiLog.Write("info", "Установка службы: " + (started ? "started" : error));
    }

    [RelayCommand]
    private void UninstallService()
    {
        var (started, error) = AdminLauncher.RunServiceCommand("uninstall");
        Message = started ? "Запущено удаление службы (UAC)" : error ?? "Не удалось";
    }

    [RelayCommand]
    private void CheckService() => _ = RefreshAsync();

    [RelayCommand]
    private void OpenDataFolder()
    {
        var folder = VpnUsPaths.ProgramDataRoot;

        try
        {
            if (!Directory.Exists(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Message = $"Папка {folder} ещё не создана. Установите и запустите службу — она создаст её от имени SYSTEM.";
                    return;
                }
            }

            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Message = "Не удалось открыть папку: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_main.ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (SubscriptionUrl.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(SubscriptionUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = ex.Message;
        }
    }
}
