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
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(SubscriptionUrl),
        nameof(SubscriptionAutoUpdate),
        nameof(SubscriptionUpdateHours),
        nameof(BypassGames),
        nameof(BlockAds),
        nameof(DnsDirect),
        nameof(DnsProxy),
        nameof(DnsStrategy),
        nameof(TunStackName),
        nameof(TunInterface),
        nameof(TunMtu),
        nameof(ClashApiPort),
        nameof(LocalProxyPort),
        nameof(UrlTestUrl),
        nameof(UrlTestInterval),
        nameof(LogLevel),
        nameof(AutoConnect),
        nameof(LaunchAtLogin),
        nameof(StartMinimized),
    ];

    private readonly ServiceClient _client;
    private readonly MainViewModel _main;
    private bool _loading;
    private DateTimeOffset? _subscriptionUpdatedAt;

    public SettingsViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;

        PropertyChanged += (_, e) =>
        {
            if (!_loading && e.PropertyName is { } name && EditableProperties.Contains(name))
            {
                if (!IsDirty)
                {
                    Message = "";
                }

                IsDirty = true;
            }
        };
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
    private string _subscriptionUpdatedLabel = "Подписка ещё не обновлялась";

    [ObservableProperty]
    private string _nextUpdateLabel = "";

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
    private string _serviceStateKind = "unknown";

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
    private bool _isDirty;

    [ObservableProperty]
    private bool _serviceInstalled;

    [ObservableProperty]
    private bool _isElevated;

    public string DataFolder => VpnUsPaths.ProgramDataRoot;

    public string ServiceExe => AdminLauncher.ServiceExePath;

    public string FooterHint => IsDirty
        ? "Изменения ещё не применены"
        : !string.IsNullOrWhiteSpace(Message)
            ? Message
            : "Все изменения сохранены";

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(FooterHint));

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(FooterHint));

    public void Load(AppSettings settings, int nodeCount, bool force = false)
    {
        if (!force && IsDirty)
        {
            return;
        }

        _loading = true;
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
            ApplySubscriptionLabels(settings.SubscriptionUpdatedAt);
            IsDirty = false;
            Message = "";
        }
        finally
        {
            _loading = false;
        }
    }

    public void ApplyStatus(ServiceStatusDto status)
    {
        ServiceStateKind = status.State switch
        {
            "running" => "running",
            "starting" or "stopping" => "connecting",
            "error" => "error",
            _ => "stopped",
        };

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
        ApplySubscriptionLabels(status.SubscriptionUpdatedAt);
    }

    private void ApplySubscriptionLabels(DateTimeOffset? updatedAt)
    {
        _subscriptionUpdatedAt = updatedAt;

        SubscriptionUpdatedLabel = updatedAt is null
            ? "Подписка ещё не обновлялась"
            : $"Обновлена {updatedAt.Value.LocalDateTime:dd.MM.yyyy HH:mm}";

        NextUpdateLabel = updatedAt is null
            ? ""
            : SubscriptionAutoUpdate && SubscriptionUpdateHours > 0
                ? $"Следующая авто-проверка ~ {updatedAt.Value.LocalDateTime.AddHours(SubscriptionUpdateHours):dd.MM HH:mm}"
                : "Автообновление выключено";
    }

    partial void OnSubscriptionUpdateHoursChanged(int value) => ApplySubscriptionLabels(_subscriptionUpdatedAt);

    partial void OnSubscriptionAutoUpdateChanged(bool value) => ApplySubscriptionLabels(_subscriptionUpdatedAt);

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

    private async Task RefreshAsync(bool force = false)
    {
        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is null)
        {
            Message = _client.LastError ?? "Служба недоступна";
            return;
        }

        Load(state.Settings, state.Nodes.Count, force);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var validationError = Validate();
        if (validationError is not null)
        {
            Message = validationError;
            return;
        }

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

        IsDirty = false;
        UiLog.Write("info", "Настройки сохранены из UI");
        await _main.ReloadStateAsync();
        Message = "Настройки сохранены";
    }

    private string? Validate()
    {
        if (ClashApiPort is < 1 or > 65535)
        {
            return "Порт Clash API должен быть от 1 до 65535";
        }

        if (LocalProxyPort is < 0 or > 65535)
        {
            return "Локальный прокси-порт должен быть от 0 до 65535";
        }

        if (TunMtu is < 576 or > 65535)
        {
            return "MTU должен быть от 576 до 65535";
        }

        if (SubscriptionUrl.Trim().Length > 0 &&
            (!Uri.TryCreate(SubscriptionUrl.Trim(), UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return "URL подписки должен начинаться с http:// или https://";
        }

        return null;
    }

    [RelayCommand]
    private async Task RevertAsync()
    {
        await RefreshAsync(force: true);
        Message = "Изменения отменены";
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
                await RefreshAsync(force: true);
                Message = $"Подписка обновлена: {result.NodeCount} серверов (HTTP {result.StatusCode}, редиректов {result.Redirects})";
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
        var confirm = MessageBox.Show(
            App.Current.MainWindow,
            "Удалить службу VpnUs? VPN перестанет работать, пока служба не будет установлена снова.",
            "Удаление службы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var (started, error) = AdminLauncher.RunServiceCommand("uninstall");
        Message = started ? "Запущено удаление службы (UAC)" : error ?? "Не удалось";
    }

    [RelayCommand]
    private async Task RestartTunnelAsync()
    {
        IsBusy = true;
        try
        {
            var ok = await _client.ExecuteAsync(IpcCommands.RestartTunnel);
            Message = ok ? "Туннель перезапущен" : _client.LastError ?? "Не удалось перезапустить туннель";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CheckService() => _ = RefreshAsync();

    [RelayCommand]
    private void OpenLogs() => _main.NavigateTo(_main.Logs);

    [RelayCommand]
    private void OpenApps() => _main.NavigateTo(_main.Apps);

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
    private async Task CheckUpdatesAsync() => await _main.CheckUpdatesCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task ApplyUpdateAsync() => await _main.ApplyUpdateCommand.ExecuteAsync(null);

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
