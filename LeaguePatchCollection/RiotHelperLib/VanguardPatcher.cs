using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LeaguePatchCollection.RiotHelperLib;

/// <summary>
/// 直接改写 Riot Client 的本地配置缓存（<c>ClientConfiguration.json</c>），
/// 复刻 Hextech 日志里的那句「[+] 正在处理Vanguard反作弊」。
///
/// 和旧的 <c>--client-config-url</c> 代理方式相比，这里不改变客户端的配置拉取地址，
/// 只在启动前把本地缓存里的 Vanguard 开关写成“关闭”，客户端启动后不会再弹
/// “Install Vanguard / Update”，左下角直接是 Play。
/// </summary>
public static class VanguardPatcher
{
    // 与 ConfigProxy.SetKey 使用的键保持一致（扁平 key，值是布尔）
    private static readonly (string Key, bool Value)[] BoolKeys =
    [
        ("anticheat.vanguard.backgroundInstall", false),
        ("anticheat.vanguard.enabled", false),
        ("keystone.client.feature_flags.restart_required.disabled", true),
        ("keystone.client.feature_flags.vanguardLaunch.disabled", true),
        ("keystone.client.feature_flags.vanguard_attestation.enabled", false),
        ("lol.client_settings.clash.vanguard_check_enabled", false),
        ("lol.client_settings.vanguard.enabled", false),
        ("lol.client_settings.vanguard.enabled_embedded", false),
        ("lion.vanguard.required", false),
        ("lion.vanguard.netrequired", false),
    ];

    private static readonly string[] DependencyPaths =
    [
        "keystone.products.lion.patchlines.live",
        "keystone.products.league_of_legends.patchlines.live",
        "keystone.products.league_of_legends.patchlines.pbe",
        "keystone.products.valorant.patchlines.live",
    ];

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Riot Games", "Riot Client", "Config", "ClientConfiguration.json");

    /// <summary>返回一句给日志/界面用的结果描述。</summary>
    public static string Apply()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
            return "ClientConfiguration.json 不存在（客户端还没初始化过）";

        try
        {
            var backup = path + ".lpcbak";
            if (!File.Exists(backup))
                File.Copy(path, backup, overwrite: false);

            var text = File.ReadAllText(path);
            int changed = 0;

            foreach (var (key, value) in BoolKeys)
            {
                var pattern = $"(\"{Regex.Escape(key)}\"\\s*:\\s*)(true|false)";
                var replaced = Regex.Replace(text, pattern, m =>
                {
                    var want = value ? "true" : "false";
                    if (m.Groups[2].Value == want)
                        return m.Value;
                    changed++;
                    return m.Groups[1].Value + want;
                });
                text = replaced;
            }

            // vanguard.url 清空
            var urlPattern = "(\"lol\\.client_settings\\.vanguard\\.url\"\\s*:\\s*)\"[^\"]*\"";
            var urlReplaced = Regex.Replace(text, urlPattern, m =>
            {
                if (m.Value.EndsWith("\"\"")) return m.Value;
                changed++;
                return m.Groups[1].Value + "\"\"";
            });
            text = urlReplaced;

            // 清空 patchline 的 dependencies 里的 vanguard 依赖
            foreach (var depPath in DependencyPaths)
            {
                text = RemoveVanguardDependency(text, depPath, ref changed);
            }

            if (changed > 0)
            {
                File.WriteAllText(path, text);
                Trace.WriteLine($"[INFO] Vanguard patch applied: {changed} change(s) -> {path}");
            }
            else
            {
                Trace.WriteLine("[INFO] Vanguard patch: already clean.");
            }

            // 关键：Riot Client 每次启动都会按 dice_roll 重新拉取配置并覆盖本文件
            // （实测启动一次后 anticheat.vanguard.enabled 又从 false 变回 true）。
            // 把文件设为只读，客户端的写入会失败并保留我们这份已打补丁的缓存，
            // 客户端随即使用内存中这份“已禁用 Vanguard”的配置，左下角才会是 Play。
            var lockMsg = LockFile(path);
            return changed > 0
                ? $"已处理 Vanguard 反作弊（{changed} 处修改，{lockMsg}）"
                : $"Vanguard 反作弊已处于禁用状态（{lockMsg}）";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [WARN] Vanguard patch failed: {ex.Message}");
            return "处理 Vanguard 失败：" + ex.Message;
        }
    }

    /// <summary>把配置文件设为只读，阻止客户端重新拉取时覆盖。</summary>
    private static string LockFile(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (!attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
            return "已锁定配置";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [WARN] lock failed: {ex.Message}");
            return "锁定配置失败";
        }
    }

    /// <summary>解除只读（需要在客户端里重新拉配置时用）。</summary>
    public static void Unlock()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
        }
    }

    /// <summary>把某个 patchline 的 dependencies 数组里的 vanguard 项删掉（纯文本方式，保留原格式）。</summary>
    private static string RemoveVanguardDependency(string text, string patchlineKey, ref int changed)
    {
        var keyIndex = text.IndexOf($"\"{patchlineKey}\"", StringComparison.Ordinal);
        if (keyIndex < 0)
            return text;

        // 只在该 patchline 对象范围内找 dependencies
        var windowEnd = Math.Min(text.Length, keyIndex + 40000);
        var window = text.Substring(keyIndex, windowEnd - keyIndex);
        var depMatch = Regex.Match(window, "\"dependencies\"\\s*:\\s*\\[");
        if (!depMatch.Success)
            return text;

        var arrStart = keyIndex + depMatch.Index + depMatch.Length;
        var depth = 1;
        var i = arrStart;
        while (i < text.Length && depth > 0)
        {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
            }
            i++;
        }
        var arrEnd = i; // 指向 ']' 之后
        var arrText = text.Substring(arrStart, arrEnd - arrStart - 1);

        if (!arrText.Contains("vanguard", StringComparison.OrdinalIgnoreCase))
            return text;

        var newText = text[..arrStart] + Environment.NewLine + "                " + text[(arrEnd - 1)..];
        changed++;
        return newText;
    }
}
