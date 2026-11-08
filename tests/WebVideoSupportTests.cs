using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RtxHub;

internal static class WebVideoSupportTests
{
    static int checks;
    static void Assert(bool value,string message){checks++;if(!value)throw new Exception("FAIL: "+message);}
    static void Throws(Action action,string message){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Assert(failed,message);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length>1&&(args[0]=="--probe-clean"||args[0]=="--probe-nr"))return CaptureOwnedFixture(args[1],args[0]=="--probe-nr");
            string built=WebVideoSupport.BuildArguments(new IntPtr(4660));
            Assert(built.Contains("gfxcapture=hwnd=4660:max_framerate=30:capture_cursor=0:output_fmt=bgra,hwdownload,format=bgra[vo]"),"capture graph targets one explicit HWND and bridges hardware frames to BGRA");
            Assert(built.Contains("--audio=no")&&built.Contains("--interpolation=no"),"viewer leaves browser audio alone and performs no frame interpolation");
            Assert(built.Contains("--d3d11-adapter=NVIDIA")&&built.Contains("--d3d11-output-csp=srgb"),"capture is rendered through the existing NVIDIA SDR path");
            Assert(built.Contains("--no-config")&&built.Contains("--load-scripts=no")&&built.Contains("--window-affinity=excludefromcapture"),"viewer uses isolated settings and cannot recursively capture itself");
            Assert(built.EndsWith(" -- \"null://\"",StringComparison.Ordinal),"capture source is a fixed mpv input rather than shell or user URL text");
            Throws(delegate{WebVideoSupport.BuildArguments(IntPtr.Zero);},"zero window handle is rejected");
            Throws(delegate{WebVideoSupport.BuildArguments(new IntPtr(-1));},"pseudo handles cannot become capture targets");
            foreach(string browser in new[]{"chrome.exe","msedge.exe","firefox.exe","brave.exe","vivaldi.exe","opera.exe"})Assert(WebVideoSupport.IsBrowserExecutable(@"C:\Browser\"+browser),"supported browser identity: "+browser);
            foreach(string other in new[]{"chromedriver.exe","mpv.exe","yysls.exe","chrome.exe.backup","explorer.exe"})Assert(!WebVideoSupport.IsBrowserExecutable(other),"non-browser process is not a capture target: "+other);
            var owner=new BrowserWindowOwner(new IntPtr(4660),42,1000,@"C:\Browser\chrome.exe");
            Assert(owner.SameOwner(new BrowserWindowOwner(new IntPtr(4660),42,1000,@"c:\browser\CHROME.EXE")),"same browser owner permits a case-normalized path");
            Assert(!owner.SameOwner(new BrowserWindowOwner(new IntPtr(4660),43,1000,@"C:\Browser\chrome.exe")),"reused window handle with another PID is rejected");
            Assert(!owner.SameOwner(new BrowserWindowOwner(new IntPtr(4660),42,2000,@"C:\Browser\chrome.exe")),"reused process ID with a different process lifetime is rejected");
            Assert(!owner.SameOwner(new BrowserWindowOwner(new IntPtr(4660),42,1000,@"C:\Other\chrome.exe")),"changed executable ownership is rejected");
            Throws(delegate{WebVideoSupport.Play(IntPtr.Zero,null);},"an invalid target is refused before package or browser operations");
            Console.WriteLine("PASS: "+checks+" browser capture argument/identity checks; no browser pixels read.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static int CaptureOwnedFixture(string player,bool neural)
    {
        if(neural)
        {
            player=NeuralPlayback.PreparePlayer(Path.GetFullPath(player),1);
            string previous=Path.Combine(Path.GetDirectoryName(player),"ReShade.log");
            if(File.Exists(previous))File.Move(previous,previous+".before-owned-capture-"+Guid.NewGuid().ToString("N"));
        }
        Application.EnableVisualStyles();using(var form=new Form())
        {
            form.Text="Owned NR capture fixture";form.Size=new Size(384,256);form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;
            int frame=0;form.Paint+=delegate(object sender,PaintEventArgs e){using(var brush=new SolidBrush(Color.FromArgb(255,60+(frame%150),90,150)))e.Graphics.FillRectangle(brush,form.ClientRectangle);};
            using(var timer=new System.Windows.Forms.Timer())
            {
                timer.Interval=60;timer.Tick+=delegate{frame++;form.Invalidate();};timer.Start();form.Show();Application.DoEvents();
                string arguments=WebVideoSupport.BuildArguments(form.Handle).Replace("--terminal=no","--terminal=yes");
                if(!neural)arguments=arguments.Replace("--vo=gpu","--vo=null");
                arguments=arguments.Replace(" -- \"null://\""," --frames=12 --window-minimized=yes -- \"null://\"");
                var start=new ProcessStartInfo(Path.GetFullPath(player),arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(player))};
                using(var process=Process.Start(start))
                {
                    if(process==null)throw new Exception("Fixture player did not start.");
                    var output=new StringBuilder();object gate=new object();DataReceivedEventHandler receive=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(gate)output.AppendLine(e.Data);};process.OutputDataReceived+=receive;process.ErrorDataReceived+=receive;process.BeginOutputReadLine();process.BeginErrorReadLine();
                    var watch=Stopwatch.StartNew();while(!process.HasExited&&watch.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(20);}
                    if(!process.HasExited){process.Kill();process.WaitForExit();throw new Exception("Owned capture fixture exceeded 15 seconds.");}process.WaitForExit();Console.WriteLine("Owned fixture capture exit: "+process.ExitCode);Console.WriteLine(output.ToString());
                    return process.ExitCode;
                }
            }
        }
    }
}
