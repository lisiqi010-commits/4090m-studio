using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace RtxHub
{
 public static class FrameGenerationPresets
 {
  // Aurora v1.1.2, source 1d22875d05fac2d7ffb86ed912b943ce1a6e915a:
  // Config.h/Config.cpp and framegen/dlssg/MfgUnlock.cpp. OverrideInterpolationCount
  // applies to the game's native Streamline; this is distinct from NR passes.
  public static void ValidateMultiplier(int multiplier){if(multiplier<1||multiplier>6)throw new ArgumentOutOfRangeException("multiplier","游戏补帧倍率只能选择 1× 至 6×。");}
  public static string BuildIni(int multiplier)
  {
   ValidateMultiplier(multiplier);string count=(multiplier-1).ToString(CultureInfo.InvariantCulture);
   return "[FrameGen]\r\nFGInput=nofg\r\nFGOutput=nofg\r\nFGNvngxReplacement=None\r\n\r\n"+
    "[DLSSG]\r\nInterpolationCount="+count+"\r\nOverrideInterpolationCount="+count+"\r\nAdaMfgUnlock="+(multiplier>1?"true":"false")+"\r\nAdaBlackwellKernels=true\r\nForceDMFG=false\r\nOverrideForceDMFG=false\r\n\r\n"+
    "[NvApi]\r\nDisableOTA=true\r\n\r\n[Inputs]\r\nEnableDlssInputs=false\r\nEnableXeSSInputs=false\r\nEnableFsr2Inputs=false\r\nEnableFsr3Inputs=false\r\nEnableFfxInputs=false\r\n\r\n[Plugins]\r\nLoadAsiPlugins=false\r\nLoadReshade=false\r\nLoadSpecialK=false\r\n\r\n"+
    "[Hotfix]\r\nCheckForUpdate=false\r\n\r\n[DlssNr]\r\nEnabled=false\r\n\r\n[Log]\r\nLogFileName=4090M-FG.log\r\nSingleFile=true\r\nLogToFile=true\r\nLogLevel=2\r\n";
  }
  public static string MergeIni(string existing,int multiplier){return MergeDesiredIni(existing,BuildIni(multiplier));}
  internal static string MergeDesiredIni(string existing,string desired)
  {
   string result=existing??"",section="";
   using(var reader=new StringReader(desired??""))
   {
    string line;while((line=reader.ReadLine())!=null)
    {
     string row=line.Trim();if(row.StartsWith("[",StringComparison.Ordinal)&&row.EndsWith("]",StringComparison.Ordinal)){section=row.Substring(1,row.Length-2);continue;}
     if(row.Length==0||row[0]==';'||row[0]=='#')continue;int equals=row.IndexOf('=');if(equals<=0||section.Length==0)continue;
     result=SetKey(result,section,row.Substring(0,equals).Trim(),row.Substring(equals+1));
    }
   }
   return result;
  }
  static string SetKey(string ini,string section,string key,string value)
  {
   bool bom=ini.StartsWith("\uFEFF",StringComparison.Ordinal);if(bom)ini=ini.Substring(1);
   string newline=ini.Contains("\r\n")?"\r\n":ini.Contains("\n")?"\n":"\r\n";
   var lines=new List<string>(ini.Replace("\r\n","\n").Replace('\r','\n').Split('\n'));string current="";int first=-1;bool found=false;
   for(int i=0;i<lines.Count;i++)
   {
    string row=lines[i].Trim();if(row.StartsWith("[",StringComparison.Ordinal)&&row.EndsWith("]",StringComparison.Ordinal)){current=row.Substring(1,row.Length-2);if(String.Equals(current,section,StringComparison.OrdinalIgnoreCase)&&first<0)first=i;continue;}
    if(!String.Equals(current,section,StringComparison.OrdinalIgnoreCase)||row.Length==0||row[0]==';'||row[0]=='#')continue;int equals=row.IndexOf('=');if(equals<0||!String.Equals(row.Substring(0,equals).Trim(),key,StringComparison.OrdinalIgnoreCase))continue;
    if(found)lines[i]="; 4090M Studio: duplicate key retained: "+lines[i];else{Match comment=Regex.Match(row.Substring(equals+1),@"\s+[;#].*$");lines[i]=key+"="+value;if(comment.Success)lines.Insert(++i,comment.Value.TrimStart());found=true;}
   }
   if(!found){if(first>=0)lines.Insert(first+1,key+"="+value);else{if(lines.Count>0&&lines[lines.Count-1].Length==0)lines.RemoveAt(lines.Count-1);lines.Add("["+section+"]");lines.Add(key+"="+value);lines.Add("");}}
   return (bom?"\uFEFF":"")+String.Join(newline,lines.ToArray());
  }
  public static string GetEvidenceSummary(string content,int multiplier)
  {
   ValidateMultiplier(multiplier);
   if(multiplier==1)return "1×：本工具的补帧已关闭；游戏原有帧生成由游戏设置控制。";
   // Successful loading or a requested ratio is not a count of distinct generated
   // images. Runtime markers are reported separately from frame-time validation.
   return "已请求 "+multiplier+"×；未确认实际生成倍率。请在游戏内开启 DLSS 帧生成，并对比实际画面和帧时间。";
  }
 }
}
