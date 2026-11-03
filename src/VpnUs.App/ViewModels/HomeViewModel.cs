using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.App.Services;
using VpnUs.Core.Apps;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Net;
using VpnUs.Core.Text;

namespace VpnUs.App.ViewModels;

public sealed partial class HomeServerChip : ObservableObject
{
    public HomeServerChip(string tag, string name, string countryCode, bool isAuto)
    {
        Tag = tag;
        Name = name;
        CountryCode = countryCode;
        IsAuto = isAuto;
    }

    public string Tag { get; }

    public string Name { get; }

    public string CountryCode { get; }

    public bool IsAuto { get; }

    public bool HasCountryCode => !IsAuto && CountryCode.Length > 0;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class HomeViewModel : ObservableObject
{
    private const int TrafficWindow = 90;
    private const int PingWindow = 45;
    private const int PingTimeoutMs = 3000;
    private const int AutoPingBatchSize = 3;

    private static readonly TimeSpan AutoPingFreshness = TimeSpan.FromSeconds(45);

    private readonly ServiceClient _client;
    private readonly MainViewModel _main;
    private readonly Dictionary<string, ServerNode> _nodeByTag = new(StringComparer.Ordinal);
    private readonly List<ServerNode> _nodeList = [];
    private readonly Dictionary<string, (int? Delay, DateTimeOffset At)> _autoPings = new(StringComparer.Ordinal);
    private readonly Queue<int?> _pingHistory = new();
    private double _peakDown;
    private double _peakUp;
    private int _selectedAppsCount;
    private string _selectedTag = "auto";
    private int _autoPingCursor;
    private bool _pinging;
    private ServerNode? _pingTarget;

    public HomeViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;
    }

    public MainViewModel Main => _main;

    public ObservableCollection<double> DownSamples { get; } = [];

    public ObservableCollection<double> UpSamples { get; } = [];

    public ObservableCollection<double> PingSamples { get; } = [];

    public ObservableCollection<HomeServerChip> QuickServers { get; } = [];

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
    private string _serverCode = "";

    [ObservableProperty]
    private bool _serverIsAuto = true;

    [ObservableProperty]
    private string _modeLabel = "—";

    [ObservableProperty]
    private string _appsLabel = "не влияет на маршрутизацию";

    [ObservableProperty]
    private string _appsDetail = "Выберите режим «Только выбранные приложения»";

    [ObservableProperty]
    private string _downSpeed = "0 Б/с";

    [ObservableProperty]
    private string _upSpeed = "0 Б/с";

    [ObservableProperty]
    private string _totalDown = "0 Б";

    [ObservableProperty]
    private string _totalUp = "0 Б";

    [ObservableProperty]
    private string _peakDownLabel = "—";

    [ObservableProperty]
    private string _peakUpLabel = "—";

    [ObservableProperty]
    private string _pingValue = "—";

    [ObservableProperty]
    private string _pingTargetLabel = "Auto · ближайший";

    [ObservableProperty]
    private string _pingAverageLabel = "—";

    [ObservableProperty]
    private string _pingLossLabel = "—";

    [ObservableProperty]
    private int? _pingCurrent;

    [ObservableProperty]
    private bool _hasPingTarget;

    [ObservableProperty]
    private string _uptime = "—";

    [ObservableProperty]
    private string _coreLabel = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _subscriptionLabel = "Подписка не обновлялась";

    [ObservableProperty]
    private string _lastUpdateLabel = "Подписка ещё не обновлялась";

    [ObservableProperty]
    private string _systemTotals = "";

    [ObservableProperty]
    private int _connectionCount;

    [ObservableProperty]
    private bool _hasQuickServers;

    [ObservableProperty]
    private bool _serviceIssue;

    [ObservableProperty]
    private string _serviceIssueText = "";

    /// <summary>Визуальное состояние: connected / connecting / error / disconnected.</summary>
    [ObservableProperty]
    private string _stateKind = "disconnected";

    public string ConnectButtonText => StateKind switch
    {
        "connecting" => "...",
        _ => IsConnected ? "ОТКЛЮЧИТЬ" : "ПОДКЛЮЧИТЬ",
    };

    public string ConnectButtonHint => StateKind switch
    {
        "connecting" => "Подождите",
        "error" => "Повторить попытку",
        _ => IsConnected ? "Туннель активен" : "Нажмите, чтобы подключиться",
    };

    public string ConnectionsLabel => ConnectionCount > 0 ? $"Активных соединений: {ConnectionCount}" : "";

    public string ConnectionsValue => ConnectionCount > 0 ? ConnectionCount.ToString() : "—";

    public string TrafficRangeLabel => "последние 90 секунд";

    public string PingRangeLabel => "последние 90 секунд";

    public string WindowTitle => IsConnected ? "VpnUs — подключено" : "VpnUs";

    private static bool IsBootGracePeriod =>
        Environment.TickCount64 < (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    partial void OnStateKindChanged(string value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonHint));
    }

    partial void OnConnectionCountChanged(int value)
    {
        OnPropertyChanged(nameof(ConnectionsLabel));
        OnPropertyChanged(nameof(ConnectionsValue));
    }

    private static string KindOf(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => "connected",
        "starting" or "stopping" => "connecting",
        "error" => "error",
        _ => "disconnected",
    };

    public Task ToggleConnectionAsync() => ToggleAsync();

    public void ApplyStatus(ServiceStatusDto? status)
    {
        if (status is null)
        {
            IsConnected = false;
            StateKind = "disconnected";
            StatusLabel = "Отключено";

            var waitingForBoot = _client.IsServiceInstalled && IsBootGracePeriod;
            StatusHint = waitingForBoot
                ? "Служба запускается после загрузки Windows…"
                : _client.IsServiceInstalled ? "Служба не отвечает" : "Служба не установлена";
            CoreLabel = "Ядро не найдено";
            ServiceIssue = true;
            ServiceIssueText = waitingForBoot
                ? "Windows только что загрузилась. Служба VpnUs стартует с задержкой, чтобы сеть и TUN-драйвер успели инициализироваться — это занимает до пары минут."
                : _client.IsServiceInstalled
                    ? "Служба VpnUs установлена, но не отвечает. Проверьте её состояние в «Настройках»."
                    : "Служба VpnUs не установлена — без неё TUN-режим недоступен. Установите её в «Настройках».";
            Error = _client.IsServiceInstalled && !waitingForBoot ? _client.LastError : null;

            foreach (var chip in QuickServers)
            {
                chip.IsSelected = false;
            }

            ResetTraffic();
            return;
        }

        ServiceIssue = false;
        ServiceIssueText = "";
        IsConnected = string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase);
        if (!IsBusy)
        {
            StateKind = KindOf(status.State);
        }

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

        ServerName = SanitizeNodeName(status.SelectedNodeName);
        var code = FlagHelper.CountryCode(status.SelectedNodeName);
        ServerCode = code.Length > 0 ? code : "•";
        ServerIsAuto = string.Equals(status.SelectedNode, "auto", StringComparison.OrdinalIgnoreCase);
        UpdatePingTarget(status.SelectedNode);
        ModeLabel = ModeName(status.Mode);
        UpdateAppsLabel(status.Mode, status.AppSplit);
        CoreLabel = status.CorePresent
            ? $"sing-box {status.CoreVersion} · серверов: {status.NodeCount}"
            : "Ядро не установлено (обновите в «Настройках»)";

        SubscriptionLabel = status.SubscriptionUpdatedAt is null
            ? "Подписка не обновлялась"
            : $"Подписка обновлена {status.SubscriptionUpdatedAt.Value.LocalDateTime:dd.MM.yyyy HH:mm} · серверов: {status.NodeCount}";

        LastUpdateLabel = RelativeTime.FormatSubscriptionUpdate(status.SubscriptionUpdatedAt);

        Error = string.IsNullOrWhiteSpace(status.LastError) ? null : status.LastError;

        foreach (var chip in QuickServers)
        {
            chip.IsSelected = string.Equals(chip.Tag, status.SelectedNode, StringComparison.Ordinal);
        }

        if (status.State != "running")
        {
            Uptime = "—";
            ResetTraffic();
        }
    }

    public void ApplyNodes(IReadOnlyList<ServerNode> nodes, string selectedTag, int selectedAppsCount)
    {
        _selectedAppsCount = selectedAppsCount;

        _nodeByTag.Clear();
        _nodeList.Clear();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Tag))
            {
                continue;
            }

            _nodeByTag[node.Tag] = node;
            _nodeList.Add(node);
        }

        QuickServers.Clear();
        QuickServers.Add(new HomeServerChip("auto", "Auto", "", isAuto: true)
        {
            IsSelected = string.Equals(selectedTag, "auto", StringComparison.OrdinalIgnoreCase),
        });

        var selected = nodes.FirstOrDefault(n => string.Equals(n.Tag, selectedTag, StringComparison.Ordinal));
        foreach (var node in nodes)
        {
            if (QuickServers.Count >= 8 && !ReferenceEquals(node, selected))
            {
                continue;
            }

            var name = SanitizeNodeName(node.Name);
            var code = FlagHelper.CountryCode(node.Name);
            QuickServers.Add(new HomeServerChip(
                node.Tag,
                string.IsNullOrWhiteSpace(name) ? node.Endpoint : name,
                code.Length > 0 ? code : "•",
                isAuto: false)
            {
                IsSelected = string.Equals(node.Tag, selectedTag, StringComparison.Ordinal),
            });
        }

        HasQuickServers = QuickServers.Count > 1;
        UpdatePingTarget(selectedTag);
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

        if (!IsConnected)
        {
            return;
        }

        AppendTraffic(downSpeed ?? 0, upSpeed ?? 0);
    }

    /// <summary>Один такт TCP-пинга выбранного сервера. Работает и без активного туннеля.</summary>
    public async Task PingTickAsync()
    {
        if (_pinging || !HasPingTarget)
        {
            return;
        }

        _pinging = true;
        try
        {
            var tag = _selectedTag;
            var delay = tag == "auto"
                ? await MeasureAutoDelayAsync()
                : _pingTarget is not null
                    ? await ServerPingService.PingAsync(_pingTarget, PingTimeoutMs)
                    : null;

            if (!string.Equals(tag, _selectedTag, StringComparison.Ordinal))
            {
                return;
            }

            AppendPing(delay);
        }
        finally
        {
            _pinging = false;
        }
    }

    private async Task<int?> MeasureAutoDelayAsync()
    {
        if (_nodeList.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var batchSize = Math.Min(AutoPingBatchSize, _nodeList.Count);
        var batch = new ServerNode[batchSize];
        for (var i = 0; i < batchSize; i++)
        {
            batch[i] = _nodeList[(_autoPingCursor + i) % _nodeList.Count];
        }

        _autoPingCursor = (_autoPingCursor + batchSize) % _nodeList.Count;

        var delays = await Task.WhenAll(batch.Select(node => ServerPingService.PingAsync(node, PingTimeoutMs)));
        for (var i = 0; i < batch.Length; i++)
        {
            _autoPings[batch[i].Tag] = (delays[i], now);
        }

        var cutoff = now - AutoPingFreshness;
        foreach (var stale in _autoPings.Where(pair => pair.Value.At < cutoff).Select(pair => pair.Key).ToList())
        {
            _autoPings.Remove(stale);
        }

        var fresh = _autoPings.Values.Where(value => value.Delay is > 0).Select(value => value.Delay!.Value).ToList();
        return fresh.Count > 0 ? fresh.Min() : null;
    }

    private void AppendPing(int? delay)
    {
        var answered = delay is > 0;
        if (PingSamples.Count == 0)
        {
            var initial = answered ? delay!.Value : double.NaN;
            for (var i = 0; i < PingWindow; i++)
            {
                PingSamples.Add(initial);
            }
        }

        PingSamples.Add(answered ? delay!.Value : double.NaN);
        while (PingSamples.Count > PingWindow)
        {
            PingSamples.RemoveAt(0);
        }

        _pingHistory.Enqueue(answered ? delay : null);
        while (_pingHistory.Count > PingWindow)
        {
            _pingHistory.Dequeue();
        }

        PingCurrent = answered ? delay : null;
        PingValue = answered ? $"{delay} мс" : "нет ответа";

        var valid = _pingHistory.Where(value => value is > 0).Select(value => value!.Value).ToList();
        PingAverageLabel = valid.Count > 0 ? $"{Math.Round(valid.Average()):0} мс" : "—";
        var failed = _pingHistory.Count(value => value is null);
        PingLossLabel = _pingHistory.Count == 0 ? "—" : $"{100.0 * failed / _pingHistory.Count:0}%";
    }

    private void ResetPing()
    {
        if (PingSamples.Count > 0)
        {
            PingSamples.Clear();
        }

        _pingHistory.Clear();
        _autoPings.Clear();
        _autoPingCursor = 0;
        PingCurrent = null;
        PingValue = "—";
        PingAverageLabel = "—";
        PingLossLabel = "—";
    }

    private void UpdatePingTarget(string? tag)
    {
        var nextTag = string.IsNullOrWhiteSpace(tag) ? "auto" : tag;
        var previousTag = _selectedTag;
        var previousEndpoint = _pingTarget?.Endpoint;

        _selectedTag = nextTag;
        _pingTarget = _nodeByTag.TryGetValue(nextTag, out var node) ? node : null;
        HasPingTarget = nextTag == "auto" ? _nodeList.Count > 0 : _pingTarget is not null;

        PingTargetLabel = nextTag == "auto"
            ? "Auto · ближайший"
            : _pingTarget is not null
                ? $"{SanitizeNodeName(_pingTarget.Name)} · TCP"
                : "Сервер не выбран";

        var changed = !string.Equals(previousTag, nextTag, StringComparison.Ordinal) ||
                      !string.Equals(previousEndpoint, _pingTarget?.Endpoint, StringComparison.Ordinal);
        if (changed)
        {
            ResetPing();
        }
    }

    private void AppendTraffic(double down, double up)
    {
        if (DownSamples.Count == 0)
        {
            for (var i = 0; i < TrafficWindow; i++)
            {
                DownSamples.Add(0);
                UpSamples.Add(0);
            }
        }

        DownSamples.Add(Math.Max(0, down));
        UpSamples.Add(Math.Max(0, up));

        while (DownSamples.Count > TrafficWindow)
        {
            DownSamples.RemoveAt(0);
        }

        while (UpSamples.Count > TrafficWindow)
        {
            UpSamples.RemoveAt(0);
        }

        if (down > _peakDown)
        {
            _peakDown = down;
            PeakDownLabel = Format.Speed((long)_peakDown);
        }

        if (up > _peakUp)
        {
            _peakUp = up;
            PeakUpLabel = Format.Speed((long)_peakUp);
        }
    }

    private void ResetTraffic()
    {
        if (DownSamples.Count > 0)
        {
            DownSamples.Clear();
        }

        if (UpSamples.Count > 0)
        {
            UpSamples.Clear();
        }

        _peakDown = 0;
        _peakUp = 0;
        PeakDownLabel = "—";
        PeakUpLabel = "—";
    }

    private void UpdateAppsLabel(string mode, string split)
    {
        if (string.Equals(mode?.Trim(), "perApp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode?.Trim(), "perapp", StringComparison.OrdinalIgnoreCase))
        {
            AppsLabel = AppSplitShort(split);
            AppsDetail = _selectedAppsCount > 0
                ? $"Выбрано приложений: {_selectedAppsCount}"
                : "Список приложений пуст";
        }
        else
        {
            AppsLabel = "не влияет";
            AppsDetail = "Включите режим «Только выбранные приложения»";
        }
    }

    public void TickUptime(DateTimeOffset connectedAt) => Uptime = Format.Uptime(DateTimeOffset.Now - connectedAt);

    public void SetError(string? error) => Error = error;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonHint));
        OnPropertyChanged(nameof(WindowTitle));
        if (!value)
        {
            ResetTraffic();
        }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        StateKind = "connecting";
        StatusLabel = IsConnected ? "Отключение..." : "Подключение...";

        try
        {
            var command = IsConnected ? IpcCommands.Disconnect : IpcCommands.Connect;
            var status = await _client.GetAsync<ServiceStatusDto>(command);

            if (status is null)
            {
                Error = _client.LastError ?? "Служба недоступна";
                StatusLabel = "Отключено";
                StateKind = "error";
                return;
            }

            IsBusy = false;
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
    private async Task SelectServerAsync(HomeServerChip? chip)
    {
        if (chip is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            if (await _main.SelectNodeAsync(chip.Tag))
            {
                foreach (var item in QuickServers)
                {
                    item.IsSelected = ReferenceEquals(item, chip);
                }

                ServerName = chip.Name;
                ServerCode = chip.CountryCode;
                ServerIsAuto = chip.IsAuto;
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

    [RelayCommand]
    private void OpenServers() => _main.NavigateTo(_main.Servers);

    [RelayCommand]
    private void OpenApps() => _main.NavigateTo(_main.Apps);

    [RelayCommand]
    private void OpenSettings() => _main.NavigateTo(_main.Settings);

    [RelayCommand]
    private void OpenLogs() => _main.NavigateTo(_main.Logs);

    private static string SanitizeNodeName(string? name)
    {
        var cleaned = FlagHelper.StripFlags(name);
        return string.IsNullOrWhiteSpace(cleaned) ? "Auto" : cleaned;
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

    private static string AppSplitShort(string split) => split?.Trim().ToLowerInvariant() switch
    {
        "allthroughvpn" => "все приложения",
        "onlyselected" => "только выбранные",
        "allexceptselected" => "все, кроме выбранных",
        _ => split ?? "",
    };
}
