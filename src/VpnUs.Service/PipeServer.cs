using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using VpnUs.Core.Ipc;
using VpnUs.Core.Storage;

namespace VpnUs.Service;

/// <summary>
/// Named pipe сервер. Каждый запрос авторизуется токеном из ProgramData\VpnUs\ipc.token
/// (файл создаёт служба, поэтому пользователь не может его подменить) и, если задан
/// ipc.allow, SID вызывающего пользователя.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly RingLog _log;
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private readonly string _token;
    private readonly HashSet<string> _allowedSids = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public PipeServer(RingLog log, Func<IpcRequest, Task<IpcResponse>> handler)
    {
        _log = log;
        _handler = handler;
        _token = EnsureTokenSafe() ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        try
        {
            if (File.Exists(VpnUsPaths.AllowFile))
            {
                foreach (var line in File.ReadAllLines(VpnUsPaths.AllowFile))
                {
                    var sid = line.Trim();
                    if (sid.Length > 0 && !sid.StartsWith('#'))
                    {
                        _allowedSids.Add(sid);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Не удалось прочитать ipc.allow: " + ex.Message);
        }

        if (_allowedSids.Count > 0)
        {
            _log.Info($"IPC: разрешены SID: {string.Join(", ", _allowedSids)}");
        }
        else
        {
            _log.Warn("IPC: ipc.allow не задан, доступ по токену разрешён всем локальным пользователям");
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"IPC-сервер слушает \\\\.\\pipe\\{VpnUsPaths.PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = IpcPipeFactory.CreateServer(VpnUsPaths.PipeName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log.Error("Не удалось создать IPC-канал: " + ex.Message);
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _ = Task.Run(() => HandleClientAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                var request = await IpcFraming.ReadAsync<IpcRequest>(pipe, ct).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var authError = Authorize(pipe, request);
                IpcResponse response;

                if (authError is not null)
                {
                    _log.Warn($"IPC отказ ({request.Cmd}): {authError}");
                    response = IpcResponse.Fail(request.Id, authError);
                }
                else
                {
                    try
                    {
                        response = await _handler(request).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"IPC {request.Cmd}: {ex}");
                        response = IpcResponse.Fail(request.Id, IpcErrors.Describe(ex));
                    }
                }

                await IpcFraming.WriteAsync(pipe, response, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    private string? Authorize(NamedPipeServerStream pipe, IpcRequest request)
    {
        var token = request.Args?["token"]?.GetValue<string>() ?? "";
        if (!FixedTimeEquals(token, _token))
        {
            return "Неверный IPC-токен";
        }

        if (_allowedSids.Count == 0)
        {
            return null;
        }

        string? sid = null;
        try
        {
            var userName = pipe.GetImpersonationUserName();
            if (!string.IsNullOrWhiteSpace(userName))
            {
                sid = new NTAccount(userName).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier is { } identifier
                    ? identifier.Value
                    : null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IdentityNotMappedException or IOException)
        {
        }

        if (sid is null)
        {
            return "Не удалось определить SID вызывающего пользователя";
        }

        return _allowedSids.Contains(sid) ? null : "Пользователь не входит в ipc.allow";
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private string? EnsureTokenSafe()
    {
        try
        {
            return EnsureToken();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _log.Error("Не удалось записать ipc.token: " + ex.Message +
                       ". Служба работает без сохранённого токена (только в памяти).");
            return null;
        }
    }

    private static string EnsureToken()
    {
        try
        {
            if (File.Exists(VpnUsPaths.TokenFile))
            {
                var existing = File.ReadAllText(VpnUsPaths.TokenFile).Trim();
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(VpnUsPaths.TokenFile, token);
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
    }
}
