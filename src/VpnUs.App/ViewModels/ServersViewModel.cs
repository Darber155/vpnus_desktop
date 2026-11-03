using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.App.Services;
using VpnUs.Core.Apps;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Net;

namespace VpnUs.App.ViewModels;

public sealed partial class ServerItemViewModel : ObservableObject
{
    public ServerItemViewModel(ServerNode? node, bool isAuto)
    {
        IsAuto = isAuto;
        Node = node;

        if (isAuto)
        {
            Tag = "auto";
            Name = "Auto (быстрейший)";
            TypeLabel = "URLTest";
            Endpoint = "Автовыбор по задержке";
            CountryCode = "";
        }
        else
        {
            Tag = node!.Tag;
            var name = FlagHelper.StripFlags(node.Name);
            Name = string.IsNullOrWhiteSpace(name) ? node.Endpoint : name;
            TypeLabel = node.Type.ToUpperInvariant();
            Endpoint = node.Endpoint;
            var code = FlagHelper.CountryCode(node.Name);
            CountryCode = code.Length > 0 ? code : "•";
        }
    }

    public ServerNode? Node { get; }

    public bool IsAuto { get; }

    public string Tag { get; }

    public string Name { get; }

    public string TypeLabel { get; }

    public string Endpoint { get; }

    public string CountryCode { get; }

    public bool HasCountryCode => CountryCode.Length > 0;

    [ObservableProperty]
    private int? _delay;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isTesting;

    public string DelayText => IsTesting ? "…" : Delay is null or <= 0 ? "—" : $"{Delay} мс";

    public bool HasDelay => Delay is > 0;

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(HasDelay));
    }

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(DelayText));
}

public sealed partial class ServersViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly MainViewModel _main;
    private readonly List<ServerItemViewModel> _all = [];
    private string _selectedTag = "auto";
    private bool _loaded;

    public ServersViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;
    }

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    public ObservableCollection<string> SortModes { get; } = ["По имени", "По задержке"];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _sortMode = "По имени";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _selectedName = "Auto (быстрейший)";

    [ObservableProperty]
    private string _selectedEndpoint = "Автовыбор по задержке";

    [ObservableProperty]
    private string _hint = "TCP-пинг работает без подключения — проверяйте серверы до старта VPN";

    [ObservableProperty]
    private ServerItemViewModel? _selectedRow;

    private bool _syncingSelection;

    public string TestAllLabel => IsTesting ? "Проверяю…" : "Проверить все";

    public string CountLabel => _all.Count <= 1
        ? "Серверов нет"
        : $"{Servers.Count(s => !s.IsAuto)} из {_all.Count(s => !s.IsAuto)}";

    public bool IsEmpty => _all.Count <= 1;

    public bool IsFilterEmpty => !IsEmpty && Servers.Count == 0;

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(TestAllLabel));

    partial void OnSortModeChanged(string value) => Rebuild();

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnSelectedRowChanged(ServerItemViewModel? value)
    {
        if (_syncingSelection || value is null)
        {
            return;
        }

        _ = SelectAsync(value);
    }

    private void SetSelectedRow(ServerItemViewModel? item)
    {
        _syncingSelection = true;
        try
        {
            SelectedRow = item;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is not null)
        {
            _loaded = true;
            ApplyNodes(state.Nodes, state.Settings.SelectedNode);
        }
    }

    public void ApplyStatus(ServiceStatusDto status)
    {
        _selectedTag = status.SelectedNode;
        SelectedName = CleanName(status.SelectedNodeName);
        var selected = _all.FirstOrDefault(s => s.Tag == _selectedTag);
        SelectedEndpoint = selected?.Endpoint ?? "Автовыбор по задержке";

        foreach (var item in _all)
        {
            item.IsSelected = item.Tag == _selectedTag;
        }

        SetSelectedRow(_all.FirstOrDefault(s => s.Tag == _selectedTag));
    }

    public void ApplyNodes(IReadOnlyList<ServerNode> nodes, string selectedTag)
    {
        _loaded = true;
        _selectedTag = string.IsNullOrWhiteSpace(selectedTag) ? "auto" : selectedTag;
        _all.Clear();

        var auto = new ServerItemViewModel(null, isAuto: true) { IsSelected = _selectedTag == "auto" };
        _all.Add(auto);

        foreach (var node in nodes)
        {
            _all.Add(new ServerItemViewModel(node, isAuto: false) { IsSelected = node.Tag == _selectedTag });
        }

        var selectedItem = _all.FirstOrDefault(s => s.Tag == _selectedTag);
        SelectedName = selectedItem?.Name ?? "Auto (быстрейший)";
        SelectedEndpoint = selectedItem?.Endpoint ?? "Автовыбор по задержке";

        Rebuild();
        SetSelectedRow(selectedItem);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    [RelayCommand]
    private void OpenSettings() => _main.NavigateTo(_main.Settings);

    private void Rebuild()
    {
        var query = Filter.Trim();
        var filtered = _all.Where(item =>
            query.Length == 0 ||
            item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.TypeLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        if (SortMode == "По задержке")
        {
            filtered = filtered
                .OrderBy(item => item.IsAuto ? 0 : 1)
                .ThenBy(item => item.Delay is > 0 ? item.Delay.Value : int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        else
        {
            filtered = filtered
                .OrderBy(item => item.IsAuto ? 0 : 1)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        Servers.Clear();
        foreach (var item in filtered)
        {
            Servers.Add(item);
        }

        SetSelectedRow(_all.FirstOrDefault(s => s.Tag == _selectedTag));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    [RelayCommand]
    private async Task SelectAsync(ServerItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(_selectedTag, item.Tag, StringComparison.Ordinal))
        {
            return;
        }

        var ok = await _client.ExecuteAsync(IpcCommands.SetSelectedNode, new { tag = item.Tag });
        if (!ok)
        {
            Status = _client.LastError ?? "Не удалось выбрать сервер";
            return;
        }

        _selectedTag = item.Tag;
        SelectedName = item.Name;
        SelectedEndpoint = item.Endpoint;
        foreach (var server in _all)
        {
            server.IsSelected = server.Tag == item.Tag;
        }

        Status = $"Выбран: {item.Name}";
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (IsTesting)
        {
            return;
        }

        var targets = _all.Where(s => !s.IsAuto && s.Node is not null).ToList();
        if (targets.Count == 0)
        {
            Status = "Нет серверов для проверки";
            return;
        }

        IsTesting = true;
        Status = "Тестирую задержки по TCP…";

        try
        {
            using var semaphore = new SemaphoreSlim(8);
            var tasks = targets.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    item.IsTesting = true;
                    item.Delay = await ServerPingService.PingAsync(item.Node!);
                }
                finally
                {
                    item.IsTesting = false;
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            UpdateAutoDelay();
            var answered = targets.Count(t => t.Delay is > 0);
            Status = answered == 0
                ? "Серверы не ответили — проверьте интернет или адреса серверов"
                : $"Готово: ответили {answered} из {targets.Count}";

            if (SortMode == "По задержке")
            {
                Rebuild();
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestOneAsync(ServerItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsAuto)
        {
            await TestAllAsync();
            return;
        }

        item.IsTesting = true;
        try
        {
            item.Delay = await ServerPingService.PingAsync(item.Node);
            UpdateAutoDelay();
            Status = item.Delay is > 0 ? $"{item.Name}: {item.Delay} мс" : $"{item.Name}: нет ответа";
            if (SortMode == "По задержке")
            {
                Rebuild();
            }
        }
        finally
        {
            item.IsTesting = false;
        }
    }

    private void UpdateAutoDelay()
    {
        var auto = _all.FirstOrDefault(s => s.IsAuto);
        if (auto is null)
        {
            return;
        }

        var delays = _all
            .Where(s => !s.IsAuto && s.Delay is > 0)
            .Select(s => s.Delay!.Value)
            .ToList();

        auto.Delay = delays.Count > 0 ? delays.Min() : null;
    }

    private static string CleanName(string? name)
    {
        var cleaned = FlagHelper.StripFlags(name);
        return string.IsNullOrWhiteSpace(cleaned) ? "Auto (быстрейший)" : cleaned;
    }
}
