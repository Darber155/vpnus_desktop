using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VpnUs.App.Services;
using VpnUs.Core.Apps;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;

namespace VpnUs.App.ViewModels;

public static class IconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryLoad(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        ImageSource? result = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(22, 22));
                result.Freeze();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        lock (Cache)
        {
            Cache[path] = result;
        }

        return result;
    }
}

public sealed partial class AppRowViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public AppRowViewModel(AppEntry entry)
    {
        Entry = entry;
        _isSelected = entry.Selected;
    }

    public AppEntry Entry { get; }

    public string Name => Entry.Name;

    public string PathText => Entry.Path;

    public string Source => Entry.Source;

    public bool IsRunning => Entry.IsRunning;

    [ObservableProperty]
    private bool _isSelected;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = IconLoader.TryLoad(Entry.Path);
            }

            return _icon;
        }
    }

    partial void OnIsSelectedChanged(bool value) => Entry.Selected = value;
}

public sealed partial class AppsViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly MainViewModel _main;
    private readonly List<AppRowViewModel> _all = [];
    private AppSettings? _draft;
    private bool _loaded;
    private bool _scanRunning;

    public AppsViewModel(ServiceClient client, MainViewModel main)
    {
        _client = client;
        _main = main;
    }

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];

    public ObservableCollection<string> Sources { get; } = ["Все", "Пуск", "Реестр", "Store", "Запущено", "Вручную"];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _sourceFilter = "Все";

    [ObservableProperty]
    private bool _showOnlyRunning;

    [ObservableProperty]
    private bool _showOnlySelected;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = "Нажмите «Обновить список», чтобы найти программы";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _dirty;

    [ObservableProperty]
    private AppSplitMode _splitMode = AppSplitMode.OnlySelected;

    [ObservableProperty]
    private ModeOption? _selectedMode;

    [ObservableProperty]
    private string _modeWarning = "";

    public ObservableCollection<ModeOption> Modes { get; } = [];

    public ObservableCollection<ResourceOption> Resources { get; } = [];

    public bool IsPerAppMode => SelectedMode?.Mode == RoutingMode.PerApp;

    partial void OnSelectedModeChanged(ModeOption? value)
    {
        OnPropertyChanged(nameof(IsPerAppMode));

        if (value is null)
        {
            return;
        }

        ModeWarning = value.Mode == RoutingMode.PerApp
            ? ""
            : $"Сейчас режим «{value.Title}»: список приложений не влияет на маршрутизацию. " +
              "Выберите «Только выбранные приложения» и сохраните, чтобы VPN применялся только к отмеченным.";

        Dirty = true;
    }

    public bool IsSplitAll
    {
        get => SplitMode == AppSplitMode.AllThroughVpn;
        set
        {
            if (value)
            {
                SplitMode = AppSplitMode.AllThroughVpn;
            }
        }
    }

    public bool IsSplitOnly
    {
        get => SplitMode == AppSplitMode.OnlySelected;
        set
        {
            if (value)
            {
                SplitMode = AppSplitMode.OnlySelected;
            }
        }
    }

    public bool IsSplitExcept
    {
        get => SplitMode == AppSplitMode.AllExceptSelected;
        set
        {
            if (value)
            {
                SplitMode = AppSplitMode.AllExceptSelected;
            }
        }
    }

    public bool IsEmpty => _all.Count == 0;

    partial void OnSplitModeChanged(AppSplitMode value)
    {
        OnPropertyChanged(nameof(IsSplitAll));
        OnPropertyChanged(nameof(IsSplitOnly));
        OnPropertyChanged(nameof(IsSplitExcept));
        Dirty = true;
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSourceFilterChanged(string value) => Rebuild();

    partial void OnShowOnlyRunningChanged(bool value) => Rebuild();

    partial void OnShowOnlySelectedChanged(bool value) => Rebuild();

    public void Load(AppSettings settings)
    {
        _draft = settings;
        _loaded = true;
        SplitMode = settings.AppSplit;

        if (Modes.Count == 0)
        {
            foreach (var routingMode in Enum.GetValues<RoutingMode>())
            {
                Modes.Add(new ModeOption(routingMode));
            }
        }

        if (Resources.Count == 0)
        {
            foreach (var name in RoutingModeInfo.SelectiveRuleSets)
            {
                Resources.Add(new ResourceOption(name));
            }
        }

        var enabledResources = settings.SelectiveRuleSets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in Resources)
        {
            resource.IsSelected = enabledResources.Contains(resource.Name);
        }

        var mode = Modes.FirstOrDefault(m => m.Mode == settings.Mode) ?? Modes[0];
        SelectedMode = mode;
        ModeWarning = mode.Mode == RoutingMode.PerApp
            ? ""
            : $"Сейчас режим «{mode.Title}»: список приложений не влияет на маршрутизацию. " +
              "Выберите «Только выбранные приложения» и сохраните, чтобы VPN применялся только к отмеченным.";

        foreach (var selected in settings.SelectedApps)
        {
            if (_all.All(a => !string.Equals(a.Entry.Path, selected.Path, StringComparison.OrdinalIgnoreCase)))
            {
                _all.Add(new AppRowViewModel(new AppEntry
                {
                    Name = selected.Name,
                    Path = selected.Path,
                    Source = "Вручную",
                    Selected = true,
                }));
            }
        }

        SyncSelectionFromDraft();
        Rebuild();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
            if (state is not null)
            {
                Load(state.Settings);
            }
        }

        if (_all.Count <= 1)
        {
            await ScanAsync();
        }
    }

    private void SyncSelectionFromDraft()
    {
        if (_draft is null)
        {
            return;
        }

        var selectedPaths = _draft.SelectedApps
            .Select(a => a.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _all)
        {
            row.IsSelected = selectedPaths.Contains(row.Entry.Path);
        }

        SelectedCount = _all.Count(a => a.IsSelected);
    }

    private void Rebuild()
    {
        var query = Search.Trim();
        Apps.Clear();

        foreach (var row in _all)
        {
            if (ShowOnlyRunning && !row.IsRunning)
            {
                continue;
            }

            if (ShowOnlySelected && !row.IsSelected)
            {
                continue;
            }

            if (SourceFilter != "Все" && !string.Equals(row.Source, SourceFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0 &&
                !row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !row.PathText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Apps.Add(row);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_scanRunning)
        {
            return;
        }

        _scanRunning = true;
        IsScanning = true;
        Status = "Сканирую установленные приложения...";

        try
        {
            var selectedNow = _all.Where(a => a.IsSelected).Select(a => a.Entry).ToList();
            var scanner = new InstalledAppScanner();

            var progress = new Progress<string>(text => Status = text);
            var entries = await scanner.ScanAsync(includeStoreApps: true, progress: progress);

            var merged = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                merged[entry.Path] = entry;
            }

            foreach (var selected in selectedNow)
            {
                if (merged.TryGetValue(selected.Path, out var existing))
                {
                    existing.Selected = true;
                }
                else
                {
                    selected.Selected = true;
                    merged[selected.Path] = selected;
                }
            }

            _all.Clear();
            foreach (var entry in merged.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _all.Add(new AppRowViewModel(entry));
            }

            if (_draft is not null)
            {
                var selectedPaths = _draft.SelectedApps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _all)
                {
                    row.IsSelected = selectedPaths.Contains(row.Entry.Path) || row.IsSelected;
                }
            }

            SelectedCount = _all.Count(a => a.IsSelected);
            Rebuild();
            OnPropertyChanged(nameof(IsEmpty));
            Status = $"Найдено приложений: {_all.Count}, выбрано: {SelectedCount}";
        }
        finally
        {
            IsScanning = false;
            _scanRunning = false;
        }
    }

    [RelayCommand]
    private void AddManual()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите исполняемый файл",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            var path = file;
            var existing = _all.FirstOrDefault(a => string.Equals(a.Entry.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.IsSelected = true;
                continue;
            }

            var entry = new AppEntry
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Path = path,
                Source = "Вручную",
                Selected = true,
            };

            _all.Insert(0, new AppRowViewModel(entry));
        }

        SelectedCount = _all.Count(a => a.IsSelected);
        Rebuild();
        OnPropertyChanged(nameof(IsEmpty));
        Dirty = true;
        Status = $"Добавлено вручную: выбрано {SelectedCount}";
    }

    [RelayCommand]
    private void SelectVisible()
    {
        foreach (var row in Apps)
        {
            row.IsSelected = true;
        }

        SelectedCount = _all.Count(a => a.IsSelected);
        Dirty = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in _all)
        {
            row.IsSelected = false;
        }

        SelectedCount = 0;
        Dirty = true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var state = await _client.GetAsync<ClientStateDto>(IpcCommands.GetSettings);
        if (state is null)
        {
            Status = _client.LastError ?? "Служба недоступна";
            return;
        }

        var settings = state.Settings;
        settings.AppSplit = SplitMode;
        if (SelectedMode is not null)
        {
            settings.Mode = SelectedMode.Mode;
        }

        settings.SelectedApps = _all
            .Where(a => a.IsSelected)
            .Select(a => new SelectedApp(a.Entry.Path, a.Entry.Name))
            .ToList();

        settings.SelectiveRuleSets = Resources
            .Where(r => r.IsSelected)
            .Select(r => r.Name)
            .ToList();

        if (await _client.ExecuteAsync(IpcCommands.SaveSettings, new { settings }))
        {
            _draft = settings;
            Dirty = false;
            SplitMode = settings.AppSplit;
            Status = settings.Mode == RoutingMode.PerApp
                ? $"Применено: {settings.SelectedApps.Count} приложений, режим «{SplitDescription()}»"
                : $"Сохранено, но активен режим «{SelectedMode?.Title}» — приложения не применяются";
            UiLog.Write("info", "Per-app настройки применены: " + settings.SelectedApps.Count);
            await _main.ReloadStateAsync();
        }
        else
        {
            Status = _client.LastError ?? "Не удалось применить настройки";
        }
    }

    [RelayCommand]
    private void MarkDirty() => Dirty = true;

    private string SplitDescription() => SplitMode switch
    {
        AppSplitMode.AllThroughVpn => "все приложения через VPN",
        AppSplitMode.AllExceptSelected => "все, кроме выбранных",
        _ => "только выбранные",
    };
}
