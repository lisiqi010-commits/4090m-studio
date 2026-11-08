using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
namespace RtxHub
{
    public static class SimpleActions
    {
        static readonly SimpleActionService Service=new SimpleActionService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Rtx4090MHub\simple-cancel-pending.xml"),new NativeSimpleActionsBackend());
        public static string Open(string selection,Action<string> progress){return Service.Open(selection,progress);}
        public static string OpenMany(string[] selections,Action<string> progress){if(selections!=null&&selections.Length==1)return Service.Open(selections[0],progress);return Service.OpenMany(selections,progress);}
        public static string OpenPotPlayer(Action<string> progress){return Service.OpenExtra(delegate(ISimpleActionsBackend backend){return backend.OpenPlayer();},progress);}
        public static string OpenBrowser(IntPtr window,Action<string> progress){return Service.OpenExtra(delegate(ISimpleActionsBackend backend){return backend.CaptureBrowser(window);},progress);}
        public static string CancelAll(Action<string> progress){return Service.CancelAll(progress);}
        public static string PollPending(Action<string> progress){return Service.PollPending(progress);}
        public static bool HasPendingCancellation {get{return Service.HasPendingCancellation;}}
    }
    internal sealed class SimpleVideoLaunch {internal string Executable,Message;}
    internal interface ISimpleActionsBackend
    {
        string Prepare(Action<string> progress); string EnsureGame(string exe); void LaunchGame(string exe);
        SimpleVideoLaunch PlayVideo(string video); SimpleVideoLaunch PlayVideos(string[] videos); SimpleVideoLaunch OpenPlayer(); SimpleVideoLaunch CaptureBrowser(IntPtr window); string[] ManagedTargets(); bool IsRunning(string exe);
        bool HasCoreFiles(string exe); string Restore(string exe); void StopOwnedPlayers(string[] targets,Action<string> progress);
    }
    internal sealed class SimpleActionService
    {
        readonly string pendingFile,mutexName;readonly ISimpleActionsBackend backend;
        internal SimpleActionService(string pendingFile,ISimpleActionsBackend backend)
        {
            this.pendingFile=Path.GetFullPath(pendingFile);this.backend=backend;
            using(var hash=SHA256.Create())mutexName=@"Local\RtxHub_SimpleActions_"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(this.pendingFile.ToUpperInvariant()))).Replace("-","");
        }
        internal bool HasPendingCancellation {get{return File.Exists(pendingFile);}}
        internal string OpenMany(string[] selections,Action<string> progress)
        {
            if(selections==null||selections.Length==0||selections.Length>500)throw new InvalidOperationException("请选择 1 至 500 个本地视频。");
            var files=new List<string>();var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string selection in selections){string file=CanonicalTarget(selection,true);if(!IsVideo(Path.GetExtension(file)))throw new InvalidOperationException("多选仅支持视频；游戏请单独选择。");if(unique.Add(file))files.Add(file);}
            return OpenExtra(delegate(ISimpleActionsBackend service)
            {
                return service.PlayVideos(files.ToArray());
            },progress);
        }
        internal string OpenExtra(Func<ISimpleActionsBackend,SimpleVideoLaunch> operation,Action<string> progress)
        {
            return Locked(delegate
            {
                Report(progress,"正在准备中文画面增强…");backend.Prepare(progress);
                SimpleVideoLaunch launch=operation(backend);
                if(launch!=null&&!String.IsNullOrEmpty(launch.Executable))ClearPending(new[]{launch.Executable});
                return launch==null?"已请求启动。":launch.Message;
            });
        }
        internal string Open(string selection,Action<string> progress)
        {
            return Locked(delegate
            {
                string target=CanonicalTarget(selection,true);string extension=Path.GetExtension(target);
                bool game=String.Equals(extension,".exe",StringComparison.OrdinalIgnoreCase);
                if(game&&WebVideoSupport.IsBrowserExecutable(target))throw new InvalidOperationException("网页视频请使用“网页视频”入口选择浏览器窗口。");
                if(!game&&!IsVideo(extension))throw new InvalidOperationException("请选择游戏 .exe 或本地视频文件。");
                Report(progress,"正在自动准备 NR 组件…");backend.Prepare(progress);
                if(!game)
                {
                    Report(progress,"正在启动视频播放器…");var launch=backend.PlayVideo(target);
                    if(launch!=null&&!String.IsNullOrEmpty(launch.Executable))ClearPending(new[]{launch.Executable});
                    return "已请求播放，使用均衡 NR 配置。\r\n"+(launch==null?"":launch.Message)+"\r\n实际 NR 是否执行以本次日志为准。";
                }
                string[] related;string launcher;GamePlan(target,out related,out launcher);
                foreach(string exe in related){Report(progress,"正在准备游戏："+Path.GetFileName(Path.GetDirectoryName(exe)));Report(progress,backend.EnsureGame(exe));}
                Report(progress,"正在启动游戏客户端…");
                try{backend.LaunchGame(launcher);}catch(Exception ex){throw new InvalidOperationException("NR 组件已准备，但程序未能启动。可重试或点击取消增强。"+ex.Message,ex);}
                ClearPending(related);
                return "已准备均衡 NR 并请求启动游戏。\r\n"+launcher+"\r\n尚未确认游戏已加载 NR；实际画面和性能以游戏及本次日志为准。";
            });
        }
        internal string CancelAll(Action<string> progress)
        {
            return Locked(delegate
            {
                var pending=Managed();SavePending(pending); // Durable intent precedes restoration of any managed target.
                try{backend.StopOwnedPlayers(ToArray(pending),progress);}catch(Exception ex){Report(progress,"独立播放器未能全部关闭："+ex.Message);}
                return CompletePending(pending,progress,true);
            });
        }
        internal string PollPending(Action<string> progress)
        {
            return Locked(delegate
            {
                if(!File.Exists(pendingFile))return "";
                return CompletePending(LoadPending(Managed()),progress,false);
            });
        }
        string CompletePending(HashSet<string> pending,Action<string> progress,bool explicitCancel)
        {
            int restored=0,retained=0,waiting=0,failed=0;bool changed=false;
            foreach(string exe in ToArray(pending))
            {
                try
                {
                if(backend.IsRunning(exe)){waiting++;if(explicitCancel)Report(progress,"程序仍在运行，退出后自动撤回："+exe);continue;}
                    string result=backend.Restore(exe);
                    bool coreLeft=backend.HasCoreFiles(exe);if(coreLeft)retained++;else restored++;
                    pending.Remove(exe);changed=true;SavePending(pending);
                    Report(progress,(coreLeft?"有被修改的组件保留，请按提示处理：":"已撤回 NR 加载组件：")+exe+"\r\n"+result);
                }
                catch(Exception ex){failed++;if(explicitCancel)Report(progress,"暂未撤回，将稍后重试："+exe+"\r\n"+ex.Message);}
            }
            if(pending.Count==0)SavePending(pending);
            if(!explicitCancel&&!changed)return "";
            if(restored==0&&retained==0&&waiting==0&&failed==0)return explicitCancel?"没有需要撤回的 NR 安装；未改动其他文件。":"";
            var summary=new StringBuilder("取消增强：已撤回 "+restored+" 个目标的 NR 加载组件。");
            if(retained>0)summary.Append(" ").Append(retained).Append(" 个目标含已改动组件，已保留供手动检查。");
            if(waiting>0)summary.Append(" ").Append(waiting).Append(" 个目标仍在运行，退出后自动撤回；不会强制关闭游戏或 PotPlayer。");
            if(failed>0)summary.Append(" ").Append(failed).Append(" 个目标暂未完成，将自动重试。");
            summary.Append("\r\n修改过的配置、日志及第三方文件会保留。请保持此窗口打开以等待退出后的撤回。");return summary.ToString();
        }
        void ClearPending(IEnumerable<string> targets)
        {
            if(!File.Exists(pendingFile))return;var pending=LoadPending(Managed());foreach(string exe in targets)pending.Remove(CanonicalTarget(exe,false));SavePending(pending);
        }
        HashSet<string> Managed()
        {
            var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string exe in backend.ManagedTargets())result.Add(CanonicalTarget(exe,false));return result;
        }
        HashSet<string> LoadPending(HashSet<string> managed)
        {
            var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);if(!File.Exists(pendingFile))return result;
            try
            {
                NoReparse(pendingFile);if(new FileInfo(pendingFile).Length>65536)throw new FormatException("记录过大");
                var xml=new XmlDocument{XmlResolver=null};using(var reader=XmlReader.Create(pendingFile,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))xml.Load(reader);
                var root=xml.DocumentElement;if(root==null||root.Name!="pending"||root.GetAttribute("version")!="1")throw new FormatException("记录版本无效");
                int count=0;foreach(XmlNode child in root.ChildNodes)
                {
                    var item=child as XmlElement;if(item==null||item.Name!="target"||++count>1024)throw new FormatException("记录条目无效");
                    string exe=CanonicalTarget(item.GetAttribute("exe"),false);if(managed.Contains(exe))result.Add(exe);
                }
                return result;
            }
            catch(Exception ex){throw new InvalidOperationException("待撤回记录无法验证，未处理未知目标。可重新点击取消增强，按已验证安装记录重新建立任务。",ex);}
        }
        void SavePending(HashSet<string> targets)
        {
            NoReparse(pendingFile);
            if(targets.Count==0){if(File.Exists(pendingFile))File.Delete(pendingFile);return;}
            string directory=Path.GetDirectoryName(pendingFile);
            string temp=pendingFile+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                NoReparse(directory);Directory.CreateDirectory(directory);NoReparse(directory);
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    using(var writer=XmlWriter.Create(file,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,CloseOutput=false}))
                    {writer.WriteStartElement("pending");writer.WriteAttributeString("version","1");foreach(string exe in targets){writer.WriteStartElement("target");writer.WriteAttributeString("exe",exe);writer.WriteEndElement();}writer.WriteEndElement();}
                    file.Flush(true);
                }
                if(File.Exists(pendingFile))File.Replace(temp,pendingFile,null);else File.Move(temp,pendingFile);
            }
            catch(Exception ex){throw new InvalidOperationException("无法保存退出后撤回记录，已停止本次自动撤回。"+ex.Message,ex);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        string Locked(Func<string> operation)
        {
            using(var mutex=new Mutex(false,mutexName))
            {bool owned=false;try{try{owned=mutex.WaitOne(5000);}catch(AbandonedMutexException){owned=true;}if(!owned)throw new InvalidOperationException("另一个增强或撤回操作正在进行。");return operation();}finally{if(owned)mutex.ReleaseMutex();}}
        }
        internal static string CanonicalTarget(string path,bool mustExist)
        {
            if(String.IsNullOrWhiteSpace(path)||path.Length<3||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.StartsWith(@"\\",StringComparison.Ordinal)||path.IndexOf(':',2)>=0)throw new InvalidOperationException("请选择本机磁盘中的完整文件路径。");
            foreach(string part in path.Substring(3).Split('\\','/'))if(part==".."||part=="."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidOperationException("文件路径包含不明确的目录段。");
            string full;try{full=Path.GetFullPath(path);}catch(Exception ex){throw new InvalidOperationException("文件路径无效。",ex);}NoReparse(full);
            if(mustExist&&!File.Exists(full))throw new InvalidOperationException("文件不存在："+full);return full;
        }
        static bool IsVideo(string extension){return ("|.mp4|.mkv|.mov|.avi|.webm|.ts|.mts|.m2ts|.m4v|.wmv|.mpg|.mpeg|.flv|.vob|").IndexOf("|"+extension.ToLowerInvariant()+"|",StringComparison.Ordinal)>=0;}
        static void GamePlan(string selected,out string[] related,out string launcher)
        {
            var list=new List<string>{selected};launcher=selected;var variant=new DirectoryInfo(Path.GetDirectoryName(selected));
            if(String.Equals(Path.GetFileName(selected),"yysls.exe",StringComparison.OrdinalIgnoreCase)&&(variant.Name.Equals("Win64r",StringComparison.OrdinalIgnoreCase)||variant.Name.Equals("Win64rh",StringComparison.OrdinalIgnoreCase))&&variant.Parent!=null&&variant.Parent.Name.Equals("Binaries",StringComparison.OrdinalIgnoreCase)&&variant.Parent.Parent!=null&&variant.Parent.Parent.Name.Equals("Engine",StringComparison.OrdinalIgnoreCase))
            {
                string other=Path.Combine(variant.Parent.FullName,variant.Name.Equals("Win64r",StringComparison.OrdinalIgnoreCase)?"Win64rh":"Win64r","yysls.exe");
                if(File.Exists(other))list.Add(CanonicalTarget(other,true));
                var client=variant.Parent.Parent.Parent;
                if(client!=null&&client.Parent!=null){string candidate=Path.Combine(client.Parent.FullName,"Win32","deploy","launcher.exe");if(File.Exists(candidate))launcher=CanonicalTarget(candidate,true);}
            }
            related=list.ToArray();
        }
        static void NoReparse(string path){string p=Path.GetFullPath(path);while(!String.IsNullOrEmpty(p)){if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("不支持经过符号链接或目录联接的路径。");string parent=Path.GetDirectoryName(p);if(parent==p)break;p=parent;}}
        static string[] ToArray(HashSet<string> values){var result=new string[values.Count];values.CopyTo(result);return result;}
        static void Report(Action<string> progress,string text){if(progress!=null&&!String.IsNullOrEmpty(text))progress(text);}
    }
    internal sealed class NativeSimpleActionsBackend : ISimpleActionsBackend
    {
        public string Prepare(Action<string> progress){return NeuralPackages.Prepare(progress);}
        public string EnsureGame(string exe){string result=NeuralInstaller.EnsureInstalled(exe,NeuralPackages.BundleDirectory,NeuralPresets.BuildGameIni(1));NeuralLanguage.ApplyChinese(exe);return result;}
        public void LaunchGame(string exe)
        {
            using(var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(exe)}))
            {if(process==null)throw new InvalidOperationException("程序未能启动。");if(process.WaitForExit(500)&&process.ExitCode!=0)throw new InvalidOperationException("程序已退出，代码 "+process.ExitCode+"。");}
        }
        public SimpleVideoLaunch PlayVideo(string video)
        {
            return PlayVideos(new[]{video});
        }
        public SimpleVideoLaunch PlayVideos(string[] videos)
        {
            string pot=PotPlayerSupport.FindPlayer();
            if(!String.IsNullOrEmpty(pot))return new SimpleVideoLaunch{Executable=pot,Message=PotPlayerSupport.Play(videos,null)};
            string source=VideoSupport.FindMpv();if(String.IsNullOrEmpty(source))throw new InvalidOperationException("未找到随附的干净 mpv 播放器。");
            string message=videos.Length==1?NeuralPlayback.Play(source,videos[0],1):NeuralPlayback.PlayMany(source,videos,1);return new SimpleVideoLaunch{Executable=NeuralPlayback.LastPlayer,Message=message};
        }
        public SimpleVideoLaunch OpenPlayer(){string player=PotPlayerSupport.FindPlayer();if(String.IsNullOrEmpty(player))throw new InvalidOperationException("未找到 PotPlayer，请先安装或选择本地视频使用随附播放器。");return new SimpleVideoLaunch{Executable=player,Message=PotPlayerSupport.Play(new string[0],null)};}
        public SimpleVideoLaunch CaptureBrowser(IntPtr window)
        {
            string message=WebVideoSupport.Play(window,null);return new SimpleVideoLaunch{Executable=NeuralPlayback.LastPlayer,Message=message};
        }
        public string[] ManagedTargets(){return NeuralInstaller.ManagedTargets();}
        public bool IsRunning(string exe)
        {
            bool unknown=false;foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))using(process)
            {try{if(String.Equals(Path.GetFullPath(process.MainModule.FileName),exe,StringComparison.OrdinalIgnoreCase))return true;}catch(System.ComponentModel.Win32Exception){unknown=true;}catch(InvalidOperationException){}}
            return unknown; // Do not restore loaded files if a same-name process cannot be identified safely.
        }
        public bool HasCoreFiles(string exe){string dir=Path.GetDirectoryName(exe);foreach(string file in new[]{"dxgi.dll","renodx-dlss5.addon64","nvngx_dlssnr.dll"})if(File.Exists(Path.Combine(dir,file)))return true;return false;}
        public string Restore(string exe){return NeuralInstaller.Restore(exe);}
        internal static bool IsPrivatePlayerPath(string exe,string playerRoot)
        {
            string full=SimpleActionService.CanonicalTarget(exe,false);string root=Path.GetFullPath(playerRoot);
            foreach(string preset in new[]{"light","balanced","quality"})if(String.Equals(full,Path.Combine(root,preset,"mpv.exe"),StringComparison.OrdinalIgnoreCase))return true;return false;
        }
        public void StopOwnedPlayers(string[] targets,Action<string> progress)
        {
            string playerRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","neural-player");var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string target in targets)if(IsPrivatePlayerPath(target,playerRoot))allowed.Add(target);
            foreach(var process in Process.GetProcessesByName("mpv"))using(process)
            {
                try
                {
                    string path=Path.GetFullPath(process.MainModule.FileName);if(!allowed.Contains(path)||!IsPrivatePlayerPath(path,playerRoot))continue;
                    DateTime started=process.StartTime;process.CloseMainWindow();if(!process.WaitForExit(1000)&&process.StartTime==started){process.Kill();process.WaitForExit(2000);}
                    if(progress!=null)progress("已请求关闭本工具的独立 NR 播放器。");
                }
                catch(System.ComponentModel.Win32Exception ex){if(progress!=null)progress("无法确认或关闭独立播放器，将等待退出："+ex.Message);}
                catch(InvalidOperationException){}
            }
        }
    }
}
