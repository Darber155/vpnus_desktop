using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.App.Services;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;

namespace VpnUs.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly MainViewModel _main;

    public HomeViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;
    }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusLabel = "Отключено";

    [ObservableProperty]
    private string _statusHint = "Служба не найдена";

    [ObservableProperty]
    private string _serverName = "—";

    [ObservableProperty]
    private string _modeLabel = "—";

    [ObservableProperty]
    private string _appsLabel = "—";

    [ObservableProperty]
    private string _downSpeed = "0 Б/с";

    [ObservableProperty]
    private string _upSpeed = "0 Б/с";

    [ObservableProperty]
    private string _totalDown = "0 Б";

    [ObservableProperty]
    private string _totalUp = "0 Б";

    [ObservableProperty]
    private string _uptime = "—";

    [ObservableProperty]
    private string _coreLabel = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _subscriptionLabel = "Подписка не обновлялась";

    [ObservableProperty]
    private string _systemTotals = "";

    [ObservableProperty]
    private int _connectionCount;

    public string ConnectButtonText => IsConnected ? "ОТКЛЮЧИТЬ" : "ПОДКЛЮЧИТЬ";

    public string ConnectButtonHint => IsConnected ? "Туннель активен" : "Нажмите, чтобы подключиться";

    public Task ToggleConnectionAsync() => ToggleAsync();

    public void ApplyStatus(ServiceStatusDto? status)
    {
        if (status is null)
        {
            IsConnected = false;
            StatusLabel = "Отключено";
            StatusHint = _client.IsServiceInstalled ? "Служба не отвечает" : "Служба не установлена — откройте «Настройки»";
            CoreLabel = "Ядро не найдено";
            Error = _client.LastError;
            return;
        }

        IsConnected = string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase);

        StatusLabel = status.State switch
        {
            "running" => "Подключено",
            "starting" => "Подключение...",
            "stopping" => "Отключение...",
            "error" => "Ошибка",
            _ => "Отключено",
        };

        StatusHint = status.State switch
        {
            "running" => $"Режим: {ModeName(status.Mode)}",
            "error" => status.LastError ?? "Проверьте логи",
            _ => "Нажмите, чтобы подключиться",
        };

        ServerName = status.SelectedNodeName;
        ModeLabel = ModeName(status.Mode);
        AppsLabel = AppSplitName(status.AppSplit);
        CoreLabel = status.CorePresent
            ? $"sing-box {status.CoreVersion} · серверов: {status.NodeCount}"
            : "Ядро не установлено (обновите в «Настройках»)";

        SubscriptionLabel = status.SubscriptionUpdatedAt is null
            ? "Подписка не обновлялась"
            : $"Подписка обновлена {status.SubscriptionUpdatedAt.Value.LocalDateTime:dd.MM.yyyy HH:mm} · серверов: {status.NodeCount}";

        Error = string.IsNullOrWhiteSpace(status.LastError) ? null : status.LastError;

        if (status.State != "running")
        {
            Uptime = "—";
        }
    }

    public void ApplyTraffic(
        long? downSpeed,
        long? upSpeed,
        long proxyDownload,
        long proxyUpload,
        long systemDownload = 0,
        long systemUpload = 0,
        int connections = 0)
    {
        DownSpeed = downSpeed is null ? "0 Б/с" : Format.Speed(downSpeed.Value);
        UpSpeed = upSpeed is null ? "0 Б/с" : Format.Speed(upSpeed.Value);
        TotalDown = Format.Bytes(proxyDownload);
        TotalUp = Format.Bytes(proxyUpload);
        ConnectionCount = connections;

        SystemTotals = systemDownload == 0 && systemUpload == 0
            ? ""
            : $"Вся система: ↓ {Format.Bytes(systemDownload)} · ↑ {Format.Bytes(systemUpload)} · соединений: {connections}";
    }

    public void TickUptime(DateTimeOffset connectedAt) => Uptime = Format.Uptime(DateTimeOffset.Now - connectedAt);

    public void SetError(string? error) => Error = error;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonHint));
    }

    [RelayCommand]
    private async Task ToggleAsync()    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        StatusLabel = IsConnected ? "Отключение..." : "Подключение...";

        try
        {
            var command = IsConnected ? IpcCommands.Disconnect : IpcCommands.Connect;
            var status = await _client.GetAsync<ServiceStatusDto>(command);

            if (status is null)
            {
                Error = _client.LastError ?? "Служба недоступна";
                StatusLabel = "Отключено";
                return;
            }

            ApplyStatus(status);
            if (!string.IsNullOrEmpty(status.LastError))
            {
                Error = status.LastError;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync()
    {
        Error = null;
        await _main.RefreshSubscriptionAsync();
    }

    private static string ModeName(string mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "bypassblocked" => "Только заблокированное",
        "selective" => "Только выбранные ресурсы",
        "bypassru" => "Всё через VPN, кроме РФ",
        "global" => "Global (всё через VPN)",
        "perapp" => "Только выбранные приложения",
        _ => mode ?? "",
    };

    private static string AppSplitName(string split) => split?.Trim().ToLowerInvariant() switch
    {
        "allthroughvpn" => "все приложения через VPN",
        "onlyselected" => "только выбранные приложения",
        "allexceptselected" => "все, кроме выбранных приложений",
        _ => split ?? "",
    };
}
