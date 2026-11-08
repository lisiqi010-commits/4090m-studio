using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
namespace RtxHub
{
    // Verified against the pinned sources, not generic INI language guesses:
    // ReShade v6.8.0 runtime_gui.cpp: load_config_gui reads [OVERLAY] Language;
    // build_font_atlas selects msyh.ttc/simsun.ttc for the zh-CN locale.
    // https://github.com/crosire/reshade/blob/v6.8.0/source/runtime_gui.cpp
    // https://github.com/crosire/reshade/blob/v6.8.0/source/dll_resources.cpp
    // Nexus v0.2.2 source (VERSION.txt pinned commit): StoreUiLanguage and
    // LoadConfiguration use UiLanguage; kLanguages stores Simplified as zh-Hans.
    // https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/blob/1e725d07a578157294b957b5adbe59bb0235f258/dlssnr.hpp
    // https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/blob/1e725d07a578157294b957b5adbe59bb0235f258/ui/i18n.hpp
    // ReShade's ini_file.cpp uses case-sensitive section/key maps and appends
    // duplicate values; duplicate language rows therefore become comments.
    public static class NeuralLanguage
    {
        public static string MergeChinese(string ini)
        {
            ini=ini??"";bool bom=ini.Length>0&&ini[0]=='\uFEFF';if(bom)ini=ini.Substring(1);
            return (bom?"\uFEFF":"")+SetKey(SetKey(ini, "OVERLAY", "Language", "zh-CN"), "RenoDX.DLSS5", "UiLanguage", "zh-Hans");
        }
        public static string ApplyChinese(string exe)
        {
            return new NeuralLanguageService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Rtx4090MHub\NeuralInstalls")).ApplyChinese(exe);
        }
        sealed class Line { internal string Text, Ending; internal Line(string text,string ending){Text=text;Ending=ending;} }
        static string SetKey(string ini,string section,string key,string value)
        {
            var lines = new List<Line>(); string newline = "";
            for (int offset=0;offset<ini.Length;)
            {
                int end=offset;while(end<ini.Length&&ini[end]!='\r'&&ini[end]!='\n')end++;
                int next=end;if(next<ini.Length){if(ini[next]=='\r'&&next+1<ini.Length&&ini[next+1]=='\n')next+=2;else next++;}
                string ending=ini.Substring(end,next-end);if(newline.Length==0&&ending.Length>0)newline=ending;
                lines.Add(new Line(ini.Substring(offset,end-offset),ending));offset=next;
            }
            if(newline.Length==0)newline="\r\n";
            string current="";int firstHeader=-1;bool found=false;
            for(int i=0;i<lines.Count;i++)
            {
                var line=lines[i];string trimmed=line.Text.Trim();
                if(trimmed.StartsWith("[",StringComparison.Ordinal))
                {
                    int close=trimmed.IndexOf(']');current=close>0?trimmed.Substring(1,close-1).Trim():"";
                    if(current==section&&firstHeader<0)firstHeader=i;continue;
                }
                if(current!=section||trimmed.Length==0||trimmed[0]==';'||trimmed[0]=='#'||trimmed[0]=='/')continue;
                int equals=line.Text.IndexOf('=');string rowKey=(equals<0?line.Text:line.Text.Substring(0,equals)).Trim();
                if(rowKey!=key)continue;
                if(found){line.Text="; RTX Hub: duplicate language row retained: "+line.Text;continue;}
                found=true;
                string suffix=equals<0?"":line.Text.Substring(equals+1);Match comment=Regex.Match(suffix,@"\s+[;#].*$");
                string prefix=equals<0?key+"=":line.Text.Substring(0,equals+1)+Regex.Match(suffix,@"^\s*").Value;
                line.Text=prefix+value;
                if(comment.Success)
                {
                    string oldEnding=line.Ending;line.Ending=newline;
                    lines.Insert(++i,new Line(comment.Value.TrimStart(),oldEnding));
                }
            }
            if(!found)
            {
                if(firstHeader>=0)
                {
                    if(lines[firstHeader].Ending.Length==0)lines[firstHeader].Ending=newline;
                    lines.Insert(firstHeader+1,new Line(key+"="+value,newline));
                }
                else
                {
                    if(lines.Count>0&&lines[lines.Count-1].Ending.Length==0)lines[lines.Count-1].Ending=newline;
                    lines.Add(new Line("["+section+"]",newline));lines.Add(new Line(key+"="+value,newline));
                }
            }
            var result=new StringBuilder();foreach(var line in lines)result.Append(line.Text).Append(line.Ending);return result.ToString();
        }
    }
    internal sealed class NeuralLanguageService
    {
        readonly string directory;
        internal NeuralLanguageService(string directory){this.directory=Path.GetFullPath(directory);}
        internal string ApplyChinese(string exe)
        {
            // Same mutex as NeuralInstallService: language edits cannot race a Hub
            // install/restore. A running ReShade process remains an external writer.
            string mutexName=@"Local\Rtx4090MHub_NeuralInstall_"+Hash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
            using(var mutex=new Mutex(false,mutexName))
            {
                bool locked=false;
                try
                {
                    try{locked=mutex.WaitOne(10000);}catch(AbandonedMutexException){locked=true;}
                    if(!locked)throw new InvalidOperationException("另一个安装或恢复操作正在处理此配置，请稍后重试。");
                    return ApplyLocked(exe);
                }
                catch(InvalidOperationException){throw;}
                catch(Exception ex){throw new InvalidOperationException("中文界面配置未完成，原有配置和备份会保留："+ex.Message,ex);}
                finally{if(locked)mutex.ReleaseMutex();}
            }
        }
        string ApplyLocked(string exe)
        {
            var installs=new NeuralInstallService(directory);string journal=installs.JournalPath(exe);
            if(!File.Exists(journal))throw new InvalidOperationException("此程序没有本工具的安装记录，拒绝修改未知配置。");
            installs.Status(exe); // strict complete journal validator; does not mutate or require the game to exit
            exe=Path.GetFullPath(exe);string config=Path.Combine(Path.GetDirectoryName(exe),"ReShade.ini");
            var document=new XmlDocument{XmlResolver=null};
            using(var reader=XmlReader.Create(journal,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))document.Load(reader);
            bool owned=false;
            foreach(XmlNode child in document.DocumentElement.ChildNodes)
            {
                var item=child as XmlElement;
                if(item!=null&&item.GetAttribute("name")=="ReShade.ini")
                    owned=item.GetAttribute("owned")=="True"&&String.Equals(Path.GetFullPath(item.GetAttribute("path")),config,StringComparison.OrdinalIgnoreCase);
            }
            if(!owned)throw new InvalidOperationException("安装记录未确认 ReShade.ini 所有权，拒绝修改该文件。");
            NeuralInstallService.CheckDirectory(Path.GetDirectoryName(config),true);
            if(!File.Exists(config)||NeuralInstallService.IsReparse(config))throw new InvalidOperationException("受管 ReShade.ini 缺失或是重解析点，未修改任何文件。");
            byte[] before=ReadBytes(config);bool bom=before.Length>=3&&before[0]==239&&before[1]==187&&before[2]==191;
            var utf8=new UTF8Encoding(false,true);string text;
            try{text=utf8.GetString(before,bom?3:0,before.Length-(bom?3:0));}
            catch(DecoderFallbackException ex){throw new InvalidOperationException("配置不是有效 UTF-8，已保留原文件，请先检查编码。",ex);}
            string merged=NeuralLanguage.MergeChinese(text);
            if(merged==text)return "ReShade 和 NR Nexus 已配置为简体中文；运行中的程序可能需要正常重启才能刷新。";
            byte[] content=utf8.GetBytes(merged);byte[] after=new byte[content.Length+(bom?3:0)];
            if(bom){after[0]=239;after[1]=187;after[2]=191;}Buffer.BlockCopy(content,0,after,bom?3:0,content.Length);
            string token=Guid.NewGuid().ToString("N"),temp=Path.Combine(Path.GetDirectoryName(config),".rtxhub-language-"+token+".tmp"),backup=config+".before-Chinese-"+token+".bak";
            try
            {
                WriteBytes(temp,after);
                if(Hash(ReadBytes(config))!=Hash(before))throw new InvalidOperationException("运行中的程序刚刚更改了配置，已停止迁移；请正常退出后重试。");
                File.Replace(temp,config,backup); // atomically retains the actual previous file, without changing the ownership journal
                if(Hash(ReadBytes(backup))!=Hash(before))
                {
                    // A concurrent write landed between the optimistic check and
                    // replacement. Roll back only if our replacement is still intact.
                    if(Hash(ReadBytes(config))==Hash(after)){WriteBytes(temp,ReadBytes(backup));File.Replace(temp,config,null);}
                    throw new InvalidOperationException("检测到并发配置保存，已保留其完整备份。请正常退出程序后重试："+backup);
                }
                if(Hash(ReadBytes(config))!=Hash(after))throw new InvalidOperationException("运行中的程序随后又保存了配置，请正常退出后重试。迁移前备份："+backup);
                return "已将 ReShade 和 NR Nexus 界面配置为简体中文；画面参数、方案和注释已保留。运行中的程序可能需要正常重启后生效。\r\n原配置备份："+backup;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        static byte[] ReadBytes(string path)
        {
            if(NeuralInstallService.IsReparse(path))throw new InvalidOperationException("拒绝访问配置重解析点："+path);
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length>4*1024*1024)throw new InvalidOperationException("配置超过安全迁移大小限制。");
                byte[] value=new byte[(int)file.Length];int offset=0,count;while(offset<value.Length&&(count=file.Read(value,offset,value.Length-offset))>0)offset+=count;
                if(offset!=value.Length)throw new IOException("配置读取不完整。");return value;
            }
        }
        static void WriteBytes(string path,byte[] bytes){using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}}
        static string Hash(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    }
}
