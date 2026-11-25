using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeaguePatchCollection;

public sealed class RiotClient
{
    /// <summary>
    /// 启动 Riot Client / 英雄联盟客户端。
    ///
    /// 关键修复（对照 Hextech 1.0.7 的“启动游戏”实现）：
    /// 1. 默认 <paramref name="useConfigProxy"/> = false，不再把 --client-config-url 指向本地
    ///    配置代理。旧版本默认注入该参数，一旦本地 ConfigProxy 返回的 JSON 与新版 Riot Client
    ///    的 keystone 配置结构不匹配，客户端就会在启动阶段报错（表现为“启动游戏失败/客户端报错”）。
    /// 2. 自动补齐官方标准启动参数 --launch-product / --launch-patchline，保证即使调用方传入
    ///    空参数也能真正把游戏拉起来（旧版本只带 config-url 时会停在登录器层面）。
    /// 3. 启动失败时自动回退为“纯净启动”（剔除 --client-config-url 后重试）。
    /// </summary>
    public static Process? Launch(IEnumerable<string>? args = null, bool useConfigProxy = false)
    {
        var path = GetPath();
        if (path is null)
        {
            Trace.WriteLine(" [ERROR] Unable to find Riot Client installation path.");
            return null;
        }

        var argv = new List<string>();
        if (useConfigProxy)
        {
            argv.Add($"--client-config-url=http://127.0.0.1:{LeagueProxy.ConfigPort}");
        }
        if (args != null)
        {
            argv.AddRange(args);
        }

        bool hasProduct = argv.Any(a => a.StartsWith("--launch-product=", StringComparison.OrdinalIgnoreCase));
        bool hasPatchline = argv.Any(a => a.StartsWith("--launch-patchline=", StringComparison.OrdinalIgnoreCase));

        if (!hasProduct)
        {
            argv.Add("--launch-product=league_of_legends");
            hasProduct = true;
        }
        if (!hasPatchline)
        {
            argv.Add("--launch-patchline=live");
        }

        Trace.WriteLine($"[INFO] Launching Riot Client: \"{path}\" {string.Join(' ', argv)}");

        try
        {
            return Process.Start(path, argv);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [ERROR] Riot Client launch failed: {ex.Message}");

            // 回退：剔除本地配置代理，用纯净参数再启动一次
            if (useConfigProxy)
            {
                var fallback = argv
                    .Where(a => !a.StartsWith("--client-config-url=", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                try
                {
                    Trace.WriteLine($"[INFO] Retrying without config proxy: {string.Join(' ', fallback)}");
                    return Process.Start(path, fallback);
                }
                catch (Exception ex2)
                {
                    Trace.WriteLine($" [ERROR] Fallback launch failed: {ex2.Message}");
                }
            }
            return null;
        }
    }

    /// <summary>Riot Client 是否已在运行（单实例模式下重复启动只会把参数转发给已运行实例）。</summary>
    public static bool IsRunning()
    {
        return RiotHelperLib.ProcessUtil.IsProcessRunning("RiotClientServices")
            || RiotHelperLib.ProcessUtil.IsProcessRunning("LeagueClient")
            || RiotHelperLib.ProcessUtil.IsProcessRunning("LeagueClientUx");
    }

    private static string? GetPath()
    {
        string installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games/RiotClientInstalls.json");

        if (File.Exists(installPath))
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(installPath));
                var rcPaths = new List<string?>
                {
                    data?["rc_default"]?.ToString(),
                    data?["rc_live"]?.ToString(),
                    data?["rc_beta"]?.ToString()
                };

                var validPath = rcPaths.FirstOrDefault(File.Exists);
                if (validPath != null)
                    return validPath;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($" [WARN] An error occurred while processing the install path, using fallback path: {ex.Message}");
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var potentialPath = Path.Combine(drive.RootDirectory.FullName, "Riot Games", "Riot Client", "RiotClientServices.exe");
            if (File.Exists(potentialPath))
            {
                Trace.WriteLine($"[INFO] Found RiotClient fallback path found at {potentialPath}");
                return potentialPath;
            }
        }

        Trace.WriteLine(" [ERROR] Failed to locate Riot Client installation path from both fallback method and RiotClientInstalls.");
        return null;
    }
}
