using System.Diagnostics;
using Microsoft.Win32;

namespace LeaguePatchCollection.RiotHelperLib;

/// <summary>
/// LCU（League Client Update）定位器。
///
/// 参考 Hextech 1.0.7 的做法：不写死安装路径，而是从“正在运行的客户端进程”反推安装目录，
/// 再读取 lockfile 拿到 <c>port:password</c>，从而连上 https://127.0.0.1:&lt;port&gt;。
///
/// 旧实现把路径写死为 <c>C:\Riot Games\League of Legends</c>，装在其它盘（例如
/// <c>D:\Riot Games\League of Legends</c>）或 WeGame 版时会直接读不到 lockfile，
/// 表现为“启动游戏后功能不生效 / 报错”。
/// </summary>
public static class LcuProbe
{
    public static int Port { get; private set; }
    public static string? Password { get; private set; }
    public static string? InstallDir { get; private set; }

    private static readonly string[] ClientProcessNames =
    [
        "LeagueClientUx", "LeagueClient", "LeagueClientUxRender"
    ];

    /// <summary>读取进程主模块路径；跨架构（32 位客户端 / 64 位宿主）访问 MainModule 可能阻塞，这里限制在 800ms 内。</summary>
    private static string? TryGetProcessPath(string processName, int timeoutMs = 800)
    {
        try
        {
            var task = Task.Run(() =>
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var f = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(f))
                            return f;
                    }
                    catch
                    {
                    }
                }
                return (string?)null;
            });

            if (task.Wait(timeoutMs))
                return task.Result;
        }
        catch
        {
        }
        return null;
    }

    /// <summary>从运行中的客户端进程反推安装目录。</summary>
    public static string? FindInstallDir()
    {
        foreach (var name in ClientProcessNames)
        {
            var file = TryGetProcessPath(name);
            if (string.IsNullOrEmpty(file))
                continue;

            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir))
                continue;

            if (File.Exists(Path.Combine(dir, "lockfile")))
                return dir;

            // LeagueClientUx 可能位于子目录，向上找两级
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "lockfile")))
                return parent;

            return dir;
        }

        // 注册表
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Riot Games\League of Legends");
                    if (key is null) continue;
                    foreach (var valueName in new[] { "InstallLocation", "Path", "InstallDir" })
                    {
                        var v = key.GetValue(valueName) as string;
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        var candidate = v.TrimEnd('\\');
                        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "lockfile")))
                            return candidate;
                        if (Directory.Exists(candidate))
                            return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        // 磁盘兜底
        var known = new[]
        {
            @"C:\Riot Games\League of Legends",
            @"D:\Riot Games\League of Legends",
            @"E:\Riot Games\League of Legends",
            @"C:\Program Files\Riot Games\League of Legends",
            @"D:\Program Files\Riot Games\League of Legends",
            @"C:\WeGameApps\英雄联盟",
            @"D:\WeGameApps\英雄联盟",
            @"D:\WeGameApps\League of Legends",
            @"E:\WeGameApps\英雄联盟",
        };
        foreach (var dir in known)
        {
            if (Directory.Exists(dir))
                return dir;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            foreach (var rel in new[] { @"Riot Games\League of Legends", @"WeGameApps\英雄联盟", @"Games\League of Legends" })
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, rel);
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>读取 lockfile：内容形如 <c>PID:port:password:protocol:timestamp</c></summary>
    public static bool TryReadLockfile(string installDir, out int port, out string password)
    {
        port = 0;
        password = string.Empty;
        var lockfile = Path.Combine(installDir, "lockfile");
        if (!File.Exists(lockfile))
            return false;

        try
        {
            // 客户端写入时可能短暂占用，重试几次
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var parts = File.ReadAllText(lockfile).Split(':');
                    if (parts.Length >= 4 && int.TryParse(parts[2], out port) && !string.IsNullOrEmpty(parts[3]))
                    {
                        password = parts[3];
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                Thread.Sleep(120);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [WARN] Failed to read lockfile: {ex.Message}");
        }

        return false;
    }

    /// <summary>立即解析一次当前的 LCU 凭据，失败返回 false。</summary>
    public static bool TryResolve()
    {
        // 先复用已缓存的目录（避免每次轮询都枚举进程/扫盘，旧实现的这条路径会卡住 UI 数十秒）
        if (!string.IsNullOrEmpty(InstallDir) && TryReadLockfile(InstallDir!, out var cachedPort, out var cachedPwd))
        {
            Port = cachedPort;
            Password = cachedPwd;
            return true;
        }

        var dir = InstallDir ?? FindInstallDir();
        if (dir is null)
            return false;

        InstallDir = dir;
        if (!TryReadLockfile(dir, out var port, out var pwd))
            return false;

        Port = port;
        Password = pwd;
        return true;
    }

    /// <summary>等待客户端就绪（启动游戏后调用），类似 Hextech 的“等待 LCU 可用”。</summary>
    public static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            // 解析放在线程池里执行：MainModule 枚举在跨架构（32 位客户端 / 64 位宿主）下可能阻塞数秒
            var resolveTask = Task.Run(() => TryResolve());
            var finished = await Task.WhenAny(resolveTask, Task.Delay(3000)).ConfigureAwait(true);
            bool ok = finished == resolveTask && resolveTask.Result;
            if (ok)
            {
                Trace.WriteLine($"[INFO] LCU ready: dir={InstallDir} port={Port}");
                return true;
            }

            attempt++;
            // 客户端进程都不在，说明用户还没登录/还没起来，继续等但放慢节奏
            if (!ProcessUtil.IsProcessRunning("LeagueClientUx") &&
                !ProcessUtil.IsProcessRunning("LeagueClient"))
            {
                await Task.Delay(attempt < 10 ? 250 : 800);
                continue;
            }

            await Task.Delay(400);
        }

        return false;
    }

    /// <summary>客户端是否处于“已经登录、可进入游戏”的状态（LCU 有 lockfile 即视为就绪）。</summary>
    public static bool IsClientReady()
    {
        return TryResolve();
    }
}
