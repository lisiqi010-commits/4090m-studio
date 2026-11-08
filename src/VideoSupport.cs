using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RtxHub
{
    /// <summary>Read-only discovery, explicit setup links and isolated mpv playback.</summary>
    public static class VideoSupport
    {
        public const string NvidiaSupportUrl = "https://nvidia.custhelp.com/app/answers/detail/a_id/5448";
        public static string LastPlaybackLogPath { get; private set; }
        public static string LastPlaybackStatus { get; private set; }

        public static string FindPotPlayer()
        {
            return FirstExisting(
                AppPath("PotPlayerMini64.exe"), AppPath("PotPlayerMini.exe"),
                Under(Environment.SpecialFolder.ProgramFiles, @"DAUM\PotPlayer\PotPlayerMini64.exe"),
                Under(Environment.SpecialFolder.ProgramFilesX86, @"DAUM\PotPlayer\PotPlayerMini.exe"));
        }

        public static string FindNvidiaApp()
        {
            return FirstExisting(AppPath("NVIDIA App.exe"),
                Under(Environment.SpecialFolder.ProgramFiles, @"NVIDIA Corporation\NVIDIA app\CEF\NVIDIA App.exe"));
        }

        public static string FindBrowser()
        {
            return FirstExisting(AppPath("msedge.exe"), AppPath("chrome.exe"), AppPath("firefox.exe"),
                Under(Environment.SpecialFolder.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                Under(Environment.SpecialFolder.ProgramFiles, @"Microsoft\Edge\Application\msedge.exe"),
                Under(Environment.SpecialFolder.ProgramFiles, @"Google\Chrome\Application\chrome.exe"),
                Under(Environment.SpecialFolder.LocalApplicationData, @"Google\Chrome\Application\chrome.exe"),
                Under(Environment.SpecialFolder.ProgramFiles, @"Mozilla Firefox\firefox.exe"));
        }

        public static string FindMpv()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            return FirstExisting(Path.Combine(root, @"tools\mpv\mpv.exe"),
                Path.Combine(root, @"tools\mpv\bin\mpv.exe"), Path.Combine(root, "mpv.exe"), AppPath("mpv.exe"));
        }

        /// <summary>Accept only known entry-point aliases, exact allowlisted URIs, or discovered executables.</summary>
        public static void OpenTarget(string target)
        {
            if (String.IsNullOrWhiteSpace(target)) throw new ArgumentException("请选择要打开的入口。", "target");
            string key = target.Trim();
            string destination = null;
            bool shell = false;
            switch (key.ToLowerInvariant())
            {
                case "potplayer": destination = FindPotPlayer(); break;
                case "nvidia": case "nvidiaapp": destination = FindNvidiaApp(); break;
                case "browser": destination = FindBrowser(); break;
                case "mpv": destination = FindMpv(); break;
                case "graphics": case "ms-settings:display-advancedgraphics":
                    destination = "ms-settings:display-advancedgraphics"; shell = true; break;
                case "display": case "ms-settings:display":
                    destination = "ms-settings:display"; shell = true; break;
                case "hdr": case "ms-settings:display-hdr":
                    destination = "ms-settings:display-hdr"; shell = true; break;
                case "support": destination = NvidiaSupportUrl; shell = true; break;
                default:
                    if (String.Equals(key, NvidiaSupportUrl, StringComparison.OrdinalIgnoreCase))
                    { destination = NvidiaSupportUrl; shell = true; }
                    else
                    {
                        string[] known = { FindPotPlayer(), FindNvidiaApp(), FindBrowser(), FindMpv() };
                        foreach (string item in known)
                            if (item.Length > 0 && String.Equals(key, item, StringComparison.OrdinalIgnoreCase))
                            { destination = item; break; }
                    }
                    if (destination == null) throw new ArgumentException("不支持的入口；只能打开已识别的程序或固定设置页面。", "target");
                    break;
            }
            if (String.IsNullOrEmpty(destination))
                throw new FileNotFoundException("没有找到该程序。请先安装，或使用随附的 mpv 播放本地视频。");
            ProcessStartInfo info = new ProcessStartInfo(destination);
            info.UseShellExecute = shell;
            if (!shell) info.WorkingDirectory = Path.GetDirectoryName(destination);
            Process.Start(info);
        }

        public static string GetGuide(int presetIndex)
        {
            ValidatePreset(presetIndex);
            string specific;
            if (presetIndex == 0)
                specific = "【RTX 超分】\r\n随附 mpv：请求 NVIDIA RTX VSR，按宽高各 2 倍处理；优先尝试 720p/1080p 视频。大分辨率源片会明显增加 GPU 负载，原生 4K 建议改用均衡档。\r\n浏览器 / PotPlayer：NVIDIA App → 系统 → 视频 → 超分辨率，选手动高质量（有 1–4 档时选 4）。画面卡顿时降低质量。\r\n";
            else if (presetIndex == 1)
                specific = "【精细缩放】\r\n随附 mpv：使用 libplacebo 的 gpu-next 渲染器和 high-quality 配置，改善缩放与抗振铃；此档不请求 RTX VSR。\r\n浏览器 / PotPlayer：NVIDIA App → 系统 → 视频 → 超分辨率，选择自动；GPU 使用量先选正常 / 中等（名称随版本不同）。\r\n";
            else
                specific = "【轻量流畅】\r\n随附 mpv：D3D11VA 硬件解码 + fast 渲染，减少缩放负载，保持源视频帧率。未增加运动插帧。\r\n浏览器 / PotPlayer：超分辨率选自动低 GPU 使用量，或手动关闭后对比；优先硬件解码。若系统支持，可把显示器刷新率设为片源帧率的整数倍。\r\n";
            return specific +
                "\r\n【PotPlayer 手动设置】\r\nF5 → 视频 → 视频渲染器，选“内置 Direct3D 11 视频渲染器”；清晰 / 均衡档可勾选“D3D11 GPU 超分辨率”（若版本提供）。解码器使用 D3D11 / DXVA 硬件加速。选项缺失时请使用随附 mpv。此工具没有改写 PotPlayer 配置。\r\n" +
                "\r\n【浏览器手动设置】\r\n开启浏览器硬件加速并重启；Windows 设置 → 系统 → 显示 → 图形，把视频浏览器设为“高性能 NVIDIA GPU”。笔记本接通电源；Edge 若有自身“增强视频”功能，先关闭以使用 NVIDIA RTX Video。\r\n" +
                "\r\n【确认效果】\r\n在 NVIDIA App 中打开 RTX Video 状态指示，播放视频时确认是否处于活动状态。不同站点、DRM 内容、源分辨率和驱动可能影响激活；打开程序不等于增强已经生效。mpv 会保存实际播放日志；VSR 接口请求成功也不等于每个片源都经过模型增强。\r\n" +
                "\r\n【HDR 可选】\r\n仅在 HDR10 显示器、Windows HDR 已开启且本人希望改变 SDR 呈现时，手动启用 RTX Video HDR。以上 mpv 档位均不请求 SDR→HDR。\r\n\r\n这里只给出浏览器 / PotPlayer 的操作步骤；在这些程序里仍需手动应用。随附 mpv 的播放按钮会实际传入所选处理参数。";
        }

        /// <summary>Build arguments for one existing local media file; no shell and no user configuration.</summary>
        public static string BuildMpvArguments(string file, int presetIndex)
        {
            ValidatePreset(presetIndex);
            string media = LocalFile(file, false);
            string common = "--no-config --load-scripts=no --input-terminal=no --terminal=no " +
                "--force-window=yes --keep-open=yes --gpu-api=d3d11 --gpu-context=d3d11 " +
                "--d3d11-adapter=NVIDIA --hwdec=d3d11va --interpolation=no ";
            string mode;
            if (presetIndex == 0)
                mode = "--vo=gpu --vf=d3d11vpp=scale=2:scaling-mode=nvidia ";
            else if (presetIndex == 1)
                mode = "--vo=gpu-next --profile=high-quality ";
            else
                mode = "--vo=gpu --profile=fast ";
            // -- ends option parsing, even for unusual media names. Windows quoting is applied to one argument.
            return common + mode + "-- " + QuoteWindowsArgument(media);
        }

        /// <summary>Probe compiled features first. Never substitute a different processing mode after failure.</summary>
        public static void PlayEnhanced(string mpv, string file, int presetIndex)
        {
            string player = LocalFile(mpv, true);
            string arguments = BuildMpvArguments(file, presetIndex);
            ProbeMpv(player, presetIndex);
            string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Rtx4090MHub\logs");
            Directory.CreateDirectory(logDirectory);
            LastPlaybackLogPath = Path.Combine(logDirectory, "mpv-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".log");
            LastPlaybackStatus = "播放器正在启动；尚未确认处理效果。";
            ProcessStartInfo info = new ProcessStartInfo(player);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = Path.GetDirectoryName(player);
            // Put the log option before --, which is included in arguments.
            info.Arguments = "--log-file=" + QuoteWindowsArgument(LastPlaybackLogPath) + " " + arguments;
            using (Process process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("无法启动 mpv。");
                Stopwatch timer = Stopwatch.StartNew();
                int timeout = presetIndex == 0 ? 5000 : 1200;
                while (timer.ElapsedMilliseconds < timeout)
                {
                    string log = ReadLog(LastPlaybackLogPath);
                    if (presetIndex == 0 && log.IndexOf("Failed to enable NVIDIA RTX Super Resolution", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        StopOwnedPlayer(process);
                        LastPlaybackStatus = "驱动拒绝 RTX VSR；已停止此次播放。";
                        throw new InvalidOperationException(LastPlaybackStatus + " 请查看日志：" + LastPlaybackLogPath);
                    }
                    if (presetIndex == 0 && log.IndexOf("NVIDIA RTX Super Resolution enabled.", StringComparison.Ordinal) >= 0)
                    {
                        LastPlaybackStatus = "已启动 2× 处理，驱动接受 RTX VSR 接口请求；片源是否实际增强请结合 NVIDIA 状态指示确认。";
                        return;
                    }
                    if (process.HasExited)
                    {
                        if (process.ExitCode != 0)
                        {
                            LastPlaybackStatus = "mpv 启动或解码失败，退出码 " + process.ExitCode + "。";
                            throw new InvalidOperationException(LastPlaybackStatus + " 日志：" + LastPlaybackLogPath);
                        }
                        LastPlaybackStatus = "播放器已退出，未确认画面处理。日志：" + LastPlaybackLogPath;
                        return;
                    }
                    Thread.Sleep(100);
                }
                if (presetIndex == 0)
                {
                    StopOwnedPlayer(process);
                    LastPlaybackStatus = "未在启动阶段确认 RTX VSR 接口启用；已停止此次播放，请查看日志或改用均衡档。";
                    throw new InvalidOperationException(LastPlaybackStatus + " 日志：" + LastPlaybackLogPath);
                }
                LastPlaybackStatus = presetIndex == 1
                    ? "mpv 已启动 libplacebo 高画质档；请在播放器按 i 检查统计信息。"
                    : "mpv 已启动轻量硬解档；保持源帧率，请在播放器按 i 检查丢帧。";
            }
        }

        public static void ProbeMpv(string mpv, int presetIndex)
        {
            ValidatePreset(presetIndex);
            string player = LocalFile(mpv, true);
            RequireText(RunProbe(player, "--no-config --load-scripts=no --gpu-context=help"), "d3d11", "此 mpv 没有 D3D11 图形上下文。");
            if (presetIndex == 0)
            {
                string output = RunProbe(player, "--no-config --load-scripts=no --vf=d3d11vpp=help");
                RequireText(output, "scaling-mode", "此 mpv 没有可配置的 D3D11 视频处理滤镜。");
                RequireText(output, "nvidia", "此 mpv 未提供 NVIDIA RTX VSR 模式。");
            }
            else
            {
                string profile = presetIndex == 1 ? "high-quality" : "fast";
                string output = RunProbe(player, "--no-config --load-scripts=no --show-profile=" + profile);
                RequireText(output, "scale", "此 mpv 没有需要的 " + profile + " 配置。");
                if (presetIndex == 1)
                    RequireText(RunProbe(player, "--no-config --load-scripts=no --vo=help"), "gpu-next", "此 mpv 没有 libplacebo gpu-next 渲染器。");
            }
        }

        /// <summary>Standard Windows argv quoting, including trailing backslashes and embedded quotes.</summary>
        public static string QuoteWindowsArgument(string argument)
        {
            if (argument == null) throw new ArgumentNullException("argument");
            if (argument.IndexOf('\0') >= 0) throw new ArgumentException("参数含有空字符。", "argument");
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in argument)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(ch);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static void ValidatePreset(int value)
        {
            if (value < 0 || value > 2) throw new ArgumentOutOfRangeException("presetIndex", "请选择 0、1 或 2 档。");
        }

        private static string LocalFile(string path, bool executable)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("请选择已有的本地文件的完整路径。", "path");
            string full = Path.GetFullPath(path);
            // Reject network and device namespaces: this API is for local desktop playback only.
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf('\0') >= 0)
                throw new ArgumentException("只支持本地磁盘文件。", "path");
            if (!File.Exists(full)) throw new FileNotFoundException("文件不存在。", full);
            if (executable && !String.Equals(Path.GetFileName(full), "mpv.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 mpv.exe。", "path");
            return full;
        }

        private static void RequireText(string output, string expected, string error)
        {
            if (output.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                throw new NotSupportedException(error + " 请使用随附版本；没有自动切换档位。");
        }

        private static string RunProbe(string player, string arguments)
        {
            StringBuilder output = new StringBuilder();
            object gate = new object();
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(player, arguments);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.WorkingDirectory = Path.GetDirectoryName(player);
                DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { if (output.Length < 256000) output.AppendLine(e.Data); }
                };
                process.OutputDataReceived += receive;
                process.ErrorDataReceived += receive;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000))
                {
                    StopOwnedPlayer(process);
                    throw new TimeoutException("mpv 功能探测超时，没有启动视频。");
                }
                process.WaitForExit(); // Drain the asynchronous stdout/stderr event queues.
                if (process.ExitCode != 0)
                    throw new NotSupportedException("mpv 不支持所需功能，探测退出码 " + process.ExitCode + "。\r\n" + output.ToString());
                return output.ToString();
            }
        }

        private static void StopOwnedPlayer(Process process)
        {
            try
            {
                if (process.HasExited) return;
                process.CloseMainWindow();
                if (!process.WaitForExit(400)) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static string ReadLog(string path)
        {
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
            catch (IOException) { return String.Empty; }
            catch (UnauthorizedAccessException) { return String.Empty; }
        }

        private static string Under(Environment.SpecialFolder folder, string path)
        {
            string root = Environment.GetFolderPath(folder);
            return root.Length == 0 ? String.Empty : Path.Combine(root, path);
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string path in paths)
            {
                if (String.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    string item = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                    if (Path.IsPathRooted(item) && File.Exists(item)) return Path.GetFullPath(item);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (SecurityException) { }
            }
            return String.Empty;
        }

        private static string AppPath(string executable)
        {
            RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in hives)
            foreach (RegistryView view in views)
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable, false))
                    {
                        string value = key == null ? null : key.GetValue(null) as string;
                        string found = FirstExisting(value);
                        if (found.Length > 0) return found;
                    }
                }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return String.Empty;
        }
    }
}
