using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RtxHub;

internal static class NeuralPackagesTests
{
    static int checks;
    static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception("FAIL: " + message); }
    static void Throws(Action action, string message) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Assert(failed, message); }
    static string Digest(string path) { using (var h = SHA256.Create()) using (var s = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    static string Zip(string root, string name, params string[] pairs)
    {
        string path = Path.Combine(root, name);
        using (var file = File.Create(path)) using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            for (int i = 0; i < pairs.Length; i += 2) using (var writer = new StreamWriter(archive.CreateEntry(pairs[i]).Open(), new UTF8Encoding(false))) writer.Write(pairs[i+1]);
        return path;
    }
    static NeuralPackageSpec Spec(string archive, string output, string member, bool nexus)
    {
        return new NeuralPackageSpec(Path.GetFileName(archive), "https://invalid.invalid/fixture", Digest(archive), new FileInfo(archive).Length, member, output, nexus);
    }
    static NeuralPackageStore Store(string root, params NeuralPackageSpec[] specs) { return new NeuralPackageStore(root, specs); }
    static string Fixture(string parent, string name) { string p=Path.Combine(parent,name); Directory.CreateDirectory(Path.Combine(p,"neural-downloads")); return p; }
    static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--prepare") { try { Console.WriteLine(NeuralPackages.Prepare(Console.WriteLine)); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; } }
        if(args.Length>0 && args[0]=="--verify") { try { Console.WriteLine(NeuralPackages.Verify()); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; } }
        string root=Path.Combine(Path.GetTempPath(),"NeuralPackages-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            if(args.Length>1 && args[0]=="--check-reshade")
            {
                string actual=Fixture(root,"actual-reshade");var official=NeuralPackages.Catalog()[0];
                File.Copy(args[1],Path.Combine(actual,"neural-downloads",official.Name));var actualStore=Store(actual,official);
                Console.WriteLine(actualStore.Prepare(Console.WriteLine,false));Console.WriteLine(actualStore.Verify());return 0;
            }
            // Losing the member hash check would let altered executable bytes pass verification.
            string good=Fixture(root,"good"); string cache=Path.Combine(good,"neural-downloads");
            string archive=Zip(cache,"nexus.zip","renodx-dlss5.addon64","native fixture","LICENSE","MIT fixture","licenses/RenoDX/LICENSE","upstream fixture","docs/guide.md","guide fixture");
            var spec=Spec(archive,"renodx-dlss5.addon64","renodx-dlss5.addon64",true); var store=Store(good,spec);
            store.Prepare(null,false);
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"renodx-dlss5.addon64"))=="native fixture","verified archive member is deployed");
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"docs/nexus/licenses/RenoDX/LICENSE"))=="upstream fixture","complete nested Nexus license tree is retained");
            Assert(store.Verify().Length>0,"fresh bundle verifies against pinned archive bytes");
            File.WriteAllText(Path.Combine(store.Bundle,"renodx-dlss5.addon64"),"changed");
            Throws(delegate{store.Verify();},"altered bundle member must fail verification");
            store.Prepare(null,false); Assert(File.ReadAllText(Path.Combine(store.Bundle,"renodx-dlss5.addon64"))=="native fixture","reprepare repairs from verified offline cache");

            // Trusting an editable local manifest or cache file would hide substitution.
            File.WriteAllText(archive,"changed cache");
            Throws(delegate{store.Verify();},"cache substitution cannot authenticate a bundle");
            Throws(delegate{store.Prepare(null,false);},"invalid cache must not be deployed in offline mode");
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"renodx-dlss5.addon64"))=="native fixture","failed preparation leaves the previous bundle intact");

            // A traversal anywhere in an archive is rejected before a destination file is written.
            foreach(string name in new[]{"../escaped.dll","folder\\..\\escaped.dll","C:/escaped.dll","/absolute.dll","safe/CON.txt","safe/name. ","safe/name:stream"})
            {
                string f=Fixture(root,Guid.NewGuid().ToString("N")); string z=Zip(Path.Combine(f,"neural-downloads"),"bad.zip","payload.dll","legit",name,"bad");
                var s=Store(f,Spec(z,"dxgi.dll","payload.dll",false));
                Throws(delegate{s.Prepare(null,false);},"unsafe ZIP member rejected: "+name);
                Assert(!Directory.Exists(s.Bundle),"unsafe ZIP never publishes a bundle");
            }
            string dup=Fixture(root,"duplicate"); string dz=Zip(Path.Combine(dup,"neural-downloads"),"dupe.zip","payload.dll","first","PAYLOAD.DLL","second");
            Throws(delegate{Store(dup,Spec(dz,"dxgi.dll","payload.dll",false)).Prepare(null,false);},"Windows case-insensitive duplicate members are rejected");
            string missing=Fixture(root,"missing"); string mz=Zip(Path.Combine(missing,"neural-downloads"),"missing.zip","other.dll","wrong");
            Throws(delegate{Store(missing,Spec(mz,"dxgi.dll","payload.dll",false)).Prepare(null,false);},"missing required runtime prevents publication");
            // ReShade's signed installer has prepended PE data and a false PK signature before the real ZIP.
            string sfx=Fixture(root,"sfx");string sz=Zip(Path.Combine(sfx,"neural-downloads"),"setup.exe","ReShade64.dll","loader fixture");
            byte[] zipBytes=File.ReadAllBytes(sz);byte[] executable=new byte[1024+zipBytes.Length];executable[0]=77;executable[1]=90;
            executable[128]=80;executable[129]=75;executable[130]=3;executable[131]=4;Buffer.BlockCopy(zipBytes,0,executable,1024,zipBytes.Length);File.WriteAllBytes(sz,executable);
            var sfxStore=Store(sfx,Spec(sz,"dxgi.dll","ReShade64.dll",false));sfxStore.Prepare(null,false);
            Assert(File.ReadAllText(Path.Combine(sfxStore.Bundle,"dxgi.dll"))=="loader fixture","SFX ZIP is read as data despite a preceding false ZIP signature");
            // Authenticode certificates follow ReShade's ZIP and contain unrelated bytes.
            string signed=Fixture(root,"signed-sfx");string signedZip=Zip(Path.Combine(signed,"neural-downloads"),"signed.exe","ReShade64.dll","signed loader fixture");
            byte[] signedBytes=File.ReadAllBytes(signedZip);byte[] signedExe=new byte[153088+signedBytes.Length+8192];signedExe[0]=77;signedExe[1]=90;
            signedExe[128]=80;signedExe[129]=75;signedExe[130]=3;signedExe[131]=4;Buffer.BlockCopy(signedBytes,0,signedExe,153088,signedBytes.Length);File.WriteAllBytes(signedZip,signedExe);
            var signedStore=Store(signed,Spec(signedZip,"dxgi.dll","ReShade64.dll",false));signedStore.Prepare(null,false);
            Assert(File.ReadAllText(Path.Combine(signedStore.Bundle,"dxgi.dll"))=="signed loader fixture","signed SFX with trailing certificate data is extracted without executing");
            Console.WriteLine("PASS: "+checks+" package cache/extraction/integrity checks; no DLL executed."); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { string full=Path.GetFullPath(root); if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) Directory.Delete(full,true); }
    }
}
