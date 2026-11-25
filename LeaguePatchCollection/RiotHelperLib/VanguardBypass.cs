using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LeaguePatchCollection.RiotHelperLib
{
    /// <summary>
    /// Vanguard 绕过的“按需部署”部分。
    ///
    /// 只作用于本程序启动的那一次客户端会话：
    ///   Deploy()          —— 把内嵌的 vghook.dll 复制成 &lt;Riot Client&gt;\version.dll
    ///                        （RiotClientServices.exe 静态导入 VERSION.dll，会在主代码前加载它）
    ///   StartLcuPatcher() —— 后台线程等 LeagueClient.exe 出现，给它打两处 jns->jmp
    ///   StopLcuPatcher()  —— 结束线程
    ///   Undeploy()        —— 删掉 version.dll，恢复原始文件（如果有备份）
    ///
    /// 撤除之后，直接用桌面快捷方式启动客户端就不会带任何 hook，回到原生行为。
    /// </summary>
    public static class VanguardBypass
    {
        // version.dll 里 patch 的两处（VGC connect / VGC login 的 jns）
        private static readonly (uint Rva, byte Expect, byte New, string Why)[] LcuPatches =
        {
            (0x15f0905, 0x79, 0xEB, "VGC connect"),
            (0x15f0a62, 0x79, 0xEB, "VGC login"),
        };

        /// <summary>
        /// 多字节补丁：把“发送登录消息成功”那段日志调用替换成直接写会话状态。
        ///
        /// 依据：0x16043b0 是登录结果处理器，它在成功分支里执行
        ///   mov dword [rdi+0xF48], 1     ← 1 就是 CONNECTED
        /// 而这个处理器只在收到 VGC 的异步结果时才被调用；我们既然伪造了“发送成功”，
        /// 结果永远不会回来，所以状态停在 0(IN_PROGRESS)，前端就把 Play 置灰。
        ///
        /// 位置 0x15f0a92（原 19 字节，是一条 lea + mov edx,2 + xor ecx,ecx + call 日志）：
        ///   4c 8d 05 2f 24 2b 00   lea  r8,[日志文本]
        ///   ba 02 00 00 00         mov  edx,2
        ///   33 c9                  xor  ecx,ecx
        ///   e8 8b 01 af fe         call <log>
        /// 改成 10 字节的 mov dword [rsi+0xF48],1 + 9 个 nop，正好在 0x15f0aa5 接回原流程。
        /// </summary>
        private static readonly (uint Rva, byte[] Expect, byte[] New, string Why)[] BlobPatches =
        {
            (
                0x15f0a92,
                new byte[] { 0x4c, 0x8d, 0x05, 0x2f, 0x24, 0x2b, 0x00, 0xba, 0x02, 0x00, 0x00, 0x00, 0x33, 0xc9, 0xe8, 0x8b, 0x01, 0xaf, 0xfe },
                new byte[] { 0xc7, 0x86, 0x48, 0x0f, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 },
                "session state -> CONNECTED(1)"
            ),
        };

        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "lpc.log");
        private static CancellationTokenSource? _cts;
        private static Task? _worker;

        private static void Log(string s)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine);
            }
            catch { }
            Trace.WriteLine("[VG] " + s);
        }

        // ---------------- 路径解析 ----------------

        /// <summary>找到 Riot Client 可执行文件所在目录。</summary>
        public static string? FindRiotClientDir()
        {
            try
            {
                var json = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                        "Riot Games", "RiotClientInstalls.json");
                if (File.Exists(json))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    foreach (var key in new[] { "rc_live", "rc_default" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var el))
                        {
                            var exe = el.GetString();
                            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                                return Path.GetDirectoryName(exe);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("FindRiotClientDir(json) failed: " + ex.Message);
            }

            foreach (var probe in new[]
                     {
                         @"D:\Riot Games\Riot Client",
                         @"C:\Riot Games\Riot Client",
                     })
            {
                if (Directory.Exists(probe) && File.Exists(Path.Combine(probe, "RiotClientServices.exe")))
                    return probe;
            }
            return null;
        }

        // ---------------- 部署 / 撤除 ----------------

        /// <summary>把内嵌的 vghook.dll 部署成 Riot Client 目录下的 version.dll。</summary>
        public static string Deploy()
        {
            var dir = FindRiotClientDir();
            if (dir == null)
                return "[error] Riot Client 目录未找到";

            var target = Path.Combine(dir, "version.dll");
            var backup = target + ".lpcbak";

            try
            {
                // 目标已存在且是我们的 hook（大小一致）就不重复部署
                var resName = "LeaguePatchCollection.Resources.vghook.dll";
                byte[] payload;
                using (var s = typeof(VanguardBypass).Assembly.GetManifestResourceStream(resName))
                {
                    if (s == null)
                        return "[error] 内嵌资源缺失: " + resName;
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    payload = ms.ToArray();
                }

                if (File.Exists(target))
                {
                    // 不能用大小判断（不同优化的 hook 可能大小相同），要比内容
                    bool same = false;
                    try
                    {
                        var existing = File.ReadAllBytes(target);
                        if (existing.Length == payload.Length)
                        {
                            same = true;
                            for (int i = 0; i < payload.Length; i++)
                            {
                                if (existing[i] != payload[i]) { same = false; break; }
                            }
                        }
                    }
                    catch { same = false; }

                    if (same)
                        return "[skip] 已是当前 hook 版本: " + target;

                    // 只有在没有备份时才把原文件挪开，避免覆盖掉真正的系统原件
                    if (!File.Exists(backup))
                        File.Move(target, backup);
                    else
                        File.Delete(target);
                }

                File.WriteAllBytes(target, payload);
                Log($"deployed hook: {target} ({payload.Length} bytes)");
                return "[ok] 已部署 " + target;
            }
            catch (Exception ex)
            {
                Log("Deploy failed: " + ex.Message);
                return "[error] 部署失败: " + ex.Message;
            }
        }

        /// <summary>撤除 hook，恢复原始 version.dll。</summary>
        public static string Undeploy()
        {
            var dir = FindRiotClientDir();
            if (dir == null)
                return "[error] Riot Client 目录未找到";

            var target = Path.Combine(dir, "version.dll");
            var backup = target + ".lpcbak";

            try
            {
                if (!File.Exists(target))
                    return "[skip] 未部署";

                var len = new FileInfo(target).Length;
                if (len != 179712 && !File.Exists(backup))
                    return "[skip] 不是本程序的 hook（大小 " + len + "），保持原样";

                File.Delete(target);
                if (File.Exists(backup))
                    File.Move(backup, target);

                Log("hook removed: " + target);
                return "[ok] 已撤除 hook";
            }
            catch (Exception ex)
            {
                Log("Undeploy failed: " + ex.Message);
                return "[error] 撤除失败: " + ex.Message;
            }
        }

        // ---------------- LeagueClient 内存补丁 ----------------

        /// <summary>
        /// 把 Riot Client 主窗口找出来并置前。
        /// 带 --launch-product 启动时客户端会先躲在后台/托盘，用户会以为“没启动起来”。
        /// </summary>
        public static bool ShowClientWindow(int timeoutMs = 60000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                foreach (var name in new[] { "Riot Client", "RiotClientUx", "LeagueClientUx" })
                {
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(name); } catch { continue; }
                    foreach (var p in procs)
                    {
                        try
                        {
                            var h = p.MainWindowHandle;
                            if (h == IntPtr.Zero) continue;
                            Native.ShowWindow(h, Native.SW_RESTORE);
                            Native.SetForegroundWindow(h);
                            Log($"brought window to front: {name} pid={p.Id}");
                            return true;
                        }
                        catch { }
                    }
                }
                Thread.Sleep(300);
            }
            Log("ShowClientWindow: no window within timeout");
            return false;
        }

        public static string StartLcuPatcher()
        {
            if (_worker is { IsCompleted: false })
                return "[skip] 监视线程已在运行";

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => PatchLoop(token), token);
            Log("LCU patcher started");
            return "[ok] 已启动 LeagueClient 补丁线程";
        }

        public static void StopLcuPatcher()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _worker = null;
            Log("LCU patcher stopped");
        }

        private static async Task PatchLoop(CancellationToken token)
        {
            var done = new System.Collections.Generic.HashSet<string>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var procs = Process.GetProcessesByName("LeagueClient");
                    if (procs.Length == 0)
                    {
                        done.Clear();
                    }
                    else
                    {
                        var pid = procs[0].Id;
                        done.RemoveWhere(k => !k.StartsWith(pid + ":", StringComparison.Ordinal));

                        await Task.Delay(150, token).ConfigureAwait(false);

                        foreach (var p in LcuPatches)
                        {
                            var key = pid + ":" + p.Rva.ToString("X");
                            if (done.Contains(key)) continue;

                            if (TryPatch(pid, p.Rva, p.Expect, p.New, p.Why))
                                done.Add(key);
                        }

                        foreach (var b in BlobPatches)
                        {
                            var key = pid + ":blob:" + b.Rva.ToString("X");
                            if (done.Contains(key)) continue;

                            if (TryPatchBlob(pid, b.Rva, b.Expect, b.New, b.Why))
                                done.Add(key);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log("PatchLoop error: " + ex.Message);
                }

                try { await Task.Delay(300, token).ConfigureAwait(false); } catch { break; }
            }
        }

        /// <summary>
        /// 把 .rdata 里的 "IN_PROGRESS" 字面量改写为 "CONNECTED"。
        ///
        /// 实测：给 VGC connect / login 打完 jns 短路后，/lol-vanguard/v1/session 仍然返回
        /// {"state":"IN_PROGRESS"}，而 UI 就是按这个字段把 Play 置灰的
        /// （availability 接口本身返回 isAvailable=true）。
        /// 该字面量在二进制里只出现一次（信誉机制去重），改它就是直接换掉序列化输出。
        /// "IN_PROGRESS" 占 12 字节（含结尾 NUL），写 "CONNECTED\0" 共 10 字节，不会波及相邻字符串。
        /// </summary>
        private static readonly uint InProgressLiteralRva = 0x171eab0;

        private static bool TryPatchLiteral(int pid)
        {
            const string OldText = "IN_PROGRESS";
            const string NewText = "CONNECTED";

            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_ALL, false, pid);
                if (h == IntPtr.Zero) return false;

                var mods = new IntPtr[1024];
                if (!Native.EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out var needed, Native.LIST_MODULES_ALL))
                    return false;

                int count = (int)(needed / (uint)IntPtr.Size);
                IntPtr baseAddr = IntPtr.Zero;
                var sb = new StringBuilder(1024);
                for (int i = 0; i < count && i < mods.Length; i++)
                {
                    sb.Clear();
                    if (Native.GetModuleFileNameExW(h, mods[i], sb, (uint)sb.Capacity) == 0) continue;
                    if (!string.Equals(Path.GetFileName(sb.ToString()), "LeagueClient.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    baseAddr = mods[i];
                    break;
                }
                if (baseAddr == IntPtr.Zero) return false;

                var addr = IntPtr.Add(baseAddr, (int)InProgressLiteralRva);

                // 读原值核对
                var cur = new byte[32];
                if (!Native.ReadProcessMemory(h, addr, cur, (IntPtr)cur.Length, out var got) || got.ToInt64() < 12)
                    return false;

                var oldBytes = System.Text.Encoding.ASCII.GetBytes(OldText + "\0");
                for (int i = 0; i < oldBytes.Length; i++)
                {
                    if (cur[i] != oldBytes[i])
                    {
                        Log($"[literal] 字节不匹配，跳过（got {System.Text.Encoding.ASCII.GetString(cur, 0, 12).TrimEnd('\0')}）");
                        return false;
                    }
                }

                if (!Native.VirtualProtectEx(h, addr, (IntPtr)16, Native.PAGE_EXECUTE_READWRITE, out var oldProt))
                    return false;

                var newBytes = new byte[12];
                var nb = System.Text.Encoding.ASCII.GetBytes(NewText);
                Array.Copy(nb, newBytes, nb.Length);

                bool ok = Native.WriteProcessMemory(h, addr, newBytes, (IntPtr)newBytes.Length, out var wrote)
                          && wrote.ToInt64() == newBytes.Length;
                Native.VirtualProtectEx(h, addr, (IntPtr)16, oldProt, out _);

                if (ok) Log($"[+] [literal] {OldText} -> {NewText} @0x{InProgressLiteralRva:X} (pid={pid})");
                return ok;
            }
            catch (Exception ex)
            {
                Log("TryPatchLiteral error: " + ex.Message);
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }

        private static bool TryPatchBlob(int pid, uint rva, byte[] expect, byte[] replacement, string why)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_ALL, false, pid);
                if (h == IntPtr.Zero)
                {
                    Log($"OpenProcess({pid}) failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                var mods = new IntPtr[1024];
                if (!Native.EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out var needed, Native.LIST_MODULES_ALL))
                    return false;

                int count = (int)(needed / (uint)IntPtr.Size);
                IntPtr baseAddr = IntPtr.Zero;
                uint imgSize = 0;
                var sb = new StringBuilder(1024);
                for (int i = 0; i < count && i < mods.Length; i++)
                {
                    sb.Clear();
                    if (Native.GetModuleFileNameExW(h, mods[i], sb, (uint)sb.Capacity) == 0) continue;
                    if (!string.Equals(Path.GetFileName(sb.ToString()), "LeagueClient.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    baseAddr = mods[i];
                    if (Native.GetModuleInformation(h, mods[i], out var mi, (uint)Marshal.SizeOf(typeof(Native.MODULEINFO))))
                        imgSize = mi.SizeOfImage;
                    break;
                }
                if (baseAddr == IntPtr.Zero) return false;
                if (imgSize > 0 && rva >= imgSize) return false;

                var addr = IntPtr.Add(baseAddr, (int)rva);
                var cur = new byte[expect.Length];
                if (!Native.ReadProcessMemory(h, addr, cur, (IntPtr)cur.Length, out var got) || got.ToInt64() < expect.Length)
                    return false;

                bool alreadyNew = true;
                for (int i = 0; i < replacement.Length; i++)
                {
                    if (cur[i] != replacement[i]) { alreadyNew = false; break; }
                }
                if (alreadyNew)
                {
                    Log($"[{why}] 已是目标字节，无需处理");
                    return true;
                }

                for (int i = 0; i < expect.Length; i++)
                {
                    if (cur[i] != expect[i])
                    {
                        Log($"[{why}] 字节不匹配（第 {i} 字节 got {cur[i]:X2}, expect {expect[i]:X2}），跳过");
                        return false;
                    }
                }

                if (!Native.VirtualProtectEx(h, addr, (IntPtr)replacement.Length, Native.PAGE_EXECUTE_READWRITE, out var oldProt))
                {
                    Log("VirtualProtectEx failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                bool ok = Native.WriteProcessMemory(h, addr, replacement, (IntPtr)replacement.Length, out var wrote)
                          && wrote.ToInt64() == replacement.Length;
                Native.VirtualProtectEx(h, addr, (IntPtr)replacement.Length, oldProt, out _);

                if (!ok)
                {
                    Log("WriteProcessMemory failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                var after = new byte[replacement.Length];
                Native.ReadProcessMemory(h, addr, after, (IntPtr)after.Length, out got);
                Log($"[+] [{why}] 0x{rva:X} 写入 {replacement.Length} 字节 (pid={pid})");
                return true;
            }
            catch (Exception ex)
            {
                Log("TryPatchBlob error: " + ex.Message);
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }

        private static bool TryPatch(int pid, uint rva, byte expect, byte newByte, string why)
        {            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_ALL, false, pid);
                if (h == IntPtr.Zero)
                {
                    Log($"OpenProcess({pid}) failed: {Marshal.GetLastWin32Error()}（需要管理员权限）");
                    return false;
                }

                var mods = new IntPtr[1024];
                if (!Native.EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out var needed, Native.LIST_MODULES_ALL))
                {
                    Log("EnumProcessModulesEx failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                int count = (int)(needed / (uint)IntPtr.Size);
                IntPtr baseAddr = IntPtr.Zero;
                uint imgSize = 0;
                var sb = new StringBuilder(1024);

                for (int i = 0; i < count && i < mods.Length; i++)
                {
                    sb.Clear();
                    if (Native.GetModuleFileNameExW(h, mods[i], sb, (uint)sb.Capacity) == 0) continue;
                    if (!string.Equals(Path.GetFileName(sb.ToString()), "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    baseAddr = mods[i];
                    if (Native.GetModuleInformation(h, mods[i], out var mi, (uint)Marshal.SizeOf(typeof(Native.MODULEINFO))))
                        imgSize = mi.SizeOfImage;
                    break;
                }

                if (baseAddr == IntPtr.Zero)
                {
                    Log("LeagueClient.exe 模块未找到");
                    return false;
                }
                if (imgSize > 0 && rva >= imgSize)
                {
                    Log($"rva 0x{rva:X} 超出映像范围 0x{imgSize:X}");
                    return false;
                }

                var addr = IntPtr.Add(baseAddr, (int)rva);
                var cur = new byte[1];
                if (!Native.ReadProcessMemory(h, addr, cur, (IntPtr)1, out var got) || got != (IntPtr)1)
                {
                    Log("ReadProcessMemory failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (cur[0] == newByte)
                {
                    Log($"[{why}] 已是 EB，无需处理");
                    return true;
                }
                if (cur[0] != expect)
                {
                    Log($"[{why}] 字节不匹配 (got {cur[0]:X2}, expect {expect:X2})，跳过");
                    return false;
                }

                if (!Native.VirtualProtectEx(h, addr, (IntPtr)1, Native.PAGE_EXECUTE_READWRITE, out var oldProt))
                {
                    Log("VirtualProtectEx failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                var nb = new[] { newByte };
                bool ok = Native.WriteProcessMemory(h, addr, nb, (IntPtr)1, out var wrote) && wrote == (IntPtr)1;
                Native.VirtualProtectEx(h, addr, (IntPtr)1, oldProt, out _);

                if (!ok)
                {
                    Log("WriteProcessMemory failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                Log($"[+] [{why}] 0x{rva:X}: {expect:X2} -> {newByte:X2} (pid={pid})");
                return true;
            }
            catch (Exception ex)
            {
                Log("TryPatch error: " + ex.Message);
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }

        private static class Native
        {
            public const uint PROCESS_ALL = 0x1F0FFF;
            public const uint LIST_MODULES_ALL = 0x03;
            public const uint PAGE_EXECUTE_READWRITE = 0x40;

            [StructLayout(LayoutKind.Sequential)]
            public struct MODULEINFO
            {
                public IntPtr lpBaseOfDll;
                public uint SizeOfImage;
                public IntPtr EntryPoint;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);
            [DllImport("kernel32.dll")]
            public static extern bool CloseHandle(IntPtr h);
            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint filter);
            [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, [Out] StringBuilder name, uint size);
            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO info, uint cb);

            public const int SW_RESTORE = 9;
            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }
}
