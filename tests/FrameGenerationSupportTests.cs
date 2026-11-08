using System;
using System.IO;
using System.Text;
using RtxHub;
internal static class FrameGenerationSupportTests
{
 static int checks;
 static void Check(bool b,string m){checks++;if(!b)throw new Exception(m);}
 static void Refuses(Action a,string m){bool b=false;try{a();}catch(InvalidOperationException){b=true;}Check(b,m);}
 static byte[] Pe(string name){byte[] b=new byte[1536];using(var w=new BinaryWriter(new MemoryStream(b))){w.Write((ushort)0x5a4d);w.BaseStream.Position=0x3c;w.Write(0x80);w.BaseStream.Position=0x80;w.Write(0x4550);w.Write((ushort)0x8664);w.Write((ushort)1);w.BaseStream.Position=0x94;w.Write((ushort)240);w.BaseStream.Position=0x98;w.Write((ushort)0x20b);w.BaseStream.Position=0x98+120;w.Write(0x1000);w.Write(40);w.BaseStream.Position=0x98+240+8;w.Write(512);w.Write(0x1000);w.Write(512);w.Write(512);w.BaseStream.Position=512+12;w.Write(0x1060);w.BaseStream.Position=0x260;w.Write(Encoding.ASCII.GetBytes(name+"\0"));}return b;}
 static int Main(string[] args){try{
  if(args.Length==2&&args[0]=="--probe"){Console.WriteLine(FrameGenerationSupport.Validate(args[1]));return 0;}
  using(var s=new MemoryStream(Pe("WINMM.dll")))Check(FrameGenerationSupport.ImportsWinmm(s),"recognize x64 static WINMM import");
  using(var s=new MemoryStream(Pe("KERNEL32.dll")))Check(!FrameGenerationSupport.ImportsWinmm(s),"unsupported loader route is not assumed");
  byte[] invalid=Pe("WINMM.dll");invalid[0x84]=0x4c;invalid[0x85]=0x01;Refuses(delegate{using(var s=new MemoryStream(invalid))FrameGenerationSupport.ImportsWinmm(s);},"x86 PE refused");
  Refuses(delegate{using(var s=new MemoryStream(new byte[20]))FrameGenerationSupport.ImportsWinmm(s);},"truncated header refused");
  byte[] bad=Pe("WINMM.dll");Array.Copy(BitConverter.GetBytes(Int32.MaxValue),0,bad,512+12,4);Refuses(delegate{using(var s=new MemoryStream(bad))FrameGenerationSupport.ImportsWinmm(s);},"out of file RVA refused");
  FrameGenerationSupport.ValidateVersions(new Version(2,14,16,0),new Version(310,9,16,0));checks++;
  FrameGenerationSupport.ValidateVersions(new Version(2,7,1,0),new Version(310,9,16,0));checks++;
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(2,7,0,0),new Version(310,9,16,0));},"old SL cannot claim MFG override");
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(1,9,0,0),new Version(310,9,16,0));},"SL1 unsupported");
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(3,0,0,0),new Version(310,9,16,0));},"unknown SL generation unsupported");
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(2,14,16,0),new Version(3,10,0,0));},"different DLSSG contract refused");
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(2,14,16,0),new Version(1,9,0,0),new Version(310,9,16,0));},"SL1 FG plugin cannot mix with a new interposer");
  Refuses(delegate{FrameGenerationSupport.ValidateVersions(new Version(2,14,16,0),new Version(2,7,1,0),new Version(310,9,16,0));},"different native SL plugin build is rejected");
  string folder=Path.Combine(Path.GetTempPath(),"fg-support-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try{string game=Path.Combine(folder,"fixture.exe");File.WriteAllBytes(game,Pe("WINMM.dll"));File.WriteAllText(Path.Combine(folder,"sl.interposer.dll"),"fixture");File.WriteAllText(Path.Combine(folder,"nvngx_dlssg.dll"),"fixture");bool missing=false;try{FrameGenerationSupport.Validate(game);}catch(InvalidOperationException e){missing=e.Message.Contains("sl.dlss_g.dll");}Check(missing,"missing actual FG plugin is identified before version checks");}
  finally{foreach(string f in Directory.GetFiles(folder))File.Delete(f);Directory.Delete(folder);}
  Console.WriteLine("PASS: "+checks+" frame-generation support checks.");return 0;
 }catch(Exception e){Console.WriteLine(e);return 1;}}
}
