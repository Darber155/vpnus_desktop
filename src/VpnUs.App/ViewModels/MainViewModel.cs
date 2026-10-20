using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.Core.Update;
using VpnUs.App.Services;
using VpnUs.Core.Clash;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;

namespace VpnUs.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string title, string glyph, ObservableObject page)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    public string Key { get; }

    public string Title { get; }

    public string Glyph { get; }

    public ObservableObject Page { get; }

    [ObservableProperty]
    private string _badge = "";

    [ObservableProperty]
    private bool _showBadge;
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
        Servers = new ServersViewModel(client, this);
        Apps = new AppsViewModel(client, this);
        Settings = new SettingsViewModel(client, this);
        Logs = new LogsViewModel(client);

        NavItems =
        [
            new NavItem("home", "Главная", "\uE80F", Home),
            new NavItem("servers", "Серверы", "\uE774", Servers),
            new NavItem("apps", "Приложения", "\uE71D", Apps),
            new NavItem("settings", "Настройки", "\uE713", Settings),
            new NavItem("logs", "Логи", "\uE9D9", Logs),
        ];

        UiPreferences = JsonStore.Load(VpnUsPaths.UiSettingsFile, () => new UiPreferences());

        var startPage = NavItems.FirstOrDefault(
            n => string.Equals(n.Key, UiPreferences.LastPage, StringComparison.OrdinalIgnoreCase)) ?? NavItems[0];
        _selectedNav = startPage;
        Current = startPage.Page;

        Apps.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppsViewModel.SelectedCount))
            {
                UpdateAppsBadge();
            }
        };

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

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private string _updateProgress = "";

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private bool _checkingUpdates;

    [ObservableProperty]
    private AppRelease? _availableRelease;

    [ObservableProperty]
    private bool _isRefreshingSubscription;

    public string VersionText => $"VpnUs {AppUpdateService.CurrentVersion}" +
                                 (VpnUsPaths.IsPortable ? " · портативная" : "");

    public string ReleasesUrl => AppUpdateService.ReleasesPageUrl;

    private readonly AppUpdater _appUpdater = new();

    public async Task InitializeAsync()
    {
        await PollAsync();
        await LoadStateAsync();
        await EnsureSelectedPageAsync();

        if (UiPreferences.CheckUpdatesOnStart)
        {
            await RunCheckUpdatesAsync(silent: true);
        }
    }

    private async Task EnsureSelectedPageAsync()
    {
        switch (SelectedNav?.Page)
        {
            case AppsViewModel apps:
                await apps.EnsureLoadedAsync();
                break;
            case ServersViewModel servers:
                await servers.EnsureLoadedAsync();
                break;
            case SettingsViewModel settings:
                await settings.EnsureLoadedAsync();
                break;
            case LogsViewModel logs:
                await logs.RebuildAsync();
                break;
        }
    }

    partial void OnUpdateAvailableChanged(bool value)
    {
        var settingsNav = NavItems.FirstOrDefault(n => n.Key == "settings");
        if (settingsNav is not null)
        {
            settingsNav.Badge = "1";
            settingsNav.ShowBadge = value;
        }
    }

    private void UpdateAppsBadge()
    {
        var nav = NavItems.FirstOrDefault(n => n.Key == "apps");
        if (nav is null)
        {
            return;
        }

        nav.Badge = Apps.SelectedCount.ToString();
        nav.ShowBadge = Apps.SelectedCount > 0;
    }

    private void UpdateServersBadge(int count)
    {
        var nav = NavItems.FirstOrDefault(n => n.Key == "servers");
        if (nav is null)
        {
            return;
        }

        nav.Badge = count.ToString();
        nav.ShowBadge = count > 0;
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync() => await RunCheckUpdatesAsync(silent: false);

    public Task CheckUpdatesSilentAsync() => RunCheckUpdatesAsync(silent: true);

    private async Task RunCheckUpdatesAsync(bool silent)
    {
        if (CheckingUpdates)
        {
            return;
        }

        CheckingUpdates = true;
        if (!silent)
        {
            UpdateStatus = "Проверяю обновления на GitHub...";
        }

        try
        {
            var release = await _appUpdater.CheckAsync();
            UiPreferences.LastUpdateCheck = DateTimeOffset.Now;
            SaveUiPreferences();

            if (release is null)
            {
                UpdateStatus = _appUpdater.LastCheckError ?? "Не удалось получить информацию о релизах";
                UpdateAvailable = false;
                return;
            }

            AvailableRelease = release;
            UpdateAvailable = release.IsNewer;

            if (release.IsNewer)
            {
                UpdateStatus = $"Доступна версия {release.Version} (у вас {AppUpdateService.CurrentVersion})";
                if (silent)
                {
                    App.Instance.Tray?.Notify($"Доступно обновление VpnUs {release.Version}");
                }
            }
            else
            {
                UpdateStatus = $"У вас последняя версия {AppUpdateService.CurrentVersion}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UpdateStatus = silent ? "" : "Ошибка проверки обновлений: " + ex.Message;
            UpdateAvailable = false;
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        var release = AvailableRelease;
        if (release is null)
        {
            return;
        }

        UpdateProgress = "Скачиваю 0%";
        var progress = new Progress<double>(value => UpdateProgress = $"Скачиваю {value * 100:0}%");

        var (ok, message) = await _appUpdater.DownloadAndApplyAsync(release, progress);
        UpdateStatus = message;
        UpdateProgress = "";

        if (ok)
        {
            await Task.Delay(1500);
            App.Instance.ExitApp();
        }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        Current = value.Page;
        UiPreferences.LastPage = value.Key;
        SaveUiPreferences();
        _ = EnsureSelectedPageAsync();
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var target = NavItems.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            SelectedNav = target;
        }
    }

    public void NavigateTo(object page)
    {
        var target = NavItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (target is not null)
        {
            SelectedNav = target;
        }
    }

    public async Task RefreshSubscriptionAsync()
    {
        if (IsRefreshingSubscription)
        {
            return;
        }

        IsRefreshingSubscription = true;
        Home.SetError(null);
        try
        {
            var result = await _client.GetAsync<SubscriptionRefreshDto>(IpcCommands.RefreshSubscription);
            if (result is { Ok: true })
            {
                ServiceStatusText = $"Подписка: {result.NodeCount} серверов";
                await LoadStateAsync();
                await PollAsync();
            }
            else
            {
                Home.SetError(_client.LastError ?? "Не удалось обновить подписку");
            }
        }
        finally
        {
            IsRefreshingSubscription = false;
        }
    }

    /// <summary>Быстрое переключение сервера с главной страницы.</summary>
    public async Task<bool> SelectNodeAsync(string tag)
    {
        var ok = await _client.ExecuteAsync(IpcCommands.SetSelectedNode, new { tag });
        if (!ok)
        {
            Home.SetError(_client.LastError ?? "Не удалось выбрать сервер");
            return false;
        }

        await LoadStateAsync();
        await PollAsync();
        return true;
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
        Home.ApplyNodes(state.Nodes, state.Settings.SelectedNode, state.Settings.SelectedApps.Count);
        Apps.Load(state.Settings);
        Settings.Load(state.Settings, state.Nodes.Count);
        UpdateServersBadge(state.Nodes.Count);
        UpdateAppsBadge();
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
