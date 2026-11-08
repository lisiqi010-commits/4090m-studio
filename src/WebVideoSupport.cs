using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace RtxHub
{
    public sealed class BrowserWindow {public IntPtr Handle;public string Title;}
    public static class WebVideoSupport
    {
        // FFmpeg's gfxcapture uses Windows Graphics Capture. Only the explicitly
        // selected HWND supplies pixels; the browser receives no DLL or setting changes.
        // https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vsrc_gfxcapture.c
        // The bundled mpv 55d3feb45 supports the filter and its source-only lavfi graph.
        public static string LastLog {get;private set;}
        delegate bool EnumWindowProc(IntPtr hwnd,IntPtr parameter);
        [DllImport("user32.dll")]static extern bool EnumWindows(EnumWindowProc callback,IntPtr parameter);
        [DllImport("user32.dll")]static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int capacity);
        [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr hwnd,out NativeRect rect);
        [StructLayout(LayoutKind.Sequential)]struct NativeRect {public int Left,Top,Right,Bottom;}
        public static BrowserWindow[] ListBrowserWindows()
        {
            var windows=new List<BrowserWindow>();
            EnumWindowProc callback=delegate(IntPtr hwnd,IntPtr ignored)
            {
                try
                {
                    ReadOwner(hwnd);NativeRect rect;if(!GetClientRect(hwnd,out rect)||rect.Right-rect.Left<160||rect.Bottom-rect.Top<90)return true;
                    var text=new StringBuilder(512);GetWindowText(hwnd,text,text.Capacity);string title=text.ToString().Replace('\r',' ').Replace('\n',' ').Trim();
                    if(title.Length>0)windows.Add(new BrowserWindow{Handle=hwnd,Title=title});
                }
                catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                return true;
            };
            if(!EnumWindows(callback,IntPtr.Zero))throw new InvalidOperationException("无法列出浏览器窗口，请先恢复并打开要观看的窗口。");
            windows.Sort(delegate(BrowserWindow a,BrowserWindow b){return StringComparer.CurrentCultureIgnoreCase.Compare(a.Title,b.Title);});return windows.ToArray();
        }
        public static string Play(IntPtr hwnd,Action<string> progress)
        {
            var selected=ReadOwner(hwnd);string expectedPlayer=Path.Combine(NeuralPlayback.PlayerDirectory(1),"mpv.exe");CheckPlayerAvailable(expectedPlayer);
            Report(progress,"正在为所选浏览器窗口准备独立 NR 播放器…");NeuralPackages.Prepare(progress);
            string source=VideoSupport.FindMpv();if(String.IsNullOrEmpty(source))throw new InvalidOperationException("未找到随附的干净 mpv 播放器。");
            string player=NeuralPlayback.PreparePlayer(source,1);CheckPlayerAvailable(player);
            if(!selected.SameOwner(ReadOwner(hwnd)))throw new InvalidOperationException("所选窗口的所属进程已改变，请重新选择浏览器窗口。");
            string directory=Path.GetDirectoryName(player);string log=Path.Combine(directory,"ReShade.log");RotateLog(log);
            string captureLog=Path.Combine(directory,"web-capture-mpv.log");RotateLog(captureLog);
            // Recheck immediately before the capture host is started to reject HWND/PID reuse.
            if(!selected.SameOwner(ReadOwner(hwnd)))throw new InvalidOperationException("所选浏览器窗口已关闭或被替换，请重新选择。");
            string arguments="--log-file="+VideoSupport.QuoteWindowsArgument(captureLog)+" "+BuildArguments(hwnd);
            var info=new ProcessStartInfo(player,arguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory};LastLog=log;
            using(var process=Process.Start(info))
            {
                if(process==null)throw new InvalidOperationException("网页捕获播放器未能启动。");
                if(process.WaitForExit(2000))
                {
                    if(process.ExitCode!=0)throw new InvalidOperationException("网页窗口捕获未启动，退出码 "+process.ExitCode+"。捕获日志："+captureLog);
                    return "网页捕获播放器已退出，尚未确认 NR 处理。\r\n"+NeuralPresets.AnalyzeLog(log);
                }
            }
            return "已请求捕获所选浏览器窗口并以 NR 播放；播放和音量仍在原浏览器控制。\r\n请保持源窗口未最小化，F6 对比、F11 查看开销。受保护视频可能黑屏，不会绕过保护。\r\n"+NeuralPresets.AnalyzeLog(log)+"\r\nNR 日志："+log+"\r\n捕获日志："+captureLog;
        }
        public static string BuildArguments(IntPtr hwnd)
        {
            long handle=hwnd.ToInt64();if(handle<=0)throw new InvalidOperationException("请先选择一个有效的浏览器窗口。");
            string graph="gfxcapture=hwnd="+handle.ToString(CultureInfo.InvariantCulture)+":max_framerate=30:capture_cursor=0:output_fmt=bgra,hwdownload,format=bgra[vo]";
            return "--no-config --load-scripts=no --input-terminal=no --terminal=no --vo=gpu --gpu-api=d3d11 --gpu-context=d3d11 --d3d11-adapter=NVIDIA --hwdec=no --interpolation=no --d3d11-output-csp=srgb --target-trc=srgb --target-prim=bt.709 --audio=no --untimed --cache=no --force-window=yes --keep-open=no --window-affinity=excludefromcapture --autofit-larger=1280x720 --force-media-title="+VideoSupport.QuoteWindowsArgument("网页视频 NR · 原浏览器控制播放")+" --lavfi-complex="+graph+" -- "+VideoSupport.QuoteWindowsArgument("null://");
        }
        static BrowserWindowOwner ReadOwner(IntPtr hwnd)
        {
            if(hwnd.ToInt64()<=0||!IsWindow(hwnd)||!IsWindowVisible(hwnd))throw new InvalidOperationException("所选浏览器窗口已关闭或不可见，请重新选择。");
            if(IsIconic(hwnd))throw new InvalidOperationException("浏览器窗口已最小化，请先恢复窗口再开启网页增强。");
            uint id;uint thread=GetWindowThreadProcessId(hwnd,out id);if(thread==0||id==0||id>Int32.MaxValue)throw new InvalidOperationException("无法确认所选窗口的所属进程。");
            try
            {
                using(var process=Process.GetProcessById((int)id))
                {
                    string exe=process.MainModule.FileName;if(!IsBrowserExecutable(exe))throw new InvalidOperationException("只能选择 Chrome、Edge、Firefox 等浏览器窗口；不会捕获其他应用。");
                    return new BrowserWindowOwner(hwnd,(int)id,process.StartTime.ToUniversalTime().Ticks,Path.GetFullPath(exe));
                }
            }
            catch(System.ComponentModel.Win32Exception ex){throw new InvalidOperationException("无法验证浏览器进程身份，未开始捕获。请重新选择可访问的浏览器窗口。",ex);}
            catch(ArgumentException ex){throw new InvalidOperationException("浏览器进程已退出，请重新选择窗口。",ex);}
        }
        internal static bool IsBrowserExecutable(string path)
        {
            if(String.IsNullOrWhiteSpace(path))return false;string name=Path.GetFileName(path);
            foreach(string browser in new[]{"chrome.exe","msedge.exe","firefox.exe","brave.exe","vivaldi.exe","opera.exe"})if(String.Equals(name,browser,StringComparison.OrdinalIgnoreCase))return true;return false;
        }
        static void CheckPlayerAvailable(string player)
        {
            foreach(var process in Process.GetProcessesByName("mpv"))using(process)
            {
                try{if(String.Equals(Path.GetFullPath(process.MainModule.FileName),Path.GetFullPath(player),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("独立 NR 播放器正在播放影片或网页，请先关闭该窗口再选择新的网页。");}
                catch(System.ComponentModel.Win32Exception){}
            }
        }
        static void RotateLog(string file)
        {
            if(!File.Exists(file))return;if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("播放器日志经过链接，已停止启动以保留原文件。");
            File.Move(file,file+"."+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"."+Guid.NewGuid().ToString("N")+".previous");
        }
        static void Report(Action<string> progress,string message){if(progress!=null)progress(message);}
    }
    internal sealed class BrowserWindowOwner
    {
        internal readonly IntPtr Handle;internal readonly int ProcessId;internal readonly long Started;internal readonly string Executable;
        internal BrowserWindowOwner(IntPtr handle,int processId,long started,string executable){Handle=handle;ProcessId=processId;Started=started;Executable=executable;}
        internal bool SameOwner(BrowserWindowOwner other){return other!=null&&Handle==other.Handle&&ProcessId==other.ProcessId&&Started==other.Started&&String.Equals(Executable,other.Executable,StringComparison.OrdinalIgnoreCase);}
    }
}
