using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace RtxHub
{
    public static class FrameGenerationInstaller
    {
        static FrameGenInstallService Service {get{return new FrameGenInstallService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Rtx4090MHub\FgInstalls"));}}
        public static string EnsureInstalled(string exe,string bundleDir,string ini){return Service.EnsureInstalled(exe,bundleDir,ini);}
        public static string Restore(string exe){return Service.Restore(exe);}
        public static string Disable(string exe){return Service.Disable(exe);}
        public static string[] ManagedTargets(){return Service.ManagedTargets();}
        public static bool HasCoreFiles(string exe){return Service.HasCoreFiles(exe);}
        public static string JournalPath(string exe){return Service.JournalPath(exe);}
    }
    internal sealed class FrameGenInstallFile
    {
        internal string Name,Source,Destination,Hash,Temp;
        internal bool Owned;
    }
    internal sealed class FrameGenInstallJournal
    {
        internal string Executable,CreatedUtc;
        internal readonly List<FrameGenInstallFile> Files=new List<FrameGenInstallFile>();
    }
    internal interface IFrameGenFileWriter {void Commit(FrameGenInstallFile file,byte[] ini);}
    internal sealed class AtomicFrameGenFileWriter:IFrameGenFileWriter
    {
        public void Commit(FrameGenInstallFile file,byte[] ini)
        {
            NeuralInstallService.CheckDirectory(Path.GetDirectoryName(file.Destination),true);
            if(File.Exists(file.Destination)||Directory.Exists(file.Destination))throw new InvalidOperationException("补帧目标文件已出现，拒绝覆盖："+file.Name);
            try
            {
                using(var input=file.Name=="OptiScaler.ini"?(Stream)new MemoryStream(ini,false):new FileStream(file.Source,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var output=new FileStream(file.Temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
                if(NeuralInstallService.FileHash(file.Temp)!=file.Hash)throw new InvalidOperationException("补帧暂存文件校验失败："+file.Name);
                NeuralInstallService.CheckDirectory(Path.GetDirectoryName(file.Destination),true);
                File.Move(file.Temp,file.Destination);file.Owned=true;
            }
            finally
            {
                if(File.Exists(file.Temp)&&!NeuralInstallService.IsReparse(file.Temp))
                    try{if(NeuralInstallService.FileHash(file.Temp)==file.Hash)File.Delete(file.Temp);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    internal sealed class FrameGenInstallService
    {
        static readonly string[] Names={"winmm.dll","OptiScaler.ini","4090M-FG-LICENSE.txt","4090M-FG-SOURCES.txt"};
        static readonly string[] Sources={"OptiScaler.dll","generated:OptiScaler.ini","LICENSE.txt","SOURCES.txt"};
        readonly string records;readonly IFrameGenFileWriter writer;readonly Action<string> deleteFile;
        internal FrameGenInstallService(string records):this(records,new AtomicFrameGenFileWriter()){}
        internal FrameGenInstallService(string records,IFrameGenFileWriter writer):this(records,writer,File.Delete){}
        internal FrameGenInstallService(string records,IFrameGenFileWriter writer,Action<string> deleteFile){this.records=CanonicalLocal(records);NeuralInstallService.CheckDirectory(this.records,false);this.writer=writer;this.deleteFile=deleteFile;}
        internal string JournalPath(string exe){return Path.Combine(records,Hash(Encoding.UTF8.GetBytes(CanonicalExe(exe,false).ToUpperInvariant()))+".xml");}
        internal bool HasCoreFiles(string exe)
        {
            exe=CanonicalExe(exe,false);
            using(var guard=new Guard(records))
            {
                if(ReadJournal(exe)==null)return false;
                string loader=Path.Combine(Path.GetDirectoryName(exe),Names[0]);
                return File.Exists(loader)||Directory.Exists(loader);
            }
        }
        internal string Disable(string exe)
        {
            exe=CanonicalExe(exe,false);
            using(var guard=new Guard(records))
            {
                string result=Restore(exe);
                if(HasCoreFiles(exe))throw new InvalidOperationException("受管补帧加载文件仍保留，尚未完成关闭。已停止 1× 启动，请先检查保留的 winmm.dll。\r\n"+result);
                return result;
            }
        }
        internal string[] ManagedTargets()
        {
            using(var guard=new Guard(records))
            {
                NeuralInstallService.CheckDirectory(records,false);if(!Directory.Exists(records))return new string[0];
                var targets=new List<string>();
                foreach(string path in Directory.GetFiles(records,"*.xml",SearchOption.TopDirectoryOnly))
                {
                    string stem=Path.GetFileNameWithoutExtension(path);if(stem.Length!=64||!IsHex(stem))throw new InvalidOperationException("补帧记录文件名无效，未采用未知目标。");
                    var document=ReadXml(path);var root=document.DocumentElement;
                    if(root==null)throw new InvalidOperationException("补帧记录缺少根节点。");
                    string exe=CanonicalExe(root.GetAttribute("exe"),false);
                    if(!SamePath(path,JournalPath(exe)))throw new InvalidOperationException("补帧记录与目标不匹配。");
                    ReadJournal(exe);targets.Add(exe);
                }
                targets.Sort(StringComparer.OrdinalIgnoreCase);return targets.ToArray();
            }
        }
        internal string EnsureInstalled(string exe,string bundleDir,string ini)
        {
            exe=CanonicalExe(exe,true);ValidateDesiredIni(ini);bundleDir=CanonicalLocal(bundleDir);
            using(var guard=new Guard(records))
            {
                NeuralInstallService.ValidateGameForMod(exe,true);CheckNotRunning(exe);
                NeuralInstallService.CheckDirectory(bundleDir,true);
                string[] sourcePaths=new string[4];string[] sourceHashes=new string[4];
                for(int i=0;i<Names.Length;i++)if(i!=1){sourcePaths[i]=Path.Combine(bundleDir,Sources[i]);ValidateRegular(sourcePaths[i]);if(new FileInfo(sourcePaths[i]).Length==0)throw new InvalidOperationException("补帧源文件为空："+Sources[i]);sourceHashes[i]=NeuralInstallService.FileHash(sourcePaths[i]);}
                var journal=ReadJournal(exe);string directory=Path.GetDirectoryName(exe);bool fresh=journal==null;
                if(fresh)
                {
                    foreach(string name in Names)if(File.Exists(Path.Combine(directory,name))||Directory.Exists(Path.Combine(directory,name)))throw new InvalidOperationException("已有非本工具管理的补帧文件，拒绝覆盖："+name);
                    journal=new FrameGenInstallJournal{Executable=exe,CreatedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)};
                    for(int i=0;i<Names.Length;i++)journal.Files.Add(new FrameGenInstallFile{Name=Names[i],Destination=Path.Combine(directory,Names[i]),Source=i==1?Sources[i]:sourcePaths[i],Owned=false});
                }
                byte[] config=new UTF8Encoding(false).GetBytes(ini);FrameGenInstallFile changedIni=null;byte[] replacement=null;string previousIniHash=null;
                var additions=new List<FrameGenInstallFile>();string token=Guid.NewGuid().ToString("N");
                for(int i=0;i<journal.Files.Count;i++)
                {
                    var file=journal.Files[i];bool exists=File.Exists(file.Destination)||Directory.Exists(file.Destination);
                    if(File.Exists(file.Temp)||Directory.Exists(file.Temp))throw new InvalidOperationException("发现未完成的补帧暂存文件，请先恢复并检查："+file.Temp);
                    if(exists)
                    {
                        if(!file.Owned)throw new InvalidOperationException("已有文件未获本工具所有权，拒绝采用："+file.Name);
                        ValidateRegular(file.Destination);
                        string current=NeuralInstallService.FileHash(file.Destination);
                        if(file.Name=="OptiScaler.ini")
                        {
                            byte[] before=ReadConfig(file.Destination);string original=DecodeConfig(before);string merged=MergeDesiredIni(original,ini);
                            if(original!=merged)
                            {
                                byte[] body=new UTF8Encoding(false).GetBytes(merged);replacement=body;
                                if(before.Length>=3&&before[0]==239&&before[1]==187&&before[2]==191){replacement=new byte[body.Length+3];replacement[0]=239;replacement[1]=187;replacement[2]=191;Buffer.BlockCopy(body,0,replacement,3,body.Length);}
                                changedIni=file;previousIniHash=current;
                                // Retain the original restoration baseline after any external edit.
                                if(current==file.Hash)file.Hash=Hash(replacement);
                            }
                        }
                        else if(current!=file.Hash||current!=sourceHashes[i])throw new InvalidOperationException("已有补帧组件已改变，已保留并拒绝覆盖："+file.Name);
                        continue;
                    }
                    file.Source=i==1?Sources[i]:sourcePaths[i];file.Hash=i==1?Hash(config):sourceHashes[i];file.Owned=false;
                    file.Temp=Path.Combine(directory,".rtxfg-"+token+"-"+i+".tmp");additions.Add(file);
                }
                if(!fresh&&additions.Count==0&&changedIni==null)return "补帧组件与请求配置已经一致；未改动游戏文件。";
                EnsureRecords();ReadKey(true);
                // Signed intent is durable before any new file or configuration replacement.
                WriteJournal(journal);
                try
                {
                    foreach(var file in additions){writer.Commit(file,config);WriteJournal(journal);}
                    if(changedIni!=null)
                    {
                        changedIni.Temp=Path.Combine(directory,".rtxfg-"+token+"-1.tmp");WriteJournal(journal);
                        WriteBytes(changedIni.Temp,replacement);
                        ValidateRegular(changedIni.Destination);
                        if(NeuralInstallService.FileHash(changedIni.Destination)!=previousIniHash)throw new InvalidOperationException("补帧配置被其他程序同时修改，已停止更新。");
                        File.Replace(changedIni.Temp,changedIni.Destination,null);
                    }
                }
                catch(Exception ex)
                {
                    var rollbackProblems=new List<string>();
                    foreach(var file in additions)
                    {
                        try{if(file.Owned&&File.Exists(file.Destination)&&!NeuralInstallService.IsReparse(file.Destination)&&NeuralInstallService.FileHash(file.Destination)==file.Hash)DeleteOwned(file,journal);}
                        catch(Exception error){rollbackProblems.Add(file.Name+"："+error.Message);}
                    }
                    try{if(changedIni!=null&&File.Exists(changedIni.Temp)&&!NeuralInstallService.IsReparse(changedIni.Temp)&&NeuralInstallService.FileHash(changedIni.Temp)==Hash(replacement))File.Delete(changedIni.Temp);}
                    catch(Exception error){rollbackProblems.Add("配置暂存文件："+error.Message);}
                    try
                    {
                        bool remains=false;foreach(var file in journal.Files)if(File.Exists(file.Destination)||Directory.Exists(file.Destination)||File.Exists(file.Temp)||Directory.Exists(file.Temp))remains=true;
                        if(!remains)File.Delete(JournalPath(exe));else WriteJournal(journal);
                    }
                    catch(Exception error){rollbackProblems.Add("恢复记录："+error.Message);}
                    throw new InvalidOperationException("补帧准备失败；仅回滚本次新增且未改变的文件，其他文件与恢复记录保留。"+(rollbackProblems.Count==0?"":" 尚未完成："+String.Join("；",rollbackProblems.ToArray())),ex);
                }
                return "补帧组件与请求配置已准备；现有 NR 和官方 DLL 保持不变。实际倍率需检查本次游戏日志及画面。";
            }
        }
        internal string Restore(string exe)
        {
            exe=CanonicalExe(exe,false);
            try
            {
            using(var guard=new Guard(records))
            {
                var journal=ReadJournal(exe);if(journal==null)return "此目标没有本工具的补帧记录；未删除任何文件。";
                CheckNotRunning(exe);int removed=0;var preserved=new List<string>();
                foreach(var file in journal.Files)
                {
                    if(File.Exists(file.Destination)||Directory.Exists(file.Destination))
                    {
                        if(!file.Owned||Directory.Exists(file.Destination)||NeuralInstallService.IsReparse(file.Destination)||NeuralInstallService.FileHash(file.Destination)!=file.Hash)preserved.Add(file.Name);
                        else{DeleteOwned(file,journal);removed++;}
                    }
                    if(File.Exists(file.Temp)||Directory.Exists(file.Temp))preserved.Add(Path.GetFileName(file.Temp));
                }
                if(preserved.Count==0){File.Delete(JournalPath(exe));return "已撤回补帧：删除 "+removed+" 个未改变的受管文件。";}
                WriteJournal(journal);return "已撤回 "+removed+" 个补帧文件；已修改或所有权不完整的文件保留："+String.Join("、",preserved.ToArray());
            }
            }
            catch(InvalidOperationException){throw;}
            catch(Exception ex){throw new InvalidOperationException("补帧撤回尚未完成，文件及所有权记录已保留；请检查后重试。",ex);}
        }
        void DeleteOwned(FrameGenInstallFile file,FrameGenInstallJournal journal)
        {
            // Revoke authority durably BEFORE deleting. An interruption must not
            // let a later foreign same-hash file inherit the old ownership bit.
            file.Owned=false;WriteJournal(journal);deleteFile(file.Destination);
        }
        internal static string MergeDesiredIni(string existing,string desired){return FrameGenerationPresets.MergeDesiredIni(existing,desired);}
        static void ValidateDesiredIni(string ini)
        {
            if(String.IsNullOrWhiteSpace(ini)||Encoding.UTF8.GetByteCount(ini)>1048576)throw new InvalidOperationException("补帧配置为空或过大。");
            string section="";int count=0;var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using(var reader=new StringReader(ini.TrimStart('\uFEFF')))
            {
                string line;while((line=reader.ReadLine())!=null)
                {
                    string row=line.Trim();if(row.Length==0||row[0]==';'||row[0]=='#')continue;
                    if(row.StartsWith("[",StringComparison.Ordinal)&&row.EndsWith("]",StringComparison.Ordinal)){section=row.Substring(1,row.Length-2).Trim();if(section.Length==0)throw new InvalidOperationException("补帧配置节名为空。");continue;}
                    int equals=row.IndexOf('=');if(section.Length==0||equals<=0)throw new InvalidOperationException("补帧配置格式无效。");
                    if(!keys.Add(section+"\0"+row.Substring(0,equals).Trim()))throw new InvalidOperationException("补帧配置包含重复指令。");count++;
                }
            }
            if(count==0)throw new InvalidOperationException("补帧配置缺少指令。");
        }
        void EnsureRecords(){NeuralInstallService.CheckDirectory(records,false);Directory.CreateDirectory(records);NeuralInstallService.CheckDirectory(records,true);}
        string KeyPath {get{return Path.Combine(records,".fg-integrity.key");}}
        byte[] ReadKey(bool create)
        {
            if(!File.Exists(KeyPath))
            {
                if(!create)throw new InvalidOperationException("补帧记录完整性密钥缺失，已停止处理未知所有权。");
                if(Directory.GetFiles(records,"*.xml",SearchOption.TopDirectoryOnly).Length>0)throw new InvalidOperationException("已有补帧所有权记录，但完整性密钥缺失；未创建新密钥或部署新目标。请恢复原密钥并保留记录。");
                byte[] key=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(key);WriteBytes(KeyPath,key);
            }
            ValidateRegular(KeyPath);byte[] result=File.ReadAllBytes(KeyPath);if(result.Length!=32)throw new InvalidOperationException("补帧记录完整性密钥损坏。");return result;
        }
        static byte[] Payload(FrameGenInstallJournal journal)
        {
            using(var memory=new MemoryStream())using(var binary=new BinaryWriter(memory,Encoding.UTF8))
            {
                binary.Write("frameGenInstall:1");binary.Write(journal.Executable.ToUpperInvariant());binary.Write(journal.CreatedUtc);
                foreach(var file in journal.Files){binary.Write(file.Name);binary.Write(file.Destination.ToUpperInvariant());binary.Write(file.Hash);binary.Write(file.Temp.ToUpperInvariant());binary.Write(file.Owned);}
                binary.Flush();return memory.ToArray();
            }
        }
        string Mac(FrameGenInstallJournal journal){using(var hmac=new HMACSHA256(ReadKey(false)))return Convert.ToBase64String(hmac.ComputeHash(Payload(journal)));}
        void WriteJournal(FrameGenInstallJournal journal)
        {
            EnsureRecords();string path=JournalPath(journal.Executable),temp=Path.Combine(records,".fg-record-"+Guid.NewGuid().ToString("N")+".tmp");
            if(File.Exists(path))ValidateRegular(path);if(Directory.Exists(path))throw new InvalidOperationException("补帧记录路径不是正常文件。");
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    using(var xml=XmlWriter.Create(output,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,CloseOutput=false}))
                    {
                        xml.WriteStartElement("frameGenInstall");xml.WriteAttributeString("version","1");xml.WriteAttributeString("exe",journal.Executable);xml.WriteAttributeString("createdUtc",journal.CreatedUtc);xml.WriteAttributeString("mac",Mac(journal));
                        foreach(var file in journal.Files){xml.WriteStartElement("file");xml.WriteAttributeString("name",file.Name);xml.WriteAttributeString("path",file.Destination);xml.WriteAttributeString("sourceHash",file.Hash);xml.WriteAttributeString("temp",file.Temp);xml.WriteAttributeString("owned",file.Owned.ToString());xml.WriteEndElement();}xml.WriteEndElement();
                    }
                    output.Flush(true);
                }
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        FrameGenInstallJournal ReadJournal(string exe)
        {
            string path=JournalPath(exe);if(!File.Exists(path)){if(Directory.Exists(path))throw new InvalidOperationException("补帧记录路径类型无效。");return null;}
            try
            {
                var document=ReadXml(path);var root=document.DocumentElement;
                if(root==null||root.Name!="frameGenInstall"||root.GetAttribute("version")!="1"||root.Attributes.Count!=4||!SamePath(root.GetAttribute("exe"),exe))throw new FormatException("记录目标或版本无效");
                DateTime timestamp;if(!DateTime.TryParseExact(root.GetAttribute("createdUtc"),"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out timestamp))throw new FormatException("记录日期无效");
                var journal=new FrameGenInstallJournal{Executable=exe,CreatedUtc=root.GetAttribute("createdUtc")};int index=0;
                foreach(XmlNode node in root.ChildNodes)
                {
                    var element=node as XmlElement;if(element==null||element.Name!="file"||element.Attributes.Count!=5||index>=Names.Length)throw new FormatException("记录条目无效");
                    string name=element.GetAttribute("name"),destination=element.GetAttribute("path"),hash=element.GetAttribute("sourceHash"),temp=element.GetAttribute("temp"),owned=element.GetAttribute("owned");
                    if(name!=Names[index]||!SamePath(destination,Path.Combine(Path.GetDirectoryName(exe),name))||hash.Length!=64||!IsHex(hash))throw new FormatException("记录文件不受允许");
                    if(!SamePath(Path.GetDirectoryName(temp),Path.GetDirectoryName(exe)))throw new FormatException("暂存文件越界");
                    string expectedPrefix=".rtxfg-",stem=Path.GetFileName(temp);if(stem.Length!=45||!stem.StartsWith(expectedPrefix,StringComparison.Ordinal)||!IsHex(stem.Substring(7,32))||stem.Substring(39)!="-"+index+".tmp")throw new FormatException("暂存文件名无效");
                    if(owned!="True"&&owned!="False")throw new FormatException("所有权状态无效");
                    journal.Files.Add(new FrameGenInstallFile{Name=name,Destination=Path.Combine(Path.GetDirectoryName(exe),name),Hash=hash,Temp=temp,Owned=owned=="True"});index++;
                }
                if(index!=Names.Length)throw new FormatException("记录不完整");
                byte[] supplied;try{supplied=Convert.FromBase64String(root.GetAttribute("mac"));}catch(FormatException){throw new FormatException("记录签名无效");}
                byte[] expected=Convert.FromBase64String(Mac(journal));int diff=supplied.Length^expected.Length;for(int i=0;i<Math.Min(supplied.Length,expected.Length);i++)diff|=supplied[i]^expected[i];if(diff!=0)throw new FormatException("记录完整性校验失败");
                return journal;
            }
            catch(Exception ex){throw new InvalidOperationException("补帧安装记录无法验证；未修改任何目标文件。请保留记录："+path,ex);}
        }
        static XmlDocument ReadXml(string path)
        {
            ValidateRegular(path);if(new FileInfo(path).Length>65536)throw new InvalidOperationException("补帧记录过大。");
            var document=new XmlDocument{XmlResolver=null};using(var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))document.Load(reader);return document;
        }
        static void CheckNotRunning(string exe)
        {
            foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))using(process)
            {
                try{if(SamePath(process.MainModule.FileName,exe))throw new InvalidOperationException("游戏仍在运行，请退出后修改或撤回补帧。");}
                catch(System.ComponentModel.Win32Exception ex){throw new InvalidOperationException("无法确认同名游戏进程，已停止修改补帧文件。",ex);}
            }
        }
        static byte[] ReadConfig(string path){ValidateRegular(path);if(new FileInfo(path).Length>1048576)throw new InvalidOperationException("补帧配置过大，原文件保留。");return File.ReadAllBytes(path);}
        static string DecodeConfig(byte[] data)
        {
            bool bom=data.Length>=3&&data[0]==239&&data[1]==187&&data[2]==191;try{return new UTF8Encoding(false,true).GetString(data,bom?3:0,data.Length-(bom?3:0));}catch(DecoderFallbackException ex){throw new InvalidOperationException("补帧配置不是有效 UTF-8，原文件保留。",ex);}
        }
        static void WriteBytes(string path,byte[] data){NeuralInstallService.CheckDirectory(Path.GetDirectoryName(path),true);using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(data,0,data.Length);output.Flush(true);}}
        static void ValidateRegular(string path){NeuralInstallService.CheckDirectory(Path.GetDirectoryName(path),true);if(!File.Exists(path)||NeuralInstallService.IsReparse(path))throw new InvalidOperationException("所需补帧文件缺失或经过链接："+Path.GetFileName(path));}
        static string CanonicalLocal(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||path.Length<3||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf(':',2)>=0||!Char.IsLetter(path[0]))throw new InvalidOperationException("补帧只支持本机磁盘的完整路径。");
            foreach(string part in path.Substring(3).Split('\\','/'))if(part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidOperationException("补帧路径包含不明确目录段。");
            string full=Path.GetFullPath(path);if(new DriveInfo(Path.GetPathRoot(full)).DriveType==DriveType.Network)throw new InvalidOperationException("补帧不支持映射网络磁盘。");return full;
        }
        static string CanonicalExe(string exe,bool exists)
        {
            exe=CanonicalLocal(exe);if(!Path.GetExtension(exe).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请选择游戏的 .exe 文件。");
            NeuralInstallService.CheckDirectory(Path.GetDirectoryName(exe),exists);if(File.Exists(exe)&&NeuralInstallService.IsReparse(exe))throw new InvalidOperationException("游戏程序不能经过链接。");if(exists&&!File.Exists(exe))throw new InvalidOperationException("游戏程序不存在。");return exe;
        }
        static bool SamePath(string first,string second){return String.Equals(CanonicalLocal(first),CanonicalLocal(second),StringComparison.OrdinalIgnoreCase);}
        static string Hash(byte[] data){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
        static bool IsHex(string value){foreach(char c in value)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))return false;return true;}
        sealed class Guard:IDisposable
        {
            readonly Mutex mutex;bool held;
            internal Guard(string directory){mutex=new Mutex(false,@"Local\RtxHub_FgInstall_"+Hash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));try{held=mutex.WaitOne(5000);}catch(AbandonedMutexException){held=true;}if(!held){mutex.Dispose();throw new InvalidOperationException("另一个补帧安装或撤回操作正在进行。");}}
            public void Dispose(){if(held){mutex.ReleaseMutex();held=false;}mutex.Dispose();}
        }
    }
}
