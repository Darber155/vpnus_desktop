using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VpnUs.App.Services;
using VpnUs.Core.Ipc;

namespace VpnUs.App.ViewModels;

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 3000;
    private readonly ServiceClient _client;
    private readonly DispatcherTimer _filterTimer;
    private long _sinceId;

    public LogsViewModel(ServiceClient client)
    {
        _client = client;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            await RebuildAsync();
        };
    }

    public ObservableCollection<LogLineDto> Lines { get; } = [];

    public ObservableCollection<string> Levels { get; } = ["Все", "info", "warn", "error", "debug", "trace", "fatal"];

    [ObservableProperty]
    private string _levelFilter = "Все";

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private string _status = "Логи загружаются...";

    public string PauseLabel => Paused ? "Продолжить" : "Пауза";

    public bool HasLines => Lines.Count > 0;

    partial void OnLevelFilterChanged(string value) => _ = RebuildAsync();

    partial void OnSearchChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    partial void OnPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    public async Task PollAsync()
    {
        if (Paused)
        {
            return;
        }

        var logs = await _client.GetAsync<LogsDto>(IpcCommands.GetLogs, new { sinceId = _sinceId });
        if (logs is null)
        {
            Status = _client.LastError ?? "Служба недоступна";
            return;
        }

        _sinceId = logs.LastId;
        Append(logs.Lines);
    }

    public async Task RebuildAsync()
    {
        _sinceId = 0;
        Lines.Clear();

        var logs = await _client.GetAsync<LogsDto>(IpcCommands.GetLogs, new { sinceId = 0L });
        if (logs is null)
        {
            Status = _client.LastError ?? "Служба недоступна";
            OnPropertyChanged(nameof(HasLines));
            return;
        }

        _sinceId = logs.LastId;
        Append(logs.Lines);
    }

    private void Append(IEnumerable<LogLineDto> lines)
    {
        foreach (var line in lines)
        {
            if (!Matches(line))
            {
                continue;
            }

            Lines.Add(line);
        }

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }

        Status = Paused
            ? $"{Lines.Count} строк · пауза"
            : $"{Lines.Count} строк · последняя: {DateTimeOffset.Now:HH:mm:ss}";
        OnPropertyChanged(nameof(HasLines));
    }

    private bool Matches(LogLineDto line)
    {
        if (LevelFilter != "Все" && !line.Level.Contains(LevelFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = Search.Trim();
        return query.Length == 0 ||
               line.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               line.Level.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               line.Time.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        var confirm = MessageBox.Show(
            App.Current.MainWindow,
            "Очистить логи службы и список на экране?",
            "Очистка логов",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _client.ExecuteAsync(IpcCommands.ClearLogs);
        Lines.Clear();
        _sinceId = 0;
        Status = "Логи очищены";
        OnPropertyChanged(nameof(HasLines));
    }

    [RelayCommand]
    private void ClearSearch() => Search = "";

    [RelayCommand]
    private void TogglePause()
    {
        Paused = !Paused;
        Status = Paused ? $"{Lines.Count} строк · пауза" : $"{Lines.Count} строк · продолжение";
    }

    [RelayCommand]
    private void CopyAll()
    {
        if (Lines.Count == 0)
        {
            Status = "Копировать нечего";
            return;
        }

        try
        {
            var text = string.Join(Environment.NewLine, Lines.Select(l => $"{l.Time} [{l.Level}] {l.Message}"));
            Clipboard.SetText(text);
            Status = $"Скопировано строк: {Lines.Count}";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status = "Не удалось скопировать логи";
        }
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт логов",
            FileName = $"vpnus-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllLines(dialog.FileName, Lines.Select(l => $"{l.Time} [{l.Level}] {l.Message}"));
            Status = "Сохранено: " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Ошибка экспорта: " + ex.Message;
        }
    }
}
