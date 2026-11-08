// Native declarations derived from NVIDIA/nvapi nvapi.h (September 2, 2026),
// nvapi_interface.h and NvApiDriverSettings.h: https://github.com/NVIDIA/nvapi
// Copyright (c) 2019-2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: MIT
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions: The above copyright notice and this
// permission notice shall be included in all copies or substantial portions of
// the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
// EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
// OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace RtxHub
{
    public static class DriverProfiles
    {
        public static string BackupDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Rtx4090MHub\Backups"); } }
        public static string Probe()
        {
            using (var guard = new DriverGuard())
            using (var session = new NativeProfileSession())
                return session.DescribeHardware() + "\r\n" + session.ValidateSettingAbi() + "\r\nNVAPI 配置数据库可读取。启动检测未更改驱动配置。";
        }
        public static string Read(string exe)
        {
            exe = ValidateExecutable(exe, true);
            using (var guard = new DriverGuard())
            using (var session = new NativeProfileSession())
            {
                ProfileRecord profile = session.Find(exe);
                if (profile == null) return "此程序尚无独立 NVIDIA 配置文件；当前使用驱动继承设置。\r\n" + Path.GetFileName(exe);
                return ProfileWorkflow.Describe(session, profile) + "\r\n读取只显示驱动配置；游戏是否采用这些设置需在游戏中验证。";
            }
        }
        public static string Apply(string exe, int presetIndex)
        {
            ProfileWorkflow.ModeFor(presetIndex);
            exe = ValidateExecutable(exe, true);
            using (var guard = new DriverGuard())
            using (var session = new NativeProfileSession())
                return new ProfileWorkflow(new SnapshotStore(BackupDirectory)).Apply(session, exe, presetIndex);
        }
        public static string Restore(string exe)
        {
            // Restoration remains possible after a game is uninstalled or moved.
            exe = ValidateExecutable(exe, false);
            using (var guard = new DriverGuard())
            using (var session = new NativeProfileSession())
                return new ProfileWorkflow(new SnapshotStore(BackupDirectory)).Restore(session, exe);
        }
        static string ValidateExecutable(string exe, bool mustExist)
        {
            if (String.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe)) throw new InvalidOperationException("请先选择游戏的完整 .exe 文件路径。");
            try { exe = Path.GetFullPath(exe); }
            catch (Exception ex) { throw new InvalidOperationException("游戏路径无效。", ex); }
            if (!String.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择游戏程序的 .exe 文件。");
            if (exe.Length >= 2048) throw new InvalidOperationException("游戏路径超过 NVAPI 长度限制。");
            if (mustExist && !File.Exists(exe)) throw new InvalidOperationException("找不到游戏程序：" + exe);
            return exe;
        }
    }

    internal sealed class DriverGuard : IDisposable
    {
        readonly Mutex mutex;
        internal DriverGuard()
        {
            mutex = new Mutex(false, @"Local\RtxHub_DriverProfiles_" + SnapshotStore.Hash(Environment.UserDomainName + "\\" + Environment.UserName));
            try { if (!mutex.WaitOne(10000)) { mutex.Dispose(); throw new InvalidOperationException("另一个 RTX Hub 正在处理驱动配置，请稍后重试。"); } }
            catch (AbandonedMutexException) { /* Previous process crashed; persisted baseline is still authoritative. */ }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal sealed class ProfileRecord
    {
        internal readonly string Name; internal readonly IntPtr Handle; internal readonly uint AppCount;
        internal ProfileRecord(string name, IntPtr handle, uint appCount) { Name = name; Handle = handle; AppCount = appCount; }
    }
    internal sealed class SettingState
    {
        internal uint Id, Type, Location, Value; internal bool Exists, Predefined;
        internal bool Explicit { get { return Exists && Location == 0 && !Predefined; } }
        internal SettingState(uint id, bool exists, uint type, uint location, bool predefined, uint value) { Id = id; Exists = exists; Type = type; Location = location; Predefined = predefined; Value = value; }
        internal SettingState Copy() { return (SettingState)MemberwiseClone(); }
        internal static SettingState Missing(uint id) { return new SettingState(id, false, 0, 0, false, 0); }
    }
    internal interface IProfileSession : IDisposable
    {
        ProfileRecord Find(string exe); ProfileRecord Create(string exe, string name);
        SettingState Get(ProfileRecord profile, uint id); void Set(ProfileRecord profile, uint id, uint value);
        void Delete(ProfileRecord profile, uint id); void Save(); void Reload();
    }

    internal sealed class ProfileSnapshot
    {
        internal string Profile, Executable, CreatedUtc;
        internal SettingState[] Settings;
    }
    internal sealed class SnapshotStore
    {
        readonly string directory;
        internal SnapshotStore(string directoryPath) { directory = directoryPath; }
        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
        }
        // Profile identity avoids conflicting baselines when multiple EXEs share one DRS profile.
        internal string PathFor(string profile) { return Path.Combine(directory, Hash(profile) + ".xml"); }
        internal ProfileSnapshot Load(string profile)
        {
            string path = PathFor(profile);
            if (!File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > 65536) throw new FormatException("备份过大");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, settings)) doc.Load(reader);
                XmlElement root = doc.DocumentElement;
                if (root == null || root.Name != "snapshot" || root.GetAttribute("version") != "1" || !String.Equals(root.GetAttribute("profile"), profile, StringComparison.OrdinalIgnoreCase)) throw new FormatException("配置标识不匹配");
                var values = new List<SettingState>(); var seen = new HashSet<uint>();
                foreach (XmlNode node in root.ChildNodes)
                {
                    var el = node as XmlElement;
                    if (el == null || el.Name != "setting") throw new FormatException("未知备份字段");
                    uint id = UInt32.Parse(el.GetAttribute("id"), CultureInfo.InvariantCulture);
                    if (!ProfileWorkflow.IsTracked(id) || !seen.Add(id)) throw new FormatException("非预期设置");
                    uint type = UInt32.Parse(el.GetAttribute("type"), CultureInfo.InvariantCulture);
                    uint location = UInt32.Parse(el.GetAttribute("location"), CultureInfo.InvariantCulture);
                    if (type != 0 || location > 3) throw new FormatException("不支持的设置类型");
                    values.Add(new SettingState(id, Boolean.Parse(el.GetAttribute("exists")), type, location, Boolean.Parse(el.GetAttribute("predefined")), UInt32.Parse(el.GetAttribute("value"), CultureInfo.InvariantCulture)));
                }
                if (seen.Count != 2) throw new FormatException("备份设置不完整");
                return new ProfileSnapshot { Profile = profile, Executable = root.GetAttribute("exe"), CreatedUtc = root.GetAttribute("createdUtc"), Settings = values.ToArray() };
            }
            catch (Exception ex) { throw new InvalidOperationException("原始备份无法验证，已停止更改驱动。请保留此文件以便恢复：" + path, ex); }
        }
        internal ProfileSnapshot Preserve(ProfileSnapshot snapshot)
        {
            var existing = Load(snapshot.Profile);
            if (existing != null) return existing;
            string path = PathFor(snapshot.Profile), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                    {
                        writer.WriteStartElement("snapshot"); writer.WriteAttributeString("version", "1"); writer.WriteAttributeString("profile", snapshot.Profile);
                        writer.WriteAttributeString("exe", snapshot.Executable); writer.WriteAttributeString("createdUtc", snapshot.CreatedUtc);
                        foreach (var s in snapshot.Settings)
                        {
                            writer.WriteStartElement("setting"); writer.WriteAttributeString("id", s.Id.ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("exists", s.Exists.ToString()); writer.WriteAttributeString("type", s.Type.ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("location", s.Location.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("predefined", s.Predefined.ToString());
                            writer.WriteAttributeString("value", s.Value.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    stream.Flush(true);
                }
                File.Move(temp, path); // same-directory atomic rename, never overwrite the baseline
                return Load(snapshot.Profile);
            }
            catch (Exception ex) { throw new InvalidOperationException("无法保存原始备份，未更改驱动配置：" + path, ex); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class ProfileWorkflow
    {
        internal const uint OverrideId = 0x10E41E01, ModeId = 0x10AFB768;
        static readonly uint[] Tracked = { OverrideId, ModeId };
        readonly SnapshotStore store;
        internal ProfileWorkflow(SnapshotStore store) { this.store = store; }
        internal static bool IsTracked(uint id) { return id == OverrideId || id == ModeId; }
        internal static uint ModeFor(int preset) { if (preset < 0 || preset > 2) throw new InvalidOperationException("请选择画质、均衡或性能预设。"); return (uint)(2 - preset); }
        internal string Apply(IProfileSession session, string exe, int preset)
        {
            uint mode = ModeFor(preset);
            ProfileRecord profile = session.Find(exe);
            string name = profile == null ? "RTX Hub - " + Path.GetFileName(exe) + " - " + SnapshotStore.Hash(exe).Substring(0, 12) : profile.Name;
            var original = new SettingState[2];
            for (int i = 0; i < Tracked.Length; i++) original[i] = profile == null ? SettingState.Missing(Tracked[i]) : session.Get(profile, Tracked[i]);
            store.Preserve(new ProfileSnapshot { Profile = name, Executable = exe, CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Settings = original });
            if (profile == null) profile = session.Create(exe, name);
            session.Set(profile, OverrideId, 1);
            session.Set(profile, ModeId, mode);
            // Any exception before this point discards this session without SaveSettings.
            session.Save(); session.Reload(); profile = session.Find(exe);
            if (profile == null || !Matches(session.Get(profile, OverrideId), 1) || !Matches(session.Get(profile, ModeId), mode))
                throw new InvalidOperationException("驱动已返回保存成功，但重新读取未确认所选值。请使用原始备份恢复；不要假定游戏已生效。");
            return "已保存并重新读取确认：" + new[] { "画质", "均衡", "性能" }[preset] + "（DLSS SR）。\r\n" + Describe(session, profile) + "\r\n请完全退出并重启支持 DLSS 的游戏，在游戏内启用 DLSS。实际画面与性能尚未验证。";
        }
        internal string Restore(IProfileSession session, string exe)
        {
            ProfileRecord profile = session.Find(exe);
            if (profile == null) throw new InvalidOperationException("找不到此程序的 NVIDIA 配置文件，未进行更改。原始备份仍保留。");
            ProfileSnapshot snapshot = store.Load(profile.Name);
            if (snapshot == null) throw new InvalidOperationException("此配置文件没有 RTX Hub 原始备份，未进行更改。");
            foreach (var s in snapshot.Settings)
            {
                if (s.Explicit) session.Set(profile, s.Id, s.Value);
                else if (session.Get(profile, s.Id).Explicit) session.Delete(profile, s.Id);
            }
            session.Save(); session.Reload(); profile = session.Find(exe);
            if (profile == null) throw new InvalidOperationException("恢复后无法重新定位配置文件，请检查 NVIDIA 驱动。原始备份仍保留。");
            foreach (var s in snapshot.Settings)
            {
                var actual = session.Get(profile, s.Id);
                if (s.Explicit ? !Matches(actual, s.Value) : actual.Explicit) throw new InvalidOperationException("恢复已保存，但重新读取未确认原始状态。原始备份仍保留，请检查驱动。");
            }
            return "已恢复并重新读取确认原始 DLSS SR 设置。\r\n其他设置、应用关联及配置文件均保留。原始备份继续保留。\r\n" + Describe(session, profile);
        }
        static bool Matches(SettingState s, uint value) { return s.Explicit && s.Type == 0 && s.Value == value; }
        internal static string Describe(IProfileSession session, ProfileRecord profile)
        {
            string text = "驱动配置文件：" + profile.Name;
            if (profile.AppCount > 1) text += "\r\n注意：此配置文件关联 " + profile.AppCount + " 个程序，这些程序共用该配置。";
            foreach (var id in Tracked)
            {
                var s = session.Get(profile, id);
                string label = id == OverrideId ? "DLSS SR 覆盖" : "DLSS SR 模式";
                string value = !s.Exists ? "未设置 / 继承" : (id == OverrideId ? (s.Value == 1 ? "启用" : "关闭（" + s.Value + "）") : ModeName(s.Value));
                text += "\r\n" + label + "：" + value + (s.Exists ? (s.Explicit ? " [用户显式设置]" : (s.Predefined ? " [驱动预定义]" : " [继承]")) : "");
            }
            return text;
        }
        static string ModeName(uint value) { string[] names = { "性能", "均衡", "画质", "由游戏控制", "DLAA", "超级性能", "自定义" }; return value < names.Length ? names[value] + "（" + value + "）" : value.ToString(CultureInfo.InvariantCulture); }
    }

    // DRS_SETTING_V1 is pack(4). Its binary union has a DWORD length plus an
    // inline 4096-byte array (not a pointer), making each union 4100 bytes.
    // Header: 4 + WCHAR[2048] + 5 DWORD = 4120; unions at 4120/8220; sizeof=12320.
    internal sealed class NativeBuffer : IDisposable
    {
        internal readonly IntPtr Pointer;
        internal NativeBuffer(int size, int version) { Pointer = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, Pointer, size); Put(0, (uint)(size | (version << 16))); }
        internal void Put(int offset, uint value) { Marshal.WriteInt32(Pointer, offset, unchecked((int)value)); }
        internal uint Get(int offset) { return unchecked((uint)Marshal.ReadInt32(Pointer, offset)); }
        internal string Text(int offset) { return Marshal.PtrToStringUni(IntPtr.Add(Pointer, offset)) ?? ""; }
        internal void Text(int offset, string value) { byte[] bytes = Encoding.Unicode.GetBytes(value + "\0"); if (bytes.Length > 4096) throw new InvalidOperationException("NVAPI 字符串过长。"); Marshal.Copy(bytes, 0, IntPtr.Add(Pointer, offset), bytes.Length); }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); }
    }

    internal sealed class NativeProfileSession : IProfileSession
    {
        const int SettingSize = 12320, ProfileSize = 4116, AppSize = 12296;
        IntPtr library, session; bool initialized;
        QueryDelegate query;
        VoidDelegate unload;
        SessionDelegate destroy, load, save;
        ErrorDelegate error;
        FindDelegate find;
        ProfileDelegate info;
        CreateProfileDelegate createProfile;
        BufferDelegate createApp, set;
        GetDelegate get;
        DeleteDelegate delete;
        HandleDelegate baseProfile, globalProfile;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryDelegate(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int VoidDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleDelegate(IntPtr session, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSessionDelegate(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionDelegate(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ErrorDelegate(int status, StringBuilder message);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] delegate int FindDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string exe, out IntPtr profile, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileDelegate(IntPtr session, IntPtr profile, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateProfileDelegate(IntPtr session, IntPtr buffer, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BufferDelegate(IntPtr session, IntPtr profile, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDelegate(IntPtr session, IntPtr profile, uint id, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeleteDelegate(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DriverVersionDelegate(out uint version, StringBuilder branch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpuDelegate([Out] IntPtr[] handles, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuNameDelegate(IntPtr handle, StringBuilder name);
        T Bind<T>(uint id) where T : class
        {
            IntPtr address = query(id);
            if (address == IntPtr.Zero) throw new InvalidOperationException("此 NVIDIA 驱动不提供所需 NVAPI 接口：0x" + id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }
        internal NativeProfileSession()
        {
            try
            {
                if (IntPtr.Size != 8) throw new InvalidOperationException("此驱动模块需要 64 位进程。");
                string path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
                library = LoadLibraryEx(path, IntPtr.Zero, 0x00000800); // absolute Windows system DLL; system-only dependencies
                if (library == IntPtr.Zero) throw new InvalidOperationException("无法载入 Windows 系统 NVIDIA NVAPI。请检查独显驱动。系统错误：" + Marshal.GetLastWin32Error());
                IntPtr entry = GetProcAddress(library, "nvapi_QueryInterface");
                if (entry == IntPtr.Zero) throw new InvalidOperationException("系统 nvapi64.dll 缺少查询接口。");
                query = (QueryDelegate)Marshal.GetDelegateForFunctionPointer(entry, typeof(QueryDelegate));
                error = Bind<ErrorDelegate>(0x6c2d048c); unload = Bind<VoidDelegate>(0xd22bdd7e);
                Check(Bind<VoidDelegate>(0x0150e828)(), "初始化 NVAPI"); initialized = true;
                destroy = Bind<SessionDelegate>(0xdad9cff8); load = Bind<SessionDelegate>(0x375dbd6b); save = Bind<SessionDelegate>(0xfcbc7e14);
                find = Bind<FindDelegate>(0xeee566b2); info = Bind<ProfileDelegate>(0x61cd6fd6);
                createProfile = Bind<CreateProfileDelegate>(0xcc176068); createApp = Bind<BufferDelegate>(0x4347a9de);
                get = Bind<GetDelegate>(0x73bf8338); set = Bind<BufferDelegate>(0x577dd202); delete = Bind<DeleteDelegate>(0xe4a26362);
                baseProfile = Bind<HandleDelegate>(0xda8466a0); globalProfile = Bind<HandleDelegate>(0x617bff9f);
                Check(Bind<CreateSessionDelegate>(0x0694d52e)(out session), "创建驱动配置会话"); Reload();
            }
            catch { Dispose(); throw; }
        }
        void Check(int status, string action)
        {
            if (status == 0) return;
            var text = new StringBuilder(64);
            if (error != null) error(status, text);
            throw new InvalidOperationException(action + "失败（NVAPI " + status + "：" + text + "）。请检查 NVIDIA 驱动和权限；原始备份会保留。");
        }
        public ProfileRecord Find(string exe)
        {
            using (var app = new NativeBuffer(AppSize, 1))
            {
                IntPtr handle; int status = find(session, exe, out handle, app.Pointer);
                if (status == -166 || status == -163) return null;
                Check(status, "查找游戏配置");
                // Defend against accidental global targeting, even with an unexpected driver result.
                IntPtr baseHandle, globalHandle; Check(baseProfile(session, out baseHandle), "读取基础配置标识"); Check(globalProfile(session, out globalHandle), "读取全局配置标识");
                if (handle == IntPtr.Zero || handle == baseHandle || handle == globalHandle) throw new InvalidOperationException("驱动返回了全局或无效配置，已拒绝更改。请选择具体游戏程序。");
                using (var p = new NativeBuffer(ProfileSize, 1)) { Check(info(session, handle, p.Pointer), "读取配置文件信息"); return new ProfileRecord(p.Text(4), handle, p.Get(4108)); }
            }
        }
        public ProfileRecord Create(string exe, string name)
        {
            IntPtr handle;
            using (var p = new NativeBuffer(ProfileSize, 1)) { p.Text(4, name); Check(createProfile(session, p.Pointer, out handle), "创建独立游戏配置"); }
            using (var app = new NativeBuffer(AppSize, 1)) { app.Text(8, exe); app.Text(4104, Path.GetFileNameWithoutExtension(exe)); Check(createApp(session, handle, app.Pointer), "关联游戏程序"); }
            return new ProfileRecord(name, handle, 1);
        }
        public SettingState Get(ProfileRecord p, uint id)
        {
            using (var buffer = new NativeBuffer(SettingSize, 1))
            {
                int status = get(session, p.Handle, id, buffer.Pointer);
                if (status == -160) return SettingState.Missing(id);
                Check(status, "读取 DLSS SR 设置");
                if (buffer.Get(4104) != 0) throw new InvalidOperationException("驱动返回非 DWORD 的 DLSS SR 设置，已停止操作。");
                return new SettingState(id, true, buffer.Get(4104), buffer.Get(4108), buffer.Get(4112) != 0, buffer.Get(8220));
            }
        }
        public void Set(ProfileRecord p, uint id, uint value)
        {
            if (!ProfileWorkflow.IsTracked(id)) throw new InvalidOperationException("拒绝修改未受跟踪的驱动设置。");
            using (var buffer = new NativeBuffer(SettingSize, 1)) { buffer.Put(4100, id); buffer.Put(8220, value); Check(set(session, p.Handle, buffer.Pointer), "暂存 DLSS SR 设置"); }
        }
        public void Delete(ProfileRecord p, uint id) { if (!ProfileWorkflow.IsTracked(id)) throw new InvalidOperationException("拒绝删除未受跟踪的驱动设置。"); Check(delete(session, p.Handle, id), "移除 DLSS SR 用户覆盖"); }
        public void Save() { Check(save(session), "保存驱动配置"); }
        public void Reload() { Check(load(session), "重新读取驱动配置数据库"); }
        internal string ValidateSettingAbi()
        {
            IntPtr handle; Check(baseProfile(session, out handle), "读取基础配置标识");
            // Read-only DRS_GetSetting calls exercise the full setting ABI even if a
            // driver reports a setting absent. No global-setting write APIs exist here.
            var profile = new ProfileRecord("基础配置（仅读取）", handle, 0);
            Get(profile, ProfileWorkflow.OverrideId); Get(profile, ProfileWorkflow.ModeId);
            return "DLSS SR 设置结构只读检查通过（NVDRS_SETTING_V1 / 12320 字节）。";
        }
        internal string DescribeHardware()
        {
            uint version; var branch = new StringBuilder(64); Check(Bind<DriverVersionDelegate>(0x2926aaad)(out version, branch), "读取驱动版本");
            var result = new StringBuilder("NVIDIA 驱动 " + (version / 100) + "." + (version % 100).ToString("D2") + " / " + branch);
            IntPtr[] handles = new IntPtr[64]; uint count; Check(Bind<EnumGpuDelegate>(0xe5ac921f)(handles, out count), "枚举 NVIDIA 显卡");
            if (count > 64) throw new InvalidOperationException("驱动返回的显卡数量无效。");
            var gpuName = Bind<GpuNameDelegate>(0xceee8e9f);
            for (int i = 0; i < count; i++) { var name = new StringBuilder(64); Check(gpuName(handles[i], name), "读取显卡名称"); result.Append("\r\n").Append(name); }
            return result.ToString();
        }
        public void Dispose()
        {
            if (session != IntPtr.Zero && destroy != null) { destroy(session); session = IntPtr.Zero; }
            if (initialized && unload != null) { unload(); initialized = false; }
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
        }
    }
}
