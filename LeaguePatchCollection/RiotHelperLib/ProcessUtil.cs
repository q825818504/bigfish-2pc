using System.Diagnostics;

namespace LeaguePatchCollection.RiotHelperLib;

public static class ProcessUtil
{
    public static bool IsProcessRunning(string process)
    {
        try
        {
            if (Process.GetProcessesByName(process).Length != 0)
            {
                return true;
            }
        }
        catch
        {
            Trace.WriteLine(" [WARN] Could not check process status, user should try running app as administrator.");
        }
        return false;
    }

    public static bool TerminateRiotServices()
    {
        // 必须杀全：
        //   RiotClientServices —— 启动器
        //   Riot Client        —— CEF 主进程（单实例锁持有者，漏了它新实例会立刻退出）
        //   LeagueClient*      —— LCU 主体与渲染进程
        //
        // 性能要求：整个终止阶段硬上限 3 秒。原实现是串行 Kill + WaitForExit(5000)，
        // 9 个进程名最坏能拖十几秒，直接把“5 秒内启动客户端”的目标吃掉。
        string[] riotProcesses =
        [
            "RiotClientServices",
            "Riot Client",
            "RiotClientUx",
            "RiotClientCrashHandler",
            "LeagueClient",
            "LeagueClientUx",
            "LeagueClientUxRender",
            "LeagueCrashHandler64",
            "Lion-Win64-Shipping",
        ];

        var victims = new List<Process>();
        foreach (var processName in riotProcesses)
        {
            try { victims.AddRange(Process.GetProcessesByName(processName)); }
            catch { }
        }

        if (victims.Count == 0)
        {
            Trace.WriteLine("[INFO] no Riot processes to terminate");
            return true;
        }

        // 一次性全部发 kill，不逐个等待
        foreach (var p in victims)
        {
            try { p.Kill(); }
            catch (Exception ex) { Trace.WriteLine($" [WARN] kill {p.ProcessName}#{p.Id} failed: {ex.Message}"); }
        }

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            bool allGone = true;
            foreach (var p in victims)
            {
                try { if (!p.HasExited) { allGone = false; break; } }
                catch { }
            }
            if (allGone)
            {
                Trace.WriteLine($"[INFO] terminated {victims.Count} Riot processes");
                return true;
            }
            Thread.Sleep(80);
        }

        Trace.WriteLine(" [WARN] terminate timed out after 3s, launching anyway");
        return false;
    }

    public static async Task RemoveVanguard()
    {
        string vgkpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Vanguard", "installer.exe");

        if (!File.Exists(vgkpath))
        {
            return;
        }

        try
        {
            using var process = Process.Start(vgkpath, "--quiet");
            if (process != null)
            {
                Trace.WriteLine(" [INFO] Attempting to uninstall Vanguard...");

                await process.WaitForExitAsync();
                Thread.Sleep(5000);

                for (int i = 0; i < 30; i++)
                {
                    if (!File.Exists(vgkpath))
                    {
                        MessageBox.Show("Vanguard uninstalled successfully!", "League Patch Collection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Thread.Sleep(1000);
                }

                MessageBox.Show("Vanguard uninstallation failed, try running this app as administrator", "League Patch Collection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Trace.WriteLine(" [WARN] Vanguard uninstallation failed, user should try running app as administrator.");
            }
            else
            {
                MessageBox.Show("Could not start Vanguard uninstaller, try running this app as administrator", "League Patch Collection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Trace.WriteLine(" [WARN] Could not start Vanguard uninstaller, user should try running app as administrator.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [ERROR] Vanguard uninstallation aborted: {ex}");
        }
    }
}