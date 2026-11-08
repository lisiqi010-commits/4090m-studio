using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;

namespace RtxHub
{
    /// <summary>Use the installed x64 player's own renderer, UI and playlist.</summary>
    public static class PotPlayerSupport
    {
        // Native switches and multi-content syntax are documented by Kakao in the
        // installed PotPlayer/CmdLine64.txt (CP0 and Chinese sections):
        // quoted contents separated by spaces; /new; /current; /add.
        // No undocumented renderer, language, security or registry setting is written.
        // Native D3D11/NR was evaluated with PotPlayer 1.7.23171 and a synthetic clip;
        // its History/English.txt [261021] documents D3D11 as the default renderer.
        // Other versions/user-selected renderers still need an execution log check.

        public static string LastPlayer { get; private set; }
        public static string LastLog { get; private set; }

        public static string FindPlayer()
        {
            string[] candidates = {
                VideoSupport.FindPotPlayer(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"DAUM\PotPlayer\PotPlayerMini64.exe")
            };
            foreach (string candidate in candidates) if (IsPotPlayer(candidate)) return Path.GetFullPath(candidate);
            return String.Empty;
        }

        /// <summary>Identify the x64 file layout; this is not a signature/authenticity claim.</summary>
        public static bool IsPotPlayer(string exe)
        {
            try
            {
                exe = LocalFile(exe);
                string name = Path.GetFileName(exe);
                if (!name.Equals("PotPlayerMini64.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("PotPlayer64.exe", StringComparison.OrdinalIgnoreCase)) return false;
                LocalFile(Path.Combine(Path.GetDirectoryName(exe), "PotPlayer64.dll"));
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public static string BuildArguments(string[] files, bool append, bool newInstance)
        {
            if (append && newInstance) throw new ArgumentException("追加播放列表时应使用已有窗口。");
            if (files == null) files = new string[0];
            if (files.Length > 2000) throw new ArgumentException("一次选择的文件过多，请分批添加。");
            if (append && files.Length == 0) throw new ArgumentException("请选择要追加的视频。");
            StringBuilder arguments = new StringBuilder();
            foreach (string file in files)
            {
                string media = LocalFile(file);
                string extension = Path.GetExtension(media).ToLowerInvariant();
                if (("|.mp4|.mkv|.mov|.avi|.webm|.m4v|.wmv|.ts|.m2ts|.mts|.flv|.mpg|.mpeg|.vob|.dpl|.m3u|.m3u8|.pls|.asx|")
                    .IndexOf("|" + extension + "|", StringComparison.Ordinal) < 0)
                    throw new ArgumentException("请选择本地视频或播放列表：" + Path.GetFileName(media));
                if (arguments.Length > 0) arguments.Append(' ');
                arguments.Append(VideoSupport.QuoteWindowsArgument(media));
                if (arguments.Length > 28000) throw new ArgumentException("播放列表参数过长，请分批添加。");
            }
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append(newInstance ? "/new" : "/current");
            if (append) arguments.Append(" /add");
            return arguments.ToString();
        }

        /// <summary>Preserve an original player's HDR encoding; video has no engine motion/depth guides.</summary>
        public static string BuildReShadeIni(int presetIndex)
        {
            return NeuralPresets.BuildGameIni(presetIndex).Replace("NRPresentGuides=0\r\n", "NRPresentGuides=2\r\n");
        }

        public static string EnsureInstalled(string exe, int presetIndex)
        {
            exe = RequirePlayer(exe);
            string ini = BuildReShadeIni(presetIndex);
            bool running = IsRunning(exe);
            if (running && !IsManaged(exe))
                throw new InvalidOperationException("PotPlayer 正在运行，尚未加载本工具的增强。请先退出 PotPlayer，再重新选择；不会向运行中的播放器注入。");
            NeuralPackages.Verify();
            VerifyOptionalSr(exe, false);
            if (running)
            {
                VerifyManagedRuntime(exe);
                NeuralLanguage.ApplyChinese(exe);
                LastPlayer = exe; LastLog = Path.Combine(Path.GetDirectoryName(exe), "ReShade.log");
                return "已校验正在运行的受管 PotPlayer；保留现有设置，可继续添加播放列表。";
            }
            string installed = NeuralInstaller.EnsureInstalled(exe, NeuralPackages.BundleDirectory, ini);
            VerifyManagedRuntime(exe);
            NeuralLanguage.ApplyChinese(exe);
            LastPlayer = exe;
            LastLog = Path.Combine(Path.GetDirectoryName(exe), "ReShade.log");
            return "PotPlayer 增强已就绪，原有设置和播放列表保持不变。\r\n" + installed;
        }

        public static string Launch(string exe, string[] files, bool append)
        {
            exe = RequirePlayer(exe);
            // Validate every selected file before inspecting or touching player state.
            BuildArguments(files, false, true);
            bool running = IsRunning(exe);
            if (!running && append) throw new InvalidOperationException("没有可追加的受管 PotPlayer 窗口，请先打开视频。");
            VerifyManagedRuntime(exe);
            string arguments = BuildArguments(files, append, !running);
            string directory = Path.GetDirectoryName(exe);
            string log = Path.Combine(directory, "ReShade.log");
            if (!running && File.Exists(log))
            {
                if ((File.GetAttributes(log) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("播放器日志经过链接，已拒绝移动或启动。");
                File.Move(log, Path.Combine(directory, "ReShade-before-launch-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".log"));
            }
            if (running && (files == null || files.Length == 0))
            {
                LastPlayer = exe; LastLog = log;
                return "PotPlayer 已打开，可直接添加视频到原生播放列表。";
            }
            ProcessStartInfo start = new ProcessStartInfo(exe, arguments);
            start.UseShellExecute = false;
            start.WorkingDirectory = directory;
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("PotPlayer 未能启动。");
                if (process.WaitForExit(1000) && process.ExitCode != 0)
                    throw new InvalidOperationException("PotPlayer 启动失败，退出码 " + process.ExitCode + "。");
            }
            LastPlayer = exe;
            LastLog = log;
            return (running ? (append ? "已追加到 PotPlayer 播放列表。" : "已发送到正在运行的 PotPlayer。") : "已打开增强 PotPlayer，可使用原生播放列表。") +
                "\r\n已请求加载 NR；实际执行以本次日志为准。F6 对比开关，F11 查看实测开销。\r\n" +
                "若未执行 NR，请在 PotPlayer 视频设置中选择“内置 Direct3D 11 视频渲染器”并重启播放器。";
        }

        public static string Play(string[] files, Action<string> progress)
        {
            BuildArguments(files, false, true);
            string player = FindPlayer();
            if (String.IsNullOrEmpty(player)) throw new FileNotFoundException("未找到已安装的 64 位 PotPlayer。");
            bool running = IsRunning(player);
            if (running && !IsManaged(player))
                throw new InvalidOperationException("请先退出当前 PotPlayer，再选择视频，以加载画面增强。");
            if (progress != null) progress("正在准备 PotPlayer 画面增强…");
            NeuralPackages.Prepare(progress);
            EnsureInstalled(player, 1);
            return Launch(player, files, running && files != null && files.Length > 0);
        }

        public static bool IsRunning(string exe)
        {
            exe = RequirePlayer(exe);
            int matching = 0, other = 0;
            bool unknown = false;
            foreach (string name in new string[] { "PotPlayerMini64", "PotPlayer64" })
                foreach (Process process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            if (String.Equals(Path.GetFullPath(process.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) matching++;
                            else other++;
                        }
                        catch (System.ComponentModel.Win32Exception) { unknown = true; }
                        catch (InvalidOperationException) { }
                    }
            if (unknown) throw new InvalidOperationException("无法确认现有 PotPlayer 进程，请先关闭播放器再重试。");
            if (matching > 1 || (matching > 0 && other > 0))
                throw new InvalidOperationException("存在多个 PotPlayer 实例，请仅保留要增强的一个窗口再添加播放列表。");
            return matching == 1;
        }

        static bool IsManaged(string exe)
        {
            foreach (string managed in NeuralInstaller.ManagedTargets())
                if (String.Equals(exe, managed, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void VerifyManagedRuntime(string exe)
        {
            if (!IsManaged(exe)) throw new InvalidOperationException("此 PotPlayer 尚未由本工具安装增强，请先完成安装。");
            NeuralPackages.Verify();
            string journal = NeuralInstaller.JournalPath(exe);
            // ManagedTargets already validates paths/schema. Also require ownership of
            // every loader/config before reusing an active process after a partial restore.
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(journal, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                document.Load(reader);
            HashSet<string> owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in document.DocumentElement.ChildNodes)
            {
                XmlElement element = child as XmlElement;
                if (element != null && element.Name == "file" && element.GetAttribute("owned") == "True") owned.Add(element.GetAttribute("name"));
            }
            string directory = Path.GetDirectoryName(exe);
            foreach (string name in new string[] { "dxgi.dll", "renodx-dlss5.addon64", "nvngx_dlssnr.dll", "ReShade.ini" })
            {
                if (!owned.Contains(name)) throw new InvalidOperationException("播放器增强正在撤回或所有权不完整，请先完成撤回后重新安装。");
                string destination = LocalFile(Path.Combine(directory, name));
                if (name != "ReShade.ini" && NeuralInstallService.FileHash(destination) != NeuralInstallService.FileHash(Path.Combine(NeuralPackages.BundleDirectory, name)))
                    throw new InvalidOperationException("PotPlayer 增强组件已改变，已保留并拒绝启动：" + name);
            }
            VerifyOptionalSr(exe, true);
        }

        static void VerifyOptionalSr(string exe, bool mustExist)
        {
            string source = Path.Combine(NeuralPackages.BundleDirectory, "nvngx_dlss.dll");
            string destination = Path.Combine(Path.GetDirectoryName(exe), "nvngx_dlss.dll");
            if (mustExist && File.Exists(source) && !File.Exists(destination))
                throw new InvalidOperationException("PotPlayer 的受管 DLSS SR 运行组件缺失，请先关闭播放器并重新准备增强。");
            if (File.Exists(destination) && File.Exists(source))
            {
                destination = LocalFile(destination);
                if (NeuralInstallService.FileHash(destination) != NeuralInstallService.FileHash(source))
                    throw new InvalidOperationException("PotPlayer 目录存在不同版本的 DLSS SR，已保留原文件；请先检查现有插件。");
            }
        }

        static string RequirePlayer(string exe)
        {
            if (!IsPotPlayer(exe)) throw new ArgumentException("请选择包含 PotPlayer64.dll 的 64 位 PotPlayer 程序。");
            return LocalFile(exe);
        }

        static string LocalFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("请选择本机磁盘中的完整文件路径。");
            foreach (string part in path.Substring(3).Split('\\', '/'))
                if (part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                    throw new ArgumentException("文件路径包含不明确的目录段。");
            string full = Path.GetFullPath(path);
            string current = full;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("文件或目录经过链接，已拒绝使用。");
                current = Path.GetDirectoryName(current);
            }
            if (!File.Exists(full)) throw new FileNotFoundException("文件不存在。", full);
            return full;
        }
    }
}
