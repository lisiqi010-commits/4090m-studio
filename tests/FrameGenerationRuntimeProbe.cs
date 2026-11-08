using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RtxHub;
internal static class FrameGenerationRuntimeProbe
{
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
 [DllImport("kernel32.dll",CharSet=CharSet.Ansi,SetLastError=true)]static extern IntPtr GetProcAddress(IntPtr module,string name);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern uint GetModuleFileName(IntPtr module,StringBuilder file,int size);
 [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint TimerFunction();
 static int Main()
 {
  try
  {
   string folder=AppDomain.CurrentDomain.BaseDirectory;
   if(!String.Equals(new DirectoryInfo(folder).Name,"framegen-probe",StringComparison.Ordinal))throw new InvalidOperationException("Run only from the app-owned framegen-probe fixture directory.");
   string dll=Path.Combine(folder,"winmm.dll");
   File.WriteAllText(Path.Combine(folder,"OptiScaler.ini"),FrameGenerationPresets.BuildIni(6)+"\r\n[ProcessFilter]\r\nTargetProcessName=FrameGenerationRuntimeProbe.exe\r\n",new UTF8Encoding(false));
   IntPtr module=LoadLibraryEx(dll,IntPtr.Zero,0);if(module==IntPtr.Zero)throw new InvalidOperationException("Wrapper load failed: "+Marshal.GetLastWin32Error());
   var path=new StringBuilder(4096);GetModuleFileName(module,path,path.Capacity);
   if(!String.Equals(Path.GetFullPath(path.ToString()),dll,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The fixture did not load its private wrapper.");
   IntPtr symbol=GetProcAddress(module,"timeGetTime");if(symbol==IntPtr.Zero)throw new InvalidOperationException("WINMM forwarding export missing.");
   var timer=(TimerFunction)Marshal.GetDelegateForFunctionPointer(symbol,typeof(TimerFunction));uint before=timer();System.Threading.Thread.Sleep(100);uint after=timer();
   if(unchecked(after-before)>5000)throw new InvalidOperationException("Timer forwarding returned an unexpected result.");
   Console.WriteLine("PASS: private Aurora wrapper loaded and WINMM timer forwarding executed. This is not frame-generation evidence.");
   Console.WriteLine("Module: "+path);return 0;
  }
  catch(Exception e){Console.WriteLine(e);return 1;}
 }
}
