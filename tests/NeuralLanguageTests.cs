using System;
using System.IO;
using System.Text;
using System.Xml;
using RtxHub;

internal static class NeuralLanguageTests
{
    static int checks;
    static void Assert(bool condition,string reason){checks++;if(!condition)throw new Exception("FAIL: "+reason);}
    static void Refuses(Action action,string reason){bool refused=false;try{action();}catch(InvalidOperationException){refused=true;}Assert(refused,reason);}
    static string MakeExe(string directory)
    {
        Directory.CreateDirectory(directory);byte[] bytes=new byte[512];bytes[0]=0x4d;bytes[1]=0x5a;bytes[60]=0x80;bytes[128]=0x50;bytes[129]=0x45;bytes[132]=0x64;bytes[133]=0x86;bytes[148]=0xf0;bytes[152]=0x0b;bytes[153]=2;
        string exe=Path.Combine(directory,"game.exe");File.WriteAllBytes(exe,bytes);return exe;
    }
    static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"RtxHub-language-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string original="; 用户注释\r\n[OVERLAY]\r\nLanguage=English\r\nShowFPS=1\r\n\r\n[RenoDX.DLSS5]\r\nUiLanguage=en\r\nNRResolutionScale=0.83\r\nNRIntensity=0.42\r\nUiTheme=custom\r\n; 保留方案\r\n[Unknown.Section]\r\nCustom=Keep,All,Values\r\n";
            string merged=NeuralLanguage.MergeChinese(original);
            Assert(merged.Contains("[OVERLAY]\r\nLanguage=zh-CN"),"ReShade receives the verified Windows locale name");
            Assert(merged.Contains("UiLanguage=zh-Hans"),"Nexus receives its verified language-table code");
            Assert(merged.Contains("NRResolutionScale=0.83\r\nNRIntensity=0.42\r\nUiTheme=custom"),"picture and theme settings remain unchanged");
            Assert(merged.Contains("; 用户注释")&&merged.Contains("; 保留方案")&&merged.Contains("[Unknown.Section]\r\nCustom=Keep,All,Values\r\n"),"comments and unknown sections remain unchanged");
            Assert(NeuralLanguage.MergeChinese(merged)==merged,"merge is idempotent");
            string lf=NeuralLanguage.MergeChinese("# comment\n[OVERLAY]\nShowFPS=1\n");
            Assert(!lf.Contains("\r")&&lf.Contains("Language=zh-CN\n"),"LF-only input keeps LF line endings while adding missing keys");
            string duplicate=NeuralLanguage.MergeChinese("[OVERLAY]\nLanguage=en-US\nLanguage=de-DE\n[RenoDX.DLSS5]\nUiLanguage=auto\nUiLanguage=en\n");
            Assert(NeuralLanguage.MergeChinese(duplicate)==duplicate,"duplicate language rows cannot defeat stable Chinese merge");
            string withBom=NeuralLanguage.MergeChinese("\uFEFF[OVERLAY]\nLanguage=en-US\n");
            Assert(withBom.StartsWith("\uFEFF[OVERLAY]\nLanguage=zh-CN\n",StringComparison.Ordinal),"pure merge handles a leading Unicode BOM without leaving the effective old language");
            string video=NeuralPresets.BuildIni(1),game=NeuralPresets.BuildGameIni(1);
            Assert(video.Contains("Language=zh-CN")&&video.Contains("UiLanguage=zh-Hans"),"new player configurations seed both Chinese languages");
            Assert(game.Contains("NRPresentGuides=0\r\n")&&game.Contains("NRPresentEncoding=0\r\n"),"game auto encoding and guides are preserved");
            Assert(video.Contains("NRPresentGuides=2\r\n")&&video.Contains("NRPresentEncoding=1\r\n"),"video SDR and neutral-guide configuration is preserved");

            string bundle=Path.Combine(root,"bundle");Directory.CreateDirectory(bundle);
            foreach(string name in new[]{"dxgi.dll","renodx-dlss5.addon64","nvngx_dlssnr.dll"})File.WriteAllText(Path.Combine(bundle,name),"test fixture "+name);
            string journals=Path.Combine(root,"journals"),exe=MakeExe(Path.Combine(root,"游戏"));
            var installs=new NeuralInstallService(journals);installs.Install(exe,bundle,original);
            var language=new NeuralLanguageService(journals);string journalBefore=File.ReadAllText(installs.JournalPath(exe));
            string result=language.ApplyChinese(exe),ini=Path.Combine(Path.GetDirectoryName(exe),"ReShade.ini");
            Assert(File.ReadAllText(ini)==merged,"managed migration changes exactly the pure language merge");
            Assert(File.ReadAllText(installs.JournalPath(exe))==journalBefore,"migration never changes original ownership journal hashes");
            string[] backups=Directory.GetFiles(Path.GetDirectoryName(exe),"ReShade.ini.before-Chinese-*.bak");
            Assert(backups.Length==1&&File.ReadAllText(backups[0])==original,"atomic replacement retains the exact original configuration backup");
            Assert(result.Contains("重启"),"migration explains that a running application may need a normal restart");
            language.ApplyChinese(exe);Assert(Directory.GetFiles(Path.GetDirectoryName(exe),"ReShade.ini.before-Chinese-*.bak").Length==1,"already Chinese config creates no redundant backup");
            installs.Restore(exe);Assert(File.Exists(ini),"original journal remains conservative and retains migrated configuration");
            string unmanaged=MakeExe(Path.Combine(root,"unmanaged"));File.WriteAllText(Path.Combine(Path.GetDirectoryName(unmanaged),"ReShade.ini"),original);
            Refuses(delegate{language.ApplyChinese(unmanaged);},"unmanaged configuration cannot be modified");
            var journal=new XmlDocument();journal.Load(installs.JournalPath(exe));((XmlElement)journal.SelectSingleNode("/neuralInstall/file[@name='ReShade.ini']")).SetAttribute("owned","False");journal.Save(installs.JournalPath(exe));
            Refuses(delegate{language.ApplyChinese(exe);},"unconfirmed INI ownership cannot be adopted for language migration");
            Console.WriteLine("PASS: "+checks+" language checks; only synthetic filesystem fixtures changed.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
