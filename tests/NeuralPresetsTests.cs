using System;
using System.Globalization;
using System.IO;
using System.Threading;
using RtxHub;

internal static class NeuralPresetsTests
{
    private static int checks;
    private static void Assert(bool condition, string label)
    {
        checks++;
        if (!condition) throw new Exception("FAIL: " + label);
    }
    private static void Contains(string text, string expected, string label)
    {
        Assert(text.IndexOf(expected, StringComparison.Ordinal) >= 0, label + " (expected " + expected + ")");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private const string Header = "12:00:00 [1] | INFO | Initializing crosire's ReShade version '6.8.0.2155' (64-bit)\n";
    private const string Loaded = "12:00:01 [1] | INFO | Registered add-on \"NR Nexus\" v0.2.2.0 using ReShade API version 18.\n";
    private const string Created = "12:00:02 [1] | INFO | NR Nexus: feature 18 created via the signed snippet after DLSS/DLAA for NR input 640x360 -> output 640x360, requested network=B (Small FP8) (preset 2; runtime confirmation unavailable)\n";
    private const string Success = "12:00:03 [1] | INFO | NR Nexus: present feature 18 evaluation succeeded (count=1, NR input 640x360 (guides 1280x720), output 1280x720, 1 stack pass(es) [native])\n";
    private const string Failure = "12:00:04 [1] | ERROR | NR Nexus: feature 18 evaluate failed with 0xBAD00005 (stack pass 1/1); the image is unchanged\n";

    private static int Main()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "Rtx4090MHub-NeuralTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            string[] scales = { "0.50", "0.67", "1.00" };
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                for (int preset = 0; preset < 3; preset++)
                {
                    string ini = NeuralPresets.BuildIni(preset);
                    Contains(ini, "[GENERAL]\r\n", "GENERAL section");
                    Assert(!ini.Contains("PresetPath=") && !ini.Contains("ReShadePreset.ini"), "no dependency on a separate effect preset");
                    Contains(ini, "[ADDON]\r\nAddonPath=.\r\n", "private-directory addon loading");
                    Contains(ini, "KeyOverlay=36,0,0,0\r\n", "Home overlay binding");
                    Contains(ini, "[RenoDX.DLSS5]\r\n", "Nexus production config section");
                    Contains(ini, "ConfigVersion=8\r\n", "pinned loader schema");
                    Contains(ini, "NeuralUplift=1\r\n", "actual NR enabled");
                    Contains(ini, "EnableHooks=2\r\n", "no forced Streamline hooks");
                    Contains(ini, "DX11Source=native\r\n", "native D3D11 Present route");
                    Contains(ini, "NRHookPoint=2\r\n", "Present insertion");
                    Contains(ini, "NRPresentGuides=2\r\n", "neutral guides for video");
                    Contains(ini, "NRPresentEncoding=1\r\n", "explicit SDR contract");
                    Contains(ini, "NRPasses=1\r\n", "one pass within 16 GB budget");
                    Contains(ini, "NRPreset=2\r\n", "model B requested");
                    Contains(ini, "NRFollowInputRes=0\r\n", "scale is explicit");
                    Contains(ini, "NRResolutionScale=" + scales[preset] + "\r\n", "culture-independent scale");
                    Assert(NeuralPresets.Describe(preset).Length > 20, "preset explanation");
                    string gameIni = NeuralPresets.BuildGameIni(preset);
                    Contains(gameIni, "NRPresentEncoding=0\r\n", "game follows actual SDR/HDR swapchain encoding");
                    Assert(!gameIni.Contains("NRPresentEncoding=1\r\n"), "HDR game is not forced to SDR interpretation");
                    Contains(gameIni, "NRPresentGuides=0\r\n", "game may use genuine matched depth/motion guides");
                    Contains(gameIni, "NRPreset=2\r\n", "game keeps model B request");
                    Contains(gameIni, "NRPasses=1\r\n", "game keeps one pass");
                    Contains(gameIni, "NRResolutionScale=" + scales[preset] + "\r\n", "game scale is culture independent");
                }
            }
            finally { Thread.CurrentThread.CurrentCulture = originalCulture; }
            Throws<ArgumentOutOfRangeException>(delegate { NeuralPresets.BuildIni(-1); });
            Throws<ArgumentOutOfRangeException>(delegate { NeuralPresets.BuildIni(3); });
            Throws<ArgumentOutOfRangeException>(delegate { NeuralPresets.Describe(3); });
            Throws<ArgumentOutOfRangeException>(delegate { NeuralPresets.BuildGameIni(3); });

            Contains(NeuralPresets.AnalyzeLog(Path.Combine(testRoot, "absent.log")), "未找到", "missing log fails closed");
            Contains(NeuralPresets.GetEvidenceSummary(null), "未发现", "empty evidence");
            Contains(NeuralPresets.GetEvidenceSummary("INFO NVIDIA RTX Super Resolution enabled."), "未发现", "VSR is not NR evidence");
            Contains(NeuralPresets.GetEvidenceSummary("INFO DLSS Super Resolution evaluation succeeded"), "未发现", "SR is not NR evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded), "仅确认插件加载", "addon registration is not model execution");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: signed NR runtime initialized\n"), "仅确认插件加载", "initialization is not evaluation");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Created), "仅确认 NR 功能创建", "feature creation is not an enhanced frame");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Created + Success), "已记录 NR 计算成功", "exact production evaluation marker");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Created + Success), "F11", "execution log does not prove visual quality or GPU cost");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: present feature 18 evaluation succeeded (count=0, NR input 1x1)\n"), "仅确认插件加载", "zero count is not success");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "Another addon: present feature 18 evaluation succeeded (count=2)\n"), "仅确认插件加载", "foreign output is not Nexus evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: present feature 18 evaluation succeeded maybe\n"), "仅确认插件加载", "partial marker is not success");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Created + Success + Failure), "最近报告失败", "failure after success takes precedence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Failure + Success), "已记录 NR 计算成功", "explicit later evaluation can show recovery");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Success + Header + Loaded), "仅确认插件加载", "newest session discards old success");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Success + Header), "未发现", "last session without addon discards old evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Success + Loaded), "仅确认插件加载", "reloaded addon requires fresh execution evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Created + Success + "12:00:05 [1] | INFO | Unregistered add-on \"NR Nexus\".\n"), "已记录 NR 计算成功", "normal addon shutdown must preserve this session's evaluation evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Success + "NR Nexus: NR-VERDICT v3 state=OFF stage=nr_enabled evals=1 submitted=1\n"), "未在处理", "later explicit OFF takes precedence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: NR-VERDICT v3 state=ENGAGED stage=- reason=- missing=- seen=60 eligible=60 evals=60 ratio=1.0000 unaccounted=0 submitted=60 presents=61 build=v0.2.2 trigger=settled api=d3d11 path=native\n"), "已记录 NR 计算成功", "positive verdict counters are evaluation evidence");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: NR-VERDICT v3 state=ENGAGED evals=0 submitted=0\n"), "仅确认插件加载", "ENGAGED label alone is insufficient");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + Success + "Other mod | ERROR | failed with 0xBAD00005\n"), "已记录 NR 计算成功", "unrelated addon failure is not attributed to NR");
            Contains(NeuralPresets.GetEvidenceSummary(Header + Loaded + "NR Nexus: feature 18 create failed with 0xBAD0000B\n"), "最近报告失败", "runtime error code fails closed");

            string logPath = Path.Combine(testRoot, "ReShade.log");
            File.WriteAllText(logPath, Header + Loaded + Created + Success);
            Contains(NeuralPresets.AnalyzeLog(logPath), "已记录 NR 计算成功", "read actual log file");
            string realLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "neural-real.log");
            Assert(File.Exists(realLog), "captured successful RTX 4090 Laptop session fixture is present");
            Contains(NeuralPresets.AnalyzeLog(realLog), "已记录 NR 计算成功", "captured full session remains successful after normal unregistration");
            Console.WriteLine("PASS: " + checks + " neural preset/evidence checks; no runtime DLL was loaded.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string logPath = Path.Combine(testRoot, "ReShade.log");
            if (File.Exists(logPath)) File.Delete(logPath);
            Directory.Delete(testRoot, false);
        }
    }
}
