using System.ServiceProcess;

namespace VpnUs.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "service";

        switch (mode)
        {
            case "install":
                return ServiceInstaller.Install();

            case "uninstall":
                return ServiceInstaller.Uninstall();

            case "start":
                return ServiceInstaller.Start();

            case "stop":
                return ServiceInstaller.Stop();

            case "console":
            case "debug":
                return await RunConsoleAsync();

            default:
                ServiceBase.Run(new VpnUsWindowsService());
                return 0;
        }
    }

    private static async Task<int> RunConsoleAsync()
    {
        var host = new ServiceHost();
        host.Start();
        Console.WriteLine($"VpnUs Service: консольный режим, ProgramData = {Core.Storage.VpnUsPaths.ProgramDataRoot}");
        Console.WriteLine("Ctrl+C — выход");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await host.StopAsync();
        return 0;
    }
}

public sealed class VpnUsWindowsService : ServiceBase
{
    private ServiceHost? _host;

    public VpnUsWindowsService()
    {
        ServiceName = ServiceInstaller.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _host = new ServiceHost();
        _host.Start();
    }

    protected override void OnStop()
    {
        try
        {
            _host?.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _host = null;
        }
    }

    protected override void OnShutdown() => OnStop();
}
