using System.Text.Json.Nodes;
using VpnUs.Core.Ipc;

namespace VpnUs.Service;

/// <summary>Кольцевой буфер логов с записью в файл (без внешних зависимостей).</summary>
public sealed class RingLog
{
    private const int Capacity = 5000;
    private readonly object _gate = new();
    private readonly Queue<LogLineDto> _lines = new();
    private readonly string? _filePath;
    private long _nextId;

    public RingLog(string? filePath = null) => _filePath = filePath;

    public event Action<LogLineDto>? LineAdded;

    public long LastId
    {
        get
        {
            lock (_gate)
            {
                return _nextId;
            }
        }
    }

    public void Info(string message) => Write("info", message);

    public void Warn(string message) => Write("warn", message);

    public void Error(string message) => Write("error", message);

    public void Debug(string message) => Write("debug", message);

    public void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogLineDto line;
        lock (_gate)
        {
            line = new LogLineDto
            {
                Id = ++_nextId,
                Time = DateTimeOffset.Now.ToString("HH:mm:ss"),
                Level = level,
                Message = message.TrimEnd(),
            };

            _lines.Enqueue(line);
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }
        }

        if (_filePath is not null)
        {
            RotatingFileWriter.Append(_filePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {line.Message}");
        }

        LineAdded?.Invoke(line);
    }

    /// <summary>Разбор JSON-строки sing-box ({"level":..,"time":..,"message":..}).</summary>
    public void WriteCoreLine(string raw, bool isErrorStream)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonObject obj && obj["message"] is JsonNode messageNode)
                {
                    var level = obj["level"]?.GetValue<string>() ?? (isErrorStream ? "error" : "info");
                    var text = messageNode.GetValue<string>();
                    Write(level, text);
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
            }
        }

        Write(isErrorStream ? "error" : "info", trimmed);
    }

    public LogsDto Since(long sinceId)
    {
        lock (_gate)
        {
            return new LogsDto
            {
                Lines = _lines.Where(l => l.Id > sinceId).ToList(),
                LastId = _nextId,
            };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}

public static class RotatingFileWriter
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 3;
    private static readonly object Gate = new();

    public static void Append(string path, string line)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    Rotate(path);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var last = $"{path}.{MaxFiles - 1}";
            if (File.Exists(last))
            {
                File.Delete(last);
            }

            for (var i = MaxFiles - 2; i >= 1; i--)
            {
                var source = $"{path}.{i}";
                if (File.Exists(source))
                {
                    File.Move(source, $"{path}.{i + 1}", overwrite: true);
                }
            }

            File.Move(path, $"{path}.1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
