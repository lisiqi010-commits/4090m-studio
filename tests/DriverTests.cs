using System;
using System.Collections.Generic;
using System.IO;
using RtxHub;

// Hardware is an external dependency; this fake stages a separate database, just as DRS does.
internal sealed class MemoryDriver : IProfileSession
{
    internal readonly Dictionary<uint, SettingState> Saved = new Dictionary<uint, SettingState>();
    internal readonly Dictionary<uint, SettingState> Staged = new Dictionary<uint, SettingState>();
    internal ProfileRecord Profile = new ProfileRecord("测试配置", new IntPtr(42), 2);
    internal int SaveCount, WriteCount;
    internal uint FailId;
    internal bool DropModeOnReload;
    internal bool BackupSeenBeforeFirstWrite;
    internal string ExpectedBackup;
    public ProfileRecord Find(string exe) { return Profile; }
    public ProfileRecord Create(string exe, string name) { Profile = new ProfileRecord(name, new IntPtr(42), 1); return Profile; }
    public SettingState Get(ProfileRecord p, uint id) { SettingState s; return Staged.TryGetValue(id, out s) ? s.Copy() : SettingState.Missing(id); }
    public void Set(ProfileRecord p, uint id, uint value)
    {
        if (WriteCount++ == 0) BackupSeenBeforeFirstWrite = File.Exists(ExpectedBackup);
        if (id == FailId) throw new InvalidOperationException("模拟驱动拒绝设置");
        Staged[id] = new SettingState(id, true, 0, 0, false, value);
    }
    public void Delete(ProfileRecord p, uint id) { WriteCount++; Staged.Remove(id); }
    public void Save() { SaveCount++; Saved.Clear(); foreach (var v in Staged) Saved[v.Key] = v.Value.Copy(); }
    public void Reload() { Staged.Clear(); foreach (var v in Saved) if (!DropModeOnReload || v.Key != 0x10AFB768) Staged[v.Key] = v.Value.Copy(); }
    public void Dispose() { }
    internal void Seed(SettingState s) { Saved[s.Id] = s.Copy(); Staged[s.Id] = s.Copy(); }
}

internal static class DriverTests
{
    static int checks;
    static void Assert(bool value, string message) { checks++; if (!value) throw new Exception("FAIL: " + message); }
    static void Throws(Action action, string message) { bool threw = false; try { action(); } catch (InvalidOperationException) { threw = true; } Assert(threw, message); }
    static MemoryDriver NewDriver(SnapshotStore store) { var d = new MemoryDriver(); d.ExpectedBackup = store.PathFor(d.Profile.Name); return d; }
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe") { Console.WriteLine(DriverProfiles.Probe()); return 0; }
        string dir = Path.Combine(Path.GetTempPath(), "RtxHub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SnapshotStore(dir);
            var workflow = new ProfileWorkflow(store);
            var d = NewDriver(store);
            d.Seed(new SettingState(0x10AFB768, true, 0, 0, false, 4));
            d.Seed(new SettingState(0x10E41E01, true, 0, 2, false, 0));
            d.Seed(new SettingState(0x12345678, true, 0, 0, false, 99));
            workflow.Apply(d, @"C:\游戏\game.exe", 0);
            Assert(d.BackupSeenBeforeFirstWrite, "durable baseline must exist before the first driver setting");
            Assert(d.Saved[0x10AFB768].Value == 2 && d.Saved[0x10E41E01].Value == 1, "quality writes official SR quality and enable DWORDs");
            string originalXml = File.ReadAllText(d.ExpectedBackup);
            workflow.Apply(d, @"C:\其他目录\sibling.exe", 2);
            Assert(d.Saved[0x10AFB768].Value == 0, "performance writes mode 0");
            Assert(File.ReadAllText(d.ExpectedBackup) == originalXml, "shared-profile and repeated applies preserve original snapshot bytes");
            workflow.Apply(d, @"C:\游戏\game.exe", 1);
            Assert(d.Saved[0x10AFB768].Value == 1, "balanced writes mode 1");
            workflow.Restore(d, @"C:\游戏\game.exe");
            Assert(d.Saved[0x10AFB768].Value == 4, "restore reinstates original explicit DWORD");
            Assert(!d.Saved.ContainsKey(0x10E41E01), "restore removes override for originally inherited value");
            Assert(d.Saved[0x12345678].Value == 99, "restore preserves untracked settings");
            Assert(d.Profile != null && d.Profile.AppCount == 2, "restore keeps profile and its other applications");
            Assert(File.Exists(d.ExpectedBackup), "restore keeps original baseline for future preset changes");
            Assert(Directory.GetFiles(dir, "*.tmp").Length == 0, "atomic baseline write leaves no temporary file");

            var dir2 = Path.Combine(dir, "stage-failure"); var store2 = new SnapshotStore(dir2); var d2 = NewDriver(store2);
            d2.FailId = 0x10AFB768;
            Throws(delegate { new ProfileWorkflow(store2).Apply(d2, @"C:\游戏\game.exe", 0); }, "staging failure is reported");
            Assert(d2.SaveCount == 0 && d2.Saved.Count == 0, "staging failure never saves a partial database");
            Assert(File.Exists(d2.ExpectedBackup), "failure retains recovery baseline");

            var dir3 = Path.Combine(dir, "predefined"); var store3 = new SnapshotStore(dir3); var d3 = NewDriver(store3);
            d3.Seed(new SettingState(0x10AFB768, true, 0, 0, true, 3));
            new ProfileWorkflow(store3).Apply(d3, @"C:\游戏\game.exe", 0);
            new ProfileWorkflow(store3).Restore(d3, @"C:\游戏\game.exe");
            Assert(!d3.Saved.ContainsKey(0x10AFB768), "predefined baseline must not become an explicit user override");

            File.WriteAllText(d.ExpectedBackup, "<snapshot version=\"1\" profile=\"wrong profile\"/>");
            int writes = d.WriteCount;
            Throws(delegate { workflow.Apply(d, @"C:\游戏\game.exe", 0); }, "corrupt or wrong-profile baseline blocks mutation");
            Assert(d.WriteCount == writes, "invalid backup causes no driver staging");
            Throws(delegate { workflow.Apply(d, @"C:\游戏\game.exe", 9); }, "invalid preset is rejected");

            string blockedDirectory = Path.Combine(dir, "file-not-directory");
            File.WriteAllText(blockedDirectory, "existing file");
            var blockedStore = new SnapshotStore(blockedDirectory); var blockedDriver = NewDriver(blockedStore);
            Throws(delegate { new ProfileWorkflow(blockedStore).Apply(blockedDriver, @"C:\游戏\game.exe", 0); }, "backup failure gives a useful application error");
            Assert(blockedDriver.WriteCount == 0 && blockedDriver.SaveCount == 0, "backup failure must happen before staging or saving");

            var readbackStore = new SnapshotStore(Path.Combine(dir, "readback-mismatch")); var readbackDriver = NewDriver(readbackStore);
            readbackDriver.DropModeOnReload = true;
            Throws(delegate { new ProfileWorkflow(readbackStore).Apply(readbackDriver, @"C:\游戏\game.exe", 0); }, "successful SaveSettings still requires fresh readback confirmation");
            Assert(File.Exists(readbackDriver.ExpectedBackup), "readback mismatch keeps original recovery snapshot");
            Console.WriteLine("PASS: " + checks + " driver/backup checks; no real driver writes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        finally { Directory.Delete(dir, true); }
    }
}
