using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Xml;
using RtxHub;

internal static class FrameGenerationInstallerTests
{
    static int checks;
    static void Assert(bool value,string reason){checks++;if(!value)throw new Exception("FAIL: "+reason);}
    static void Refuses(Action action,string reason){bool refused=false;try{action();}catch(InvalidOperationException){refused=true;}Assert(refused,reason);}
    static string MakeExe(string directory,bool x64)
    {
        Directory.CreateDirectory(directory);string exe=Path.Combine(directory,"game.exe");byte[] bytes=new byte[512];
        bytes[0]=77;bytes[1]=90;bytes[60]=128;bytes[128]=80;bytes[129]=69;
        bytes[132]=x64?(byte)100:(byte)76;bytes[133]=x64?(byte)134:(byte)1;bytes[148]=240;bytes[152]=11;bytes[153]=x64?(byte)2:(byte)1;
        File.WriteAllBytes(exe,bytes);return exe;
    }
    const string Ini2="[DLSSG]\r\nOverrideInterpolationCount=1\r\nAdaMfgUnlock=true\r\n[Hooks]\r\nLoadASIFiles=false\r\n";
    const string Ini6="[DLSSG]\r\nOverrideInterpolationCount=5\r\nAdaMfgUnlock=true\r\n[Hooks]\r\nLoadASIFiles=false\r\n";
    sealed class FailingWriter:IFrameGenFileWriter
    {
        internal Action BeforeFirst,BeforeFailure;int calls;
        public void Commit(FrameGenInstallFile file,byte[] ini)
        {
            calls++;if(calls==1&&BeforeFirst!=null)BeforeFirst();
            if(calls==2){if(BeforeFailure!=null)BeforeFailure();throw new IOException("fixture failure on second file");}
            new AtomicFrameGenFileWriter().Commit(file,ini);
        }
    }
    static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"RtxHub-fg-install-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string bundle=Path.Combine(root,"bundle");Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle,"OptiScaler.dll"),"fixed synthetic component");File.WriteAllText(Path.Combine(bundle,"LICENSE.txt"),"fixture GPL license");File.WriteAllText(Path.Combine(bundle,"SOURCES.txt"),"fixture immutable source");
            var service=new FrameGenInstallService(Path.Combine(root,"records"));
            string exe=MakeExe(Path.Combine(root,"game"),true),dir=Path.GetDirectoryName(exe);
            File.WriteAllText(Path.Combine(dir,"dxgi.dll"),"existing NR loader");File.WriteAllText(Path.Combine(dir,"nvngx_dlssg.dll"),"original game FG");
            service.EnsureInstalled(exe,bundle,Ini2);
            Assert(File.ReadAllText(Path.Combine(dir,"winmm.dll"))=="fixed synthetic component","installs the fixed library under the independent winmm proxy name");
            Assert(File.Exists(Path.Combine(dir,"4090M-FG-LICENSE.txt"))&&File.Exists(Path.Combine(dir,"4090M-FG-SOURCES.txt")),"license and immutable source accompany the game loader");
            Assert(File.ReadAllText(Path.Combine(dir,"dxgi.dll"))=="existing NR loader"&&File.ReadAllText(Path.Combine(dir,"nvngx_dlssg.dll"))=="original game FG","NR and original game runtime remain unchanged");
            Assert(service.ManagedTargets().Length==1&&service.ManagedTargets()[0]==exe,"validated journal discovers only its associated game");
            string journalBefore=File.ReadAllText(service.JournalPath(exe));
            service.EnsureInstalled(exe,bundle,Ini2);Assert(File.ReadAllText(service.JournalPath(exe))==journalBefore,"identical requests are idempotent");
            service.EnsureInstalled(exe,bundle,Ini6);Assert(File.ReadAllText(Path.Combine(dir,"OptiScaler.ini")).Contains("OverrideInterpolationCount=5"),"changing the requested multiplier updates known configuration");
            service.Disable(exe);Assert(!File.Exists(Path.Combine(dir,"winmm.dll"))&&!File.Exists(Path.Combine(dir,"OptiScaler.ini")),"normal disable removes the unmodified tool configuration and loader");
            Assert(!File.Exists(service.JournalPath(exe))&&!service.HasCoreFiles(exe),"complete restoration retires journal and loader");
            Assert(File.ReadAllText(Path.Combine(dir,"dxgi.dll"))=="existing NR loader","restoration never removes NR");

            service.EnsureInstalled(exe,bundle,Ini2);File.AppendAllText(Path.Combine(dir,"OptiScaler.ini"),"\r\n[Personal]\r\nFavorite=keep\r\n");
            service.EnsureInstalled(exe,bundle,Ini6);string edited=File.ReadAllText(Path.Combine(dir,"OptiScaler.ini"));
            Assert(edited.Contains("Favorite=keep")&&edited.Contains("OverrideInterpolationCount=5"),"rate changes preserve unknown keys and sections");
            service.Restore(exe);Assert(File.ReadAllText(Path.Combine(dir,"OptiScaler.ini"))==edited&&File.Exists(service.JournalPath(exe)),"edited INI remains with its recovery record");
            File.WriteAllText(Path.Combine(dir,"winmm.dll"),"fixed synthetic component");
            Refuses(delegate{service.EnsureInstalled(exe,bundle,Ini6);},"a same-hash foreign proxy after cancellation cannot be adopted");
            service.Restore(exe);Assert(File.Exists(Path.Combine(dir,"winmm.dll")),"restoration preserves a newly created unowned proxy");
            File.Delete(Path.Combine(dir,"winmm.dll"));File.Delete(Path.Combine(dir,"OptiScaler.ini"));service.Restore(exe);
            Assert(!File.Exists(service.JournalPath(exe)),"retained record can retire after manual file removal");

            service.EnsureInstalled(exe,bundle,Ini2);File.AppendAllText(Path.Combine(dir,"winmm.dll")," edited");
            Refuses(delegate{service.EnsureInstalled(exe,bundle,Ini6);},"an externally modified owned loader is never replaced");
            Refuses(delegate{service.Disable(exe);},"disable cannot report 1x success while an externally modified managed proxy remains");
            service.Restore(exe);Assert(File.ReadAllText(Path.Combine(dir,"winmm.dll")).Contains("edited"),"externally modified loader survives restore");
            File.Delete(Path.Combine(dir,"winmm.dll"));service.Restore(exe);

            File.WriteAllText(Path.Combine(dir,"winmm.dll"),"personal proxy");
            Assert(!service.HasCoreFiles(exe),"an unrelated foreign proxy without an FG journal is not a managed core file");
            string foreignDisable=service.Disable(exe);Assert(foreignDisable.Contains("没有")&&File.ReadAllText(Path.Combine(dir,"winmm.dll"))=="personal proxy","disable without an FG record leaves a foreign proxy untouched and returns normally");
            Refuses(delegate{service.EnsureInstalled(exe,bundle,Ini2);},"an existing foreign proxy blocks first installation");File.Delete(Path.Combine(dir,"winmm.dll"));
            File.WriteAllText(Path.Combine(dir,"OptiScaler.ini"),"personal settings");Refuses(delegate{service.EnsureInstalled(exe,bundle,Ini2);},"an existing unowned configuration is never overwritten");File.Delete(Path.Combine(dir,"OptiScaler.ini"));
            Refuses(delegate{service.EnsureInstalled(MakeExe(Path.Combine(root,"game32"),false),bundle,Ini2);},"32-bit game is rejected");
            Refuses(delegate{service.EnsureInstalled(@"\\server\share\game.exe",bundle,Ini2);},"UNC target is rejected");
            Refuses(delegate{service.EnsureInstalled(exe+":stream",bundle,Ini2);},"alternate data stream path is rejected");
            Refuses(delegate{service.EnsureInstalled(Path.Combine(dir,"..","game","game.exe"),bundle,Ini2);},"ambiguous traversal segments are rejected");
            Refuses(delegate{service.EnsureInstalled(exe,bundle,"" );},"empty generated configuration cannot deploy a library");
            Refuses(delegate{service.EnsureInstalled(exe,bundle,"[Broken]\r\nnot-a-setting\r\n");},"invalid generated configuration is rejected");
            string protectedExe=MakeExe(Path.Combine(root,"protected","bin","x64"),true);Directory.CreateDirectory(Path.Combine(root,"protected","EasyAntiCheat"));
            Refuses(delegate{service.EnsureInstalled(protectedExe,bundle,Ini2);},"known anti-cheat directory blocks new deployment");
            Refuses(delegate{service.EnsureInstalled(Assembly.GetExecutingAssembly().Location,bundle,Ini2);},"current running executable cannot be modified");

            string missingSource=Path.Combine(bundle,"SOURCES.txt");File.Delete(missingSource);
            Refuses(delegate{service.EnsureInstalled(exe,bundle,Ini2);},"all sources are preflighted before writing game files");
            Assert(!File.Exists(Path.Combine(dir,"winmm.dll")),"source failure publishes no loader");File.WriteAllText(missingSource,"fixture immutable source");

            string failureExe=MakeExe(Path.Combine(root,"failed"),true);var writer=new FailingWriter();var failing=new FrameGenInstallService(Path.Combine(root,"failed-records"),writer);
            writer.BeforeFirst=delegate{Assert(File.Exists(failing.JournalPath(failureExe)),"durable ownership intent precedes first game write");};
            Refuses(delegate{failing.EnsureInstalled(failureExe,bundle,Ini2);},"a staged commit failure reports failure");
            Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(failureExe),"winmm.dll")),"failed installation rolls back its newly published loader");
            Assert(!File.Exists(failing.JournalPath(failureExe)),"fully rolled back install retires its record");

            string editedFailExe=MakeExe(Path.Combine(root,"failed-external-edit"),true),editedFailDir=Path.GetDirectoryName(editedFailExe);var changedWriter=new FailingWriter();var changedService=new FrameGenInstallService(Path.Combine(root,"edited-failure-records"),changedWriter);
            changedWriter.BeforeFailure=delegate{File.AppendAllText(Path.Combine(editedFailDir,"winmm.dll")," external modification");};
            Refuses(delegate{changedService.EnsureInstalled(editedFailExe,bundle,Ini2);},"failure after an external edit remains explicit");
            Assert(File.ReadAllText(Path.Combine(editedFailDir,"winmm.dll")).Contains("external modification")&&File.Exists(changedService.JournalPath(editedFailExe)),"rollback preserves externally edited newly created files and their record");

            service.EnsureInstalled(exe,bundle,Ini2);string goodRecord=File.ReadAllText(service.JournalPath(exe));
            File.WriteAllText(service.JournalPath(exe),goodRecord.Replace("winmm.dll","other.dll"));Refuses(delegate{service.Restore(exe);},"modified allowed file list cannot authorize removal");
            Assert(File.Exists(Path.Combine(dir,"winmm.dll")),"tampered journal never deletes the proxy");
            File.WriteAllText(service.JournalPath(exe),goodRecord.Replace("owned=\"True\"","owned=\"False\""));Refuses(delegate{service.Restore(exe);},"ownership bit edits fail record integrity validation");
            File.WriteAllText(service.JournalPath(exe),"<!DOCTYPE x SYSTEM 'file:///untrusted'><frameGenInstall/>");Refuses(delegate{service.Restore(exe);},"records forbid external DTDs");
            File.WriteAllText(service.JournalPath(exe),goodRecord);File.Delete(exe);service.Restore(exe);
            Assert(!File.Exists(Path.Combine(dir,"winmm.dll")),"cleanup remains possible after the game executable was removed");

            string interruptedExe=MakeExe(Path.Combine(root,"interrupted-delete"),true),interruptedRecords=Path.Combine(root,"interrupted-records");FrameGenInstallService interrupted=null;
            interrupted=new FrameGenInstallService(interruptedRecords,new AtomicFrameGenFileWriter(),delegate(string path)
            {
                var observed=new XmlDocument();observed.Load(interrupted.JournalPath(interruptedExe));
                Assert(observed.DocumentElement.SelectSingleNode("file[@name='winmm.dll']").Attributes["owned"].Value=="False","ownership revocation is durable before the deletion operation");
                throw new IOException("fixture interruption before deletion");
            });
            interrupted.EnsureInstalled(interruptedExe,bundle,Ini2);Refuses(delegate{interrupted.Restore(interruptedExe);},"a failed deletion reports an incomplete restoration");
            var restarted=new FrameGenInstallService(interruptedRecords);Refuses(delegate{restarted.EnsureInstalled(interruptedExe,bundle,Ini2);},"interrupted ownership revocation never reacquires the same-hash file");
            restarted.Restore(interruptedExe);Assert(File.Exists(Path.Combine(Path.GetDirectoryName(interruptedExe),"winmm.dll")),"a fresh service preserves a proxy whose deletion was interrupted");

            string keyExe=MakeExe(Path.Combine(root,"key-game"),true),keyRecords=Path.Combine(root,"key-records");var keyService=new FrameGenInstallService(keyRecords);
            keyService.EnsureInstalled(keyExe,bundle,Ini2);string keyPath=Path.Combine(keyRecords,".fg-integrity.key");byte[] signingKey=File.ReadAllBytes(keyPath);byte[] wrongKey=(byte[])signingKey.Clone();wrongKey[0]^=255;File.WriteAllBytes(keyPath,wrongKey);
            Refuses(delegate{keyService.Restore(keyExe);},"a replaced integrity key cannot validate older ownership records");
            Assert(File.Exists(Path.Combine(Path.GetDirectoryName(keyExe),"winmm.dll")),"invalid record integrity never removes the loader");File.WriteAllBytes(keyPath,signingKey);
            File.Move(keyPath,keyPath+".saved");Refuses(delegate{keyService.Restore(keyExe);},"missing integrity key fails closed rather than generating a new one");
            string keyOtherExe=MakeExe(Path.Combine(root,"key-other-game"),true);
            Refuses(delegate{keyService.EnsureInstalled(keyOtherExe,bundle,Ini2);},"a fresh target cannot regenerate a key while older ownership records exist");
            Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(keyOtherExe),"winmm.dll"))&&!File.Exists(keyService.JournalPath(keyOtherExe)),"failed key recovery publishes neither new loader nor new record");
            Assert(File.Exists(keyService.JournalPath(keyExe)),"missing-key rejection preserves the original installation record");
            Assert(!File.Exists(keyPath),"restoring without a key cannot silently create new trust");File.Move(keyPath+".saved",keyPath);keyService.Restore(keyExe);

            string realDirectory=Path.Combine(root,"junction-target"),junction=Path.Combine(root,"junction-link");MakeExe(realDirectory,true);
            var junctionStart=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /c mklink /J \""+junction+"\" \""+realDirectory+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var junctionProcess=Process.Start(junctionStart)){junctionProcess.WaitForExit();Assert(junctionProcess.ExitCode==0,"test fixture junction can be created on the local Windows filesystem");}
            try
            {
                Refuses(delegate{service.EnsureInstalled(Path.Combine(junction,"game.exe"),bundle,Ini2);},"game paths through an actual junction are rejected");
                Refuses(delegate{new FrameGenInstallService(Path.Combine(junction,"records"));},"record directories through an actual junction are rejected");
                Refuses(delegate{service.EnsureInstalled(MakeExe(Path.Combine(root,"junction-bundle-game"),true),junction,Ini2);},"bundle paths through an actual junction are rejected");
            }
            finally{Directory.Delete(junction,false);}

            string merged=FrameGenInstallService.MergeDesiredIni("; personal\n[dlssg]\nOverrideInterpolationCount=1 ; comment\nUnknown=keep\nOverrideInterpolationCount=2\n",Ini6);
            Assert(merged.Contains("OverrideInterpolationCount=5")&&merged.Contains("Unknown=keep")&&merged.Contains("; comment"),"case-insensitive merge retains unrelated keys and comments");
            int active=0;foreach(string row in merged.Split('\n'))if(row.TrimStart().StartsWith("OverrideInterpolationCount=",StringComparison.OrdinalIgnoreCase))active++;
            Assert(active==1&&merged.Contains("OverrideInterpolationCount=2"),"duplicate active managed values become preserved comments");
            Assert(FrameGenInstallService.MergeDesiredIni(merged,Ini6)==merged,"generic desired-key merge is idempotent");
            Console.WriteLine("PASS: "+checks+" FG installer filesystem checks; no game or native DLL executed.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally
        {
            string actual=Path.GetFullPath(root),expected=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(actual.StartsWith(expected,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(actual).StartsWith("RtxHub-fg-install-tests-",StringComparison.Ordinal))Directory.Delete(actual,true);
        }
    }
}
