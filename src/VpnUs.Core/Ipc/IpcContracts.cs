using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;

namespace VpnUs.Core.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string Status = "status";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string GetSettings = "getSettings";
    public const string SaveSettings = "saveSettings";
    public const string RefreshSubscription = "refreshSubscription";
    public const string SetSelectedNode = "setSelectedNode";
    public const string GetLogs = "getLogs";
    public const string ClearLogs = "clearLogs";
    public const string UpdateCore = "updateCore";
    public const string GetCoreInfo = "getCoreInfo";
    public const string PreviewConfig = "previewConfig";
    public const string ValidateConfig = "validateConfig";
    public const string RestartTunnel = "restartTunnel";
}

public sealed class IpcRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Cmd { get; set; } = "";
    public JsonObject? Args { get; set; }

    public static IpcRequest Create(string cmd, object? args = null)
    {
        var request = new IpcRequest { Cmd = cmd };
        if (args is not null)
        {
            request.Args = JsonSerializer.SerializeToNode(args, JsonStore.Options) as JsonObject;
        }

        return request;
    }

    public T? GetArg<T>(string name)
    {
        if (Args?[name] is not JsonNode node)
        {
            return default;
        }

        try
        {
            return node.Deserialize<T>(JsonStore.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public string GetString(string name) => Args?[name]?.GetValue<string>() ?? "";
}

public sealed class IpcResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public JsonObject? Result { get; set; }
    public string? Error { get; set; }

    public static IpcResponse Success(string id, object? result = null)
        => new()
        {
            Id = id,
            Ok = true,
            Result = result is null ? null : JsonSerializer.SerializeToNode(result, JsonStore.Options) as JsonObject,
        };

    public static IpcResponse SuccessNode(string id, JsonNode? result)
        => new() { Id = id, Ok = true, Result = result as JsonObject };

    public static IpcResponse Fail(string id, string error) => new() { Id = id, Ok = false, Error = error };

    public T? As<T>() => Result is null ? default : Result.Deserialize<T>(JsonStore.Options);
}

public static class IpcFraming
{
    private const int MaxFrame = 64 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonStore.Options);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrame)
        {
            return default;
        }

        var buffer = new byte[length];
        if (!await ReadExactAsync(stream, buffer, ct).ConfigureAwait(false))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(buffer, JsonStore.Options);
    }

    public static IpcResponse? ReadResponse(Stream stream, CancellationToken ct = default)
        => ReadAsync<IpcResponse>(stream, ct).GetAwaiter().GetResult();

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

public static class IpcErrors
{
    /// <summary>Понятные сообщения вместо сырых исключений (в первую очередь про права на ProgramData).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => $"Нет доступа к {Storage.VpnUsPaths.ProgramDataRoot} ({ex.Message}). " +
                                       "Переустановите службу от администратора: VpnUs.Service.exe install.",
        IOException io => "Ошибка ввода-вывода: " + io.Message,
        _ => ex.Message,
    };
}

public sealed class ServiceStatusDto
{
    public string State { get; set; } = "stopped";
    public int Pid { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public string? LastError { get; set; }
    public int ClashApiPort { get; set; } = 9090;
    public int NodeCount { get; set; }
    public string SelectedNode { get; set; } = "auto";
    public string SelectedNodeName { get; set; } = "Auto";
    public DateTimeOffset? SubscriptionUpdatedAt { get; set; }
    public string Mode { get; set; } = "";
    public string AppSplit { get; set; } = "";
    public string CoreVersion { get; set; } = "";
    public bool CorePresent { get; set; }
    public string SubscriptionUrl { get; set; } = "";
    public string ServicePath { get; set; } = "";
}

public sealed class LogLineDto
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class LogsDto
{
    public List<LogLineDto> Lines { get; set; } = [];
    public long LastId { get; set; }
}

public sealed class SubscriptionRefreshDto
{
    public bool Ok { get; set; }
    public int NodeCount { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }
    public int StatusCode { get; set; }
    public int Redirects { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CoreInfoDto
{
    public string Version { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Present { get; set; }
    public bool WintunPresent { get; set; }
    public bool WintunEmbedded { get; set; }
}

public sealed class NodeDelayDto
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Delay { get; set; }
}

public sealed class DelaysDto
{
    public List<NodeDelayDto> Delays { get; set; } = [];
}

public sealed class ClientStateDto
{
    public AppSettings Settings { get; set; } = new();
    public List<ServerNode> Nodes { get; set; } = [];
}

public sealed class ConfigPreviewDto
{
    public string Json { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class ValidationDto
{
    public bool Ok { get; set; }
    public string Output { get; set; } = "";
}
