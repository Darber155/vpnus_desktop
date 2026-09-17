using System.Diagnostics;
using VpnUs.Core.Config;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace VpnUs.Core.Tests;

public class RealDataSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealDataSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void UserSubscription_ProducesConfigAcceptedBySingBox()
    {
        var nodesFile = VpnUsPaths.NodesFile;
        var settingsFile = VpnUsPaths.SettingsFile;
        var core = VpnUsPaths.CoreExe;

        if (!File.Exists(nodesFile) || !File.Exists(core))
        {
            return;
        }

        var nodes = JsonStore.Load(nodesFile, () => new List<ServerNode>());
        var settings = JsonStore.Load(settingsFile, () => new AppSettings());
        Assert.NotEmpty(nodes);

        var workDir = Path.Combine(Path.GetTempPath(), "vpnus-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (var mode in Enum.GetValues<RoutingMode>())
            {
                settings.Mode = mode;
                var json = SingBoxConfigBuilder.BuildJson(settings, nodes, new SingBoxBuildOptions
                {
                    CachePath = Path.Combine(workDir, "cache.db"),
                    ClashApiPort = 19091,
                });

                var configPath = Path.Combine(workDir, $"config-{mode}.json");
                File.WriteAllText(configPath, json);

                var psi = new ProcessStartInfo(core)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workDir,
                };

                psi.ArgumentList.Add("check");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(configPath);
                psi.ArgumentList.Add("-D");
                psi.ArgumentList.Add(workDir);
                psi.Environment["ENABLE_DEPRECATED_TUN_STACK"] = "true";

                using var process = Process.Start(psi)!;
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(60000);

                _output.WriteLine($"{mode}: exit={process.ExitCode} {stdout}{stderr}".Trim());
                Assert.True(process.ExitCode == 0, $"{mode}: {stdout}{stderr}");
            }
        }
        finally
        {
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
