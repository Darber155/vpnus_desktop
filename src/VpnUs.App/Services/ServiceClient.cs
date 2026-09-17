using System.IO.Pipes;
using System.Text.Json.Nodes;
using VpnUs.Core.Ipc;
using VpnUs.Core.Storage;

namespace VpnUs.App.Services;

/// <summary>Клиент named pipe для общения со службой. Токен читается из ProgramData.</summary>
public sealed class ServiceClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _token = "";
    private DateTimeOffset _tokenReadAt = DateTimeOffset.MinValue;

    public string? LastError { get; private set; }

    public bool IsServiceInstalled
    {
        get
        {
            EnsureToken();
            return _token.Length >= 32;
        }
    }

    public string TokenFile => VpnUsPaths.TokenFile;

    private void EnsureToken(bool force = false)
    {
        if (!force && _token.Length >= 32 && DateTimeOffset.Now - _tokenReadAt < TimeSpan.FromSeconds(20))
        {
            return;
        }

        try
        {
            _token = File.Exists(VpnUsPaths.TokenFile) ? File.ReadAllText(VpnUsPaths.TokenFile).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _token = "";
        }

        _tokenReadAt = DateTimeOffset.Now;
    }

    public async Task<IpcResponse?> SendAsync(string cmd, object? args = null, CancellationToken ct = default)
    {
        EnsureToken();

        if (_token.Length < 32)
        {
            LastError = "Служба VpnUs не установлена (нет ipc.token)";
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var pipe = new NamedPipeClientStream(".", VpnUsPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));

            try
            {
                await pipe.ConnectAsync(4000, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastError = "Служба не отвечает (нет подключения к pipe)";
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                LastError = "Нет доступа к службе: " + ex.Message +
                            ". Переустановите службу кнопкой «Установить / починить службу (UAC)» в «Настройках».";
                return null;
            }
            catch (System.Security.SecurityException ex)
            {
                LastError = "Политика безопасности запрещает подключение к службе: " + ex.Message;
                return null;
            }
            catch (IOException ex)
            {
                LastError = "Служба не запущена: " + ex.Message;
                return null;
            }

            var request = IpcRequest.Create(cmd, args);
            request.Args ??= new JsonObject();
            request.Args["token"] = _token;

            await IpcFraming.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            var response = await IpcFraming.ReadAsync<IpcResponse>(pipe, timeout.Token).ConfigureAwait(false);

            if (response is null)
            {
                LastError = "Служба закрыла соединение";
                return null;
            }

            if (response.Ok)
            {
                LastError = null;
                return response;
            }

            LastError = response.Error ?? "Неизвестная ошибка службы";
            if (LastError.Contains("токен", StringComparison.OrdinalIgnoreCase))
            {
                EnsureToken(force: true);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            LastError = "Превышено время ожидания ответа службы";
            return null;
        }
        catch (IOException ex)
        {
            LastError = "Ошибка связи со службой: " + ex.Message;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> GetAsync<T>(string cmd, object? args = null, CancellationToken ct = default)
        where T : class
    {
        var response = await SendAsync(cmd, args, ct).ConfigureAwait(false);
        if (response is null || !response.Ok)
        {
            return null;
        }

        try
        {
            return response.As<T>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            LastError = "Некорректный ответ службы";
            return null;
        }
    }

    public async Task<bool> ExecuteAsync(string cmd, object? args = null, CancellationToken ct = default)
    {
        var response = await SendAsync(cmd, args, ct).ConfigureAwait(false);
        return response is { Ok: true };
    }
}
