using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RtxHub;

internal sealed class FrameGenArchiveFixture : IFrameGenArchive
{
    internal Dictionary<string,byte[]> Files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
    internal string[] Names;
    internal int Extractions;
    public string[] List(string archive){return Names??new List<string>(Files.Keys).ToArray();}
    public void Extract(string archive,string destination,string[] members)
    {
        Extractions++;
        foreach(string member in members){byte[] bytes;if(!Files.TryGetValue(member,out bytes))throw new InvalidOperationException("missing fixture member");string output=Path.Combine(destination,member.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllBytes(output,bytes);}
    }
}

internal static class FrameGenerationPackagesTests
{
    static int checks;
    static void Assert(bool value,string message){checks++;if(!value)throw new Exception("FAIL: "+message);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Assert(rejected,message);}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static byte[] Pe(){var b=new byte[512];b[0]=77;b[1]=90;b[0x3c]=64;b[64]=80;b[65]=69;b[68]=0x64;b[69]=0x86;b[84]=0xf0;b[86]=0x02;b[87]=0x20;b[88]=0x0b;b[89]=0x02;return b;}
    static FrameGenPackageSpec Spec(FrameGenArchiveFixture reader)
    {
        byte[] zip=Encoding.UTF8.GetBytes("pinned archive fixture"),license=Encoding.UTF8.GetBytes("GPL fixture");
        return new FrameGenPackageSpec(new FrameGenDownload("aurora.7z","https://invalid.invalid/aurora.7z",null,zip.Length,Hash(zip)),new FrameGenDownload("GPL.txt","https://invalid.invalid/LICENSE",null,license.Length,Hash(license)),new[]{new FrameGenMember("OptiScaler.dll","OptiScaler.dll",reader.Files["OptiScaler.dll"].Length,Hash(reader.Files["OptiScaler.dll"]),true),new FrameGenMember("Licenses/NOTICE.txt","docs/NOTICE.txt",reader.Files["Licenses/NOTICE.txt"].Length,Hash(reader.Files["Licenses/NOTICE.txt"]),false)},"fixture sources\r\n");
    }
    static FrameGenArchiveFixture Reader(){var r=new FrameGenArchiveFixture();r.Files.Add("OptiScaler.dll",Pe());r.Files.Add("Licenses/NOTICE.txt",Encoding.UTF8.GetBytes("third-party license fixture"));return r;}
    static FrameGenPackageStore Fixture(string parent,string name,FrameGenArchiveFixture reader)
    {
        string path=Path.Combine(parent,name);Directory.CreateDirectory(Path.Combine(path,"framegen-downloads"));File.WriteAllText(Path.Combine(path,"framegen-downloads/aurora.7z"),"pinned archive fixture",new UTF8Encoding(false));File.WriteAllText(Path.Combine(path,"framegen-downloads/GPL.txt"),"GPL fixture",new UTF8Encoding(false));return new FrameGenPackageStore(path,Spec(reader),reader);
    }
    static int Main(string[] args)
    {
        if(args.Length==2&&(args[0]=="--prepare-root"||args[0]=="--verify-root"))
        {try{var actual=new FrameGenPackageStore(Path.GetFullPath(args[1]),FrameGenerationPackages.Catalog(),new WindowsFrameGenArchive());Console.WriteLine(args[0]=="--prepare-root"?actual.Prepare(Console.WriteLine,false):actual.Verify());return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
        string root=Path.Combine(Path.GetTempPath(),"FrameGenPackages-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var reader=Reader();var store=Fixture(root,"good",reader);store.Prepare(null,false);
            Assert(File.Exists(Path.Combine(store.Bundle,"OptiScaler.dll")),"only verified native loader is published");
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"LICENSE.txt"))=="GPL fixture","separately pinned GPL text accompanies wrapper");
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"docs/NOTICE.txt"))=="third-party license fixture","archive attribution is retained");
            Assert(File.ReadAllText(Path.Combine(store.Bundle,"SOURCES.txt"))=="fixture sources\r\n","source provenance is deterministic");
            Assert(store.Verify().Length>0,"prepared offline bundle verifies");
            store.Prepare(null,false);Assert(reader.Extractions==1,"unchanged verified bundle is reused");
            string bundleDll=Path.Combine(store.Bundle,"OptiScaler.dll");byte[] changedDll=File.ReadAllBytes(bundleDll);changedDll[changedDll.Length-1]^=1;File.WriteAllBytes(bundleDll,changedDll);Reject(delegate{store.Verify();},"same-length edited DLL cannot authenticate");
            store.Prepare(null,false);Assert(reader.Extractions==2,"verified cache repairs changed bundle");
            Assert(Directory.GetDirectories(Path.GetDirectoryName(store.Bundle),"framegen-bundle.previous-*").Length==1,"old changed bundle is preserved");
            File.WriteAllText(Path.Combine(store.Bundle,"SOURCES.txt"),"changed provenance");Reject(delegate{store.Verify();},"editable provenance cannot weaken verification");
            store.Prepare(null,false);
            string cachedArchive=Path.Combine(root,"good/framegen-downloads/aurora.7z");byte[] changedArchive=File.ReadAllBytes(cachedArchive);changedArchive[0]^=1;File.WriteAllBytes(cachedArchive,changedArchive);Reject(delegate{store.Verify();},"same-length cached archive is revalidated every time");Reject(delegate{store.Prepare(null,false);},"offline corrupt cache cannot republish");Assert(File.Exists(Path.Combine(store.Bundle,"OptiScaler.dll")),"failed cache check retains existing bundle");

            var licenseStore=Fixture(root,"license",Reader());licenseStore.Prepare(null,false);string cachedLicense=Path.Combine(root,"license/framegen-downloads/GPL.txt");byte[] changedLicense=File.ReadAllBytes(cachedLicense);changedLicense[0]^=1;File.WriteAllBytes(cachedLicense,changedLicense);Reject(delegate{licenseStore.Verify();},"same-length GPL cache hash is pinned as well");
            var missing=Fixture(root,"missing",Reader());File.Delete(Path.Combine(root,"missing/framegen-downloads/GPL.txt"));Reject(delegate{missing.Prepare(null,false);},"missing offline GPL is explicit");Assert(!Directory.Exists(missing.Bundle),"missing license prevents publication");

            foreach(string name in new[]{"../escape.dll","folder\\..\\escape.dll","/absolute.dll","C:/escape.dll","folder/file:stream","folder/CON.txt","folder/name. ","folder//nested.dll"})
            {
                var bad=Reader();bad.Names=new[]{"OptiScaler.dll","Licenses/NOTICE.txt",name};var badStore=Fixture(root,Guid.NewGuid().ToString("N"),bad);
                Reject(delegate{badStore.Prepare(null,false);},"unsafe archive path rejected: "+name);Assert(bad.Extractions==0&&!Directory.Exists(badStore.Bundle),"unsafe member is rejected before extraction");
            }
            var duplicate=Reader();duplicate.Names=new[]{"OptiScaler.dll","OPTISCALER.DLL","Licenses/NOTICE.txt"};var duplicateStore=Fixture(root,"duplicate",duplicate);Reject(delegate{duplicateStore.Prepare(null,false);},"case-insensitive duplicate core member rejected");
            var absent=Reader();absent.Names=new[]{"Licenses/NOTICE.txt"};var absentStore=Fixture(root,"no-core",absent);Reject(delegate{absentStore.Prepare(null,false);},"missing core member rejected before extraction");Assert(absent.Extractions==0,"incomplete archive never extracts");
            var substituted=Reader();var substitutedStore=Fixture(root,"substituted",substituted);substituted.Files["OptiScaler.dll"][511]^=1;Reject(delegate{substitutedStore.Prepare(null,false);},"same-length member substitution rejected against fixed member hash");Assert(!Directory.Exists(substitutedStore.Bundle),"substituted native member never publishes");
            var notPe=Reader();notPe.Files["OptiScaler.dll"]=Encoding.UTF8.GetBytes("not a PE image");var notPeStore=Fixture(root,"not-pe",notPe);Reject(delegate{notPeStore.Prepare(null,false);},"even a matching hash cannot turn non-PE bytes into a loader");
            var x86=Reader();byte[] x86Bytes=x86.Files["OptiScaler.dll"];x86Bytes[68]=0x4c;x86Bytes[69]=0x01;var x86Store=Fixture(root,"x86",x86);Reject(delegate{x86Store.Prepare(null,false);},"x86 DLL cannot satisfy x64 contract");
            var wrongLicense=Reader();var wrongLicenseStore=Fixture(root,"bad-doc",wrongLicense);wrongLicense.Files["Licenses/NOTICE.txt"][0]^=1;Reject(delegate{wrongLicenseStore.Prepare(null,false);},"same-length changed attribution rejects whole bundle");
            var extra=Fixture(root,"extra",Reader());extra.Prepare(null,false);File.WriteAllText(Path.Combine(extra.Bundle,"unknown.exe"),"foreign");Reject(delegate{extra.Verify();},"unexpected bundle payload is never silently accepted");
            Reject(delegate{FrameGenPackageStore.NormalizeMember("../x");},"member normalizer cannot escape root");
            Console.WriteLine("PASS: "+checks+" frame-generation package checks; no executable or network operation.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{string checkedRoot=Path.GetFullPath(root);if(checkedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(checkedRoot,true);}
    }
}
