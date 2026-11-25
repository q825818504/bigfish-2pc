using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using LeaguePatchCollection.RiotHelperLib;

namespace LeaguePatchCollection
{
    public partial class LeaguePatchCollectionUX : Form
    {
        private static readonly char[] separator = [' '];
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public static string[] LatestBloatKeys { get; private set; } = [];

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReleaseCapture();

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        private static partial int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public LeaguePatchCollectionUX()
        {
            InitializeComponent();

            MainHeaderBackdrop.MouseDown += MainHeaderBackdrop_MouseDown!;
            WindowTitle.MouseDown += WindowTitle_MouseDown!;
            TopWindowIcon.MouseDown += TopWindowIcon_MouseDown!;

            SettingsManager.LoadSettings();

            DisableVanguard.Checked = SettingsManager.ConfigSettings.Novgk;
            LegacyHonor.Checked = SettingsManager.ConfigSettings.Legacyhonor;
            SupressBehavior.Checked = SettingsManager.ConfigSettings.Nobehavior;
            NameChangeBypass.Checked = SettingsManager.ConfigSettings.Namebypass;
            NoBloatware.Checked = SettingsManager.ConfigSettings.Nobloatware;
            AutoAccept.Checked = SettingsManager.ConfigSettings.AutoAccept;
            ShowOfflineButton.Checked = SettingsManager.ChatSettings.EnableOffline;
            ShowMobileButton.Checked = SettingsManager.ChatSettings.EnableMobile;
            ShowAwayButton.Checked = SettingsManager.ChatSettings.EnableAway;
            ShowOnlineButton.Checked = SettingsManager.ChatSettings.EnableOnline;
            ArgsBox.Text = SettingsManager.ConfigSettings.Args;
            Shown += LeaguePatchCollectionUX_Shown;
        }

        private async void LeaguePatchCollectionUX_Shown(object? sender, EventArgs e)
        {
            await FetchLatestBloatKeyAsync();

            await LeagueProxy.Start();

            await Task.Delay(1000);

            StartButton.Enabled = true;
            StartButton.Text = "LAUNCH CLIENT";
        }

        private void MainHeaderBackdrop_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _ = ReleaseCapture();
                _ = SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        private void TopWindowIcon_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _ = ReleaseCapture();
                _ = SendMessage(Handle, 0xA1, 0x2, 0);
            }

        }

        private void WindowTitle_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _ = ReleaseCapture();
                _ = SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        private void BanReasonButton_Click(object sender, EventArgs e)
        {
            string banReason = HttpProxy.banReason;

            string message = $"\n{HttpProxy.banReason}\n\nDo you want to copy this to clipboard?";

            DialogResult result = MessageBox.Show(message, "League Patch Collection", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                Clipboard.SetText(banReason);
            }
        }

        private void CleanLogsButton_Click(object sender, EventArgs e)
        {
            LogCleaner.ClearLogs();
        }

        private async void StartButton_Click(object sender, EventArgs e)
        {
            string args = SettingsManager.ConfigSettings.Args ?? string.Empty;
            string[] rawArgs = args.Split(separator, StringSplitOptions.RemoveEmptyEntries);

            // --config-proxy：显式启用“本地配置注入”。
            // 只有需要用到 禁用Vanguard / 旧版荣誉 / 改名绕过 / 去臃肿 等配置补丁时才加这个开关；
            // 默认不加，直接把 --client-config-url 交给客户端会在新版 Riot Client 上
            // 因为 keystone 配置结构变化而启动报错（这就是旧版本“启动游戏失败”的根因）。
            bool useConfigProxy = rawArgs.Any(a => a.Equals("--config-proxy", StringComparison.OrdinalIgnoreCase));
            string[] argsArray = rawArgs
                .Where(a => !a.Equals("--config-proxy", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            bool alreadyRunning = RiotClient.IsRunning();
            bool allowMultiple = argsArray.Contains("--allow-multiple-clients");

            // 落盘日志：方便排查“点了 LAUNCH 到底走的是复用还是重启”
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "lpc.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") +
                    " click: alreadyRunning=" + alreadyRunning +
                    " allowMultiple=" + allowMultiple +
                    " useConfigProxy=" + useConfigProxy +
                    " args=[" + string.Join(" ", argsArray) + "]" + Environment.NewLine);
            }
            catch { }

            // 按你的要求：完全复刻海克斯「启动游戏」的行为 —— 不管客户端是否在跑，
            // 都先关掉再重新拉起（海克斯的流程就是 kill -> 重启 -> 处理配置 -> 注入）。
            // 这里复刻 kill -> 重启 这一段，保持和海克斯一致的启动语义。
            if (!allowMultiple)
            {
                ProcessUtil.TerminateRiotServices();
            }

            // 关掉产品元数据里的 vanguard 依赖并锁只读。
            // 这是让“VG 已卸载”的机器还能保持 Play 的关键：依赖一旦是 false，
            // 客户端就不会去碰 vgk/vgc/vgrl 那条检查链，也就不会弹 Install Vanguard。
            // 失败不阻塞启动（比如没管理员权限时 ProgramData 写不进去）。
            try
            {
                string depPatch = VanguardDependencyPatcher.Apply();
                Trace.WriteLine("[INFO] vanguard dependency patch:\r\n" + depPatch);
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "lpc.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " vanguard-dependency-patch:" + Environment.NewLine +
                        depPatch + Environment.NewLine);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[WARN] vanguard dependency patch failed: " + ex.Message);
            }

            StartButton.Enabled = false;
            string originalText = StartButton.Text;
            StartButton.Text = "LAUNCHING...";

            try
            {
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "lpc.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " path=LAUNCH (restart)" + Environment.NewLine); } catch { }
                var proc = LeagueProxy.LaunchRCS(argsArray, useConfigProxy);
                if (proc == null)
                {
                    MessageBox.Show(
                        "无法启动 Riot Client。\r\n\r\n请确认已安装 Riot Client，或尝试以管理员身份运行本程序。",
                        "League Patch Collection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // 按 Hextech 的方式：启动后等待 LCU 就绪（读取 lockfile 拿 port/password），
                // 确认客户端真的起来，而不是“点一下没反应”。
                bool clientReady = await LcuProbe.WaitUntilReadyAsync(TimeSpan.FromSeconds(90));
                Trace.WriteLine(clientReady
                    ? $"[INFO] League Client ready. dir={LcuProbe.InstallDir} port={LcuProbe.Port}"
                    : "[WARN] League Client did not report a lockfile within 90s (still logging in?).");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($" [ERROR] StartButton_Click failed: {ex}");
                MessageBox.Show("启动游戏时出错：\r\n" + ex.Message, "League Patch Collection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                StartButton.Text = originalText;
                StartButton.Enabled = true;
            }
        }

        private void CloseClientsButton_Click(object sender, EventArgs e)
        {
            ProcessUtil.TerminateRiotServices();
        }

        private async void RestartUXbutton_Click(object sender, EventArgs e)
        {
            await RiotLocalApi.SendRequest(ApiTarget.Lcu, "/riotclient/kill-and-restart-ux", HttpMethod.Post);
        }

        private async void DodgeButton_Click(object sender, EventArgs e)
        {
            await RiotLocalApi.SendRequest(ApiTarget.Lcu, "/lol-login/v1/session/invoke?destination=lcdsServiceProxy&method=call&args=[\"\",\"teambuilder-draft\",\"quitV2\",\"\"]", HttpMethod.Post);
        }

        private async void DisconnectChatButton_Click(object sender, EventArgs e)
        {
            var response = await RiotLocalApi.SendRequest(ApiTarget.Rcs, "/chat/v1/session", HttpMethod.Get);
            if (response == null || !response.IsSuccessStatusCode)
            {
                Trace.WriteLine(" [ERROR] Failed to fetch chat session state.");
                return;
            }

            string content = await response.Content.ReadAsStringAsync();

            var sessionData = JsonConvert.DeserializeObject<JObject>(content);

            if (sessionData?["state"]?.ToString() == "connected")
            {
                var suspendContent = new StringContent("{\"config\":\"disable\"}", Encoding.UTF8, "application/json");
                await RiotLocalApi.SendRequest(ApiTarget.Rcs, "/chat/v1/suspend", HttpMethod.Post, suspendContent);
            }
            else
            {
                await RiotLocalApi.SendRequest(ApiTarget.Rcs, "/chat/v1/resume", HttpMethod.Post);
            }
        }

        private async void RemoveVgk_Click(object sender, EventArgs e)
        {
            // 按钮保留，卸载动作先停用（后续会重新安排它的行为）。
            //
            // 原实现调用 ProcessUtil.RemoveVanguard()，会删除
            // C:\Program Files\Riot Vanguard 整个目录。一旦 vgrl.dll 没了，
            // Riot Client 的 vanguard 依赖检查就会失败（dependency status=2），
            // 左下角立刻从 Play 变成 Update 并弹 "Install Vanguard"。
            // 所以这里只留占位，不执行任何文件删除。
            await Task.CompletedTask;
            Trace.WriteLine(" [INFO] RemoveVgk clicked: uninstall action disabled (placeholder button).");
        }

        private void DisableVanguard_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Novgk = DisableVanguard.Checked;
            SettingsManager.SaveSettings();
        }

        private void LegacyHonor_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Legacyhonor = LegacyHonor.Checked;
            SettingsManager.SaveSettings();
        }

        private void SupressBehavior_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Nobehavior = SupressBehavior.Checked;
            SettingsManager.SaveSettings();
        }

        private void NameChangeBypass_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Namebypass = NameChangeBypass.Checked;
            SettingsManager.SaveSettings();
        }

        private void NoBloatware_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Nobloatware = NoBloatware.Checked;
            SettingsManager.SaveSettings();
        }

        private void AutoAccept_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.AutoAccept = AutoAccept.Checked;
            SettingsManager.SaveSettings();
        }

        private void ShowOfflineButton_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ChatSettings.EnableOffline = ShowOfflineButton.Checked;
            SettingsManager.SaveSettings();
        }

        private void ShowMobileButton_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ChatSettings.EnableMobile = ShowMobileButton.Checked;
            SettingsManager.SaveSettings();
        }

        private void ShowAwayButton_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ChatSettings.EnableAway = ShowAwayButton.Checked;
            SettingsManager.SaveSettings();
        }

        private void ShowOnlineButton_CheckedChanged(object sender, EventArgs e)
        {
            SettingsManager.ChatSettings.EnableOnline = ShowOnlineButton.Checked;
            SettingsManager.SaveSettings();
        }
        private void ArgsBox_TextChanged(object sender, EventArgs e)
        {
            SettingsManager.ConfigSettings.Args = ArgsBox.Text;
            SettingsManager.SaveSettings();
        }

        public static class SettingsManager
        {
            private static readonly string settingsFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeaguePatchCollection");
            private static readonly string settingsFilePath = Path.Combine(settingsFolderPath, "settings.json");

            public static ChatSettings ChatSettings { get; set; } = new ChatSettings();
            public static ConfigSettings ConfigSettings { get; set; } = new ConfigSettings();

            public static void LoadSettings()
            {
                if (!Directory.Exists(settingsFolderPath))
                {
                    Directory.CreateDirectory(settingsFolderPath);
                }

                try
                {
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        dynamic? settings = JsonConvert.DeserializeObject(json);

                        if (settings?.ChatSettings == null ||
                            settings?.ConfigSettings == null ||
                            settings?.ChatSettings.EnableOffline == null ||
                            settings?.ChatSettings.EnableMobile == null ||
                            settings?.ChatSettings.EnableAway == null ||
                            settings?.ChatSettings.EnableOnline == null ||
                            settings?.ConfigSettings.Novgk == null ||
                            settings?.ConfigSettings.Legacyhonor == null ||
                            settings?.ConfigSettings.Namebypass == null ||
                            settings?.ConfigSettings.AutoAccept == null ||
                            settings?.ConfigSettings.Nobloatware == null ||
                            settings?.ConfigSettings.Nobehavior == null ||
                            settings?.ConfigSettings.Args == null)
                        {
                            Trace.WriteLine("[WARN] Settings file is invalid or corrupted. Resetting to default values.");
                            SaveSettings();
                        }
                        else
                        {
                            ChatSettings.EnableOffline = settings?.ChatSettings.EnableOffline;
                            ChatSettings.EnableMobile = settings?.ChatSettings.EnableMobile;
                            ChatSettings.EnableAway = settings?.ChatSettings.EnableAway;
                            ChatSettings.EnableOnline = settings?.ChatSettings.EnableOnline;

                            ConfigSettings.Novgk = settings?.ConfigSettings.Novgk;
                            ConfigSettings.Legacyhonor = settings?.ConfigSettings.Legacyhonor;
                            ConfigSettings.Namebypass = settings?.ConfigSettings.Namebypass;
                            ConfigSettings.AutoAccept = settings?.ConfigSettings.AutoAccept;
                            ConfigSettings.Nobloatware = settings?.ConfigSettings.Nobloatware;
                            ConfigSettings.Nobehavior = settings?.ConfigSettings.Nobehavior;
                            ConfigSettings.Args = settings?.ConfigSettings.Args ?? string.Empty;
                        }
                    }
                    else
                    {
                        SaveSettings();
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($" [ERROR] An error occurred while loading settings, resetting to defaults: {ex.Message}");
                    SaveSettings();
                }
            }
            public static void SaveSettings()
            {
                var settings = new
                {
                    ChatSettings = new
                    {
                        ChatSettings.EnableOffline,
                        ChatSettings.EnableMobile,
                        ChatSettings.EnableAway,
                        ChatSettings.EnableOnline
                    },
                    ConfigSettings = new
                    {
                        ConfigSettings.Novgk,
                        ConfigSettings.Legacyhonor,
                        ConfigSettings.Namebypass,
                        ConfigSettings.AutoAccept,
                        ConfigSettings.Nobloatware,
                        ConfigSettings.Nobehavior,
                        ConfigSettings.MinimalConfig,
                        ConfigSettings.Args
                    }
                };

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, json);
            }
        }
        public class ChatSettings
        {
            public bool EnableOffline { get; set; } = false;
            public bool EnableMobile { get; set; } = false;
            public bool EnableAway { get; set; } = false;
            public bool EnableOnline { get; set; } = true;
        }
        public class ConfigSettings
        {
            public bool Novgk { get; set; } = true;
            public bool Legacyhonor { get; set; } = false;
            public bool Namebypass { get; set; } = false;
            public bool AutoAccept { get; set; } = true;
            public bool Nobloatware { get; set; } = true;
            public bool Nobehavior { get; set; } = false;
            /// <summary>最小配置模式：只改 Vanguard 键，不把 mailbox/rms/chat 服务地址改到本地代理
            /// （旧行为会让 Riot Client 的 UI 进程起不来）。</summary>
            public bool MinimalConfig { get; set; } = true;
            public string Args { get; set; } = "--launch-product=league_of_legends --launch-patchline=live";

        }

        public class TimestampedTextWriterTraceListener(string fileName) : TextWriterTraceListener(fileName)
        {
            public override void WriteLine(string? message)
            {
                base.WriteLine($"[{DateTime.Now:MM-dd-yyyy hh:mm:ss tt}] {message}");
            }

            public override void Write(string? message)
            {
                base.Write($"[{DateTime.Now:MM-dd-yyyy hh:mm:ss tt}] {message}");
            }
        }

        private static async Task FetchLatestBloatKeyAsync()
        {
            try
            {
                var githubUrl = "https://raw.githubusercontent.com/Cat1Bot/league-patch-collection/refs/heads/main/latestbloatkey";

                using var client = new HttpClient(new HttpClientHandler
                {
                    UseCookies = false,
                    UseProxy = false,
                    Proxy = null,
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true,
                    CheckCertificateRevocationList = false
                });

                client.Timeout = TimeSpan.FromSeconds(5);

                using var stream = await client.GetStreamAsync(githubUrl);
                using var sr = new StreamReader(stream);
                var bloatKeysList = new List<string>();

                string? line;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        bloatKeysList.Add(trimmed);
                    }
                }

                LatestBloatKeys = [.. bloatKeysList];
            }
            catch (TaskCanceledException)
            {
                Trace.WriteLine(" [ERROR] Request timed out while fetching the bloat keys.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($" [ERROR] Failed to fetch latest bloat key: {ex.Message}");
            }
        }

        internal static class Program
        {
            [STAThread]
            static void Main(string[] args)
            {
                string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeaguePatchCollection");
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
                string logFilePath = Path.Combine(logDirectory, "debug.log");
                Trace.Listeners.Add(new TimestampedTextWriterTraceListener(logFilePath));
                Trace.AutoFlush = true;

                ApplicationConfiguration.Initialize();
                Application.Run(new LeaguePatchCollectionUX());
                LeagueProxy.Stop();
            }
        }
    }
}
