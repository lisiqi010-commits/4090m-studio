using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
namespace RtxHub
{
 public static class FrameGenerationSupport
 {
  public static string Validate(string exe)
  {
   if(String.IsNullOrWhiteSpace(exe)||!Path.IsPathRooted(exe)||exe.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidOperationException("请选择本地的 64 位游戏程序。");
   exe=Path.GetFullPath(exe);string folder=Path.GetDirectoryName(exe);NeuralInstallService.CheckDirectory(folder,true);
   if(!File.Exists(exe)||NeuralInstallService.IsReparse(exe))throw new InvalidOperationException("游戏程序不存在或经过链接。");
   using(var stream=File.OpenRead(exe))if(!ImportsWinmm(stream))throw new InvalidOperationException("这个程序没有可验证的 WINMM 加载入口，暂不能自动安装补帧；请选游戏实际的 64 位渲染程序。");
   string sl=Find(folder,"sl.interposer.dll"),plugin=Find(folder,"sl.dlss_g.dll"),fg=Find(folder,"nvngx_dlssg.dll");
   if(plugin==null)throw new InvalidOperationException("游戏缺少原生帧生成插件 sl.dlss_g.dll；本轮补帧不能使用此目录，不会替换官方文件。");
   if(sl==null||fg==null)throw new InvalidOperationException("未找到可验证的原生 DLSS 帧生成运行库；本轮补帧需要游戏自带 Streamline 2.7.1+ 与 DLSSG 310.9。");
   ValidateNativeDll(sl);ValidateNativeDll(plugin);ValidateNativeDll(fg);
   var sv=FileVersionInfo.GetVersionInfo(sl);var pv=FileVersionInfo.GetVersionInfo(plugin);var fv=FileVersionInfo.GetVersionInfo(fg);
   Version streamline=new Version(sv.FileMajorPart,sv.FileMinorPart,sv.FileBuildPart,sv.FilePrivatePart),dlssg=new Version(fv.FileMajorPart,fv.FileMinorPart,fv.FileBuildPart,fv.FilePrivatePart);
   ValidateVersions(streamline,new Version(pv.FileMajorPart,pv.FileMinorPart,pv.FileBuildPart,pv.FilePrivatePart),dlssg);
   foreach(string name in new[]{"msvcp140.dll","msvcp140_atomic_wait.dll","vcruntime140.dll","vcruntime140_1.dll"})
    if(!File.Exists(Path.Combine(Environment.SystemDirectory,name)))throw new InvalidOperationException("缺少补帧所需的 VC++ x64 运行库："+name+"。请安装微软官方 Visual C++ 2015–2022 x64 运行库后重试。");
   return "已识别原生 Streamline "+streamline+" / DLSSG "+dlssg+"；游戏内仍需开启 DLSS 帧生成。";
  }
  static string Find(string folder,string name)
  {
   foreach(string dir in new[]{Path.Combine(folder,"Streamline"),folder})
   {
    string file=Path.Combine(dir,name);if(!File.Exists(file))continue;NeuralInstallService.CheckDirectory(dir,true);
    if(NeuralInstallService.IsReparse(file))throw new InvalidOperationException("游戏运行库经过链接，未准备补帧。");return file;
   }
   return null;
  }
  internal static void ValidateVersions(Version streamline,Version dlssg)
  {ValidateVersions(streamline,streamline,dlssg);}
  internal static void ValidateVersions(Version streamline,Version plugin,Version dlssg)
  {
   if(streamline==null||streamline.Major!=2||streamline.CompareTo(new Version(2,7,1,0))<0)throw new InvalidOperationException("游戏的 Streamline 版本未达到本补帧路径要求（2.7.1+ 的 2.x）；不会替换游戏官方文件。");
   if(plugin==null||plugin.Major!=streamline.Major||plugin.Minor!=streamline.Minor||plugin.Build!=streamline.Build)throw new InvalidOperationException("原生帧生成插件 sl.dlss_g.dll 与 Streamline 版本不匹配，未安装补帧。");
   if(dlssg==null||dlssg.Major!=310||dlssg.Minor!=9)throw new InvalidOperationException("游戏的 DLSSG 不是本补帧路径核实的 310.9 版本；保留原运行库，未请求多倍补帧。");
  }
  static void ValidateNativeDll(string file)
  {
   try{using(var stream=File.OpenRead(file))using(var reader=new BinaryReader(stream)){Range(stream,0,64);if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException();stream.Position=0x3c;uint pe=reader.ReadUInt32();Range(stream,pe,26);stream.Position=pe;if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=0x8664)throw new InvalidDataException();stream.Position=pe+24;if(reader.ReadUInt16()!=0x20b)throw new InvalidDataException();}}
   catch(Exception ex){if(!(ex is IOException)&&!(ex is InvalidDataException))throw;throw new InvalidOperationException("游戏帧生成运行库不是有效的 x64 PE："+Path.GetFileName(file),ex);}
  }
  internal static bool ImportsWinmm(Stream stream)
  {
   try
   {
    using(var reader=new BinaryReader(stream,Encoding.ASCII,true))
    {
     Range(stream,0,64);if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException();stream.Position=0x3c;uint pe=reader.ReadUInt32();Range(stream,pe,24);stream.Position=pe;
     if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=0x8664)throw new InvalidDataException();int count=reader.ReadUInt16();if(count<1||count>96)throw new InvalidDataException();stream.Position=pe+20;int optionalSize=reader.ReadUInt16();long optional=pe+24L;
     Range(stream,optional,optionalSize);if(optionalSize<128)throw new InvalidDataException();stream.Position=optional;if(reader.ReadUInt16()!=0x20b)throw new InvalidDataException();stream.Position=optional+120;uint imports=reader.ReadUInt32();if(imports==0)return false;
     var sections=new List<Section>();for(int i=0;i<count;i++){long offset=optional+optionalSize+i*40L;Range(stream,offset,40);stream.Position=offset+12;uint va=reader.ReadUInt32(),rawSize=reader.ReadUInt32(),raw=reader.ReadUInt32();sections.Add(new Section{Address=va,Size=rawSize,Raw=raw});}
     long descriptor=Map(stream,sections,imports);bool found=false;
     for(int n=0;n<1024;n++)
     {
      Range(stream,descriptor+n*20L,20);stream.Position=descriptor+n*20L;uint thunk=reader.ReadUInt32(),stamp=reader.ReadUInt32(),forward=reader.ReadUInt32(),name=reader.ReadUInt32(),first=reader.ReadUInt32();
      if((thunk|stamp|forward|name|first)==0)return found;if(name==0)throw new InvalidDataException();stream.Position=Map(stream,sections,name);var text=new StringBuilder();bool ended=false;
      for(int c=0;c<260;c++){Range(stream,stream.Position,1);byte b=reader.ReadByte();if(b==0){ended=true;break;}if(b<32||b>126)throw new InvalidDataException();text.Append((char)b);}if(!ended)throw new InvalidDataException();if(String.Equals(text.ToString(),"winmm.dll",StringComparison.OrdinalIgnoreCase))found=true;
     }
     throw new InvalidDataException();
    }
   }
   catch(Exception ex){if(!(ex is IOException)&&!(ex is InvalidDataException)&&!(ex is OverflowException)&&!(ex is ArgumentException))throw;throw new InvalidOperationException("无法验证游戏 PE 的 64 位加载入口，未安装补帧。",ex);}
  }
  sealed class Section {internal uint Address,Size,Raw;}
  static long Map(Stream stream,List<Section> sections,uint rva){foreach(var s in sections)if(rva>=s.Address&&(ulong)rva<(ulong)s.Address+s.Size){long offset=(long)s.Raw+rva-s.Address;Range(stream,offset,1);return offset;}throw new InvalidDataException();}
  static void Range(Stream stream,long offset,long size){if(offset<0||size<0||offset>stream.Length||size>stream.Length-offset)throw new InvalidDataException();}
 }
}
