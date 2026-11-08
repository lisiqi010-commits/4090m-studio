using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RtxHub
{
    // Treat every download as data. No installer, add-on or vendor DLL is executed here.
    public static class NeuralPackages
    {
        static string ToolsRoot {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools");}}
        public static string BundleDirectory {get{return Path.Combine(ToolsRoot,"neural-bundle");}}
        public static string DownloadDirectory {get{return Path.Combine(ToolsRoot,"neural-downloads");}}
        internal static NeuralPackageSpec[] Catalog()
        {
            return new[]{
                new NeuralPackageSpec("reshade-6.8.0-addon.exe","https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe","afe4c8f13048306307983b8b3d41d5bf00a86820440b0e57dea10950e1176445",0,"ReShade64.dll","dxgi.dll",false){Native=true},
                new NeuralPackageSpec("nexus-0.2.2.zip","https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/releases/download/v0.2.2/NR-Nexus-v0.2.2-1e725d07-windows-x64.zip","5a6b81f9acddec6099fa6913bcf40145872b04e18a63e90406de70764e94cc6c",4731136,"renodx-dlss5.addon64","renodx-dlss5.addon64",true){Native=true,Alternate="https://api.github.com/repos/williamstosh/RenoDX-DLSSNR-Nexus/releases/assets/706176352?download=1"},
                new NeuralPackageSpec("nr-310.9.16.zip","https://github.com/RankFTW/rhi-repo/releases/download/dlssnr-310.9.16/nvngx_dlssnr_310.9.16.zip","dbeb1f2790b5c621887db99b1a56ac8bd5cb35207e8c074cbb806710f7460cab",192378839,"nvngx_dlssnr.dll","nvngx_dlssnr.dll",false){Native=true,Version="310.9.16.0",Alternate="https://api.github.com/repos/RankFTW/rhi-repo/releases/assets/678225606?download=1"},
                new NeuralPackageSpec("sr-310.9.16.zip","https://github.com/RankFTW/rhi-repo/releases/download/dlss-310.9.16/nvngx_dlss_310.9.16.zip","1823ec911c77482e6731447dfb0db3def8aa710e092f59f58c4c4c55285d492d",44480231,"nvngx_dlss.dll","nvngx_dlss.dll",false){Native=true,Alternate="https://api.github.com/repos/RankFTW/rhi-repo/releases/assets/637069958?download=1"}
            };
        }
        public static string Prepare(Action<string> progress){return new NeuralPackageStore(ToolsRoot,Catalog()).Prepare(progress,true);}
        public static string Verify(){return new NeuralPackageStore(ToolsRoot,Catalog()).Verify();}
        public static string Status()
        {
            foreach(var s in Catalog())if(!File.Exists(Path.Combine(BundleDirectory,s.Output)))return "社区 NR 组件尚未备齐。可下载，或将固定版本原始包放入缓存后准备。";
            return "组件已准备；启动前仍须验证，尚未据此确认 NR 运行。";
        }
    }
    internal sealed class NeuralPackageSpec
    {
        internal readonly string Name,Url,Hash,Member,Output; internal readonly long Size; internal readonly bool Nexus;
        internal bool Native; internal string Version,Alternate;
        internal NeuralPackageSpec(string name,string url,string hash,long size,string member,string output,bool nexus){Name=name;Url=url;Hash=hash;Size=size;Member=member;Output=output;Nexus=nexus;}
    }
    internal sealed class NeuralPackageStore
    {
        const long MaxExpanded=1073741824L,MaxArchive=536870912L;
        readonly string root,cache; readonly NeuralPackageSpec[] packages;
        internal string Bundle {get{return Path.Combine(root,"neural-bundle");}}
        internal NeuralPackageStore(string toolsRoot,NeuralPackageSpec[] specs)
        {
            root=Path.GetFullPath(toolsRoot);cache=Path.Combine(root,"neural-downloads");packages=specs;
            foreach(var s in specs){SafeName(s.Name);SafeName(s.Member);SafeName(s.Output);}
        }
        internal string Prepare(Action<string> progress,bool online)
        {
            string id;using(var h=SHA256.Create())id=Hex(h.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Substring(0,24);
            using(var mutex=new Mutex(false,@"Local\RtxHub_NeuralPackages_"+id))
            {
                bool owned=false;
                try
                {
                    try{owned=mutex.WaitOne(1000);}catch(AbandonedMutexException){owned=true;}
                    if(!owned)throw new InvalidOperationException("另一个窗口正在准备 NR 组件。");
                    MakeDirectory(root);MakeDirectory(cache);
                    foreach(var s in packages)EnsureArchive(s,progress,online);
                    try{string ready=Verify();Report(progress,"现有组件验证通过，无需重复部署。");return ready;}catch(InvalidOperationException){}
                    string stage=Path.Combine(root,"neural-bundle.stage-"+Guid.NewGuid().ToString("N"));string previous=null;
                    try
                    {
                        MakeDirectory(stage);
                        foreach(var s in packages){Report(progress,"展开并核对 "+s.Name);ProcessArchive(s,stage,false);}
                        WriteSources(stage);VerifyDirectory(stage);NoReparse(Bundle);
                        if(Directory.Exists(Bundle)){previous=Path.Combine(root,"neural-bundle.previous-"+Guid.NewGuid().ToString("N"));Directory.Move(Bundle,previous);}
                        try{Directory.Move(stage,Bundle);}catch{if(previous!=null&&!Directory.Exists(Bundle))Directory.Move(previous,Bundle);throw;}
                        if(previous!=null)Report(progress,"旧组件保留在："+previous);
                        return "社区 NR 组件已准备并验证："+Bundle+"\r\n未执行社区 DLL；实际运行仍需检查 NR 求值日志。";
                    }
                    finally
                    {
                        if(Directory.Exists(stage))
                        {
                            string full=Path.GetFullPath(stage);string expected=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar+"neural-bundle.stage-";
                            if(!full.StartsWith(expected,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("拒绝清理非本次暂存目录。");
                            NoReparse(full);Directory.Delete(full,true);
                        }
                    }
                }
                catch(InvalidOperationException){throw;}
                catch(Exception ex){throw new InvalidOperationException("NR 组件准备未完成；原有组件保留。"+ex.Message,ex);}
                finally{if(owned)mutex.ReleaseMutex();}
            }
        }
        internal string Verify()
        {
            try{VerifyDirectory(Bundle);return "固定原始包 SHA-256、实际 DLL 与 Nexus 许可文件验证通过。尚未据此确认 NR 运行。";}
            catch(InvalidOperationException){throw;}
            catch(Exception ex){throw new InvalidOperationException("组件验证失败，请重新准备："+ex.Message,ex);}
        }
        void VerifyDirectory(string directory)
        {
            NoReparse(directory);if(!Directory.Exists(directory))throw new InvalidOperationException("请先准备社区 NR 组件。");
            foreach(var s in packages){ProcessArchive(s,directory,true);if(s.Native)ValidateNative(Path.Combine(directory,s.Output),s.Version);}
        }
        void EnsureArchive(NeuralPackageSpec s,Action<string> progress,bool online)
        {
            string path=Path.Combine(cache,s.Name);if(Authenticated(path,s)){Report(progress,"使用固定包缓存："+s.Name);return;}
            string original=Path.Combine(cache,Path.GetFileName(new Uri(s.Url).AbsolutePath));
            if(!String.Equals(path,original,StringComparison.OrdinalIgnoreCase)&&Authenticated(original,s))
            {string import=path+".import-"+Guid.NewGuid().ToString("N");File.Copy(original,import,false);PublishFile(import,path);return;}
            string partial=path+".partial";if(Authenticated(partial,s)){PublishFile(partial,path);return;}
            if(!online)throw new InvalidOperationException("固定包缺失或 SHA-256 不匹配："+path);
            string failure="";string[] urls=s.Alternate==null?new[]{s.Url,s.Url+"?download=1"}:new[]{s.Alternate,s.Url+"?download=1",s.Url};
            for(int i=0;i<urls.Length;i++)
            {
                try{Report(progress,"下载 "+s.Name+"，尝试 "+(i+1)+"/"+urls.Length);Download(urls[i],partial,s,progress);if(!Authenticated(partial,s))throw new InvalidOperationException("下载包 SHA-256／长度不匹配。");PublishFile(partial,path);return;}
                catch(Exception ex){failure=ex.Message;Report(progress,"下载未完成："+failure);}
            }
            throw new InvalidOperationException("未能下载 "+s.Name+"。可将固定原始包放入 "+cache+" 后重试。"+failure);
        }
        static void Download(string url,string path,NeuralPackageSpec s,Action<string> progress)
        {
            NoReparse(path);ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
            var request=(HttpWebRequest)WebRequest.Create(url);request.Timeout=25000;request.ReadWriteTimeout=25000;request.UserAgent="4090M-Studio/1.0";
            request.Accept=url.IndexOf("api.github.com",StringComparison.OrdinalIgnoreCase)>=0?"application/octet-stream":"*/*";
            using(var response=(HttpWebResponse)request.GetResponse())
            {
                if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>MaxArchive)throw new InvalidOperationException("下载状态或长度异常。");
                using(var input=response.GetResponseStream())using(var output=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))
                {
                    byte[] block=new byte[65536];long total=0,last=0;var timer=Stopwatch.StartNew();int count;
                    while((count=input.Read(block,0,block.Length))>0)
                    {total+=count;if(total>MaxArchive||(s.Size>0&&total>s.Size)||timer.ElapsedMilliseconds>300000)throw new InvalidOperationException("下载超过长度或五分钟限制。");output.Write(block,0,count);if(timer.ElapsedMilliseconds-last>5000){last=timer.ElapsedMilliseconds;Report(progress,s.Name+"："+(total/1048576)+" MiB");}}
                    output.Flush(true);
                }
            }
        }
        static bool Authenticated(string path,NeuralPackageSpec s)
        {
            NoReparse(path);if(!File.Exists(path))return false;long length=new FileInfo(path).Length;
            if(length<=0||length>MaxArchive||(s.Size>0&&length!=s.Size))return false;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Equal(Hash(stream),s.Hash);
        }
        void ProcessArchive(NeuralPackageSpec s,string destination,bool verify)
        {
            string path=Path.Combine(cache,s.Name);NoReparse(path);
            if(!File.Exists(path))throw new InvalidOperationException("固定原始包缺失："+path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length<=0||input.Length>MaxArchive||(s.Size>0&&input.Length!=s.Size)||!Equal(Hash(input),s.Hash))throw new InvalidOperationException("原始包 SHA-256／长度不匹配："+path);
                input.Position=0;
                using(var archive=OpenZip(input))
                {
                    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long expanded=0;int count=0;ZipArchiveEntry selected=null;string prefix="";
                    foreach(var entry in archive.Entries)
                    {
                        string name=Normalize(entry.FullName);if(!names.Add(name))throw new InvalidOperationException("ZIP 包含重复路径。");
                        if(++count>8192||entry.Length<0||entry.Length>MaxExpanded||(expanded+=entry.Length)>MaxExpanded)throw new InvalidOperationException("ZIP 超过 1 GiB 或条目过多。");
                        if(((entry.ExternalAttributes>>16)&0xf000)==0xa000||(entry.ExternalAttributes&0x400)!=0)throw new InvalidOperationException("ZIP 不允许链接或重解析点。");
                        if(!name.EndsWith("/",StringComparison.Ordinal)&&String.Equals(Path.GetFileName(name),s.Member,StringComparison.OrdinalIgnoreCase)){if(selected!=null)throw new InvalidOperationException("ZIP 存在多个同名核心组件。");selected=entry;prefix=name.Substring(0,name.Length-s.Member.Length);}
                    }
                    if(selected==null)throw new InvalidOperationException("包内缺少组件："+s.Member);
                    Transfer(selected,Path.Combine(destination,s.Output),verify);
                    if(s.Nexus)
                    {
                        int licenses=0;bool rootLicense=false,notice=false;
                        foreach(var entry in archive.Entries)
                        {
                            string name=Normalize(entry.FullName);if(!name.StartsWith(prefix,StringComparison.Ordinal)||name.EndsWith("/",StringComparison.Ordinal))continue;
                            string rel=name.Substring(prefix.Length);
                            if(!(rel.StartsWith("licenses/",StringComparison.Ordinal)||rel.StartsWith("docs/",StringComparison.Ordinal)||rel=="LICENSE"||rel=="THIRD_PARTY_NOTICES.md"||rel=="README.md"||rel=="NR-Nexus-Guide.html"||rel=="VERSION.txt"))continue;
                            if(rel.StartsWith("licenses/",StringComparison.Ordinal))licenses++;if(rel=="LICENSE")rootLicense=true;if(rel=="THIRD_PARTY_NOTICES.md")notice=true;
                            Transfer(entry,Path.Combine(destination,"docs","nexus",rel.Replace('/',Path.DirectorySeparatorChar)),verify);
                        }
                        if(!rootLicense||licenses==0||(s.Native&&!notice))throw new InvalidOperationException("Nexus 包缺少许可或第三方声明。");
                    }
                }
            }
        }
        static ZipArchive OpenZip(FileStream stream)
        {
            ZipArchive archive=null;
            try{archive=new ZipArchive(stream,ZipArchiveMode.Read,true);int count=archive.Entries.Count;return archive;}
            catch(InvalidDataException)
            {
                if(archive!=null)archive.Dispose();stream.Position=0;if(stream.Length>16777216)throw;
                byte[] bytes=new byte[(int)stream.Length];int count=0,read;while(count<bytes.Length&&(read=stream.Read(bytes,count,bytes.Length-count))>0)count+=read;
                for(int i=2;i<count-4;i++)if(bytes[i]==80&&bytes[i+1]==75&&bytes[i+2]==3&&bytes[i+3]==4)
                {var slice=new MemoryStream(bytes,i,count-i,false);try{var zip=new ZipArchive(slice,ZipArchiveMode.Read);int entries=zip.Entries.Count;return zip;}catch(InvalidDataException){slice.Dispose();}}
                throw new InvalidOperationException("无法只读解析 ReShade 内嵌 ZIP，不会执行安装器。");
            }
        }
        static void Transfer(ZipArchiveEntry entry,string target,bool verify)
        {
            NoReparse(target);
            if(verify)
            {
                if(!File.Exists(target)||new FileInfo(target).Length!=entry.Length)throw new InvalidOperationException("组件缺失或长度变化："+target);
                using(var a=entry.Open())using(var b=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read))if(!Equal(Hash(a),Hash(b)))throw new InvalidOperationException("组件内容与固定包不符："+target);
                return;
            }
            MakeDirectory(Path.GetDirectoryName(target));using(var a=entry.Open())using(var b=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {byte[] bytes=new byte[65536];long total=0;int count;while((count=a.Read(bytes,0,bytes.Length))>0){total+=count;if(total>entry.Length||total>MaxExpanded)throw new InvalidOperationException("ZIP 实际展开量异常。");b.Write(bytes,0,count);}if(total!=entry.Length)throw new InvalidOperationException("ZIP 内容被截断。");b.Flush(true);}
        }
        static string Normalize(string raw)
        {
            if(String.IsNullOrEmpty(raw))throw new InvalidOperationException("ZIP 条目为空。");string name=raw.Replace('\\','/');
            if(name.StartsWith("/",StringComparison.Ordinal)||name.IndexOf(':')>=0||name.Length>1024)throw new InvalidOperationException("ZIP 路径无效。");
            string[] parts=name.Split('/');for(int i=0;i<parts.Length;i++){if(i==parts.Length-1&&parts[i].Length==0)continue;SafeName(parts[i]);}return name;
        }
        static void SafeName(string name)
        {
            if(String.IsNullOrEmpty(name)||name=="."||name==".."||name.EndsWith(".",StringComparison.Ordinal)||name.EndsWith(" ",StringComparison.Ordinal)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.IndexOf('/')>=0||name.IndexOf('\\')>=0)throw new InvalidOperationException("包路径不安全："+name);
            string stem=name.Split('.')[0].ToUpperInvariant();if(stem=="CON"||stem=="PRN"||stem=="AUX"||stem=="NUL"||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3]>='0'&&stem[3]<='9'))throw new InvalidOperationException("包内含 Windows 设备名。");
        }
        static void NoReparse(string path)
        {
            string p=Path.GetFullPath(path);while(!String.IsNullOrEmpty(p)){if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("组件路径不能经过链接或重解析点："+p);string parent=Path.GetDirectoryName(p);if(parent==p)break;p=parent;}
        }
        static void MakeDirectory(string path){NoReparse(path);Directory.CreateDirectory(path);NoReparse(path);}
        static void ValidateNative(string path,string version)
        {
            using(var stream=File.OpenRead(path))using(var reader=new BinaryReader(stream))
            {if(stream.Length<128||reader.ReadUInt16()!=0x5a4d)throw new InvalidOperationException("组件不是 PE DLL："+path);stream.Position=60;int offset=reader.ReadInt32();if(offset<64||offset>stream.Length-24)throw new InvalidOperationException("PE 头无效。");stream.Position=offset;if(reader.ReadUInt32()!=0x00004550||reader.ReadUInt16()!=0x8664)throw new InvalidOperationException("只接受 Windows x64 DLL。");}
            if(version!=null){var v=FileVersionInfo.GetVersionInfo(path);string actual=v.FileMajorPart+"."+v.FileMinorPart+"."+v.FileBuildPart+"."+v.FilePrivatePart;if(actual!=version)throw new InvalidOperationException("NR Runtime 版本须为 "+version+"，实际 "+actual);}
        }
        void WriteSources(string stage)
        {
            string docs=Path.Combine(stage,"docs");MakeDirectory(docs);var text=new StringBuilder("4090M Studio community NR provenance\r\nNo downloaded installer or DLL is executed during preparation.\r\nNVIDIA runtimes/models retain their own terms; no license is granted by this manifest.\r\n\r\n");
            foreach(var s in packages)text.Append(s.Name).Append("\r\n").Append(s.Url).Append("\r\nSHA-256: ").Append(s.Hash).Append("\r\nMember: ").Append(s.Member).Append(" -> ").Append(s.Output).Append("\r\n\r\n");File.WriteAllText(Path.Combine(docs,"SOURCES.txt"),text.ToString(),new UTF8Encoding(false));
        }
        static void PublishFile(string from,string to){NoReparse(from);NoReparse(to);if(File.Exists(to))File.Replace(from,to,null);else File.Move(from,to);}
        static string Hash(Stream stream){using(var h=SHA256.Create())return Hex(h.ComputeHash(stream));}
        static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        static bool Equal(string a,string b){return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
        static void Report(Action<string> callback,string text){if(callback!=null)callback(text);}
    }
}
