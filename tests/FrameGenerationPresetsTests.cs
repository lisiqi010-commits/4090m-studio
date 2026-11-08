using System;
using RtxHub;
internal static class FrameGenerationPresetsTests
{
 static int checks;
 static void Check(bool c,string m){checks++;if(!c)throw new Exception(m);}
 static void Refuses(Action a){bool b=false;try{a();}catch(ArgumentOutOfRangeException){b=true;}Check(b,"invalid multiplier refused");}
 static int Main(){try{
  for(int m=2;m<=6;m++){
   string ini=FrameGenerationPresets.BuildIni(m);
   Check(ini.Contains("OverrideInterpolationCount="+(m-1)+"\r\n"),"total multiplier maps to interpolated frame count");
   Check(ini.Contains("FGInput=nofg\r\n")&&ini.Contains("FGOutput=nofg\r\n"),"game native FG supplies frames");
   Check(ini.Contains("AdaMfgUnlock=true")&&ini.Contains("AdaBlackwellKernels=true"),"request distinct generated frames on Ada");
   Check(ini.Contains("DisableOTA=true")&&ini.Contains("CheckForUpdate=false")&&ini.Contains("LoadAsiPlugins=false"),"no rolling updates or unrelated plugins");
   Check(ini.Contains("[DlssNr]\r\nEnabled=false"),"existing Nexus remains sole NR provider");
  }
  Refuses(delegate{FrameGenerationPresets.BuildIni(0);});Refuses(delegate{FrameGenerationPresets.BuildIni(7);});
  Check(FrameGenerationPresets.BuildIni(1).Contains("OverrideInterpolationCount=0"),"1x is no inserted frames");
  string original="; mine\r\n[DLSSG]\r\nOverrideInterpolationCount=1\r\n[Custom]\r\nValue=keep\r\n";
  string merged=FrameGenerationPresets.MergeIni(original,6);
  Check(merged.Contains("OverrideInterpolationCount=5")&&merged.Contains("Value=keep")&&merged.Contains("; mine"),"ratio update retains unrelated settings and comments");
  Check(FrameGenerationPresets.MergeIni(merged,6)==merged,"repeated configuration is stable");
  Check(FrameGenerationPresets.BuildIni(6).Contains("SingleFile=true")&&FrameGenerationPresets.BuildIni(6).Contains("LogFileName=4090M-FG.log"),"fixed runtime log is freshly truncated by the verified upstream logger");
  string lower=FrameGenerationPresets.MergeIni("[dlssg]\r\noverrideinterpolationcount=1 ; keep ratio note\r\nOverrideInterpolationCount=2\r\n",6);
  Check(lower.Contains("OverrideInterpolationCount=5")&&lower.Contains("; keep ratio note"),"case-insensitive managed keys keep inline comments");
  Check(lower.Contains("duplicate key retained"),"duplicate live keys cannot override the selected ratio");
  Check(FrameGenerationPresets.GetEvidenceSummary("",6).Contains("未确认"),"missing log cannot prove frames");
  Check(FrameGenerationPresets.GetEvidenceSummary("OptiScaler ready FPS 360 multiplier 6",6).Contains("未确认"),"generic FPS or initialization never prove distinct frames");
  Console.WriteLine("PASS: "+checks+" frame-generation preset checks.");return 0;
 }catch(Exception e){Console.WriteLine(e);return 1;}}
}
