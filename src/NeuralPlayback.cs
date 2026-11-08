using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;

namespace RtxHub
{
 public static class NeuralPlayback
 {
  public static string LastPlayer {get;private set;}
  public static string LastLog {get;private set;}
  public static string PlayerDirectory(int preset)
  {
   if(preset<0||preset>2)throw new ArgumentOutOfRangeException("preset");
   return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","neural-player",new[]{"light","balanced","quality"}[preset]);
  }
  public static string BuildArguments(string movie)
  {
   if(String.IsNullOrWhiteSpace(movie)||!Path.IsPathRooted(movie)||movie.StartsWith(@"\\"))throw new ArgumentException("请选择本地影片的完整路径。");
   movie=Path.GetFullPath(movie);if(!File.Exists(movie))throw new FileNotFoundException("影片不存在。",movie);
   return "--no-config --load-scripts=no --input-terminal=no --terminal=no --vo=gpu --gpu-api=d3d11 --gpu-context=d3d11 --d3d11-adapter=NVIDIA --hwdec=d3d11va --interpolation=no --d3d11-output-csp=srgb --target-trc=srgb --target-prim=bt.709 --force-window=yes --keep-open=yes --autofit-larger=1280x720 -- "+VideoSupport.QuoteWindowsArgument(movie);
  }
  public static string BuildPlaylistArguments(string[] movies)
  {
   if(movies==null||movies.Length==0||movies.Length>500)throw new ArgumentException("请选择 1 至 500 个本地视频。");
   var paths=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string options=null;
   foreach(string movie in movies)
   {
    string single=BuildArguments(movie);string path=Path.GetFullPath(movie);
    if(options==null)options=single.Substring(0,single.IndexOf(" -- ",StringComparison.Ordinal));
    if(seen.Add(path))paths.Add(VideoSupport.QuoteWindowsArgument(path));
   }
   string result=options+" --osd-playing-msg="+VideoSupport.QuoteWindowsArgument("第 ${playlist-pos-1} / ${playlist-count} 项 · ${media-title}\n上一项 <  /  下一项 >")+" -- "+String.Join(" ",paths.ToArray());
   if(result.Length>28000)throw new ArgumentException("播放列表参数过长，请分批添加视频。");
   return result;
  }
  public static string PreparePlayer(string sourceMpv,int preset)
  {
   string dir=PlayerDirectory(preset);NeuralPackages.Verify();
   if(!File.Exists(sourceMpv)||!String.Equals(Path.GetFileName(sourceMpv),"mpv.exe",StringComparison.OrdinalIgnoreCase))throw new FileNotFoundException("请先在视频页面选择有效的 mpv.exe。",sourceMpv);
   EnsureLocalTree(dir);Directory.CreateDirectory(dir);EnsureLocalTree(dir);
   string player=Path.Combine(dir,"mpv.exe");
   if(File.Exists(player)){if(Hash(player)!=Hash(sourceMpv))throw new InvalidOperationException("独立 NR 播放器与所选 mpv 不一致，已保留现有文件。请先关闭并移走旧的独立播放器目录。");}
   else {string temp=player+".copy-"+Guid.NewGuid().ToString("N");try{File.Copy(sourceMpv,temp,false);if(Hash(temp)!=Hash(sourceMpv))throw new IOException("播放器副本校验失败。");File.Move(temp,player);}finally{if(File.Exists(temp))File.Delete(temp);}}
   NeuralInstaller.EnsureInstalled(player,NeuralPackages.BundleDirectory,NeuralPresets.BuildIni(preset));
   NeuralLanguage.ApplyChinese(player);
   
   {
    // Reuse its saved configuration. Do not overwrite settings edited in the plug-in.
    try
    {
     foreach(string name in new[]{"dxgi.dll","renodx-dlss5.addon64","nvngx_dlssnr.dll"})
      VerifyPrivateFile(Path.Combine(dir,name),Path.Combine(NeuralPackages.BundleDirectory,name));
    }
    catch(InvalidOperationException ex){throw new InvalidOperationException(ex.Message+"\r\n"+PrivateRecovery(player),ex);}
   }
   string sr=Path.Combine(NeuralPackages.BundleDirectory,"nvngx_dlss.dll");
   try{if(File.Exists(sr))VerifyPrivateFile(Path.Combine(dir,"nvngx_dlss.dll"),sr);VerifyPrivateConfig(Path.Combine(dir,"ReShade.ini"));}
   catch(InvalidOperationException ex){throw new InvalidOperationException(ex.Message+"\r\n"+PrivateRecovery(player),ex);}
   LastPlayer=player;LastLog=Path.Combine(dir,"ReShade.log");return player;
  }
  public static string Play(string sourceMpv,string movie,int preset)
  {
   return StartPlayer(sourceMpv,BuildArguments(movie),preset);
  }
  public static string PlayMany(string sourceMpv,string[] movies,int preset)
  {
   return StartPlayer(sourceMpv,BuildPlaylistArguments(movies),preset);
  }
  static string StartPlayer(string sourceMpv,string args,int preset)
  {
   string player=PreparePlayer(sourceMpv,preset);string dir=Path.GetDirectoryName(player);
   foreach(Process p in Process.GetProcessesByName("mpv"))using(p){try{if(String.Equals(p.MainModule.FileName,player,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("该预设的独立播放器正在运行，请先关闭它再重新播放。");}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException ex){if(ex.Message.Contains("独立播放器"))throw;}}
   string log=Path.Combine(dir,"ReShade.log");if(File.Exists(log)){string archived=Path.Combine(dir,"ReShade-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".log");File.Move(log,archived);}
   ProcessStartInfo info=new ProcessStartInfo(player,args){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=dir};
   using(Process p=Process.Start(info))
   {
    if(p==null)throw new InvalidOperationException("独立 NR 播放器未能启动。");
    if(p.WaitForExit(2000)&&p.ExitCode!=0)throw new InvalidOperationException("播放器启动失败，退出码 "+p.ExitCode+"。日志："+log);
   }
   LastPlayer=player;LastLog=log;
   return "已请求启动独立 DLSS 5 NR 播放："+new[]{"轻量 50%","均衡 67%","画质 100%"}[preset]+"（Present / B / 单层 / SDR）。\r\n按 F6 对比开关，F11 查看 NR 开销。已有插件内设置会保留；请点击检查日志确认实际执行。\r\n日志："+log+"\r\n"+NeuralPresets.AnalyzeLog(log);
  }
  internal static void VerifyPrivateFile(string file,string expected)
  {
   if(!File.Exists(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||Hash(file)!=Hash(expected))
    throw new InvalidOperationException("独立播放器组件缺失、被修改或经过链接，已拒绝启动："+Path.GetFileName(file)+"。原文件已保留。");
  }
  internal static void VerifyPrivateConfig(string file)
  {
   if(!File.Exists(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
    throw new InvalidOperationException("独立播放器配置缺失或经过链接，已拒绝启动，原文件已保留。");
  }
  static string PrivateRecovery(string player){return "如需重建此私有播放器：关闭播放器，把下列私有目录及其安装记录分别移动到备份位置，再重新选择影片。\r\n私有目录："+Path.GetDirectoryName(player)+"\r\n安装记录："+NeuralInstaller.JournalPath(player)+"\r\n原有修改过的配置会被保留，因此只点撤回可能仍会保留安装记录。";}
  static string Hash(string file){using(SHA256 sha=SHA256.Create())using(FileStream s=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","");}
  static void EnsureLocalTree(string dir){string p=Path.GetFullPath(dir);if(p.StartsWith(@"\\"))throw new IOException("只支持本地播放器目录。");while(!String.IsNullOrEmpty(p)){if(Directory.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("播放器目录不能经过链接或重解析点。");p=Path.GetDirectoryName(p);}}
 }
}

