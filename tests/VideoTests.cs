using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RtxHub;

// Build and run: powershell -ExecutionPolicy Bypass -File .\tests\Run-VideoTests.ps1
// These tests probe mpv's compiled features without opening or playing any media.
internal static class VideoTests
{
    private static int checks;

    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static int Main(string[] args)
    {
        // Self-contained child helper: base64 preserves even empty/newline argv values.
        if (args.Length > 0 && args[0] == "--capture-argv")
        {
            for (int i = 1; i < args.Length; i++)
                Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])));
            return 0;
        }

        string testDirectory = Path.Combine(Path.GetTempPath(), "Rtx4090MHub-VideoTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            CheckArgumentRoundTrip();
            string media = Path.Combine(testDirectory, "movie & parameter 中文 --profile=fast.mp4");
            File.WriteAllText(media, "synthetic test placeholder; never played");
            string vsr = VideoSupport.BuildMpvArguments(media, 0);
            string quality = VideoSupport.BuildMpvArguments(media, 1);
            string light = VideoSupport.BuildMpvArguments(media, 2);
            Assert(vsr.Contains("d3d11vpp=scale=2:scaling-mode=nvidia"), "VSR requests actual NVIDIA video filter");
            Assert(quality.Contains("--vo=gpu-next --profile=high-quality"), "quality requests libplacebo profile");
            Assert(light.Contains("--vo=gpu --profile=fast"), "light requests fast profile");
            Assert(vsr.Contains("--d3d11-adapter=NVIDIA"), "select NVIDIA adapter on hybrid laptops");
            foreach (string value in new[] { vsr, quality, light })
            {
                Assert(value.Contains("--no-config"), "isolate existing player configuration");
                Assert(value.Contains("--interpolation=no"), "preserve source framerate");
                Assert(!value.Contains("nvidia-true-hdr=yes"), "do not enable SDR-to-HDR");
                Assert(value.EndsWith("-- " + VideoSupport.QuoteWindowsArgument(media)), "media is one argument after end-of-options");
            }
            Throws<ArgumentOutOfRangeException>(delegate { VideoSupport.BuildMpvArguments(media, 3); });
            Throws<FileNotFoundException>(delegate { VideoSupport.BuildMpvArguments(Path.Combine(testDirectory, "missing.mkv"), 0); });
            Throws<ArgumentException>(delegate { VideoSupport.BuildMpvArguments("https://example.org/video.mp4", 0); });
            Throws<ArgumentException>(delegate { VideoSupport.BuildMpvArguments(@"\\server\share\movie.mkv", 0); });
            Throws<ArgumentException>(delegate { VideoSupport.OpenTarget("unrecognized-program.exe"); });
            Throws<ArgumentException>(delegate { VideoSupport.OpenTarget("https://example.org/"); });
            Throws<ArgumentException>(delegate { VideoSupport.QuoteWindowsArgument("abc\0def"); });
            string[] names = { "【RTX 超分】", "【精细缩放】", "【轻量流畅】" };
            for (int preset = 0; preset < names.Length; preset++)
                Assert(VideoSupport.GetGuide(preset).StartsWith(names[preset]), "guide matches visible preset title " + preset);

            string player = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\tools\mpv\mpv.exe"));
            bool skipMpvProbe = args.Length == 1 && args[0] == "--skip-mpv-probe";
            if (args.Length == 2 && args[0] == "--mpv") player = args[1];
            else if (args.Length != 0 && !skipMpvProbe) throw new ArgumentException("Usage: VideoTests.exe [--mpv <absolute mpv.exe path> | --skip-mpv-probe]");
            for (int preset = 0; !skipMpvProbe && preset < 3; preset++)
            {
                VideoSupport.ProbeMpv(player, preset);
                checks++;
            }
            Console.WriteLine("PASS: " + checks + " video checks; no media was played.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // Only remove this test's known file and then its empty, uniquely named directory.
            string media = Path.Combine(testDirectory, "movie & parameter 中文 --profile=fast.mp4");
            if (File.Exists(media)) File.Delete(media);
            Directory.Delete(testDirectory, false);
        }
    }

    private static void CheckArgumentRoundTrip()
    {
        string[] samples = { "", "plain", "has spaces", @"C:\folder with spaces\", "embedded\"quote", "slashes\\\\\"quote", "中文 & parameter --vf=example", "line\nend" };
        ProcessStartInfo info = new ProcessStartInfo(typeof(VideoTests).Assembly.Location);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.CreateNoWindow = true;
        info.Arguments = "--capture-argv";
        foreach (string sample in samples) info.Arguments += " " + VideoSupport.QuoteWindowsArgument(sample);
        using (Process process = Process.Start(info))
        {
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                throw new TimeoutException("argv child helper timed out");
            }
            string captured = process.StandardOutput.ReadToEnd();
            Assert(process.ExitCode == 0, "argv helper exits successfully");
            string[] lines = captured.Replace("\r", "").TrimEnd('\n').Split('\n');
            Assert(lines.Length == samples.Length, "argv count survives Windows quoting");
            for (int i = 0; i < samples.Length; i++)
                Assert(Encoding.UTF8.GetString(Convert.FromBase64String(lines[i])) == samples[i], "argv exact round-trip " + i);
        }
    }
}
