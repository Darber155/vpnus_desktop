using System.Diagnostics;
using VpnUs.Core.Clash;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;

namespace VpnUs.Service;

/// <summary>
/// Управляет процессом sing-box: запуск под LocalSystem, ожидание готовности Clash API,
/// автоперезапуск при падении, корректная остановка (kill процесса удаляет Wintun-адаптер,
/// вместе с которым Windows снимает маршруты; системный DNS не меняется).
/// </summary>
public sealed class SingBoxSupervisor
{
    private readonly RingLog _log;
    private readonly object _gate = new();
    private Process? _process;
    private string? _configPath;
    private int _clashApiPort = 9090;
    private bool _clashApiEnabled = true;
    private volatile bool _stopRequested;
    private int _restartAttempts;

    public SingBoxSupervisor(RingLog log) => _log = log;

    public ServiceState State { get; private set; } = ServiceState.Stopped;

    public string? LastError { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    public event Action? Changed;

    public bool IsBusy => State is ServiceState.Starting or ServiceState.Stopping;

    public bool IsRunning => State is ServiceState.Running;

    public int Pid
    {
        get
        {
            try
            {
                return _process?.Id ?? 0;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    public bool CorePresent => File.Exists(VpnUsPaths.CoreExe);

    public async Task StartAsync(string configPath, int clashApiPort, bool clashApiEnabled, CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsRunning || State == ServiceState.Starting)
            {
                return;
            }
        }

        if (!File.Exists(VpnUsPaths.CoreExe))
        {
            LastError = $"Ядро не найдено: {VpnUsPaths.CoreExe}. Нажмите «Обновить ядро».";
            _log.Error(LastError);
            SetState(ServiceState.Error);
            return;
        }

        if (!File.Exists(VpnUsPaths.WintunDll))
        {
            // Начиная с 1.10 sing-box несёт wintun.dll внутри себя (в zip её нет).
            _log.Debug("wintun.dll рядом с sing-box.exe нет — используется встроенная в ядро");
        }

        _configPath = configPath;
        _clashApiPort = clashApiPort;
        _clashApiEnabled = clashApiEnabled;
        _stopRequested = false;

        SetState(ServiceState.Starting);
        LastError = null;

        var psi = new ProcessStartInfo(VpnUsPaths.CoreExe)
        {
            WorkingDirectory = VpnUsPaths.DataDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("-D");
        psi.ArgumentList.Add(VpnUsPaths.DataDir);

        // sing-box 1.16+ требует явного разрешения на устаревший tun.stack.
        psi.Environment["ENABLE_DEPRECATED_TUN_STACK"] = "true";
        psi.Environment["SING_BOX_LOG_LEVEL"] = "warn";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _log.WriteCoreLine(e.Data, isErrorStream: false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _log.WriteCoreLine(e.Data, isErrorStream: true);
            }
        };
        process.Exited += OnProcessExited;

        lock (_gate)
        {
            _process = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить процесс sing-box");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = "Запуск sing-box не удался: " + ex.Message;
            _log.Error(LastError);
            SetState(ServiceState.Error);
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        StartedAt = DateTimeOffset.Now;

        _log.Info($"sing-box запущен (pid {process.Id}), конфиг {configPath}");

        var ready = await WaitReadyAsync(process, ct).ConfigureAwait(false);
        if (_stopRequested)
        {
            return;
        }

        if (process.HasExited)
        {
            var code = SafeExitCode(process);
            LastError = $"sing-box завершился сразу после запуска (код {code}). Проверьте логи: занят порт Clash API {_clashApiPort}, нет прав на TUN или ошибка конфига.";
            _log.Error(LastError);
            SetState(ServiceState.Error);
            return;
        }

        if (!ready)
        {
            _log.Warn("Clash API не ответил за 20 с, но процесс жив — считаем туннель поднятым");
        }

        ConnectedAt = DateTimeOffset.Now;
        SetState(ServiceState.Running);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null)
        {
            SetState(ServiceState.Stopped);
            return;
        }

        _stopRequested = true;
        SetState(ServiceState.Stopping);

        try
        {
            if (!process.HasExited)
            {
                _log.Info("Останавливаю sing-box...");
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log.Warn("sing-box не завершился за 8 с, продолжаю");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _log.Warn("Остановка sing-box: " + ex.Message);
        }
        finally
        {
            process.Dispose();
            lock (_gate)
            {
                _process = null;
            }

            ConnectedAt = null;
            SetState(ServiceState.Stopped);
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        var configPath = _configPath;
        var port = _clashApiPort;
        var clashEnabled = _clashApiEnabled;

        await StopAsync(ct).ConfigureAwait(false);
        if (configPath is not null)
        {
            await StartAsync(configPath, port, clashEnabled, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitReadyAsync(Process process, CancellationToken ct)
    {
        if (!_clashApiEnabled)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            return true;
        }

        using var client = new ClashApiClient(_clashApiPort);
        var deadline = DateTimeOffset.Now.AddSeconds(20);

        while (DateTimeOffset.Now < deadline)
        {
            if (process.HasExited)
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();

            var version = await client.GetVersionAsync(ct).ConfigureAwait(false);
            if (version is not null)
            {
                _log.Info($"sing-box {version} готов, Clash API 127.0.0.1:{_clashApiPort}");
                return true;
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        return false;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = sender as Process;
        var code = SafeExitCode(process);

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
        }

        lock (_gate)
        {
            _process = null;
        }

        ConnectedAt = null;

        if (_stopRequested)
        {
            _log.Info($"sing-box остановлен (код {code})");
            SetState(ServiceState.Stopped);
            return;
        }

        var uptime = StartedAt is null ? TimeSpan.Zero : DateTimeOffset.Now - StartedAt.Value;
        if (uptime > TimeSpan.FromSeconds(60))
        {
            _restartAttempts = 0;
        }

        _log.Warn($"sing-box неожиданно завершился (код {code}, uptime {uptime.TotalSeconds:0} с)");

        if (_restartAttempts >= 3)
        {
            LastError = $"sing-box падает повторно (код {code}). Автоперезапуск остановлен.";
            _log.Error(LastError);
            SetState(ServiceState.Error);
            return;
        }

        _restartAttempts++;
        var delay = TimeSpan.FromSeconds(3 * Math.Pow(2, _restartAttempts - 1));
        var configPath = _configPath;
        var port = _clashApiPort;
        var clashEnabled = _clashApiEnabled;

        if (configPath is null)
        {
            SetState(ServiceState.Error);
            return;
        }

        SetState(ServiceState.Starting);
        _log.Warn($"Автоперезапуск ядра через {delay.TotalSeconds:0} с (попытка {_restartAttempts}/3)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (_stopRequested)
                {
                    return;
                }

                await StartAsync(configPath, port, clashEnabled, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Автоперезапуск: " + ex.Message);
                SetState(ServiceState.Error);
            }
        });
    }

    private static int SafeExitCode(Process? process)
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void SetState(ServiceState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
