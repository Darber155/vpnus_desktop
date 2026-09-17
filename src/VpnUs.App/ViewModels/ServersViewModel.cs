using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpnUs.App.Services;
using VpnUs.Core.Apps;
using VpnUs.Core.Clash;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;

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
            Flag = "\u26A1";
        }
        else
        {
            Tag = node!.Tag;
            Name = node.Display;
            TypeLabel = node.Type.ToUpperInvariant();
            Endpoint = node.Endpoint;
            Flag = FlagHelper.Guess(node.Name);
        }
    }

    public ServerNode? Node { get; }

    public bool IsAuto { get; }

    public string Tag { get; }

    public string Name { get; }

    public string TypeLabel { get; }

    public string Endpoint { get; }

    public string Flag { get; }

    [ObservableProperty]
    private int? _delay;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isTesting;

    public string DelayText => IsTesting ? "тест..." : Delay is null or <= 0 ? "—" : $"{Delay} мс";

    public bool HasFlag => Flag.Length > 0;

    partial void OnDelayChanged(int? value) => OnPropertyChanged(nameof(DelayText));

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(DelayText));
}

public sealed partial class ServersViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly List<ServerItemViewModel> _all = [];
    private string _selectedTag = "auto";
    private bool _loaded;

    public ServersViewModel(ServiceClient client) => _client = client;

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    public int ClashApiPort { get; private set; } = 9090;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _selectedName = "Auto (быстрейший)";

    [ObservableProperty]
    private bool _tunnelRunning;

    [ObservableProperty]
    private string _hint = "Пинг измеряется через Clash API и доступен после подключения";

    [ObservableProperty]
    private ServerItemViewModel? _selectedRow;

    private bool _syncingSelection;

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

    public bool IsEmpty => _all.Count == 0;

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
        ClashApiPort = status.ClashApiPort;
        _selectedTag = status.SelectedNode;
        SelectedName = status.SelectedNodeName;

        TunnelRunning = string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase);
        Hint = TunnelRunning
            ? $"Clash API: 127.0.0.1:{status.ClashApiPort}"
            : status.State == "error"
                ? "Туннель не поднялся — серверы не пингуются. Смотрите «Логи»."
                : "Пинг измеряется через Clash API и доступен после подключения";

        foreach (var item in _all)
        {
            item.IsSelected = item.Tag == _selectedTag;
        }

        SetSelectedRow(_all.FirstOrDefault(s => s.Tag == _selectedTag));
    }

    public void ApplyNodes(IReadOnlyList<ServerNode> nodes, string selectedTag)
    {
        _selectedTag = string.IsNullOrWhiteSpace(selectedTag) ? "auto" : selectedTag;
        _all.Clear();

        var auto = new ServerItemViewModel(null, isAuto: true) { IsSelected = _selectedTag == "auto" };
        _all.Add(auto);

        foreach (var node in nodes)
        {
            _all.Add(new ServerItemViewModel(node, isAuto: false) { IsSelected = node.Tag == _selectedTag });
        }

        var selected = _all.FirstOrDefault(s => s.Tag == _selectedTag);
        SelectedName = selected?.Name ?? "Auto (быстрейший)";

        Rebuild();
        SetSelectedRow(_all.FirstOrDefault(s => s.Tag == _selectedTag));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnFilterChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var query = Filter.Trim();
        Servers.Clear();

        foreach (var item in _all)
        {
            if (query.Length == 0 ||
                item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.TypeLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                Servers.Add(item);
            }
        }
    }

    [RelayCommand]
    private async Task SelectAsync(ServerItemViewModel? item)
    {
        if (item is null)
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

        if (!TunnelRunning)
        {
            Status = "Сначала подключитесь: Clash API доступен только при активном туннеле";
            return;
        }

        IsTesting = true;
        Status = "Тестирую задержки...";

        try
        {
            using var clash = new ClashApiClient(ClashApiPort);
            using var semaphore = new SemaphoreSlim(6);
            var targets = Servers.ToList();

            var tasks = targets.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    item.IsTesting = true;
                    item.Delay = await clash.GetDelayAsync(item.Tag, "http://cp.cloudflare.com/generate_204", 5000);
                }
                finally
                {
                    item.IsTesting = false;
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            var answered = targets.Count(t => t.Delay is > 0);
            Status = answered == 0
                ? "Clash API не ответил — проверьте, что туннель поднят (Логи)"
                : $"Готово: ответили {answered} из {targets.Count}";
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

        using var clash = new ClashApiClient(ClashApiPort);
        item.IsTesting = true;
        try
        {
            item.Delay = await clash.GetDelayAsync(item.Tag, "http://cp.cloudflare.com/generate_204", 5000);
        }
        finally
        {
            item.IsTesting = false;
        }
    }
}
