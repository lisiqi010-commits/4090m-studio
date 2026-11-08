using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RtxHub
{
    // Fixed upstream data only. Never execute setup_windows.bat, OptiPatcher,
    // runtime_sync, or any executable contained in the archive.
    public static class FrameGenerationPackages
    {
        static string ToolsRoot { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools"); } }
        public static string BundleDirectory { get { return Path.Combine(ToolsRoot,"framegen-bundle"); } }
        public static string DownloadDirectory { get { return Path.Combine(ToolsRoot,"framegen-downloads"); } }
        public static string Prepare(Action<string> progress) { return new FrameGenPackageStore(ToolsRoot,Catalog(),new WindowsFrameGenArchive()).Prepare(progress,true); }
        public static string Verify() { return new FrameGenPackageStore(ToolsRoot,Catalog(),new WindowsFrameGenArchive()).Verify(); }
        internal static FrameGenPackageSpec Catalog()
        {
            const string commit="1d22875d05fac2d7ffb86ed912b943ce1a6e915a";
            const string release="https://github.com/abc354402600/OptiScaler-Aurora/releases/tag/aurora-v1.1.2";
            const string archiveHash="17c608e1a848505ab72dea9ab0e3dce5e192072757189a73bc3fd87fd6e88af8";
            return new FrameGenPackageSpec(
                new FrameGenDownload("OptiScaler_Aurora_v1.1.2_20261004.7z",release.Replace("/tag/","/download/")+"/OptiScaler_Aurora_v1.1.2_20261004.7z?download=1","https://api.github.com/repos/abc354402600/OptiScaler-Aurora/releases/assets/610798094?download=1",219575034,archiveHash),
                new FrameGenDownload("Aurora-1d22875-GPL-3.0.txt","https://raw.githubusercontent.com/abc354402600/OptiScaler-Aurora/"+commit+"/LICENSE",null,35149,"3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986"),
                new[] {
                    new FrameGenMember("OptiScaler.dll","OptiScaler.dll",26079744,"aad61ff7534ffcef9efbbe0589bcbbb5c35ce6833ca086481b694edd85efa450",true),
                    new FrameGenMember("Licenses/DirectX_LICENSE.txt","docs/DirectX_LICENSE.txt",13147,"5239850894610071566f7ecee0b751fde43c862032d92b99d7d0f596b3433ebd",false),
                    new FrameGenMember("Licenses/FidelityFX_v2_LICENSE.md","docs/FidelityFX_v2_LICENSE.md",61784,"f0da09d71ad5c82759a179e774535d4a829e5c96c49294167c1152402b2cb400",false),
                    new FrameGenMember("Licenses/RenoDX_ATTRIBUTION.txt","docs/RenoDX_ATTRIBUTION.txt",2976,"1d2f2b2f8cf5c9acfe2eaaa2d2c22face87bd19f7df85453079a28e7123ae727",false),
                    new FrameGenMember("Licenses/XeSS_LICENSE.txt","docs/XeSS_LICENSE.txt",4194,"784be42c39a4a4d03cf85d88e15e59d75828fca773ce45b8dbfffbd7eaf208df",false)
                },
                "OptiScaler Aurora v1.1.2 (experimental RTX 40 MFG)\r\n"+
                "Release: "+release+"\r\nSource commit: "+commit+"\r\n"+
                "Corresponding source: https://github.com/abc354402600/OptiScaler-Aurora/tree/"+commit+"\r\n"+
                "Source archive: https://github.com/abc354402600/OptiScaler-Aurora/archive/"+commit+".tar.gz\r\n"+
                "Archive SHA256: "+archiveHash+"\r\n"+
                "License: GPL-3.0; see LICENSE.txt and docs for upstream attribution.\r\n"+
                "Only OptiScaler.dll and license/provenance data were extracted. No setup or updater was executed.\r\n"+
                "No NVIDIA/Intel/AMD runtime DLLs are bundled in this subset. Native game Streamline/DLSSG is retained.\r\n"+
                "The upstream compatibility modification is unsupported by NVIDIA. A configured multiplier is not evidence of generated frames.\r\n");
        }
    }

    internal sealed class FrameGenDownload
    {
        internal readonly string Name,Url,Alternate,Hash; internal readonly long Length;
        internal FrameGenDownload(string name,string url,string alternate,long length,string hash) { Name=name;Url=url;Alternate=alternate;Length=length;Hash=hash; }
    }
    internal sealed class FrameGenMember
    {
        internal readonly string Member,Output,Hash;internal readonly long Length;internal readonly bool Native;
        internal FrameGenMember(string member,string output,long length,string hash,bool native) { Member=member;Output=output;Length=length;Hash=hash;Native=native; }
    }
    internal sealed class FrameGenPackageSpec
    {
        internal readonly FrameGenDownload Archive,License;internal readonly FrameGenMember[] Members;internal readonly string Sources;
        internal FrameGenPackageSpec(FrameGenDownload archive,FrameGenDownload license,FrameGenMember[] members,string sources) { Archive=archive;License=license;Members=members;Sources=sources; }
    }
    internal interface IFrameGenArchive
    {
        string[] List(string archive);
        void Extract(string archive,string directory,string[] members);
    }
    internal sealed class WindowsFrameGenArchive : IFrameGenArchive
    {
        public string[] List(string archive) { return Run("-tf "+Quote(archive)).Replace("\r","").Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries); }
        public void Extract(string archive,string directory,string[] members)
        {
            string args="-xf "+Quote(archive)+" -C "+Quote(directory)+" --";
            foreach(string member in members)args+=" "+Quote(member);
            Run(args);
        }
        internal static string Quote(string value)
        {
            var output=new StringBuilder("\"");int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"'){output.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}
                output.Append('\\',slashes).Append(c);slashes=0;
            }
            return output.Append('\\',slashes*2).Append('"').ToString();
        }
        static string Run(string arguments)
        {
            string tar=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","tar.exe");
            if(!File.Exists(tar))throw new InvalidOperationException("需要 Windows 自带的 tar.exe 才能读取固定补帧组件包；未执行下载内容。");
            var start=new ProcessStartInfo(tar,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
            using(var process=Process.Start(start))
            {
                if(process==null)throw new InvalidOperationException("Windows 归档读取工具未能启动。");
                var output=new StringBuilder();var error=new StringBuilder();object gate=new object();bool exceeded=false;
                DataReceivedEventHandler stdout=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(gate){if(output.Length+e.Data.Length>2097152)exceeded=true;else output.AppendLine(e.Data);}};
                DataReceivedEventHandler stderr=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(gate){if(error.Length+e.Data.Length>32768)exceeded=true;else error.AppendLine(e.Data);}};
                process.OutputDataReceived+=stdout;process.ErrorDataReceived+=stderr;process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(60000)){try{process.Kill();process.WaitForExit();}catch(InvalidOperationException){}throw new InvalidOperationException("读取补帧归档超过一分钟，已停止本次解包。");}
                process.WaitForExit();
                if(exceeded||process.ExitCode!=0)throw new InvalidOperationException("固定补帧归档读取失败；未执行任何包内程序。"+error.ToString());
                return output.ToString();
            }
        }
    }

    internal sealed class FrameGenPackageStore
    {
        readonly string root,cache;readonly FrameGenPackageSpec spec;readonly IFrameGenArchive extractor;
        internal string Bundle { get { return Path.Combine(root,"framegen-bundle"); } }
        internal FrameGenPackageStore(string root,FrameGenPackageSpec spec,IFrameGenArchive extractor)
        {
            this.root=Canonical(root);cache=Path.Combine(this.root,"framegen-downloads");this.spec=spec;this.extractor=extractor;
            if(spec==null||spec.Members==null||spec.Members.Length==0||String.IsNullOrEmpty(spec.Sources))throw new InvalidOperationException("补帧包定义不完整。");
            CheckDownload(spec.Archive);CheckDownload(spec.License);var outputs=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"LICENSE.txt","SOURCES.txt"};var members=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var member in spec.Members)
            {
                if(member==null||!members.Add(NormalizeMember(member.Member))||!outputs.Add(NormalizeMember(member.Output)))throw new InvalidOperationException("补帧包定义包含重复文件。");
                CheckHash(member.Hash);if(member.Length<=0||member.Length>536870912)throw new InvalidOperationException("补帧组件长度无效。");
            }
        }
        internal string Prepare(Action<string> progress,bool online)
        {
            string mutexName=@"Local\RtxHub_FrameGenPackages_"+HashBytes(Encoding.UTF8.GetBytes(root.ToUpperInvariant()));
            using(var mutex=new Mutex(false,mutexName))
            {
                bool owned=false;
                try
                {
                    try{owned=mutex.WaitOne(1000);}catch(AbandonedMutexException){owned=true;}
                    if(!owned)throw new InvalidOperationException("另一个补帧组件准备操作正在进行。");
                    MakeDirectory(root);MakeDirectory(cache);EnsureDownload(spec.Archive,progress,online);EnsureDownload(spec.License,progress,online);
                    try{return Verify();}catch(InvalidOperationException){}
                    string archive=Path.Combine(cache,spec.Archive.Name);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string[] entries=extractor.List(archive);
                    if(entries==null||entries.Length>8192)throw new InvalidOperationException("补帧归档条目过多或无法读取。");
                    foreach(string entry in entries)if(!names.Add(NormalizeMember(entry)))throw new InvalidOperationException("补帧归档含 Windows 同名重复路径。");
                    var selected=new List<string>();foreach(var member in spec.Members){if(!names.Contains(member.Member))throw new InvalidOperationException("固定归档缺少文件："+member.Member);selected.Add(member.Member);}
                    string stage=Path.Combine(root,"framegen-bundle.stage-"+Guid.NewGuid().ToString("N"));string previous=null;
                    try
                    {
                        MakeDirectory(stage);string extracted=Path.Combine(stage,".extract");MakeDirectory(extracted);
                        Report(progress,"正在以数据方式读取固定补帧包…");extractor.Extract(archive,extracted,selected.ToArray());
                        // An immutable whole-archive hash and member hashes both apply.
                        // Only allowlisted member names ever reach tar's extraction command.
                        foreach(var member in spec.Members)
                        {
                            string input=Path.Combine(extracted,member.Member.Replace('/',Path.DirectorySeparatorChar));VerifyFile(input,member.Length,member.Hash);if(member.Native)ValidateNative(input);
                            string output=Path.Combine(stage,member.Output.Replace('/',Path.DirectorySeparatorChar));MakeDirectory(Path.GetDirectoryName(output));File.Move(input,output);
                        }
                        RemoveEmptyTree(extracted);
                        string license=Path.Combine(cache,spec.License.Name);VerifyFile(license,spec.License.Length,spec.License.Hash);File.Copy(license,Path.Combine(stage,"LICENSE.txt"),false);
                        File.WriteAllText(Path.Combine(stage,"SOURCES.txt"),spec.Sources,new UTF8Encoding(false));VerifyDirectory(stage);NoReparse(Bundle);
                        if(Directory.Exists(Bundle)){previous=Path.Combine(root,"framegen-bundle.previous-"+Guid.NewGuid().ToString("N"));Directory.Move(Bundle,previous);}
                        try{Directory.Move(stage,Bundle);}catch{if(previous!=null&&!Directory.Exists(Bundle))Directory.Move(previous,Bundle);throw;}
                        if(previous!=null)Report(progress,"旧补帧组件已保留："+previous);
                        return "固定 Aurora 补帧组件与许可已校验就绪；尚未据此确认游戏实际补帧。";
                    }
                    finally{CleanupStage(stage);}
                }
                catch(InvalidOperationException){throw;}
                catch(Exception ex){throw new InvalidOperationException("补帧组件准备未完成，已有组件和游戏文件保留。"+ex.Message,ex);}
                finally{if(owned)mutex.ReleaseMutex();}
            }
        }
        internal string Verify()
        {
            try
            {
                VerifyFile(Path.Combine(cache,spec.Archive.Name),spec.Archive.Length,spec.Archive.Hash);VerifyFile(Path.Combine(cache,spec.License.Name),spec.License.Length,spec.License.Hash);VerifyDirectory(Bundle);
                return "固定 Aurora 原包、x64 加载库、GPL 与声明校验通过；尚未据此确认游戏补帧。";
            }
            catch(InvalidOperationException){throw;}
            catch(Exception ex){throw new InvalidOperationException("补帧组件校验失败："+ex.Message,ex);}
        }
        void VerifyDirectory(string directory)
        {
            NoReparse(directory);if(!Directory.Exists(directory))throw new InvalidOperationException("请先准备固定补帧组件。");
            var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"LICENSE.txt","SOURCES.txt"};
            foreach(var member in spec.Members){string file=Path.Combine(directory,member.Output.Replace('/',Path.DirectorySeparatorChar));VerifyFile(file,member.Length,member.Hash);if(member.Native)ValidateNative(file);allowed.Add(member.Output);}
            VerifyFile(Path.Combine(directory,"LICENSE.txt"),spec.License.Length,spec.License.Hash);
            byte[] sourceBytes=new UTF8Encoding(false).GetBytes(spec.Sources);VerifyFile(Path.Combine(directory,"SOURCES.txt"),sourceBytes.Length,HashBytes(sourceBytes));
            ValidateTree(directory,directory,allowed);
        }
        static void ValidateTree(string top,string directory,HashSet<string> allowed)
        {
            NoReparse(directory);
            foreach(string path in Directory.GetFileSystemEntries(directory))
            {
                NoReparse(path);if(Directory.Exists(path)){ValidateTree(top,path,allowed);continue;}
                string relative=path.Substring(top.TrimEnd(Path.DirectorySeparatorChar).Length+1).Replace(Path.DirectorySeparatorChar,'/');if(!allowed.Contains(relative))throw new InvalidOperationException("补帧组件目录含未知文件，已保留："+relative);
            }
        }
        void EnsureDownload(FrameGenDownload download,Action<string> progress,bool online)
        {
            string path=Path.Combine(cache,download.Name);NoReparse(path);
            try{VerifyFile(path,download.Length,download.Hash);return;}catch(InvalidOperationException){}
            if(!online)throw new InvalidOperationException("离线补帧原始包缺失或校验失败："+path);
            string last="";string[] urls=download.Alternate==null?new[]{download.Url}:new[]{download.Url,download.Alternate};
            foreach(string url in urls)
            {
                string partial=Path.Combine(cache,".framegen-download-"+Guid.NewGuid().ToString("N")+".partial");
                try
                {
                    Report(progress,"下载固定补帧文件："+download.Name);Receive(url,partial,download,progress);VerifyFile(partial,download.Length,download.Hash);NoReparse(path);
                    if(File.Exists(path))File.Move(path,path+".invalid-"+Guid.NewGuid().ToString("N"));File.Move(partial,path);return;
                }
                catch(Exception ex){last=ex.Message;Report(progress,"本次下载未完成："+last);}
                finally{if(File.Exists(partial)){NoReparse(partial);File.Delete(partial);}}
            }
            throw new InvalidOperationException("固定补帧文件无法准备。可将正确原始文件放入 "+cache+" 后重试。"+last);
        }
        static void Receive(string url,string path,FrameGenDownload expected,Action<string> progress)
        {
            NoReparse(path);ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
            var request=(HttpWebRequest)WebRequest.Create(url);request.Timeout=25000;request.ReadWriteTimeout=25000;request.UserAgent="4090M-Studio/1.0";request.Accept=new Uri(url).Host.Equals("api.github.com",StringComparison.OrdinalIgnoreCase)?"application/octet-stream":"*/*";
            using(var response=(HttpWebResponse)request.GetResponse())
            {
                if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>expected.Length)throw new InvalidOperationException("下载状态或文件长度不符。");
                using(var input=response.GetResponseStream())using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    byte[] buffer=new byte[65536];long total=0,last=0;var watch=Stopwatch.StartNew();int count;
                    while((count=input.Read(buffer,0,buffer.Length))>0)
                    {
                        total+=count;if(total>expected.Length||watch.ElapsedMilliseconds>300000)throw new InvalidOperationException("下载超过固定长度或五分钟限制。");output.Write(buffer,0,count);
                        if(watch.ElapsedMilliseconds-last>5000){Report(progress,expected.Name+"："+(total/1048576)+" MiB");last=watch.ElapsedMilliseconds;}
                    }
                    output.Flush(true);
                }
            }
        }
        static void CheckDownload(FrameGenDownload item)
        {
            if(item==null||NormalizeMember(item.Name).IndexOf('/')>=0||item.Length<=0||item.Length>536870912)throw new InvalidOperationException("补帧下载定义无效。");CheckHash(item.Hash);
            foreach(string url in item.Alternate==null?new[]{item.Url}:new[]{item.Url,item.Alternate}){Uri parsed;if(!Uri.TryCreate(url,UriKind.Absolute,out parsed)||parsed.Scheme!="https"||!String.IsNullOrEmpty(parsed.UserInfo))throw new InvalidOperationException("补帧下载仅允许明确的 HTTPS 地址。");}
        }
        static void CheckHash(string hash){if(hash==null||!Regex.IsMatch(hash,@"\A[0-9a-fA-F]{64}\z",RegexOptions.CultureInvariant))throw new InvalidOperationException("补帧固定哈希无效。");}
        internal static string NormalizeMember(string value)
        {
            if(String.IsNullOrEmpty(value)||value.Length>512||value.IndexOf(':')>=0||value[0]=='/'||value[0]=='\\')throw new InvalidOperationException("归档路径无效。");
            string normalized=value.Replace('\\','/');string trimmed=normalized.EndsWith("/",StringComparison.Ordinal)?normalized.Substring(0,normalized.Length-1):normalized;
            foreach(string part in trimmed.Split('/'))
            {
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||part.IndexOfAny(new[]{'\0','\r','\n','*','?','"','<','>','|'})>=0||Regex.IsMatch(part,@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw new InvalidOperationException("拒绝归档中的不明确 Windows 路径。");
            }
            return normalized;
        }
        static string Canonical(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||path.Length<3||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf(':',2)>=0)throw new InvalidOperationException("补帧组件仅支持本机磁盘路径。");
            foreach(string part in path.Substring(3).Split('\\','/'))if(part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidOperationException("组件路径含不明确目录段。");return Path.GetFullPath(path);
        }
        internal static void NoReparse(string path)
        {
            string checkedPath=Canonical(path);while(!String.IsNullOrEmpty(checkedPath)){if((File.Exists(checkedPath)||Directory.Exists(checkedPath))&&(File.GetAttributes(checkedPath)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("补帧组件路径不能经过链接或重解析点。");checkedPath=Path.GetDirectoryName(checkedPath);}
        }
        static void MakeDirectory(string path){NoReparse(path);Directory.CreateDirectory(path);NoReparse(path);}
        static void VerifyFile(string path,long length,string hash)
        {
            NoReparse(path);if(!File.Exists(path)||new FileInfo(path).Length!=length)throw new InvalidOperationException("补帧文件缺失或长度改变："+path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())if(!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("补帧文件 SHA-256 不符："+path);
        }
        internal static void ValidateNative(string path)
        {
            using(var reader=new BinaryReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)))
            {
                if(reader.BaseStream.Length<128||reader.ReadUInt16()!=0x5a4d)throw new InvalidOperationException("补帧加载库不是有效 PE。");reader.BaseStream.Position=0x3c;long pe=reader.ReadUInt32();
                if(pe<64||pe>reader.BaseStream.Length-136)throw new InvalidOperationException("补帧 PE 头越界。");reader.BaseStream.Position=pe;if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=0x8664)throw new InvalidOperationException("补帧加载库必须为 x64。");reader.BaseStream.Position=pe+20;int optional=reader.ReadUInt16(),flags=reader.ReadUInt16();if(optional<112||pe+24+optional>reader.BaseStream.Length||(flags&0x2000)==0||reader.ReadUInt16()!=0x20b)throw new InvalidOperationException("补帧组件必须为完整的 x64 DLL。");
            }
        }
        static string HashBytes(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static void RemoveEmptyTree(string path)
        {
            NoReparse(path);foreach(string child in Directory.GetDirectories(path)){NoReparse(child);RemoveEmptyTree(child);}if(Directory.GetFiles(path).Length!=0)throw new InvalidOperationException("解包产生未知文件，已保留暂存目录。");Directory.Delete(path,false);
        }
        void CleanupStage(string stage)
        {
            if(!Directory.Exists(stage))return;
            if(!String.Equals(Path.GetDirectoryName(stage),root,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(stage).StartsWith("framegen-bundle.stage-",StringComparison.Ordinal))throw new InvalidOperationException("拒绝清理非本次组件暂存目录。");
            try{DeleteTreeNoLinks(stage);}catch(IOException){}catch(UnauthorizedAccessException){}catch(InvalidOperationException){}
        }
        static void DeleteTreeNoLinks(string directory)
        {
            NoReparse(directory);foreach(string entry in Directory.GetFileSystemEntries(directory)){NoReparse(entry);if(Directory.Exists(entry))DeleteTreeNoLinks(entry);else File.Delete(entry);}Directory.Delete(directory,false);
        }
        static void Report(Action<string> progress,string value){if(progress!=null)progress(value);}
    }
}
