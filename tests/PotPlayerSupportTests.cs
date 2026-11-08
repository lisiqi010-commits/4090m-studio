using System;
using System.Globalization;
using System.IO;
using System.Threading;
using RtxHub;

internal static class PotPlayerSupportTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); } catch (ArgumentException) { refused = true; } catch (IOException) { refused = true; } catch (InvalidOperationException) { refused = true; }
        Check(refused, message);
    }
    static string Touch(string directory, string name)
    {
        string file = Path.Combine(directory, name);
        File.WriteAllText(file, "inert test fixture");
        return file;
    }

    static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PotPlayerSupport-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Touch(directory, "片名 & (一).mp4");
            string second = Touch(directory, "第二段.mkv");
            string playlist = Touch(directory, "本地列表.dpl");
            string arguments = PotPlayerSupport.BuildArguments(new string[] { first, second }, false, true);
            Check(arguments == VideoSupport.QuoteWindowsArgument(first) + " " + VideoSupport.QuoteWindowsArgument(second) + " /new", "Native multi-file playlist preserves order and quotes paths.");
            Check(PotPlayerSupport.BuildArguments(new string[] { first }, true, false) == VideoSupport.QuoteWindowsArgument(first) + " /current /add", "Appending targets the current native playlist.");
            Check(PotPlayerSupport.BuildArguments(new string[] { playlist }, false, false).EndsWith(" /current"), "Native DPL can be opened.");
            Check(PotPlayerSupport.BuildArguments(new string[0], false, true) == "/new", "Opening the player without media retains its native UI.");
            Check(PotPlayerSupport.BuildArguments(null, false, true) == "/new", "Null media array means open native player.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { first }, true, true); }, "Append and new instance cannot conflict.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[0], true, false); }, "Cannot append an empty selection.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { Path.Combine(directory, "missing.mp4") }, false, true); }, "Missing media is refused.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { "https://example.com/movie.mp4" }, false, true); }, "A URL cannot be smuggled into a local selection.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { @"\\server\movie.mp4" }, false, true); }, "UNC media is refused.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { "relative.mp4" }, false, true); }, "Relative paths are refused.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { first + ":payload" }, false, true); }, "Alternate data streams are refused.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { Touch(directory, "program.exe") }, false, true); }, "A program cannot become a media argument.");
            Refuses(delegate { PotPlayerSupport.BuildArguments(new string[] { Touch(directory, "notes.txt") }, false, true); }, "Unrelated files are refused.");
            string[] longSelection = new string[2001];
            for (int i = 0; i < longSelection.Length; i++) longSelection[i] = first;
            Refuses(delegate { PotPlayerSupport.BuildArguments(longSelection, false, true); }, "Overlong playlists are refused before process start.");

            string player = Touch(directory, "PotPlayerMini64.exe");
            Check(!PotPlayerSupport.IsPotPlayer(player), "Player identification requires its adjacent main library.");
            Touch(directory, "PotPlayer64.dll");
            Check(PotPlayerSupport.IsPotPlayer(player), "Recognizes the x64 mini-player layout without executing it.");
            Check(PotPlayerSupport.IsPotPlayer(Touch(directory, "PotPlayer64.exe")), "Recognizes the alternate native entry point.");
            Check(!PotPlayerSupport.IsPotPlayer(Touch(directory, "PotPlayerMini.exe")), "Does not accept a 32-bit player for the x64 add-on.");
            Check(!PotPlayerSupport.IsPotPlayer(Touch(directory, "other.exe")), "Does not identify another executable as PotPlayer.");
            Check(!PotPlayerSupport.IsPotPlayer(null), "Null player path is refused.");

            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string[] scales = { "0.50", "0.67", "1.00" };
                for (int preset = 0; preset < 3; preset++)
                {
                    string ini = PotPlayerSupport.BuildReShadeIni(preset);
                    Check(ini.Contains("NRPresentEncoding=0\r\n"), "Original player keeps automatic SDR/HDR encoding.");
                    Check(ini.Contains("NRPresentGuides=2\r\n"), "Video uses neutral guides.");
                    Check(ini.Contains("NRResolutionScale=" + scales[preset] + "\r\n"), "Invariant preset scale.");
                    Check(ini.Contains("DX11Source=native\r\n") && ini.Contains("NRHookPoint=2\r\n"), "Native D3D11 Present bridge.");
                    Check(!ini.Contains("VideoRenderer") && !ini.Contains("LanguageIni"), "NR configuration cannot guess or change player settings.");
                }
            }
            finally { Thread.CurrentThread.CurrentCulture = before; }
            Refuses(delegate { PotPlayerSupport.BuildReShadeIni(-1); }, "Invalid preset refused.");
            Refuses(delegate { PotPlayerSupport.BuildReShadeIni(3); }, "Out of range preset refused.");
            Refuses(delegate { PotPlayerSupport.EnsureInstalled(Path.Combine(directory, "other.exe"), 1); }, "Non-player cannot reach installation.");
            Refuses(delegate { PotPlayerSupport.Launch(Path.Combine(directory, "other.exe"), new string[] { first }, false); }, "Non-player cannot reach process launch.");

            string realLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "potplayer-neural-real.log");
            Check(File.Exists(realLog), "Real synthetic PotPlayer log fixture accompanies these tests.");
            string evidence = NeuralPresets.AnalyzeLog(realLog);
            Check(evidence.Contains("已记录 NR 计算成功"), "Normal PotPlayer shutdown keeps explicit evaluation evidence.");
            string contents = File.ReadAllText(realLog);
            Check(contents.Contains("PotPlayerMini64.exe") && contents.Contains("api=d3d11 path=present"), "Fixture is native PotPlayer D3D11 Present.");
            Check(contents.Contains("evaluation succeeded (count=60") && contents.Contains("evals=64"), "Fixture proves evaluation, beyond add-on creation.");

            Console.WriteLine("PotPlayerSupportTests: " + checks + " checks passed; no player, DLL or network operation executed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            // Every fixture is created in this unique flat temporary directory.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
