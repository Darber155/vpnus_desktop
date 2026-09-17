using System.Collections.ObjectModel;
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
    private long _sinceId;

    public LogsViewModel(ServiceClient client) => _client = client;

    public ObservableCollection<LogLineDto> Lines { get; } = [];

    public ObservableCollection<string> Levels { get; } = ["Все", "info", "warn", "error", "debug"];

    [ObservableProperty]
    private string _levelFilter = "Все";

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private string _status = "Логи загружаются...";

    partial void OnLevelFilterChanged(string value) => _ = RebuildAsync();

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

        Status = $"{Lines.Count} строк · последняя: {DateTimeOffset.Now:HH:mm:ss}";
    }

    private bool Matches(LogLineDto line)
    {
        if (LevelFilter == "Все")
        {
            return true;
        }

        return line.Level.Contains(LevelFilter, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        await _client.ExecuteAsync(IpcCommands.ClearLogs);
        Lines.Clear();
        _sinceId = 0;
        Status = "Логи очищены";
    }

    [RelayCommand]
    private void TogglePause() => Paused = !Paused;

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
