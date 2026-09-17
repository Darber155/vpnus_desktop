using System.Diagnostics;
using System.Text.Json.Nodes;
using VpnUs.Core.Clash;
using VpnUs.Core.Config;
using VpnUs.Core.Ipc;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;
using VpnUs.Core.Subscription;

namespace VpnUs.Service;

/// <summary>Оркестрация: настройки, подписка, генерация конфига, sing-box, IPC, автообновление.</summary>
public sealed class ServiceHost
{
    private readonly RingLog _log;
    private readonly SingBoxSupervisor _supervisor;
    private readonly CoreUpdater _updater;
    private readonly SubscriptionClient _subscription = new();
    private readonly PipeServer _pipe;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private AppSettings _settings;
    private List<ServerNode> _nodes;
    private Task? _schedulerTask;
    private bool _started;

    public ServiceHost()
    {
        _log = new RingLog(VpnUsPaths.ServiceLogFile);

        if (!VpnUsPaths.TryEnsureProgramData(out var pathError))
        {
            _log.Error(pathError!);
        }

        _settings = JsonStore.Load(VpnUsPaths.SettingsFile, () => new AppSettings());
        _nodes = JsonStore.Load(VpnUsPaths.NodesFile, () => new List<ServerNode>());
        _supervisor = new SingBoxSupervisor(_log);
        _updater = new CoreUpdater(_log);
        _pipe = new PipeServer(_log, HandleAsync);
    }

    public RingLog Log => _log;

    public SingBoxSupervisor Supervisor => _supervisor;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _log.Info($"VpnUs Service: узлов {_nodes.Count}, режим {_settings.Mode}, per-app {RoutingModeInfo.DisplayName(_settings.AppSplit)}");
        _pipe.Start();
        _schedulerTask = Task.Run(() => SchedulerLoopAsync(_cts.Token));
        _ = Task.Run(StartupAsync);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        await _supervisor.StopAsync().ConfigureAwait(false);

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _log.Info("Служба остановлена");
    }

    private async Task StartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            if (ShouldRefreshSubscription())
            {
                var result = await RefreshSubscriptionAsync(_cts.Token).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _log.Warn("Автообновление подписки: " + result.Error);
                }
            }

            if (_settings.AutoConnect && _nodes.Count > 0)
            {
                _log.Info("Автоподключение...");
                var (ok, error) = await ConnectInternalAsync(_cts.Token).ConfigureAwait(false);
                if (!ok)
                {
                    _log.Error("Автоподключение не удалось: " + error);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Startup: " + ex);
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

                await _mutex.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ShouldRefreshSubscription())
                    {
                        await RefreshSubscriptionAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _mutex.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldRefreshSubscription()
    {
        if (!_settings.SubscriptionAutoUpdate || string.IsNullOrWhiteSpace(_settings.SubscriptionUrl))
        {
            return false;
        }

        var hours = Math.Clamp(_settings.SubscriptionUpdateHours, 1, 720);
        return _settings.SubscriptionUpdatedAt is null ||
               DateTimeOffset.Now - _settings.SubscriptionUpdatedAt.Value >= TimeSpan.FromHours(hours);
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            return request.Cmd switch
            {
                IpcCommands.Ping => IpcResponse.Success(request.Id, new { pong = true, time = DateTimeOffset.Now }),
                IpcCommands.Status => IpcResponse.Success(request.Id, BuildStatus()),
                IpcCommands.Connect => await ConnectAsync(request).ConfigureAwait(false),
                IpcCommands.Disconnect => await DisconnectAsync(request).ConfigureAwait(false),
                IpcCommands.GetSettings => IpcResponse.Success(request.Id, new ClientStateDto { Settings = _settings, Nodes = _nodes }),
                IpcCommands.SaveSettings => await SaveSettingsAsync(request).ConfigureAwait(false),
                IpcCommands.SetSelectedNode => await SetSelectedNodeAsync(request).ConfigureAwait(false),
                IpcCommands.RefreshSubscription => await RefreshCommandAsync(request).ConfigureAwait(false),
                IpcCommands.GetLogs => GetLogs(request),
                IpcCommands.ClearLogs => ClearLogs(request),
                IpcCommands.UpdateCore => await UpdateCoreAsync(request).ConfigureAwait(false),
                IpcCommands.GetCoreInfo => IpcResponse.Success(request.Id, _updater.GetInfo()),
                IpcCommands.PreviewConfig => PreviewConfig(request),
                IpcCommands.ValidateConfig => await ValidateConfigAsync(request).ConfigureAwait(false),
                IpcCommands.RestartTunnel => await RestartTunnelAsync(request).ConfigureAwait(false),
                _ => IpcResponse.Fail(request.Id, $"Неизвестная команда: {request.Cmd}"),
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    private ServiceStatusDto BuildStatus() => new()
    {
        State = _supervisor.State.ToString().ToLowerInvariant(),
        Pid = _supervisor.Pid,
        StartedAt = _supervisor.StartedAt,
        ConnectedAt = _supervisor.ConnectedAt,
        LastError = _supervisor.LastError,
        ClashApiPort = _settings.ClashApiPort,
        NodeCount = _nodes.Count,
        SelectedNode = _settings.SelectedNode,
        SelectedNodeName = ResolveSelectedName(),
        SubscriptionUpdatedAt = _settings.SubscriptionUpdatedAt,
        // camelCase, чтобы UI сравнивал со своими строками ("perApp", "onlySelected", ...)
        Mode = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(_settings.Mode.ToString()),
        AppSplit = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(_settings.AppSplit.ToString()),
        CoreVersion = _updater.GetInfo().Version,
        CorePresent = File.Exists(VpnUsPaths.CoreExe),
        SubscriptionUrl = _settings.SubscriptionUrl,
        ServicePath = Environment.ProcessPath ?? "",
    };

    private string ResolveSelectedName()
    {
        if (_settings.SelectedNode is "" or "auto")
        {
            return "Auto (быстрейший)";
        }

        var node = _nodes.FirstOrDefault(n => n.Tag == _settings.SelectedNode);
        return node?.Display ?? "Auto (быстрейший)";
    }

    private async Task<IpcResponse> ConnectAsync(IpcRequest request)
    {
        var (ok, error) = await ConnectInternalAsync(CancellationToken.None).ConfigureAwait(false);
        return ok
            ? IpcResponse.Success(request.Id, BuildStatus())
            : IpcResponse.Fail(request.Id, error ?? "Не удалось запустить туннель");
    }

    private async Task<(bool Ok, string? Error)> ConnectInternalAsync(CancellationToken ct)
    {
        if (_nodes.Count == 0)
        {
            return (false, "Подписка пуста: укажите URL подписки и обновите её");
        }

        var configPath = WriteConfig();
        await _supervisor.StartAsync(configPath, _settings.ClashApiPort, clashApiEnabled: true, ct).ConfigureAwait(false);

        if (_supervisor.State == ServiceState.Error)
        {
            return (false, _supervisor.LastError);
        }

        await ApplySelectedNodeAsync().ConfigureAwait(false);
        return (true, null);
    }

    private async Task ApplySelectedNodeAsync()
    {
        if (_settings.SelectedNode is "" or "auto")
        {
            return;
        }

        using var clash = new ClashApiClient(_settings.ClashApiPort);
        if (await clash.SelectAsync("proxy", _settings.SelectedNode).ConfigureAwait(false))
        {
            _log.Info($"Активный сервер: {ResolveSelectedName()}");
        }
    }

    private async Task<IpcResponse> DisconnectAsync(IpcRequest request)
    {
        await _supervisor.StopAsync().ConfigureAwait(false);
        return IpcResponse.Success(request.Id, BuildStatus());
    }

    private async Task<IpcResponse> SaveSettingsAsync(IpcRequest request)
    {
        var incoming = request.GetArg<AppSettings>("settings");
        if (incoming is null)
        {
            return IpcResponse.Fail(request.Id, "Не переданы настройки");
        }

        NormalizeSettings(incoming);

        var beforeJson = SingBoxConfigBuilder.BuildJson(_settings, _nodes, BuildOptions());
        var afterJson = SingBoxConfigBuilder.BuildJson(incoming, _nodes, BuildOptions());

        _settings = incoming;
        JsonStore.Save(VpnUsPaths.SettingsFile, _settings);
        _log.Info($"Настройки сохранены: режим {_settings.Mode}, приложения {RoutingModeInfo.DisplayName(_settings.AppSplit)}, выбранных приложений {_settings.SelectedApps.Count}");

        if (_supervisor.IsRunning && !string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {
            _log.Info("Настройки влияют на маршрутизацию — перезапускаю туннель");
            WriteConfig();
            await _supervisor.RestartAsync().ConfigureAwait(false);
        }

        return IpcResponse.Success(request.Id, BuildStatus());
    }

    private async Task<IpcResponse> SetSelectedNodeAsync(IpcRequest request)
    {
        var tag = request.GetString("tag");
        if (tag.Length == 0)
        {
            return IpcResponse.Fail(request.Id, "Не передан тег сервера");
        }

        if (tag != "auto" && _nodes.All(n => n.Tag != tag))
        {
            return IpcResponse.Fail(request.Id, "Сервер не найден в текущей подписке");
        }

        _settings.SelectedNode = tag;
        JsonStore.Save(VpnUsPaths.SettingsFile, _settings);

        if (_supervisor.IsRunning)
        {
            using var clash = new ClashApiClient(_settings.ClashApiPort);
            var target = tag == "auto" ? "auto" : tag;
            if (await clash.SelectAsync("proxy", target).ConfigureAwait(false))
            {
                _log.Info($"Активный сервер: {ResolveSelectedName()}");
            }
            else
            {
                _log.Warn("Clash API не ответил на выбор сервера");
            }
        }

        return IpcResponse.Success(request.Id, BuildStatus());
    }

    private async Task<IpcResponse> RefreshCommandAsync(IpcRequest request)
    {
        var url = request.GetString("url");
        if (url.Length > 0 && url != _settings.SubscriptionUrl)
        {
            _settings.SubscriptionUrl = url;
            JsonStore.Save(VpnUsPaths.SettingsFile, _settings);
        }

        var result = await RefreshSubscriptionAsync(CancellationToken.None).ConfigureAwait(false);
        return result.Ok ? IpcResponse.Success(request.Id, result) : IpcResponse.Fail(request.Id, result.Error ?? "Не удалось обновить подписку");
    }

    public async Task<SubscriptionRefreshDto> RefreshSubscriptionAsync(CancellationToken ct)
    {
        var url = _settings.SubscriptionUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new SubscriptionRefreshDto { Ok = false, Error = "URL подписки не задан" };
        }

        _log.Info("Обновляю подписку...");
        var (fetch, parse) = await _subscription.FetchAndParseAsync(url, ct).ConfigureAwait(false);

        if (!fetch.Success)
        {
            _log.Error("Подписка: " + fetch.Error);
            return new SubscriptionRefreshDto
            {
                Ok = false,
                Error = fetch.Error,
                StatusCode = fetch.StatusCode,
                Redirects = fetch.Redirects,
            };
        }

        if (parse.Nodes.Count == 0)
        {
            var error = parse.Error ?? "Подписка не содержит поддерживаемых серверов";
            _log.Error("Подписка: " + error);
            return new SubscriptionRefreshDto
            {
                Ok = false,
                Error = error,
                StatusCode = fetch.StatusCode,
                Redirects = fetch.Redirects,
            };
        }

        _nodes = parse.Nodes;
        JsonStore.Save(VpnUsPaths.NodesFile, _nodes);

        try
        {
            File.WriteAllText(Path.Combine(VpnUsPaths.DataDir, "subscription.txt"), fetch.Body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _settings.SubscriptionUpdatedAt = DateTimeOffset.Now;
        if (_settings.SelectedNode != "auto" && _nodes.All(n => n.Tag != _settings.SelectedNode))
        {
            _settings.SelectedNode = "auto";
        }

        JsonStore.Save(VpnUsPaths.SettingsFile, _settings);

        _log.Info($"Подписка обновлена: серверов {_nodes.Count}, пропущено {parse.Skipped}, " +
                  $"HTTP {fetch.StatusCode}, редиректов {fetch.Redirects}");

        if (_supervisor.IsRunning)
        {
            WriteConfig();
            await _supervisor.RestartAsync(ct).ConfigureAwait(false);
            await ApplySelectedNodeAsync().ConfigureAwait(false);
        }

        return new SubscriptionRefreshDto
        {
            Ok = true,
            NodeCount = _nodes.Count,
            Skipped = parse.Skipped,
            StatusCode = fetch.StatusCode,
            Redirects = fetch.Redirects,
        };
    }

    private IpcResponse GetLogs(IpcRequest request)
    {
        long sinceId = 0;
        if (request.Args?["sinceId"] is JsonNode node)
        {
            try
            {
                sinceId = node.GetValue<long>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
            }
        }

        return IpcResponse.Success(request.Id, _log.Since(sinceId));
    }

    private IpcResponse ClearLogs(IpcRequest request)
    {
        _log.Clear();
        return IpcResponse.Success(request.Id, new LogsDto { LastId = _log.LastId });
    }

    private IpcResponse PreviewConfig(IpcRequest request)
    {
        var json = SingBoxConfigBuilder.BuildJson(_settings, _nodes, BuildOptions());
        return IpcResponse.Success(request.Id, new ConfigPreviewDto { Json = json, Path = VpnUsPaths.ConfigFile });
    }

    private async Task<IpcResponse> ValidateConfigAsync(IpcRequest request)
    {
        var json = request.Args?["json"]?.GetValue<string>();
        var path = "";

        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                path = Path.Combine(Path.GetTempPath(), $"vpnus-check-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            }
            else
            {
                path = WriteConfig();
            }

            var (code, output) = await RunCoreCheckAsync(path).ConfigureAwait(false);
            return IpcResponse.Success(request.Id, new ValidationDto { Ok = code == 0, Output = output });
        }
        finally
        {
            if (json is not null && path.Length > 0)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task<(int Code, string Output)> RunCoreCheckAsync(string configPath)
    {
        if (!File.Exists(VpnUsPaths.CoreExe))
        {
            return (-1, "sing-box.exe не найден");
        }

        var psi = new ProcessStartInfo(VpnUsPaths.CoreExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = VpnUsPaths.DataDir,
        };

        psi.ArgumentList.Add("check");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("-D");
        psi.ArgumentList.Add(VpnUsPaths.DataDir);
        psi.Environment["ENABLE_DEPRECATED_TUN_STACK"] = "true";

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "не удалось запустить sing-box check");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return (process.ExitCode, output.Trim());
    }

    private async Task<IpcResponse> UpdateCoreAsync(IpcRequest request)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var wasRunning = _supervisor.IsRunning;

        try
        {
            if (wasRunning)
            {
                await _supervisor.StopAsync().ConfigureAwait(false);
            }

            var info = await _updater.UpdateAsync(CancellationToken.None).ConfigureAwait(false);
            _log.Info($"Обновление ядра завершено за {stopwatch.Elapsed.TotalSeconds:0} с");
            return IpcResponse.Success(request.Id, info);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _log.Error("Обновление ядра: " + ex.Message);
            return IpcResponse.Fail(request.Id, "Не удалось обновить ядро: " + ex.Message);
        }
        finally
        {
            if (wasRunning && File.Exists(VpnUsPaths.CoreExe))
            {
                WriteConfig();
                await _supervisor.StartAsync(VpnUsPaths.ConfigFile, _settings.ClashApiPort, clashApiEnabled: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<IpcResponse> RestartTunnelAsync(IpcRequest request)
    {
        if (!_supervisor.IsRunning)
        {
            return await ConnectAsync(request).ConfigureAwait(false);
        }

        WriteConfig();
        await _supervisor.RestartAsync().ConfigureAwait(false);
        await ApplySelectedNodeAsync().ConfigureAwait(false);
        return IpcResponse.Success(request.Id, BuildStatus());
    }

    private SingBoxBuildOptions BuildOptions() => new()
    {
        ClashApiPort = _settings.ClashApiPort,
        CachePath = Path.Combine(VpnUsPaths.DataDir, "cache.db"),
        EnableClashApi = true,
        LogLevel = string.IsNullOrWhiteSpace(_settings.LogLevel) ? "warn" : _settings.LogLevel,
    };

    private string WriteConfig()
    {
        var json = SingBoxConfigBuilder.BuildJson(_settings, _nodes, BuildOptions());
        Directory.CreateDirectory(Path.GetDirectoryName(VpnUsPaths.ConfigFile)!);
        File.WriteAllText(VpnUsPaths.ConfigFile, json);

        _log.Info($"Конфиг сохранён: {VpnUsPaths.ConfigFile} (серверов {_nodes.Count}, режим {_settings.Mode}, " +
                  $"приложений {(_settings.Mode == RoutingMode.PerApp ? _settings.SelectedApps.Count : 0)})");

        return VpnUsPaths.ConfigFile;
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.SubscriptionUrl = settings.SubscriptionUrl?.Trim() ?? "";
        settings.SubscriptionUpdateHours = Math.Clamp(settings.SubscriptionUpdateHours, 1, 720);
        settings.ClashApiPort = settings.ClashApiPort is < 1024 or > 65535 ? 9090 : settings.ClashApiPort;
        settings.TunMtu = settings.TunMtu is < 576 or > 65535 ? 9000 : settings.TunMtu;
        settings.UrlTestInterval = string.IsNullOrWhiteSpace(settings.UrlTestInterval) ? "3m" : settings.UrlTestInterval;
        settings.UrlTestUrl = string.IsNullOrWhiteSpace(settings.UrlTestUrl) ? "http://cp.cloudflare.com/generate_204" : settings.UrlTestUrl;
        settings.DnsStrategy = settings.DnsStrategy is "ipv4_only" or "ipv6_only" or "prefer_ipv4" or "prefer_ipv6" ? settings.DnsStrategy : "ipv4_only";
        settings.TunInterface = string.IsNullOrWhiteSpace(settings.TunInterface) ? "happwrt0" : settings.TunInterface.Trim();
        settings.SelectedApps = settings.SelectedApps
            .Where(a => !string.IsNullOrWhiteSpace(a.Path))
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
