using System.Diagnostics;
using VpnUs.Core.Config;
using VpnUs.Core.Models;
using VpnUs.Core.Storage;
using VpnUs.Core.Subscription;
using Xunit;

namespace VpnUs.Core.Tests;

/// <summary>
/// Интеграционная проверка: сгенерированный config.json должен приниматься реальным sing-box
/// (`sing-box check`). Выполняется только если ядро установлено (ProgramData\VpnUs\core или PATH),
/// иначе тихо пропускается — это защита от расхождения формата с новой версией sing-box.
/// </summary>
public class SingBoxCheckIntegrationTests
{
    private static string? FindCore()
    {
        var local = VpnUsPaths.CoreExe;
        if (File.Exists(local))
        {
            return local;
        }

        var env = Environment.GetEnvironmentVariable("VPNUS_SINGBOX");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        return null;
    }

    [Fact]
    public void GeneratedConfigs_AreAcceptedByRealSingBox()
    {
        var core = FindCore();
        if (core is null)
        {
            return;
        }

        var nodes = ShareLinkParser.ParseSubscription(ShareLinkParserTests.SamplePlainBody()).Nodes;
        Assert.NotEmpty(nodes);

        var workDir = Path.Combine(Path.GetTempPath(), "vpnus-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var failures = new List<string>();

        try
        {
            foreach (var mode in Enum.GetValues<RoutingMode>())
            {
                foreach (var split in Enum.GetValues<AppSplitMode>())
                {
                    var settings = new AppSettings
                    {
                        Mode = mode,
                        AppSplit = split,
                        BypassGames = true,
                        BlockAds = true,
                        SelectiveRuleSets = ["youtube", "telegram", "category-games"],
                        SelectedApps =
                        [
                            new SelectedApp(@"C:\Games\MyGame\game.exe", "Game"),
                            new SelectedApp(@"D:\Apps\Editor\editor.exe", "Editor"),
                        ],
                    };

                    var json = SingBoxConfigBuilder.BuildJson(settings, nodes, new SingBoxBuildOptions
                    {
                        CachePath = Path.Combine(workDir, "cache.db"),
                        ClashApiPort = 19090,
                    });

                    var configPath = Path.Combine(workDir, $"config-{mode}-{split}.json");
                    File.WriteAllText(configPath, json);

                    var (exitCode, output) = RunCheck(core, configPath, workDir);
                    if (exitCode != 0)
                    {
                        failures.Add($"{mode}/{split}: {output}");
                    }
                }
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

        Assert.True(failures.Count == 0, "sing-box check не принял конфиг:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void GeneratedConfig_WithRealLocalRuleSets_IsAcceptedByRealSingBox()
    {
        var core = FindCore();
        if (core is null || !Directory.Exists(VpnUsPaths.RuleSetsDir))
        {
            return;
        }

        var files = Directory.GetFiles(VpnUsPaths.RuleSetsDir, "*.srs");
        if (files.Length == 0)
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
        settings.BypassGames = true;
        settings.BlockAds = true;

        var workDir = Path.Combine(Path.GetTempPath(), "vpnus-check-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        try
        {
            var json = SingBoxConfigBuilder.BuildJson(settings, nodes, new SingBoxBuildOptions
            {
                CachePath = Path.Combine(workDir, "cache.db"),
                ClashApiPort = 19093,
                // Реальный кэш службы: проверяем, что sing-box принимает local-rule-set c format=binary.
                RuleSetDirectory = VpnUsPaths.RuleSetsDir,
            });

            File.WriteAllText(configPath, json);
            var (exitCode, output) = RunCheck(core, configPath, workDir);

            Assert.True(exitCode == 0, "sing-box check: " + output);
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

    private static (int ExitCode, string Output) RunCheck(string core, string configPath, string workDir)
    {
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

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "не удалось запустить sing-box check");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);

        return (process.ExitCode, (stdout + stderr).Trim());
    }
}
