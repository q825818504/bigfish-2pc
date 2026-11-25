using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LeaguePatchCollection.RiotHelperLib
{
    /// <summary>
    /// 把产品元数据里的 vanguard 依赖关掉并锁成只读。
    ///
    /// 背景（实测）：Riot Client 在启动时会读
    ///   C:\ProgramData\Riot Games\Metadata\&lt;product&gt;\&lt;product&gt;.product_settings.yaml
    /// 里的 dependencies.vanguard。只要这一项是 true，客户端就会去走
    ///   vgk 服务注册表 -> 加载 vgrl.dll -> 重置 vgk boot status -> 回退启动 vgc 服务
    /// 这条链；而 VG 已卸载的机器上这三步全失败，最终 dependency status=2，
    /// 左下角从 Play 变成 Update 并弹 "Install Vanguard"。
    ///
    /// 把 vanguard 改成 false 之后整条检查链根本不会触发（日志里一行 vanguard 都没有）。
    /// 但客户端下次生成这份文件时会从服务器 manifest 重新写回 true，所以必须同时
    /// 设成只读锁住。只读对这份文件是安全的：客户端只读不写它，不会像
    /// ClientConfiguration.json 那样因为写失败而崩溃。
    /// </summary>
    public static class VanguardDependencyPatcher
    {
        private static readonly string[] ProductSettingsPaths =
        {
            @"C:\ProgramData\Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml",
            @"C:\ProgramData\Riot Games\Metadata\teamfighttactics.live\teamfighttactics.live.product_settings.yaml",
        };

        public static string Apply()
        {
            var sb = new StringBuilder();
            foreach (string path in ProductSettingsPaths)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        sb.AppendLine("[skip] not found: " + path);
                        continue;
                    }

                    var fi = new FileInfo(path);
                    if (fi.IsReadOnly)
                        fi.IsReadOnly = false;

                    string text = File.ReadAllText(path, Encoding.UTF8);
                    bool changed = false;
                    if (Regex.IsMatch(text, @"(\r?\n\s*vanguard:\s*)true"))
                    {
                        text = Regex.Replace(text, @"(\r?\n\s*vanguard:\s*)true", "${1}false");
                        changed = true;
                    }

                    // 只在确实需要时重写，避免无谓改动 mtime
                    if (changed)
                    {
                        File.WriteAllText(path, text, new UTF8Encoding(false));
                        sb.AppendLine("[patched] vanguard -> false: " + path);
                    }

                    fi.IsReadOnly = true;
                    sb.AppendLine("[locked] readonly: " + path);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("[error] " + path + " -> " + ex.Message);
                }
            }
            return sb.ToString();
        }

        /// <summary>把两份文件的只读解除（排障用）。</summary>
        public static string Unlock()
        {
            var sb = new StringBuilder();
            foreach (string path in ProductSettingsPaths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var fi = new FileInfo(path);
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    sb.AppendLine("[unlocked] " + path);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("[error] " + path + " -> " + ex.Message);
                }
            }
            return sb.ToString();
        }
    }
}
