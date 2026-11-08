using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Diagnostics;
using RtxHub;

internal static class NeuralInstallerTests
{
    sealed class FailingSecondCommit : INeuralFileWriter
    {
        internal Action BeforeFirst,BeforeSecond; int calls;
        public void Commit(NeuralInstallFile file,byte[] ini)
        {
            calls++;if(calls==1&&BeforeFirst!=null)BeforeFirst();
            if(calls==2){if(BeforeSecond!=null)BeforeSecond();throw new IOException("Injected source read failure during the second staged file");}
            new AtomicNeuralFileWriter().Commit(file,ini);
        }
    }
    static int assertions;
    static void Assert(bool condition, string reason) { assertions++; if (!condition) throw new Exception("FAIL: " + reason); }
    static void Refuses(Action action, string reason) { bool refused=false; try { action(); } catch (InvalidOperationException) { refused=true; } Assert(refused, reason); }
    static string MakeExe(string directory, string name, bool x64)
    {
        Directory.CreateDirectory(directory); string path=Path.Combine(directory,name); byte[] bytes=new byte[512];
        bytes[0]=0x4d;bytes[1]=0x5a;bytes[60]=0x80;bytes[128]=0x50;bytes[129]=0x45;
        bytes[132]=x64?(byte)0x64:(byte)0x4c;bytes[133]=x64?(byte)0x86:(byte)0x01;
        bytes[148]=0xf0;bytes[152]=x64?(byte)0x0b:(byte)0x0b;bytes[153]=x64?(byte)2:(byte)1;
        File.WriteAllBytes(path,bytes);return path;
    }
    static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"RtxHub-neural-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string bundle=Path.Combine(root,"bundle");Directory.CreateDirectory(bundle);
            foreach(string file in new[]{"dxgi.dll","renodx-dlss5.addon64","nvngx_dlssnr.dll","nvngx_dlss.dll"}) File.WriteAllText(Path.Combine(bundle,file),"vetted test fixture "+file,Encoding.UTF8);
            var service=new NeuralInstallService(Path.Combine(root,"journals"));
            string exe=MakeExe(Path.Combine(root,"游戏甲"),"game.exe",true), game=Path.GetDirectoryName(exe);
            File.WriteAllText(Path.Combine(game,"nvngx_dlss.dll"),"user original SR");
            string preview=service.Preflight(exe,bundle);
            Assert(preview.Contains("dxgi.dll")&&preview.Contains("ReShade.ini"),"preflight lists concrete planned files");
            service.Install(exe,bundle,"[GENERAL]\r\nTestFixture=1\r\n");
            Assert(File.ReadAllText(Path.Combine(game,"nvngx_dlss.dll"))=="user original SR","existing game SR is preserved");
            Assert(File.Exists(service.JournalPath(exe)),"successful install retains durable ownership journal");
            Assert(File.ReadAllText(Path.Combine(game,"ReShade.ini")).Contains("TestFixture=1"),"requested INI installed");
            Refuses(delegate {service.Install(exe,bundle,"other");},"repeat install requires restore first");
            File.WriteAllText(Path.Combine(game,"ReShade.log"),"user log");
            service.Restore(exe);
            Assert(!File.Exists(Path.Combine(game,"dxgi.dll"))&&!File.Exists(Path.Combine(game,"ReShade.ini")),"restore removes tool-owned unchanged files");
            Assert(File.ReadAllText(Path.Combine(game,"nvngx_dlss.dll"))=="user original SR"&&File.Exists(Path.Combine(game,"ReShade.log")),"restore preserves original SR and harmless logs");
            Assert(!File.Exists(service.JournalPath(exe)),"complete restore retires journal");

            service.Install(exe,bundle,"fixture");File.AppendAllText(Path.Combine(game,"dxgi.dll"),"external edit");
            string restore=service.Restore(exe);
            Assert(File.ReadAllText(Path.Combine(game,"dxgi.dll")).Contains("external edit"),"mutated owned file is preserved");
            Assert(File.Exists(service.JournalPath(exe))&&restore.Contains("dxgi.dll"),"mutated preservation retains journal and reports exact file");
            File.Delete(Path.Combine(game,"dxgi.dll"));service.Restore(exe);
            Assert(!File.Exists(service.JournalPath(exe)),"journal can retire after preserved file is removed manually");

            File.WriteAllText(Path.Combine(game,"d3d12.dll"),"existing loader");
            Refuses(delegate{service.Preflight(exe,bundle);},"existing graphics hook blocks install");File.Delete(Path.Combine(game,"d3d12.dll"));
            File.WriteAllText(Path.Combine(game,"ReShade.ini"),"personal settings");
            Refuses(delegate{service.Preflight(exe,bundle);},"existing personal ReShade config blocks install");File.Delete(Path.Combine(game,"ReShade.ini"));
            string exe32=MakeExe(Path.Combine(root,"32-bit"),"game32.exe",false);
            Refuses(delegate{service.Preflight(exe32,bundle);},"PE32 executable rejected");
            Refuses(delegate{service.Preflight(@"\\server\share\game.exe",bundle);},"UNC executable rejected");
            Refuses(delegate{service.Preflight(@"\\?\C:\game.exe",bundle);},"device path rejected");
            string protectedExe=MakeExe(Path.Combine(root,"protected","binaries","win64"),"game.exe",true);
            Directory.CreateDirectory(Path.Combine(root,"protected","EasyAntiCheat_EOS"));
            Refuses(delegate{service.Preflight(protectedExe,bundle);},"finite ancestor anti-cheat detection blocks install");
            string sameBase=MakeExe(Path.Combine(root,"same-basename"),Path.GetFileName(Assembly.GetExecutingAssembly().Location),true);
            service.Preflight(sameBase,bundle);Assert(true,"same basename in another directory is not a running-target conflict");
            Refuses(delegate{service.Preflight(Assembly.GetExecutingAssembly().Location,bundle);},"running target is detected by full path");

            string optionalExe=MakeExe(Path.Combine(root,"optional-sr"),"game.exe",true);
            service.Install(optionalExe,bundle,"fixture");Assert(File.Exists(Path.Combine(Path.GetDirectoryName(optionalExe),"nvngx_dlss.dll")),"optional SR copied only when absent");
            File.Delete(optionalExe);service.Restore(optionalExe);
            Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(optionalExe),"nvngx_dlss.dll")),"recovery works after target EXE is removed");

            string failureExe=MakeExe(Path.Combine(root,"failed-staging"),"game.exe",true), failureDir=Path.GetDirectoryName(failureExe);
            var failureWriter=new FailingSecondCommit();var failingService=new NeuralInstallService(Path.Combine(root,"failure-journals"),failureWriter);
            string crashIntent=null;
            failureWriter.BeforeFirst=delegate{
                crashIntent=File.ReadAllText(failingService.JournalPath(failureExe));var intent=new XmlDocument();intent.LoadXml(crashIntent);
                Assert(intent.SelectNodes("/neuralInstall/file").Count==5,"all concrete intent entries are durable before first target write");
                foreach(XmlElement file in intent.SelectNodes("/neuralInstall/file")) Assert(file.GetAttribute("sourceHash").Length==64&&file.GetAttribute("owned")=="False","initial intent records expected hashes without claiming ownership");
            };
            Refuses(delegate{failingService.Install(failureExe,bundle,"fixture");},"staging failure reported");
            Assert(!File.Exists(Path.Combine(failureDir,"dxgi.dll"))&&!File.Exists(failingService.JournalPath(failureExe)),"staging failure rolls back only successfully committed files");
            File.WriteAllText(failingService.JournalPath(failureExe),crashIntent);
            failingService.Restore(failureExe);
            Assert(!File.Exists(failingService.JournalPath(failureExe)),"crash after intent before writes restores safely");

            string crashExe=MakeExe(Path.Combine(root,"crash-window"),"game.exe",true), crashDir=Path.GetDirectoryName(crashExe);
            service.Install(crashExe,bundle,"fixture");string originalJournal=File.ReadAllText(service.JournalPath(crashExe));
            var crashJournal=new XmlDocument();crashJournal.LoadXml(originalJournal);((XmlElement)crashJournal.SelectSingleNode("/neuralInstall/file[@name='dxgi.dll']")).SetAttribute("owned","False");crashJournal.Save(service.JournalPath(crashExe));
            string crashRestore=service.Restore(crashExe);
            Assert(File.Exists(Path.Combine(crashDir,"dxgi.dll"))&&crashRestore.Contains("所有权"),"unconfirmed move-to-journal crash window never deletes file merely because hash matches");
            Assert(!File.Exists(Path.Combine(crashDir,"nvngx_dlssnr.dll"))&&File.Exists(service.JournalPath(crashExe)),"crash restore removes confirmed files and retains uncertain journal");
            File.WriteAllText(service.JournalPath(crashExe),originalJournal);service.Restore(crashExe);
            Assert(!File.Exists(service.JournalPath(crashExe)),"confirmed committed crash journal restores idempotently");

            string tamperExe=MakeExe(Path.Combine(root,"wrong-journal-path"),"game.exe",true);service.Install(tamperExe,bundle,"fixture");
            string goodJournal=File.ReadAllText(service.JournalPath(tamperExe)), sentinel=Path.Combine(root,"personal-file.dll");File.WriteAllText(sentinel,"personal data");
            var tampered=new XmlDocument();tampered.LoadXml(goodJournal);((XmlElement)tampered.SelectSingleNode("/neuralInstall/file")).SetAttribute("path",sentinel);tampered.Save(service.JournalPath(tamperExe));
            Refuses(delegate{service.Restore(tamperExe);},"wrong journal destination is rejected before deletion");
            Assert(File.ReadAllText(sentinel)=="personal data"&&File.Exists(Path.Combine(Path.GetDirectoryName(tamperExe),"dxgi.dll")),"tampered journal cannot delete outside files or start partial restore");
            File.WriteAllText(service.JournalPath(tamperExe),goodJournal);service.Restore(tamperExe);

            Refuses(delegate{service.JournalPath(@"C:drive-relative.exe");},"drive-relative path is rejected even for recovery identity");

            string editFailureExe=MakeExe(Path.Combine(root,"external-edit-during-failure"),"game.exe",true),editFailureDir=Path.GetDirectoryName(editFailureExe);
            var editWriter=new FailingSecondCommit();editWriter.BeforeSecond=delegate{File.AppendAllText(Path.Combine(editFailureDir,"dxgi.dll"),"external modification");};
            var editService=new NeuralInstallService(Path.Combine(root,"edit-failure-journals"),editWriter);
            Refuses(delegate{editService.Install(editFailureExe,bundle,"fixture");},"failed install still reports external mutation during rollback");
            Assert(File.ReadAllText(Path.Combine(editFailureDir,"dxgi.dll")).Contains("external modification")&&File.Exists(editService.JournalPath(editFailureExe)),"exception rollback preserves externally modified file and its journal");

            string realDirectory=Path.Combine(root,"junction-target"),junction=Path.Combine(root,"junction");
            MakeExe(realDirectory,"game.exe",true);
            var junctionStart=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /c mklink /J \""+junction+"\" \""+realDirectory+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(junctionStart)){process.StandardOutput.ReadToEnd();process.StandardError.ReadToEnd();process.WaitForExit();Assert(process.ExitCode==0,"test fixture junction created");}
            try{Refuses(delegate{service.Preflight(Path.Combine(junction,"game.exe"),bundle);},"reparse-point target directory is rejected");}
            finally{Directory.Delete(junction);}

            string wwm=MakeExe(Path.Combine(root,"known-online-title"),"wwm.exe",true);
            service.Preflight(wwm,bundle);Assert(true,"explicit user choice permits title name when real anti-cheat checks are clear");
            string wwmFolder=MakeExe(Path.Combine(root,"Where Winds Meet","Binaries","Win64"),"game.exe",true);
            service.Preflight(wwmFolder,bundle);Assert(true,"title-only ancestor restriction removed");
            // Model an older journal written before title protection existed. Recovery
            // must remain available even when current installation checks would refuse.
            string legacy=MakeExe(Path.GetDirectoryName(wwm),"legacy.exe",true);service.Install(legacy,bundle,"fixture");
            var legacyJournal=new XmlDocument();legacyJournal.Load(service.JournalPath(legacy));legacyJournal.DocumentElement.SetAttribute("exe",wwm);legacyJournal.Save(service.JournalPath(wwm));File.Delete(service.JournalPath(legacy));
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(wwm),"ACE"));
            service.Restore(wwm);
            Assert(!File.Exists(service.JournalPath(wwm))&&!File.Exists(Path.Combine(Path.GetDirectoryName(wwm),"dxgi.dll")),"protected-title and anti-cheat checks never block recovery of a valid owned-file journal");

            string ensureExe=MakeExe(Path.Combine(root,"自动安装"),"game.exe",true),ensureDir=Path.GetDirectoryName(ensureExe);
            service.EnsureInstalled(ensureExe,bundle,"initial config");
            Assert(File.Exists(Path.Combine(ensureDir,"dxgi.dll")),"EnsureInstalled supports first installation");
            File.AppendAllText(Path.Combine(ensureDir,"ReShade.ini")," user edits");
            service.EnsureInstalled(ensureExe,bundle,"new defaults must not replace edits");
            Assert(File.ReadAllText(Path.Combine(ensureDir,"ReShade.ini"))=="initial config user edits","complete owned install reuses DLLs and preserves custom INI");
            service.Restore(ensureExe);
            Assert(!File.Exists(Path.Combine(ensureDir,"dxgi.dll"))&&File.Exists(Path.Combine(ensureDir,"ReShade.ini"))&&File.Exists(service.JournalPath(ensureExe)),"cancel removes loading core while retaining edited INI and journal");
            service.EnsureInstalled(ensureExe,bundle,"defaults");
            Assert(File.Exists(Path.Combine(ensureDir,"dxgi.dll"))&&File.Exists(Path.Combine(ensureDir,"nvngx_dlssnr.dll"))&&File.ReadAllText(Path.Combine(ensureDir,"ReShade.ini")).Contains("user edits"),"reinstall refills missing managed files without replacing edited INI");
            File.AppendAllText(Path.Combine(ensureDir,"nvngx_dlssnr.dll"),"foreign modification");
            Refuses(delegate{service.EnsureInstalled(ensureExe,bundle,"defaults");},"altered owned runtime cannot be reused or overwritten");
            Assert(File.ReadAllText(Path.Combine(ensureDir,"nvngx_dlssnr.dll")).Contains("foreign modification"),"rejected repair preserves changed runtime");
            File.Delete(Path.Combine(ensureDir,"nvngx_dlssnr.dll"));service.Restore(ensureExe);
            File.Copy(Path.Combine(bundle,"dxgi.dll"),Path.Combine(ensureDir,"dxgi.dll"));
            Refuses(delegate{service.EnsureInstalled(ensureExe,bundle,"defaults");},"foreign same-hash loader after restore is never adopted as owned");
            File.Delete(Path.Combine(ensureDir,"dxgi.dll"));
            string[] targets=service.ManagedTargets();
            Assert(Array.IndexOf(targets,ensureExe)>=0,"validated retained journal remains a managed target");
            string spoofPath=Path.Combine(root,"journals",new string('0',64)+".xml");File.Copy(service.JournalPath(ensureExe),spoofPath);
            Refuses(delegate{service.ManagedTargets();},"enumeration refuses valid XML under wrong executable hash filename");
            File.Delete(spoofPath);

            string repairExe=MakeExe(Path.Combine(root,"repair-failure"),"game.exe",true),repairDir=Path.GetDirectoryName(repairExe);
            service.EnsureInstalled(repairExe,bundle,"original config");
            File.AppendAllText(Path.Combine(repairDir,"ReShade.ini")," custom values");
            File.Delete(Path.Combine(repairDir,"renodx-dlss5.addon64"));File.Delete(Path.Combine(repairDir,"nvngx_dlssnr.dll"));
            var repairWriter=new FailingSecondCommit();var repairService=new NeuralInstallService(Path.Combine(root,"journals"),repairWriter);
            repairWriter.BeforeFirst=delegate{
                var intent=new XmlDocument();intent.Load(service.JournalPath(repairExe));
                Assert(((XmlElement)intent.SelectSingleNode("/neuralInstall/file[@name='renodx-dlss5.addon64']")).GetAttribute("owned")=="False"&&((XmlElement)intent.SelectSingleNode("/neuralInstall/file[@name='dxgi.dll']")).GetAttribute("owned")=="True","refill intent is durable while existing ownership remains intact");
            };
            Refuses(delegate{repairService.EnsureInstalled(repairExe,bundle,"defaults");},"refill staging failure is reported");
            Assert(File.Exists(Path.Combine(repairDir,"dxgi.dll"))&&File.Exists(Path.Combine(repairDir,"nvngx_dlss.dll"))&&!File.Exists(Path.Combine(repairDir,"renodx-dlss5.addon64")),"refill rollback removes only newly committed files, preserving existing owned DLLs");
            Assert(File.ReadAllText(Path.Combine(repairDir,"ReShade.ini"))=="original config custom values","refill failure preserves edited configuration");
            service.EnsureInstalled(repairExe,bundle,"defaults");
            Assert(File.Exists(Path.Combine(repairDir,"renodx-dlss5.addon64"))&&File.Exists(Path.Combine(repairDir,"nvngx_dlssnr.dll")),"failed refill remains safely retryable");

            string streamlineExe=MakeExe(Path.Combine(root,"native-streamline-sr"),"game.exe",true),streamlineDir=Path.GetDirectoryName(streamlineExe);
            Directory.CreateDirectory(Path.Combine(streamlineDir,"Streamline"));string nativeSr=Path.Combine(streamlineDir,"Streamline","nvngx_dlss.dll");File.WriteAllText(nativeSr,"native game SR");
            service.EnsureInstalled(streamlineExe,bundle,"fixture");
            Assert(File.ReadAllText(nativeSr)=="native game SR"&&!File.Exists(Path.Combine(streamlineDir,"nvngx_dlss.dll")),"native Streamline SR is preserved without adding a shadowing root SR");
            service.Restore(streamlineExe);Assert(File.ReadAllText(nativeSr)=="native game SR","restore leaves native Streamline SR intact");
            Console.WriteLine("PASS: "+assertions+" neural installer filesystem checks; no game or DLL execution.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
