using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VpnUs.App.Services;
using VpnUs.Core.Clash;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;

namespace VpnUs.App.ViewModels;

public sealed class NavItem
{
    public NavItem(string title, string glyph, ObservableObject page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    public string Title { get; }

    public string Glyph { get; }

    public ObservableObject Page { get; }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ServiceClient _client;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _trafficTimer;
    private readonly VpnTrafficTracker _tracker = new();
    private ClashApiClient? _clash;
    private int _clashPort;
    private long _lastDownload;
    private long _lastUpload;
    private DateTimeOffset _lastTrafficAt = DateTimeOffset.Now;
    private ServiceStatusDto? _lastStatus;
    private bool _stateLoaded;
    private bool _polling;

    public MainViewModel(ServiceClient client)
    {
        _client = client;

        Home = new HomeViewModel(client, this);
        Servers = new ServersViewModel(client);
        Apps = new AppsViewModel(client, this);
        Settings = new SettingsViewModel(client, this);
        Logs = new LogsViewModel(client);

        NavItems =
        [
            new NavItem("Главная", "\uE80F", Home),
            new NavItem("Серверы", "\uE774", Servers),
            new NavItem("Приложения", "\uE71D", Apps),
            new NavItem("Настройки", "\uE713", Settings),
            new NavItem("Логи", "\uE9D9", Logs),
        ];

        UiPreferences = JsonStore.Load(VpnUsPaths.UiSettingsFile, () => new UiPreferences());

        _selectedNav = NavItems[0];
        Current = Home;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();

        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trafficTimer.Tick += async (_, _) => await PollTrafficAsync();
        _trafficTimer.Start();
    }

    public HomeViewModel Home { get; }

    public ServersViewModel Servers { get; }

    public AppsViewModel Apps { get; }

    public SettingsViewModel Settings { get; }

    public LogsViewModel Logs { get; }

    public UiPreferences UiPreferences { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private object? _current;

    [ObservableProperty]
    private string _serviceStatusText = "Поиск службы...";

    [ObservableProperty]
    private bool _serviceAvailable;

    public async Task InitializeAsync()
    {
        await PollAsync();
        await LoadStateAsync();
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        Current = value.Page;

        if (value.Page is AppsViewModel apps)
        {
            _ = apps.EnsureLoadedAsync();
        }
        else if (value.Page is ServersViewModel servers)
        {
            _ = servers.EnsureLoadedAsync();
        }
        else if (value.Page is SettingsViewModel settings)
        {
            _ = settings.EnsureLoadedAsync();
        }
        else if (value.Page is LogsViewModel logs)
        {
            _ = logs.RebuildAsync();
        }
    }

    public async Task RefreshSubscriptionAsync()
    {
        var result = await _client.GetAsync<SubscriptionRefreshDto>(IpcCommands.RefreshSubscription);
        if (result is { Ok: true })
        {
            ServiceStatusText = $"Подписка: {result.NodeCount} серверов";
            await LoadStateAsync();
        }
        else
        {
            Home.SetError(_client.LastError);
        }
    }

    private async Task LoadStateAsync()
    {
        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is null)
        {
            return;
        }

        _stateLoaded = true;
        Servers.ApplyNodes(state.Nodes, state.Settings.SelectedNode);
        Apps.Load(state.Settings);
        Settings.Load(state.Settings, state.Nodes.Count);
    }

    /// <summary>Перечитать настройки после изменений на другом экране.</summary>
    public Task ReloadStateAsync() => LoadStateAsync();

    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            var status = await _client.GetAsync<ServiceStatusDto>(IpcCommands.Status);
            ServiceAvailable = status is not null;

            if (status is null)
            {
                ServiceStatusText = _client.IsServiceInstalled ? "Служба не отвечает" : "Служба не установлена";
                Home.ApplyStatus(null);
                return;
            }

            _lastStatus = status;
            ServiceStatusText = status.State switch
            {
                "running" => $"Подключено · {status.NodeCount} серверов",
                "starting" => "Подключение...",
                "stopping" => "Отключение...",
                "error" => "Ошибка ядра",
                _ => $"Отключено · {status.NodeCount} серверов",
            };

            Home.ApplyStatus(status);
            Settings.ApplyStatus(status);
            Servers.ApplyStatus(status);

            if (!_stateLoaded)
            {
                await LoadStateAsync();
            }

            if (SelectedNav?.Page is LogsViewModel)
            {
                await Logs.PollAsync();
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task PollTrafficAsync()
    {
        var status = _lastStatus;
        if (status is null || status.State != "running")
        {
            _tracker.Reset();
            Home.ApplyTraffic(null, null, 0, 0);
            return;
        }

        if (_clash is null || _clashPort != status.ClashApiPort)
        {
            _clash?.Dispose();
            _clashPort = status.ClashApiPort;
            _clash = new ClashApiClient(_clashPort);
            _tracker.Reset();
        }

        var connections = await _clash.GetConnectionsAsync();
        if (connections.Count == 0 && _clash.LastRequestFailed)
        {
            Home.ApplyTraffic(null, null, 0, 0);
            return;
        }

        _tracker.Update(connections);

        if (status.ConnectedAt is { } connectedAt)
        {
            Home.TickUptime(connectedAt);
        }

        long downSpeed = 0;
        long upSpeed = 0;
        var now = DateTimeOffset.Now;
        var elapsed = (now - _lastTrafficAt).TotalSeconds;
        var proxyDownload = _tracker.ProxyDownload;
        var proxyUpload = _tracker.ProxyUpload;

        if (elapsed > 0.2)
        {
            downSpeed = Math.Max(0, (long)((proxyDownload - _lastDownload) / elapsed));
            upSpeed = Math.Max(0, (long)((proxyUpload - _lastUpload) / elapsed));
        }

        _lastDownload = proxyDownload;
        _lastUpload = proxyUpload;
        _lastTrafficAt = now;

        Home.ApplyTraffic(downSpeed, upSpeed, proxyDownload, proxyUpload,
            _tracker.SystemDownload, _tracker.SystemUpload, connections.Count);
    }

    public void SaveUiPreferences()
    {
        try
        {
            JsonStore.Save(VpnUsPaths.UiSettingsFile, UiPreferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiLog.Write("warn", "Не удалось сохранить ui.json: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _trafficTimer.Stop();
        _clash?.Dispose();
    }
}
