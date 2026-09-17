using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using VpnUs.Core.Config;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;
using VpnUs.Core.Subscription;
using Xunit;
using Xunit.Abstractions;

namespace VpnUs.Core.Tests;

/// <summary>
/// Рантайм-проверка: sing-box реально стартует с нашим конфигом (TUN заменяется на mixed-in,
/// чтобы можно было запускать без прав администратора). Ловит ошибки уровня "start service",
/// которые `sing-box check` не видит (например, detour к пустому outbound).
/// </summary>
public class SingBoxRuntimeTests
{
    private readonly ITestOutputHelper _output;

    public SingBoxRuntimeTests(ITestOutputHelper output) => _output = output;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void RuntimeStart_WithMixedInbound_Works()
    {
        var core = VpnUsPaths.CoreExe;
        if (!File.Exists(core))
        {
            return;
        }

        var nodes = File.Exists(VpnUsPaths.NodesFile)
            ? JsonStore.Load(VpnUsPaths.NodesFile, () => new List<ServerNode>())
            : ShareLinkParser.ParseSubscription(ShareLinkParserTests.SamplePlainBody()).Nodes;

        if (nodes.Count == 0)
        {
            return;
        }

        var settings = JsonStore.Load(VpnUsPaths.SettingsFile, () => new AppSettings());
        settings.Mode = RoutingMode.PerApp;
        settings.AppSplit = AppSplitMode.OnlySelected;
        settings.BypassGames = true;
        settings.BlockAds = true;
        settings.SelectedApps = [new SelectedApp(@"C:\Windows\System32\notepad.exe", "Notepad")];

        var clashPort = FreePort();
        var mixedPort = FreePort();
        var workDir = Path.Combine(Path.GetTempPath(), "vpnus-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        Process? process = null;
        try
        {
            var config = SingBoxConfigBuilder.Build(settings, nodes, new SingBoxBuildOptions
            {
                CachePath = Path.Combine(workDir, "cache.db"),
                ClashApiPort = clashPort,
            });

            // TUN требует прав администратора: в тесте подменяем его на mixed-in.
            config["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "tun-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = mixedPort,
                },
            };

            File.WriteAllText(configPath, config.ToJsonString());

            var psi = new ProcessStartInfo(core)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };

            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-D");
            psi.ArgumentList.Add(workDir);
            psi.Environment["ENABLE_DEPRECATED_TUN_STACK"] = "true";

            process = Process.Start(psi)!;
            var output = new StringWriter();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.WriteLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.WriteLine(e.Data); } } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var ready = false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.Now.AddSeconds(15);

            while (DateTimeOffset.Now < deadline)
            {
                if (process.HasExited)
                {
                    break;
                }

                try
                {
                    var response = http.GetAsync($"http://127.0.0.1:{clashPort}/version").GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        ready = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                }

                Thread.Sleep(300);
            }

            string text;
            lock (output)
            {
                text = output.ToString();
            }

            _output.WriteLine(text);
            Assert.True(ready, "sing-box не стартовал с нашим конфигом:\n" + text);
            Assert.DoesNotContain("FATAL", text);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            process?.Dispose();

            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
