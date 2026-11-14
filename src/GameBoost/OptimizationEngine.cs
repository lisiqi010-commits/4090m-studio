using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameBoost
{
    internal sealed class OptimizationSnapshot
    {
        public List<AppUsage> Apps { get; private set; }
        public int AdjustedCount { get; internal set; }
        public int SkippedCount { get; internal set; }
        public int PendingRestoreCount { get; internal set; }
        public string ActiveGamePath { get; internal set; }
        public bool IsOptimizationActive { get; internal set; }

        public OptimizationSnapshot()
        {
            Apps = new List<AppUsage>();
        }
    }

    internal sealed class AppUsage
    {
        public string Name { get; internal set; }
        public string Path { get; internal set; }
        public int ProcessCount { get; internal set; }
        public double CpuPercent { get; internal set; }
        public long WorkingSetBytes { get; internal set; }
        public bool IsDefault { get; internal set; }
        public bool IsAdjusted { get; internal set; }
        public bool IsForeground { get; internal set; }
        public bool IsProtected { get; internal set; }
    }

    internal sealed class OptimizationEngine
    {
        private const uint QueryProcess = 0x1000;
        private const uint SetProcess = 0x0200;
        private const uint SynchronizeProcess = 0x100000;
        private const uint WaitObject = 0;
        private const uint QueryToken = 0x0008;
        private const uint BelowNormal = (uint)ProcessPriorityClass.BelowNormal;
        private const int InvalidParameter = 87;
        private const int InsufficientBuffer = 122;
        private const uint ReplaceExisting = 0x1;
        private const uint WriteThrough = 0x8;

        private readonly object sync = new object();
        private readonly string journalPath;
        private readonly string windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private readonly int ownPid;
        private readonly uint ownSession;
        private readonly string ownPath;
        private readonly SecurityIdentifier ownUser;
        private readonly Dictionary<string, List<string>> defaults =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, PriorityChange> changes = new Dictionary<int, PriorityChange>();
        private readonly Dictionary<int, long> externalOverrides = new Dictionary<int, long>();
        private Dictionary<int, Sample> previousSamples = new Dictionary<int, Sample>();
        private bool journalDirty;

        public OptimizationEngine(string journalPath)
        {
            this.journalPath = Path.GetFullPath(journalPath);
            using (Process process = Process.GetCurrentProcess())
            {
                ownPid = process.Id;
                ownSession = (uint)process.SessionId;
            }

            using (ProcessHandle handle = OpenProcess(QueryProcess, false, ownPid))
            {
                ownPath = ReadImagePath(handle);
                ownUser = ReadProcessUser(handle);
            }

            string localApps = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddDefault("OneDrive.exe", localApps, @"Microsoft\OneDrive");
            AddDefault("Dropbox.exe", localApps, "Dropbox");
            AddDefault("quark_cloud_drive.exe", localApps, @"Programs\QuarkCloudDrive");
            AddProgramFilesDefaults(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddProgramFilesDefaults(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddProgramFilesDefaults(Environment.GetEnvironmentVariable("ProgramW6432"));
            LoadJournal();
        }

        public OptimizationSnapshot Scan(ICollection<string> customPaths, bool includeDefaults, bool optimize,
            ICollection<string> excludedDefaults = null, ICollection<string> protectedPaths = null)
        {
            lock (sync)
            {
                Foreground foreground = ReadForeground();
                Dictionary<int, Sample> samples = CollectSamples(foreground);
                return ApplySelection(samples, foreground, customPaths, includeDefaults, excludedDefaults,
                    NormalizePaths(protectedPaths), new OptimizationSnapshot { IsOptimizationActive = optimize });
            }
        }

        public OptimizationSnapshot ScanGames(ICollection<GameProfile> profiles, bool enabled)
        {
            lock (sync)
            {
                Foreground foreground = ReadForeground();
                Dictionary<int, Sample> samples = CollectSamples(foreground);
                var runningPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Sample sample in samples.Values)
                    runningPaths.Add(sample.Path);

                var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new OptimizationSnapshot();
                GameProfile active = null;
                foreach (GameProfile profile in profiles)
                {
                    string path = NormalizeExecutablePath(profile.GamePath);
                    if (path == null)
                        continue;

                    protectedPaths.Add(path);
                    if (active == null && runningPaths.Contains(path))
                    {
                        active = profile;
                        result.ActiveGamePath = path;
                    }
                }

                if (active == null)
                    return ApplySelection(samples, foreground, null, false, null, protectedPaths, result);

                protectedPaths.UnionWith(NormalizePaths(active.ProtectedPaths));
                result.IsOptimizationActive = enabled;
                return ApplySelection(samples, foreground, active.Paths, active.IncludeDefaults,
                    active.ExcludedDefaults, protectedPaths, result);
            }
        }

        public int Restore()
        {
            lock (sync)
            {
                externalOverrides.Clear();
                foreach (PriorityChange change in new List<PriorityChange>(changes.Values))
                {
                    if (TryRestore(change))
                        ForgetChange(change.Pid);
                }

                SaveJournal();
                return changes.Count;
            }
        }

        private static HashSet<string> NormalizePaths(ICollection<string> paths)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths == null)
                return normalized;

            foreach (string path in paths)
            {
                string fullPath = NormalizeExecutablePath(path);
                if (fullPath != null)
                    normalized.Add(fullPath);
            }
            return normalized;
        }

        private OptimizationSnapshot ApplySelection(Dictionary<int, Sample> samples, Foreground foreground,
            ICollection<string> customPaths, bool includeDefaults, ICollection<string> excludedDefaults,
            HashSet<string> protectedPaths, OptimizationSnapshot result)
        {
            HashSet<string> selected = NormalizePaths(customPaths);
            HashSet<string> excluded = NormalizePaths(excludedDefaults);
            foreach (Sample sample in samples.Values)
            {
                sample.Protected = protectedPaths.Contains(sample.Path);
                sample.Selected = !sample.Protected && (selected.Contains(sample.Path) ||
                    (includeDefaults && sample.Default && !excluded.Contains(sample.Path)));
            }

            bool optimize = result.IsOptimizationActive && foreground.Resolved;
            ReconcileChanges(samples, optimize, result);
            if (!result.IsOptimizationActive)
                externalOverrides.Clear();
            RemoveOldOverrides(samples);

            var grouped = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (Sample sample in samples.Values)
            {
                if (optimize && sample.Selected && !sample.Foreground &&
                    !changes.ContainsKey(sample.Pid) && !externalOverrides.ContainsKey(sample.Pid))
                {
                    bool unreadable = sample.Priority == 0;
                    bool applyFailed = CanLower(sample.Priority) && !TryApply(sample);
                    if (unreadable || applyFailed)
                        result.SkippedCount++;
                }

                AddUsage(grouped, sample, result);
            }

            SaveJournal();
            previousSamples = samples;
            result.Apps.AddRange(grouped.Values);
            result.Apps.Sort(CompareUsage);
            return result;
        }

        private Dictionary<int, Sample> CollectSamples(Foreground foreground)
        {
            var samples = new Dictionary<int, Sample>();
            var pathBuffer = new StringBuilder(512);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    int pid = process.Id;
                    if (pid == ownPid || ownSession == 0 || ownUser == null)
                        continue;

                    using (ProcessHandle handle = OpenProcess(QueryProcess, false, pid))
                    {
                        uint session;
                        if (handle.IsInvalid || !ProcessIdToSessionId(pid, out session) || session != ownSession)
                            continue;

                        string path = ReadImagePath(handle, pathBuffer);
                        long creation;
                        long cpuTicks;
                        if (!IsEligiblePath(path) || !ownUser.Equals(ReadProcessUser(handle)) ||
                            !ReadTimes(handle, out creation, out cpuTicks))
                            continue;

                        long timestamp = Stopwatch.GetTimestamp();
                        Sample sample = new Sample
                        {
                            Pid = pid,
                            Creation = creation,
                            CpuTicks = cpuTicks,
                            Timestamp = timestamp,
                            WorkingSet = ReadWorkingSet(handle),
                            Path = path,
                            Default = IsDefaultPath(path),
                            Foreground = foreground.Matches(path),
                            Priority = GetPriorityClass(handle)
                        };
                        samples.Add(pid, sample);
                    }
                }
            }

            return samples;
        }

        private void ReconcileChanges(Dictionary<int, Sample> samples, bool optimize, OptimizationSnapshot result)
        {
            foreach (PriorityChange change in new List<PriorityChange>(changes.Values))
            {
                Sample sample;
                bool found = samples.TryGetValue(change.Pid, out sample);
                if (found && (sample.Creation != change.Creation || !SamePath(sample.Path, change.Path)))
                {
                    ForgetChange(change.Pid);
                    continue;
                }

                if (found && optimize && sample.Selected && !sample.Foreground)
                {
                    if (sample.Priority == BelowNormal)
                        continue;
                    if (sample.Priority == 0)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    // Preserve a priority changed by the application or by another tool.
                    externalOverrides[change.Pid] = change.Creation;
                    ForgetChange(change.Pid);
                    continue;
                }

                bool externallyChanged;
                if (!TryRestore(change, out externallyChanged))
                {
                    result.PendingRestoreCount++;
                    continue;
                }

                if (found && externallyChanged && optimize && sample.Selected)
                    externalOverrides[change.Pid] = change.Creation;
                ForgetChange(change.Pid);
            }
        }

        private void RemoveOldOverrides(Dictionary<int, Sample> samples)
        {
            foreach (int pid in new List<int>(externalOverrides.Keys))
            {
                Sample sample;
                if (!samples.TryGetValue(pid, out sample) || sample.Creation != externalOverrides[pid] || !sample.Selected)
                    externalOverrides.Remove(pid);
            }
        }

        private bool TryApply(Sample sample)
        {
            using (ProcessHandle handle = OpenProcess(QueryProcess | SetProcess, false, sample.Pid))
            {
                if (handle.IsInvalid)
                    return Marshal.GetLastWin32Error() == InvalidParameter;

                if (!MatchesIdentity(handle, sample.Creation, sample.Path))
                    return true;

                uint original = GetPriorityClass(handle);
                if (!CanLower(original))
                    return original != 0;
                if (!ReadForeground().Allows(sample.Path))
                    return true;

                // Record the original priority before changing the live process.
                changes.Add(sample.Pid, new PriorityChange(sample.Pid, sample.Creation, sample.Path, original));
                journalDirty = true;
                try
                {
                    SaveJournal();
                }
                catch
                {
                    ForgetChange(sample.Pid);
                    throw;
                }

                Foreground foreground = ReadForeground();
                uint current = GetPriorityClass(handle);
                if (!foreground.Allows(sample.Path) || current != original)
                {
                    ForgetChange(sample.Pid);
                    return current != 0;
                }

                if (!SetPriorityClass(handle, BelowNormal))
                {
                    ForgetChange(sample.Pid);
                    return false;
                }

                sample.Priority = BelowNormal;
                return true;
            }
        }

        private bool TryRestore(PriorityChange change)
        {
            bool externallyChanged;
            return TryRestore(change, out externallyChanged);
        }

        private bool TryRestore(PriorityChange change, out bool externallyChanged)
        {
            externallyChanged = false;
            if (change.Pid == ownPid || !IsEligiblePath(change.Path))
                return true;

            using (ProcessHandle handle = OpenProcess(QueryProcess | SynchronizeProcess, false, change.Pid))
            {
                if (handle.IsInvalid)
                    return Marshal.GetLastWin32Error() == InvalidParameter;
                if (WaitForSingleObject(handle, 0) == WaitObject)
                    return true;

                long creation;
                long cpuTicks;
                if (!ReadTimes(handle, out creation, out cpuTicks))
                    return false;
                if (creation != change.Creation)
                    return true;

                string path = ReadImagePath(handle);
                SecurityIdentifier user = ReadProcessUser(handle);
                if (path == null || user == null || ownUser == null)
                    return false;
                if (!SamePath(path, change.Path) || !ownUser.Equals(user))
                    return true;

                uint session;
                if (!ProcessIdToSessionId(change.Pid, out session) || session != ownSession || ownSession == 0)
                    return false;

                uint current = GetPriorityClass(handle);
                if (current == 0)
                    return false;
                if (current != BelowNormal)
                {
                    externallyChanged = true;
                    return true;
                }

                using (ProcessHandle writable = OpenProcess(QueryProcess | SetProcess, false, change.Pid))
                {
                    if (writable.IsInvalid)
                        return Marshal.GetLastWin32Error() == InvalidParameter;
                    if (!MatchesIdentity(writable, change.Creation, change.Path))
                        return false;

                    current = GetPriorityClass(writable);
                    if (current == 0)
                        return false;
                    if (current != BelowNormal)
                    {
                        externallyChanged = true;
                        return true;
                    }

                    return SetPriorityClass(writable, change.Original) || WaitForSingleObject(handle, 0) == WaitObject;
                }
            }
        }

        private bool MatchesIdentity(ProcessHandle handle, long expectedCreation, string expectedPath)
        {
            long creation;
            long cpuTicks;
            return ReadTimes(handle, out creation, out cpuTicks) && creation == expectedCreation &&
                SamePath(expectedPath, ReadImagePath(handle)) && ownUser.Equals(ReadProcessUser(handle));
        }

        private static bool CanLower(uint priority)
        {
            return priority == (uint)ProcessPriorityClass.Normal ||
                priority == (uint)ProcessPriorityClass.AboveNormal ||
                priority == (uint)ProcessPriorityClass.High;
        }

        private void AddUsage(Dictionary<string, AppUsage> grouped, Sample sample, OptimizationSnapshot result)
        {
            AppUsage app;
            if (!grouped.TryGetValue(sample.Path, out app))
            {
                app = new AppUsage
                {
                    Path = sample.Path,
                    Name = Path.GetFileNameWithoutExtension(sample.Path),
                    IsDefault = sample.Default,
                    IsForeground = sample.Foreground,
                    IsProtected = sample.Protected
                };
                grouped.Add(sample.Path, app);
            }

            app.ProcessCount++;
            app.WorkingSetBytes += sample.WorkingSet;
            Sample previous;
            if (previousSamples.TryGetValue(sample.Pid, out previous) &&
                previous.Creation == sample.Creation && previous.Timestamp < sample.Timestamp)
            {
                double elapsed = (double)(sample.Timestamp - previous.Timestamp) / Stopwatch.Frequency;
                double cpuSeconds = (double)(sample.CpuTicks - previous.CpuTicks) / TimeSpan.TicksPerSecond;
                app.CpuPercent += Math.Max(0, cpuSeconds / elapsed / Environment.ProcessorCount * 100);
            }

            if (sample.Priority == BelowNormal && changes.ContainsKey(sample.Pid))
            {
                result.AdjustedCount++;
                app.IsAdjusted = true;
            }
        }

        private static int CompareUsage(AppUsage first, AppUsage second)
        {
            int order = second.CpuPercent.CompareTo(first.CpuPercent);
            if (order == 0)
                order = second.WorkingSetBytes.CompareTo(first.WorkingSetBytes);
            if (order == 0)
                order = StringComparer.OrdinalIgnoreCase.Compare(first.Path, second.Path);
            return order;
        }

        private void AddProgramFilesDefaults(string root)
        {
            AddDefault("OneDrive.exe", root, "Microsoft OneDrive");
            AddDefault("Dropbox.exe", root, "Dropbox");
            AddDefault("GoogleDriveFS.exe", root, @"Google\Drive File Stream");
        }

        private void AddDefault(string file, string root, string folder)
        {
            if (String.IsNullOrEmpty(root))
                return;

            List<string> locations;
            if (!defaults.TryGetValue(file, out locations))
            {
                locations = new List<string>();
                defaults.Add(file, locations);
            }

            locations.Add(Path.Combine(root, folder));
        }

        private bool IsDefaultPath(string path)
        {
            List<string> locations;
            if (!defaults.TryGetValue(Path.GetFileName(path), out locations))
                return false;
            return locations.Exists(location => IsInside(path, location));
        }

        private bool IsEligiblePath(string path)
        {
            return path != null && !String.IsNullOrEmpty(windowsPath) && !IsInside(path, windowsPath) &&
                !SamePath(path, ownPath);
        }

        private static bool IsInside(string path, string directory)
        {
            return path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return null;

            try
            {
                string fullPath = Path.GetFullPath(path);
                return String.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
                    ? fullPath : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private void LoadJournal()
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(journalPath, Encoding.UTF8);
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }

            foreach (string line in lines)
            {
                string[] fields = line.Split(new[] { '\t' }, 4);
                int pid;
                long creation;
                uint original;
                if (fields.Length != 4 ||
                    !Int32.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0 ||
                    !Int64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out creation) || creation <= 0 ||
                    !UInt32.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out original) || !CanLower(original))
                    continue;

                string path = NormalizeExecutablePath(fields[3]);
                if (path != null)
                    changes[pid] = new PriorityChange(pid, creation, path, original);
            }
        }

        private void ForgetChange(int pid)
        {
            changes.Remove(pid);
            journalDirty = true;
        }

        private void SaveJournal()
        {
            if (!journalDirty)
                return;
            if (changes.Count == 0)
            {
                File.Delete(journalPath);
                journalDirty = false;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            string temporary = journalPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (PriorityChange change in changes.Values)
                {
                    writer.WriteLine(String.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}",
                        change.Pid, change.Creation, change.Original, change.Path));
                }
                writer.Flush();
                stream.Flush(true);
            }

            if (!MoveFileEx(temporary, journalPath, ReplaceExisting | WriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            journalDirty = false;
        }

        private static Foreground ReadForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return new Foreground(true, null);

            int pid;
            GetWindowThreadProcessId(window, out pid);
            using (ProcessHandle handle = OpenProcess(QueryProcess, false, pid))
            {
                string path = ReadImagePath(handle);
                return new Foreground(path != null, path);
            }
        }

        private static string ReadImagePath(ProcessHandle handle, StringBuilder buffer = null)
        {
            if (handle.IsInvalid)
                return null;
            if (buffer == null)
                buffer = new StringBuilder(512);

            while (buffer.Capacity <= 32768)
            {
                buffer.Clear();
                int length = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref length))
                    return NormalizeExecutablePath(buffer.ToString());
                if (Marshal.GetLastWin32Error() != InsufficientBuffer || buffer.Capacity == 32768)
                    break;
                buffer.EnsureCapacity(buffer.Capacity * 2);
            }

            return null;
        }

        private static SecurityIdentifier ReadProcessUser(ProcessHandle handle)
        {
            if (handle.IsInvalid)
                return null;

            ProcessHandle token;
            if (!OpenProcessToken(handle, QueryToken, out token))
            {
                if (token != null)
                    token.Dispose();
                return null;
            }

            using (token)
            {
                try
                {
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        return identity.User;
                }
                catch (System.Security.SecurityException) { return null; }
                catch (ArgumentException) { return null; }
            }
        }

        private static bool ReadTimes(ProcessHandle handle, out long creation, out long cpuTicks)
        {
            long exit;
            long kernel;
            long user;
            bool success = GetProcessTimes(handle, out creation, out exit, out kernel, out user);
            cpuTicks = kernel + user;
            return success;
        }

        private static long ReadWorkingSet(ProcessHandle handle)
        {
            ProcessMemoryCounters counters = new ProcessMemoryCounters();
            counters.Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
            return GetProcessMemoryInfo(handle, ref counters, counters.Size)
                ? (long)counters.WorkingSetSize.ToUInt64() : 0;
        }

        private sealed class PriorityChange
        {
            public readonly int Pid;
            public readonly long Creation;
            public readonly string Path;
            public readonly uint Original;

            public PriorityChange(int pid, long creation, string path, uint original)
            {
                Pid = pid;
                Creation = creation;
                Path = path;
                Original = original;
            }
        }

        private sealed class Sample
        {
            public int Pid;
            public long Creation;
            public long CpuTicks;
            public long Timestamp;
            public long WorkingSet;
            public string Path;
            public bool Default;
            public bool Selected;
            public bool Foreground;
            public bool Protected;
            public uint Priority;
        }

        private struct Foreground
        {
            public readonly bool Resolved;
            private readonly string path;

            public Foreground(bool resolved, string path)
            {
                Resolved = resolved;
                this.path = path;
            }

            public bool Matches(string candidate)
            {
                return SamePath(path, candidate);
            }

            public bool Allows(string candidate)
            {
                return Resolved && !Matches(candidate);
            }
        }

        private sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public ProcessHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ProcessHandle OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(ProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string currentPath, string newPath, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out uint session);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(ProcessHandle process, int flags, StringBuilder image, ref int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(ProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(ProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(ProcessHandle process, uint priority);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(ProcessHandle process, uint access, out ProcessHandle token);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(ProcessHandle process, ref ProcessMemoryCounters counters, uint size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int pid);
    }
}
