using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace RtxHub
{
    // Explicit opt-in file deployment. No DLL loading, injection API,
    // game execution, renderer detection or compatibility claims happen here.
    public static class NeuralInstaller
    {
        static NeuralInstallService Service { get { return new NeuralInstallService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Rtx4090MHub\NeuralInstalls")); } }
        public static string Preflight(string exe, string bundleDir) { return Service.Preflight(exe, bundleDir); }
        public static string Install(string exe, string bundleDir, string ini) { return Service.Install(exe, bundleDir, ini); }
        public static string Restore(string exe) { return Service.Restore(exe); }
        public static string Status(string exe) { return Service.Status(exe); }
        public static string JournalPath(string exe) { return Service.JournalPath(exe); }
        public static string[] ManagedTargets() { return Service.ManagedTargets(); }
        public static string EnsureInstalled(string exe, string bundleDir, string ini) { return Service.EnsureInstalled(exe, bundleDir, ini); }
    }

    internal sealed class NeuralInstallFile
    {
        internal string Name, Destination, Source, Hash, Temp;
        internal bool Owned;
    }
    internal sealed class NeuralInstallJournal
    {
        internal string Executable, Directory, CreatedUtc;
        internal readonly List<NeuralInstallFile> Files = new List<NeuralInstallFile>();
    }
    internal interface INeuralFileWriter { void Commit(NeuralInstallFile file, byte[] ini); }
    internal sealed class AtomicNeuralFileWriter : INeuralFileWriter
    {
        public void Commit(NeuralInstallFile file, byte[] ini)
        {
            bool created = false;
            try
            {
                NeuralInstallService.CheckDirectory(Path.GetDirectoryName(file.Destination), true);
                if (File.Exists(file.Destination) || System.IO.Directory.Exists(file.Destination)) throw new InvalidOperationException("目标文件已出现，拒绝覆盖：" + file.Destination);
                using (var input = file.Source == "generated:ReShade.ini" ? (Stream)new MemoryStream(ini, false) : new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(file.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true; input.CopyTo(output); output.Flush(true);
                }
                if (!String.Equals(NeuralInstallService.FileHash(file.Temp), file.Hash, StringComparison.Ordinal)) throw new InvalidOperationException("安装源或暂存文件校验失败：" + file.Name);
                NeuralInstallService.CheckDirectory(Path.GetDirectoryName(file.Destination), true);
                File.Move(file.Temp, file.Destination); // atomic same-directory move; never replaces a game file
            }
            catch
            {
                // A complete temp file is verified. A partial/externally modified file
                // stays recorded for review instead of being deleted indiscriminately.
                if (created && File.Exists(file.Temp) && !NeuralInstallService.IsReparse(file.Temp))
                    try { if (NeuralInstallService.FileHash(file.Temp) == file.Hash) File.Delete(file.Temp); } catch { }
                throw;
            }
        }
    }

    internal sealed class NeuralInstallService
    {
        static readonly string[] Required = { "dxgi.dll", "renodx-dlss5.addon64", "nvngx_dlssnr.dll", "ReShade.ini" };
        static readonly string[] HookConflicts = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "version.dll", "ReShade.ini", "renodx-dlss5.addon64", "nvngx_dlssnr.dll" };
        static readonly string[] AntiCheatDirs = { "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "ACE", "AntiCheatExpert" };
        static readonly string[] AntiCheatFiles = { "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_x64.dll", "EasyAntiCheat_EOS_x64.dll", "BEService.exe", "BEService_x64.exe", "BEClient.dll", "BEClient_x64.dll", "ACE-Base.sys", "ACE-BOOT.sys", "ACE-Service64.exe", "AntiCheatExpert.exe", "AntiCheatExpert.dll", "ACE.dll", "ACE32.dll", "ACE64.dll", "start_protected_game.exe" };
        readonly string journalDirectory;
        readonly INeuralFileWriter writer;
        internal NeuralInstallService(string journalDirectory) : this(journalDirectory, new AtomicNeuralFileWriter()) { }
        internal NeuralInstallService(string journalDirectory, INeuralFileWriter writer) { this.journalDirectory = Path.GetFullPath(journalDirectory); this.writer = writer; }
        internal string JournalPath(string exe) { return Path.Combine(journalDirectory, TextHash(CanonicalExe(exe, false)) + ".xml"); }
        internal string[] ManagedTargets()
        {
            using (var guard = new InstallerGuard(journalDirectory))
            {
                CheckDirectory(journalDirectory, false);
                if (!System.IO.Directory.Exists(journalDirectory)) return new string[0];
                var targets = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in System.IO.Directory.GetFiles(journalDirectory, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string stem = Path.GetFileNameWithoutExtension(path);
                        if (stem.Length != 64 || !IsHex(stem) || IsReparse(path) || new FileInfo(path).Length > 1024 * 1024) throw new FormatException("日志名称或类型无效");
                        var document = new XmlDocument { XmlResolver = null };
                        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                        if (document.DocumentElement == null) throw new FormatException("缺少日志根节点");
                        string exe = CanonicalExe(document.DocumentElement.GetAttribute("exe"), false);
                        if (!SamePath(path, JournalPath(exe))) throw new FormatException("日志文件名与目标 SHA-256 不匹配");
                        if (ReadJournal(exe) == null) throw new FormatException("日志在验证期间消失");
                        if (seen.Add(exe)) targets.Add(exe);
                    }
                    catch (Exception ex) { throw new InvalidOperationException("管理目标日志无法验证，未采用其中的程序路径：" + path, ex); }
                }
                targets.Sort(StringComparer.OrdinalIgnoreCase); return targets.ToArray();
            }
        }
        internal string EnsureInstalled(string exe, string bundleDir, string ini)
        {
            exe = CanonicalExe(exe, true);
            if (String.IsNullOrWhiteSpace(ini) || Encoding.UTF8.GetByteCount(ini) > 1024 * 1024) throw new InvalidOperationException("ReShade 配置为空或过大，已取消安装。");
            using (var guard = new InstallerGuard(journalDirectory))
            {
                var journal = ReadJournal(exe);
                if (journal == null) return Install(exe, bundleDir, ini);
                CheckX64(exe); CheckNotRunning(exe); CheckAntiCheat(journal.Directory);
                bundleDir = CanonicalLocal(bundleDir); CheckDirectory(bundleDir, true);
                foreach (string hook in HookConflicts)
                {
                    bool tracked = false; foreach (var file in journal.Files) if (file.Name == hook) tracked = true;
                    if (!tracked && (File.Exists(Path.Combine(journal.Directory, hook)) || System.IO.Directory.Exists(Path.Combine(journal.Directory, hook)))) throw new InvalidOperationException("发现非本工具管理的加载器，拒绝补齐：" + hook);
                }
                byte[] config = new UTF8Encoding(false).GetBytes(ini); string token = Guid.NewGuid().ToString("N");
                var missing = new List<NeuralInstallFile>(); int verifiedDlls = 0; bool preservedIni = false;
                for (int index = 0; index < journal.Files.Count; index++)
                {
                    var file = journal.Files[index];
                    if (File.Exists(file.Temp) || System.IO.Directory.Exists(file.Temp)) throw new InvalidOperationException("存在未完成的暂存文件，请先恢复并检查：" + file.Temp);
                    bool exists = File.Exists(file.Destination) || System.IO.Directory.Exists(file.Destination);
                    if (exists && (!file.Owned || System.IO.Directory.Exists(file.Destination) || IsReparse(file.Destination))) throw new InvalidOperationException("目标文件不具备可验证的本工具所有权，拒绝采用或覆盖：" + file.Name);
                    if (file.Name == "ReShade.ini")
                    {
                        if (exists) { preservedIni = true; continue; } // preserve regular owned INI edits, retaining its original restore hash
                        file.Source = "generated:ReShade.ini"; file.Hash = BytesHash(config);
                    }
                    else
                    {
                        string source = Path.Combine(bundleDir, file.Name);
                        if (!File.Exists(source) || IsReparse(source) || new FileInfo(source).Length == 0) throw new InvalidOperationException("经过验证的组件包缺少文件：" + file.Name);
                        string expected = FileHash(source);
                        if (exists)
                        {
                            if (FileHash(file.Destination) != file.Hash || file.Hash != expected) throw new InvalidOperationException("已有工具 DLL / 插件被修改或与当前组件包不符，已保留并拒绝启动：" + file.Name);
                            verifiedDlls++; continue;
                        }
                        file.Source = source; file.Hash = expected;
                    }
                    file.Owned = false; file.Temp = Path.Combine(journal.Directory, ".rtxhub-" + token + "-" + index + ".tmp"); missing.Add(file);
                }
                if (missing.Count == 0) return "已校验并复用现有 " + verifiedDlls + " 个 NR DLL / 插件；新增 0 个文件。已有 ReShade.ini 设置保持不变。";
                WriteJournal(journal, true); // all refill paths/hashes are durable before any new staging
                var committed = new List<NeuralInstallFile>();
                try
                {
                    foreach (var file in missing) { writer.Commit(file, config); file.Owned = true; committed.Add(file); WriteJournal(journal, true); }
                }
                catch (Exception ex)
                {
                    int reverted = 0;
                    foreach (var file in committed)
                        if (File.Exists(file.Destination) && !IsReparse(file.Destination) && FileHash(file.Destination) == file.Hash) { File.Delete(file.Destination); file.Owned = false; reverted++; }
                    WriteJournal(journal, true);
                    throw new InvalidOperationException("补齐失败，已回滚本次新增且未变的 " + reverted + " 个文件；原有文件和恢复日志保留。", ex);
                }
                return "已校验现有 " + verifiedDlls + " 个 NR DLL / 插件，并补齐 " + missing.Count + " 个工具文件。" + (preservedIni ? "已有 ReShade.ini 自定义设置保持不变。" : "已生成缺失的 ReShade.ini。") + "\r\n文件已就绪，实际 NR 运行仍需检查日志。";
            }
        }
        static string TextHash(string value) { return BytesHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant())); }
        static string BytesHash(byte[] value) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(value)); }
        static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
        internal static string FileHash(string path)
        {
            if (IsReparse(path)) throw new InvalidOperationException("拒绝读取重解析点文件：" + path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }
        internal static bool IsReparse(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        static string CanonicalLocal(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathRooted(path)) throw new InvalidOperationException("请选择本机磁盘的完整路径；不支持网络、设备或相对路径。");
            try
            {
                string canonical = Path.GetFullPath(path);
                string root = Path.GetPathRoot(canonical);
                if (root.Length != 3 || root[1] != ':' || canonical.IndexOf(':', 2) >= 0) throw new InvalidOperationException("不支持此磁盘路径。");
                if (new DriveInfo(root).DriveType == DriveType.Network) throw new InvalidOperationException("不支持映射网络磁盘，请选择本机游戏目录。");
                foreach (string part in canonical.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) throw new InvalidOperationException("路径包含不明确的尾部空格或句点。");
                return canonical;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException("路径无效。", ex); }
        }
        internal static void CheckDirectory(string directory, bool mustExist)
        {
            directory = CanonicalLocal(directory);
            if (mustExist && !System.IO.Directory.Exists(directory)) throw new InvalidOperationException("找不到目标目录：" + directory);
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("不支持符号链接、目录联接或其他重解析点目录：" + current.FullName);
        }
        static string CanonicalExe(string exe, bool mustExist)
        {
            exe = CanonicalLocal(exe);
            if (!String.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择游戏的 .exe 文件。");
            CheckDirectory(Path.GetDirectoryName(exe), mustExist);
            if (File.Exists(exe) && IsReparse(exe)) throw new InvalidOperationException("不支持重解析点游戏文件。");
            if (mustExist && !File.Exists(exe)) throw new InvalidOperationException("找不到游戏程序：" + exe);
            return exe;
        }
        static void CheckX64(string exe)
        {
            try
            {
                using (var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new FormatException();
                    stream.Position = 0x3c; int offset = reader.ReadInt32();
                    if (offset < 64 || offset > stream.Length - 26) throw new FormatException();
                    stream.Position = offset;
                    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new FormatException();
                    stream.Position = offset + 20; ushort optionalSize = reader.ReadUInt16();
                    if (optionalSize < 2 || offset + 24L + optionalSize > stream.Length) throw new FormatException();
                    stream.Position = offset + 24;
                    if (reader.ReadUInt16() != 0x20b) throw new FormatException();
                }
            }
            catch (Exception ex) { throw new InvalidOperationException("目标必须是本机 64 位 Windows（x64 / PE32+）游戏程序。", ex); }
        }
        static void CheckNotRunning(string exe)
        {
            foreach (var process in Process.GetProcesses()) using (process)
            {
                string running;
                try { running = process.MainModule.FileName; }
                catch (Exception) { continue; } // protected unrelated processes cannot be inspected
                if (String.Equals(Path.GetFullPath(running), exe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此目标程序仍在运行。请完全退出游戏后再安装或恢复：" + exe);
            }
        }
        static void CheckAntiCheat(string directory)
        {
            var found = new List<string>(); var current = new DirectoryInfo(directory);
            for (int level = 0; level < 3 && current != null; level++, current = current.Parent)
            {
                foreach (string name in AntiCheatDirs) if (System.IO.Directory.Exists(Path.Combine(current.FullName, name))) found.Add(Path.Combine(current.FullName, name));
                foreach (string name in AntiCheatFiles) if (File.Exists(Path.Combine(current.FullName, name))) found.Add(Path.Combine(current.FullName, name));
            }
            if (found.Count > 0) throw new InvalidOperationException("检测到反作弊组件，已阻止第三方画面插件部署。\r\n" + String.Join("\r\n", found.ToArray()));
        }
        static string ExistingGameSr(string directory)
        {
            string rootSr = Path.Combine(directory, "nvngx_dlss.dll");
            if (File.Exists(rootSr))
            {
                if (IsReparse(rootSr)) throw new InvalidOperationException("游戏原有 DLSS SR 是重解析点，已停止部署：" + rootSr);
                return rootSr;
            }
            // A known native location, not a recursive game-directory search.
            string streamline = Path.Combine(directory, "Streamline");
            if (System.IO.Directory.Exists(streamline)) CheckDirectory(streamline, true);
            string nativeSr = Path.Combine(streamline, "nvngx_dlss.dll");
            if (!File.Exists(nativeSr)) return null;
            if (IsReparse(nativeSr)) throw new InvalidOperationException("Streamline 原有 DLSS SR 是重解析点，已停止部署：" + nativeSr);
            return nativeSr;
        }
        List<NeuralInstallFile> Plan(string exe, string bundleDir)
        {
            CheckX64(exe); CheckNotRunning(exe); string directory = Path.GetDirectoryName(exe); CheckAntiCheat(directory);
            if (File.Exists(JournalPath(exe)) || System.IO.Directory.Exists(JournalPath(exe))) throw new InvalidOperationException("此游戏已有安装/恢复日志，请先执行恢复并处理保留文件，再重新安装。\r\n" + JournalPath(exe));
            var conflicts = new List<string>();
            foreach (string name in HookConflicts) if (File.Exists(Path.Combine(directory, name)) || System.IO.Directory.Exists(Path.Combine(directory, name))) conflicts.Add(Path.Combine(directory, name));
            if (conflicts.Count > 0) throw new InvalidOperationException("检测到已有加载器、NR 组件或个人 ReShade 配置，拒绝覆盖：\r\n" + String.Join("\r\n", conflicts.ToArray()));
            bundleDir = CanonicalLocal(bundleDir); CheckDirectory(bundleDir, true);
            var files = new List<NeuralInstallFile>();
            foreach (string name in Required)
            {
                if (name == "ReShade.ini") { files.Add(new NeuralInstallFile { Name = name, Source = "generated:ReShade.ini" }); continue; }
                string source = Path.Combine(bundleDir, name);
                if (!File.Exists(source) || IsReparse(source) || new FileInfo(source).Length == 0) throw new InvalidOperationException("经过固定版本验证的组件包缺少正常文件：" + source);
                files.Add(new NeuralInstallFile { Name = name, Source = source, Hash = FileHash(source) });
            }
            string sr = Path.Combine(bundleDir, "nvngx_dlss.dll"), targetSr = Path.Combine(directory, "nvngx_dlss.dll");
            if (ExistingGameSr(directory) == null && !System.IO.Directory.Exists(targetSr) && File.Exists(sr))
            {
                if (IsReparse(sr) || new FileInfo(sr).Length == 0) throw new InvalidOperationException("可选 DLSS SR 组件无效：" + sr);
                files.Add(new NeuralInstallFile { Name = "nvngx_dlss.dll", Source = sr, Hash = FileHash(sr) });
            }
            string token = Guid.NewGuid().ToString("N"); int index = 0;
            foreach (var file in files) { file.Destination = Path.Combine(directory, file.Name); file.Temp = Path.Combine(directory, ".rtxhub-" + token + "-" + (index++) + ".tmp"); }
            return files;
        }
        internal string Preflight(string exe, string bundleDir)
        {
            exe = CanonicalExe(exe, true);
            using (var guard = new InstallerGuard(journalDirectory))
            {
                var files = Plan(exe, bundleDir); var text = new StringBuilder("安装预检通过。第三方画面插件用于 DX11 / DX12，具体游戏兼容性需验证。\r\n目标目录：" + Path.GetDirectoryName(exe) + "\r\n将新增：");
                foreach (var file in files) text.Append("\r\n").Append(file.Destination);
                string nativeSr = ExistingGameSr(Path.GetDirectoryName(exe));
                if (nativeSr != null) text.Append("\r\n保留游戏原有 DLSS SR：").Append(nativeSr);
                text.Append("\r\n这些文件尚未安装；未加载 DLL。反作弊检查仅覆盖本目录及向上两级的已知名称。");
                return text.ToString();
            }
        }
        internal string Install(string exe, string bundleDir, string ini)
        {
            exe = CanonicalExe(exe, true);
            if (String.IsNullOrWhiteSpace(ini) || Encoding.UTF8.GetByteCount(ini) > 1024 * 1024) throw new InvalidOperationException("ReShade 配置为空或过大，已取消安装。");
            using (var guard = new InstallerGuard(journalDirectory))
            {
                var journal = new NeuralInstallJournal { Executable = exe, Directory = Path.GetDirectoryName(exe), CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                journal.Files.AddRange(Plan(exe, bundleDir)); byte[] config = new UTF8Encoding(false).GetBytes(ini);
                foreach (var file in journal.Files) if (file.Name == "ReShade.ini") file.Hash = BytesHash(config);
                WriteJournal(journal, false); // intent/hashes/path list durable before ANY writes in the game directory
                try
                {
                    foreach (var file in journal.Files)
                    {
                        writer.Commit(file, config);
                        file.Owned = true; // in-memory ownership is set before journal I/O can fail
                        WriteJournal(journal, true);
                    }
                    return "已安装第三方 NR 组件，并校验新增文件。请按游戏要求以 DX11 / DX12 模式启动。\r\n尚未验证游戏兼容性、实际画面或性能。\r\n恢复日志：" + JournalPath(exe);
                }
                catch (Exception ex)
                {
                    string recovery;
                    try { recovery = RestoreJournal(journal); }
                    catch (Exception recoveryError) { recovery = "自动恢复未完成：" + recoveryError.Message + "。请保留日志：" + JournalPath(exe); }
                    throw new InvalidOperationException("安装失败，已尝试仅回滚确认属于本次安装的文件。\r\n" + recovery, ex);
                }
            }
        }
        internal string Restore(string exe)
        {
            exe = CanonicalExe(exe, false);
            using (var guard = new InstallerGuard(journalDirectory))
            {
                var journal = ReadJournal(exe);
                if (journal == null) return "未找到此游戏的 NR 安装日志；未删除任何文件。";
                CheckNotRunning(exe); return RestoreJournal(journal);
            }
        }
        string RestoreJournal(NeuralInstallJournal journal)
        {
            CheckDirectory(journal.Directory, false);
            var preserved = new List<string>(); int removed = 0;
            foreach (var file in journal.Files)
            {
                if (File.Exists(file.Destination) || System.IO.Directory.Exists(file.Destination))
                {
                    if (!file.Owned) preserved.Add(file.Name + "（意图记录尚未确认所有权）");
                    else if (System.IO.Directory.Exists(file.Destination) || IsReparse(file.Destination)) preserved.Add(file.Name + "（类型已改变）");
                    else if (FileHash(file.Destination) != file.Hash) preserved.Add(file.Name + "（内容已被外部修改）");
                    else { File.Delete(file.Destination); file.Owned = false; removed++; }
                }
                if (File.Exists(file.Temp) || System.IO.Directory.Exists(file.Temp)) preserved.Add(Path.GetFileName(file.Temp) + "（未完成的暂存文件，保留供检查）");
            }
            if (preserved.Count == 0)
            {
                File.Delete(JournalPath(journal.Executable));
                return "恢复完成，已删除 " + removed + " 个确认属于本工具且内容未变的新增文件。原有游戏文件、ReShade.log 和缓存均保留。";
            }
            WriteJournal(journal, true); // record removed ownership so a later foreign same-hash file cannot be adopted
            return "已移除 " + removed + " 个未改变的工具文件。以下文件已保留，恢复日志继续保留：\r\n" + String.Join("\r\n", preserved.ToArray());
        }
        internal string Status(string exe)
        {
            exe = CanonicalExe(exe, false);
            using (var guard = new InstallerGuard(journalDirectory))
            {
                var journal = ReadJournal(exe);
                if (journal == null) return "此游戏没有本工具的 NR 安装日志。";
                var text = new StringBuilder("存在 NR 安装/恢复日志：" + JournalPath(exe));
                foreach (var file in journal.Files)
                {
                    string state = !File.Exists(file.Destination) ? "文件缺失" : (!file.Owned ? "所有权未确认" : (IsReparse(file.Destination) ? "文件类型改变" : (FileHash(file.Destination) == file.Hash ? "已校验工具文件" : "内容已改变，恢复时保留")));
                    text.Append("\r\n").Append(file.Name).Append("：").Append(state);
                }
                return text.ToString();
            }
        }
        void WriteJournal(NeuralInstallJournal journal, bool update)
        {
            CheckDirectory(journalDirectory, false); System.IO.Directory.CreateDirectory(journalDirectory); CheckDirectory(journalDirectory, true);
            string path = JournalPath(journal.Executable), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (File.Exists(path) && IsReparse(path)) throw new InvalidOperationException("安装日志是重解析点，拒绝写入。");
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                    {
                        xml.WriteStartElement("neuralInstall"); xml.WriteAttributeString("version", "1"); xml.WriteAttributeString("exe", journal.Executable); xml.WriteAttributeString("directory", journal.Directory); xml.WriteAttributeString("createdUtc", journal.CreatedUtc);
                        foreach (var file in journal.Files)
                        {
                            xml.WriteStartElement("file"); xml.WriteAttributeString("name", file.Name); xml.WriteAttributeString("path", file.Destination); xml.WriteAttributeString("source", file.Source);
                            xml.WriteAttributeString("sourceHash", file.Hash); xml.WriteAttributeString("owned", file.Owned.ToString()); xml.WriteAttributeString("temp", file.Temp); xml.WriteEndElement();
                        }
                        xml.WriteEndElement();
                    }
                    stream.Flush(true);
                }
                if (update) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception ex) { throw new InvalidOperationException("无法持久保存 NR 安装意图/所有权日志，已停止后续安装：" + path, ex); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        NeuralInstallJournal ReadJournal(string exe)
        {
            string path = JournalPath(exe);
            if (!File.Exists(path)) return null;
            try
            {
                CheckDirectory(journalDirectory, true);
                if (IsReparse(path) || new FileInfo(path).Length > 1024 * 1024) throw new FormatException("日志类型或大小无效");
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                var root = document.DocumentElement; string directory = Path.GetDirectoryName(exe);
                if (root == null || root.Name != "neuralInstall" || root.GetAttribute("version") != "1" || !SamePath(root.GetAttribute("exe"), exe) || !SamePath(root.GetAttribute("directory"), directory)) throw new FormatException("日志目标路径不匹配");
                var journal = new NeuralInstallJournal { Executable = exe, Directory = directory, CreatedUtc = root.GetAttribute("createdUtc") }; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlNode node in root.ChildNodes)
                {
                    var element = node as XmlElement;
                    if (element == null || element.Name != "file") throw new FormatException("非预期的日志条目");
                    string name = element.GetAttribute("name"), destination = element.GetAttribute("path"), hash = element.GetAttribute("sourceHash"), temp = element.GetAttribute("temp");
                    bool allowed = name == "nvngx_dlss.dll"; foreach (string required in Required) if (name == required) allowed = true;
                    if (!allowed || !names.Add(name) || !SamePath(destination, Path.Combine(directory, name)) || !SamePath(Path.GetDirectoryName(temp), directory)) throw new FormatException("日志包含不受允许的文件路径");
                    if (hash.Length != 64 || !IsHex(hash)) throw new FormatException("日志校验值无效");
                    string tempName = Path.GetFileName(temp);
                    if (!tempName.StartsWith(".rtxhub-", StringComparison.Ordinal) || tempName.Length != 46 || tempName.Substring(40, 1) != "-" || !IsHex(tempName.Substring(8, 32)) || tempName[41] < '0' || tempName[41] > '4' || !tempName.EndsWith(".tmp", StringComparison.Ordinal)) throw new FormatException("日志暂存路径无效");
                    journal.Files.Add(new NeuralInstallFile { Name = name, Destination = Path.Combine(directory, name), Source = element.GetAttribute("source"), Hash = hash, Temp = temp, Owned = Boolean.Parse(element.GetAttribute("owned")) });
                }
                foreach (string required in Required) if (!names.Contains(required)) throw new FormatException("日志清单不完整");
                return journal;
            }
            catch (Exception ex) { throw new InvalidOperationException("安装日志无法验证，已拒绝删除任何文件。请保留日志：" + path, ex); }
        }
        static bool SamePath(string a, string b) { return String.Equals(CanonicalLocal(a), CanonicalLocal(b), StringComparison.OrdinalIgnoreCase); }
        static bool IsHex(string text) { foreach (char ch in text) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false; return true; }
        sealed class InstallerGuard : IDisposable
        {
            readonly Mutex mutex;
            internal InstallerGuard(string directory)
            {
                mutex = new Mutex(false, @"Local\Rtx4090MHub_NeuralInstall_" + TextHash(directory));
                try { if (!mutex.WaitOne(10000)) { mutex.Dispose(); throw new InvalidOperationException("另一个安装/恢复操作正在运行，请稍后重试。"); } } catch (AbandonedMutexException) { }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
