using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace RtxHub
{
    /// <summary>Configuration and evidence only; this class never loads an NR runtime.</summary>
    public static class NeuralPresets
    {
        // Independently written adapter. Configuration verified against NR Nexus v0.2.2,
        // source commit 1e725d07a578157294b957b5adbe59bb0235f258 (MIT):
        // https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/blob/v0.2.2/dlssnr.hpp
        // kConfigSection/kConfigVersion; LoadConfiguration; LogNrEvaluationSuccess.
        // https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/blob/v0.2.2/profile_templates.hpp
        // Present=2; model B=2. Original project copyright: Carlos Lopez Jr.
        // ReShade AddonPath/KeyOverlay are upstream settings, not guessed keys:
        // https://github.com/crosire/reshade/blob/v6.8.0/source/addon_manager.cpp
        // https://github.com/crosire/reshade/blob/v6.8.0/source/runtime.cpp

        private static readonly string[] Scales = { "0.50", "0.67", "1.00" };
        private static readonly string[] Names = { "轻量", "均衡", "画质" };
        private const string MonitorNote = "请按 F11 查看 NR Nexus 实测监视器；未从日志推测帧率或 GPU 耗时。";

        /// <summary>Private-player ReShade.ini; the add-on embeds its shaders and needs no effect preset.</summary>
        public static string BuildIni(int presetIndex)
        {
            ValidatePreset(presetIndex);
            // Literal decimal strings remain valid under comma-decimal Windows cultures.
            return "[GENERAL]\r\n\r\n" +
                "[INPUT]\r\nKeyOverlay=36,0,0,0\r\n\r\n" +
                "[OVERLAY]\r\nLanguage=zh-CN\r\n\r\n" +
                "[ADDON]\r\nAddonPath=.\r\n\r\n" +
                "[RenoDX.DLSS5]\r\n" +
                "UiLanguage=zh-Hans\r\n" +
                "ConfigVersion=8\r\n" +
                "NeuralUplift=1\r\n" +
                "EnableHooks=2\r\n" +
                "DX11Source=native\r\n" +
                "NRHookPoint=2\r\n" +
                "NRPresentGuides=2\r\n" +
                "NRPresentEncoding=1\r\n" +
                "NRPresentFrames=0\r\n" +
                "NRPasses=1\r\n" +
                "NRPreset=2\r\n" +
                "NRFollowInputRes=0\r\n" +
                "NRResolutionScale=" + Scales[presetIndex] + "\r\n" +
                "NRIntensity=1\r\n";
        }

        /// <summary>Games retain their swapchain encoding and use matched engine guides when available.</summary>
        public static string BuildGameIni(int presetIndex)
        {
            // v0.2.2 dlssnr.hpp defaults, lines 1591-1597: Guides 0 = Optional
            // (matched game guides, otherwise neutral); Encoding 0 = Auto.
            // This does not change Windows HDR or force an SDR interpretation on an HDR game.
            return BuildIni(presetIndex)
                .Replace("NRPresentGuides=2\r\n", "NRPresentGuides=0\r\n")
                .Replace("NRPresentEncoding=1\r\n", "NRPresentEncoding=0\r\n");
        }

        public static string Describe(int presetIndex)
        {
            ValidatePreset(presetIndex);
            string percent = presetIndex == 0 ? "50%" : presetIndex == 1 ? "67%" : "100%";
            return "实验" + Names[presetIndex] + "：Present 阶段，模型 B，单次处理，工作宽高 " + percent +
                "。使用中性深度和零运动向量，面向 SDR 视频；画面可能改变或出现时序瑕疵，需对比验收。";
        }

        public static string AnalyzeLog(string logFile)
        {
            if (String.IsNullOrWhiteSpace(logFile) || !File.Exists(logFile))
                return "未找到本次 ReShade 日志；尚未确认插件安装或运行。" + MonitorNote;
            try
            {
                using (FileStream stream = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    return GetEvidenceSummary(reader.ReadToEnd());
            }
            catch (IOException) { return "日志暂时无法读取；未确认 NR 运行。" + MonitorNote; }
            catch (UnauthorizedAccessException) { return "无法读取本次日志；未确认 NR 运行。" + MonitorNote; }
            catch (SecurityException) { return "无法读取本次日志；未确认 NR 运行。" + MonitorNote; }
        }

        /// <summary>
        /// Read only explicit Nexus production markers, scoped to the newest recorded session.
        /// Evaluation success is evidence of a model call, not a guarantee of visual improvement.
        /// </summary>
        public static string GetEvidenceSummary(string content)
        {
            if (String.IsNullOrWhiteSpace(content)) return MissingEvidence();
            // 0 unknown, 1 addon/runtime only, 2 created, 3 evaluated, -1 failed, -2 off/unavailable.
            int state = 0;
            string problem = String.Empty;
            using (StringReader reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Official ReShade startup marker: dll_main.cpp. A new appended session
                    // invalidates every earlier success, even before its addon has registered.
                    if (line.IndexOf("Initializing crosire's ReShade version", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        state = 0;
                        problem = String.Empty;
                        continue;
                    }
                    // A normal "Unregistered add-on" at shutdown contains the same
                    // case-insensitive substring, but does not start a new addon session.
                    if (Regex.IsMatch(line, @"\bRegistered add-on ""NR Nexus""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        state = 1;
                        problem = String.Empty;
                        continue;
                    }
                    int prefix = line.IndexOf("NR Nexus: ", StringComparison.Ordinal);
                    if (prefix < 0)
                    {
                        // ReShade can reject the DLL before the addon's own logger starts.
                        if (line.IndexOf("Failed to load add-on", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            line.IndexOf("renodx-dlss5", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            state = -1;
                            problem = "ReShade 未能加载 NR 插件";
                        }
                        continue;
                    }
                    string message = line.Substring(prefix + "NR Nexus: ".Length);
                    if (state == 0) state = 1;
                    if (Regex.IsMatch(message, @"\bfeature 18 (?:create|evaluate) failed\b", RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(message, @"\b0xBAD[0-9A-F]+\b", RegexOptions.IgnoreCase) ||
                        (line.IndexOf("| ERROR |", StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (line.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        state = -1;
                        problem = ShortMessage(message);
                        continue;
                    }
                    // Exact successful production-call marker (dlssnr.hpp lines 9584-9594).
                    // A generic 'initialized', a created handle, or a different addon's text
                    // cannot satisfy this check. Count=0 is not a successful model call.
                    if (Regex.IsMatch(message,
                        @"^(?:present|inline|pre-SR) feature 18 evaluation succeeded \(count=[1-9][0-9]*,", RegexOptions.CultureInvariant))
                    {
                        state = 3;
                        problem = String.Empty;
                        continue;
                    }
                    if (message.StartsWith("NR-VERDICT v3 ", StringComparison.Ordinal))
                    {
                        string verdict = Token(message, "state");
                        if (verdict == "OFF" || verdict == "UNAVAILABLE" || verdict == "NOT_ENGAGED")
                        {
                            state = -2;
                            problem = verdict;
                        }
                        else if (verdict == "ENGAGED" && PositiveCounter(message, "evals"))
                        {
                            state = 3;
                            problem = String.Empty;
                        }
                        continue;
                    }
                    if (state >= 0 && message.StartsWith("feature 18 created via ", StringComparison.Ordinal))
                        state = Math.Max(state, 2);
                }
            }
            if (state == -1)
                return "NR 最近报告失败：" + problem + "。先前的成功记录不能确认当前仍在处理。" + MonitorNote;
            if (state == -2)
                return "NR 当前未在处理（日志状态 " + problem + "）；未沿用较早的成功记录。" + MonitorNote;
            if (state == 3)
                return "已记录 NR 计算成功：本次会话至少有一次明确的 feature 18 计算成功证据；这不证明每帧处理或画面改善。" + MonitorNote;
            if (state == 2)
                return "仅确认 NR 功能创建；尚无成功 NR 计算证据。" + MonitorNote;
            if (state == 1)
                return "仅确认插件加载；尚无成功 NR 计算证据。" + MonitorNote;
            return MissingEvidence();
        }

        private static string Token(string message, string name)
        {
            Match match = Regex.Match(message, @"(?:^|\s)" + Regex.Escape(name) + @"=([^\s]+)", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : String.Empty;
        }

        private static bool PositiveCounter(string message, string name)
        {
            ulong value;
            return UInt64.TryParse(Token(message, name), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static string ShortMessage(string value)
        {
            return value.Length <= 200 ? value : value.Substring(0, 200) + "…";
        }

        private static string MissingEvidence()
        {
            return "未发现本次会话的 NR Nexus 加载证据；不能确认安装或运行。" + MonitorNote;
        }

        private static void ValidatePreset(int presetIndex)
        {
            if (presetIndex < 0 || presetIndex >= Scales.Length)
                throw new ArgumentOutOfRangeException("presetIndex", "请选择轻量、均衡或画质档（0、1、2）。");
        }
    }
}
