using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace RtxHub
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length > 0 && args[0] == "--diagnose")
                {
                    string report = DriverProfiles.Probe() + "\r\nPotPlayer: " + VideoSupport.FindPotPlayer() + "\r\nmpv: " + VideoSupport.FindMpv();
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics.txt"), report, Encoding.UTF8);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--render")
                {
                    using (SimpleHome form = new SimpleHome(true))
                    {
                        form.ShowInTaskbar=false;
                        form.StartPosition=FormStartPosition.Manual;
                        form.Location=new Point(-32000,-32000);
                        form.Show();
                        Application.DoEvents();
                        form.PerformLayout();
                        Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-simple.png"));
                        }
                    }
                    using (Form form = SourcePicker.CreatePreview())
                    {
                        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
                        form.Show();Application.DoEvents();form.PerformLayout();Application.DoEvents();
                        using(Bitmap bitmap=new Bitmap(form.Width,form.Height))
                        {form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-sources.png"));}
                    }
                    return 0;
                }
                Application.Run(new SimpleHome(false));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "rtx-hub-error.txt"), ex.ToString());
                if (args.Length == 0) MessageBox.Show(ex.Message, "RTX Hub 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    public class PresetTile : Button
    {
        public string Caption;
        public string Detail;
        public string Number;
        public bool Selected;
        public PresetTile(string number,string title,string detail)
        {
            Number=number; Caption=title; Detail=detail;
            FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0;
            Cursor=Cursors.Hand; Dock=DockStyle.Fill; Margin=new Padding(0,0,12,0);
            AccessibleName=title; AccessibleDescription=detail;
            TabStop=true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg=Selected?Color.FromArgb(34,49,38):Color.FromArgb(30,34,39);
            e.Graphics.Clear(bg);
            using(Pen pen=new Pen(Selected?HubForm.Accent:Color.FromArgb(57,64,71),Selected?2:1))
                e.Graphics.DrawRectangle(pen,1,1,Width-3,Height-3);
            using(Font numberFont=new Font("Segoe UI",10,FontStyle.Bold))
            using(Font titleFont=new Font("Microsoft YaHei UI",15,FontStyle.Bold))
            using(Font detailFont=new Font("Microsoft YaHei UI",9.5f))
            {
                TextRenderer.DrawText(e.Graphics,Number,numberFont,new Point(18,14),Selected?HubForm.Accent:Color.FromArgb(149,159,167));
                TextRenderer.DrawText(e.Graphics,Caption,titleFont,new Rectangle(16,42,Width-27,35),Color.White,TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics,Detail,detailFont,new Rectangle(17,82,Width-33,60),Color.FromArgb(180,191,198),TextFormatFlags.WordBreak);
            }
            if(Focused) ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(6,6,Width-12,Height-12));
        }
    }

    public partial class HubForm : Form
    {
        public static readonly Color Accent=Color.FromArgb(178,235,126);
        static readonly Color Background=Color.FromArgb(18,21,25);
        static readonly Color Surface=Color.FromArgb(24,28,33);
        static readonly Color TextColor=Color.FromArgb(233,239,242);
        static readonly Color Muted=Color.FromArgb(163,175,184);
        Panel page;
        TextBox log;
        TextBox gamePath;
        TextBox mpvPath;
        TextBox detail;
        Label hardware;
        Button[] nav=new Button[4];
        PresetTile[] tiles=new PresetTile[3];
        int activePage=0;
        int gamePreset=1;
        int videoPreset=0;
        string selectedGame="";
        string selectedMpv="";
        string lastProbe="尚未检测";
        bool busy;
        bool renderMode;
        static string LocalDir {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Rtx4090MHub");}}
        public HubForm(bool render)
        {
            renderMode=render;
            Text="4090M Studio 2.0 · DLSS 5 NR 实验版";
            Size=new Size(1180,890); MinimumSize=new Size(1080,840);
            StartPosition=FormStartPosition.CenterScreen;
            BackColor=Background; ForeColor=TextColor;
            Font=new Font("Microsoft YaHei UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;
            selectedMpv=VideoSupport.FindMpv();
            LoadPreferences();

            TableLayoutPanel shell=new TableLayoutPanel();
            shell.Dock=DockStyle.Fill; shell.ColumnCount=2; shell.RowCount=1;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,183));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(shell);
            Panel sidebar=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(13,16,19),Margin=Padding.Empty};
            shell.Controls.Add(sidebar,0,0);
            Label brand=Label("4090M",25,Color.White,true);brand.Location=new Point(22,30);brand.Size=new Size(154,47);sidebar.Controls.Add(brand);
            Label studio=Label("S T U D I O",10,Accent,true);studio.Location=new Point(25,79);studio.Size=new Size(150,25);sidebar.Controls.Add(studio);
            Label tagline=Label("游戏与观影增强",9,Muted,false);tagline.Location=new Point(24,116);tagline.Size=new Size(153,30);sidebar.Controls.Add(tagline);
            string[] names={"01   游戏画面","02   视频观影","03   DLSS 5 实验","04   设备与恢复"};
            for(int i=0;i<4;i++)
            {
                int idx=i;nav[i]=Button(names[i],false);nav[i].SetBounds(12,193+i*61,159,47);
                nav[i].TextAlign=ContentAlignment.MiddleLeft;nav[i].Padding=new Padding(9,0,0,0);
                nav[i].Click+=delegate {if(!busy)SwitchPage(idx);};sidebar.Controls.Add(nav[i]);
            }
            Label foot=Label("RTX 40 SERIES\r\n本机处理 · 随时恢复",9,Muted,false);
            foot.Dock=DockStyle.Bottom;foot.Height=89;foot.Padding=new Padding(22,9,0,0);sidebar.Controls.Add(foot);

            TableLayoutPanel right=new TableLayoutPanel();right.Dock=DockStyle.Fill;right.RowCount=3;right.ColumnCount=1;right.Padding=new Padding(27,15,27,18);
            right.RowStyles.Add(new RowStyle(SizeType.Absolute,110));
            right.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute,99));
            shell.Controls.Add(right,1,0);
            Panel hero=new Panel{Dock=DockStyle.Fill};right.Controls.Add(hero,0,0);
            Label eyebrow=Label("RTX ENHANCEMENT / LOCAL COMPANION",9,Accent,true);eyebrow.SetBounds(0,4,650,21);hero.Controls.Add(eyebrow);
            Label title=Label("让每一帧，更合你意。",24,TextColor,true);title.SetBounds(-3,34,730,45);hero.Controls.Add(title);
            hardware=Label("RTX 4090 Laptop GPU · 可切换的游戏与视频预设",10,Muted,false);hardware.SetBounds(0,86,840,25);hero.Controls.Add(hardware);
            page=new Panel{Dock=DockStyle.Fill,BackColor=Background,Margin=Padding.Empty};right.Controls.Add(page,0,1);
            Panel bottom=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,13,0,0)};right.Controls.Add(bottom,0,2);
            log=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Surface,ForeColor=Muted,BorderStyle=BorderStyle.None,Font=new Font("Microsoft YaHei UI",9)};
            bottom.Controls.Add(log);
            WriteLog("就绪。新增 DLSS 5 NR 实验页面；社区兼容运行库不等同于 NVIDIA 官方支持。\r\n组件已安装与神经渲染已运行会分别显示；选择预设不会立即修改游戏。");
            SwitchPage(0);
            Shown+=async delegate {if(!renderMode) await RunOperation(delegate {lastProbe=DriverProfiles.Probe();return lastProbe;});};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;WriteLog("正在完成操作，请稍后关闭窗口。");}};
        }
        static Label Label(string text,float size,Color color,bool bold)
        {
            return new Label{Text=text,Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=color,BackColor=Color.Transparent,AutoSize=false};
        }
        static Button Button(string text,bool primary)
        {
            Button b=new Button{Text=text,Height=42,AutoSize=true,MinimumSize=new Size(110,42),FlatStyle=FlatStyle.Flat,BackColor=primary?Accent:Color.FromArgb(34,40,47),ForeColor=primary?Color.FromArgb(20,31,19):TextColor,Cursor=Cursors.Hand,Margin=new Padding(0,0,10,9),Padding=new Padding(10,0,10,0)};
            b.FlatAppearance.BorderSize=0;return b;
        }
        public void SwitchPage(int index)
        {
            if(busy)return;
            activePage=index;
            while(page.Controls.Count>0){Control old=page.Controls[0];page.Controls.RemoveAt(0);old.Dispose();}
            for(int i=0;i<4;i++){nav[i].BackColor=i==index?Color.FromArgb(34,49,38):Color.FromArgb(13,16,19);nav[i].ForeColor=i==index?Accent:Muted;}
            if(index==0)BuildGames(); else if(index==1)BuildVideo(); else if(index==2)BuildNeural(); else BuildSystem();
        }
        TableLayoutPanel Content(string title,string subtitle)
        {
            TableLayoutPanel c=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty};
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,55));
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,140));
            c.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,39));
            Panel top=new Panel{Dock=DockStyle.Fill};
            Label t=Label(title,17,TextColor,true);t.SetBounds(0,0,850,33);top.Controls.Add(t);
            Label s=Label(subtitle,9,Muted,false);s.SetBounds(0,33,850,22);top.Controls.Add(s);
            c.Controls.Add(top,0,0);page.Controls.Add(c);return c;
        }
        void BuildGames()
        {
            TableLayoutPanel c=Content("游戏画面","为《燕云十六声》和支持 DLSS 的 3A 游戏保存独立驱动配置。");
            TableLayoutPanel picker=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=Padding.Empty};
            picker.RowCount=1;picker.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,129));
            gamePath=new TextBox{Text=selectedGame,ReadOnly=false,Dock=DockStyle.Fill,BackColor=Surface,ForeColor=TextColor,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,10,10,0)};
            gamePath.AccessibleName="游戏主程序路径";
            gamePath.TextChanged+=delegate{selectedGame=gamePath.Text;};
            Button browse=Button("选择游戏",false);browse.Dock=DockStyle.Fill;browse.Margin=new Padding(0,2,0,6);
            browse.Click+=delegate{using(OpenFileDialog dialog=new OpenFileDialog{Title="选择游戏实际运行的主程序（尽量不要选择启动器）",Filter="Windows 程序|*.exe",CheckFileExists=true}){if(dialog.ShowDialog(this)==DialogResult.OK){selectedGame=dialog.FileName;gamePath.Text=selectedGame;SavePreferences();WriteLog("已选择 "+Path.GetFileName(selectedGame)+"。可先读取当前驱动配置。");}}};
            picker.Controls.Add(gamePath,0,0);picker.Controls.Add(browse,1,0);c.Controls.Add(picker,0,1);
            AddPresets(c,false);
            detail=DetailsBox();c.Controls.Add(detail,0,3);
            FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,9,0,0)};
            Button apply=Button("保存到驱动",true);apply.Click+=async delegate{if(RequireGame())await RunOperation(delegate {return DriverProfiles.Apply(selectedGame,gamePreset);});};
            Button read=Button("读取当前配置",false);read.Click+=async delegate{if(RequireGame())await RunOperation(delegate{return DriverProfiles.Read(selectedGame);});};
            Button restore=Button("恢复此游戏",false);restore.Click+=async delegate{if(RequireGame(false))await RunOperation(delegate{return DriverProfiles.Restore(selectedGame);});};
            Button nvidia=Button("NVIDIA app",false);nvidia.Click+=delegate{Open("nvidia");};
            actions.Controls.AddRange(new Control[]{apply,read,restore,nvidia});c.Controls.Add(actions,0,4);
            Label note=Label("需在游戏内开启 DLSS 并重启游戏。驱动保存成功不代表该游戏已采用覆盖。",9,Muted,false);note.Dock=DockStyle.Fill;note.Padding=new Padding(0,7,0,0);c.Controls.Add(note,0,5);
            UpdatePreset();
        }
        void BuildVideo()
        {
            TableLayoutPanel c=Content("视频观影","本地影片可直接用增强播放器对比；网页与 PotPlayer 使用其原生设置。");
            TableLayoutPanel picker=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=Padding.Empty};
            picker.RowCount=1;picker.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,129));
            mpvPath=new TextBox{Text=String.IsNullOrEmpty(selectedMpv)?"未发现 mpv，请指定播放器":selectedMpv,ReadOnly=true,Dock=DockStyle.Fill,BackColor=Surface,ForeColor=TextColor,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,10,10,0)};
            mpvPath.AccessibleName="独立增强播放器路径";
            Button browse=Button("选择播放器",false);browse.Dock=DockStyle.Fill;browse.Margin=new Padding(0,2,0,6);
            browse.Click+=delegate{using(OpenFileDialog d=new OpenFileDialog{Title="指定便携 mpv.exe",Filter="mpv 播放器|mpv.exe",CheckFileExists=true}){if(d.ShowDialog(this)==DialogResult.OK){selectedMpv=d.FileName;mpvPath.Text=selectedMpv;SavePreferences();}}};
            picker.Controls.Add(mpvPath,0,0);picker.Controls.Add(browse,1,0);c.Controls.Add(picker,0,1);
            AddPresets(c,true);
            detail=DetailsBox();c.Controls.Add(detail,0,3);
            FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,9,0,0)};
            Button play=Button("选择影片并增强播放",true);play.Click+=async delegate
            {
                using(OpenFileDialog d=new OpenFileDialog{Title="选择本地影片",Filter="视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.m4v;*.wmv;*.mpg;*.mpeg|所有文件|*.*",CheckFileExists=true})
                {
                    if(d.ShowDialog(this)==DialogResult.OK)
                    {
                        string movie=d.FileName;int preset=videoPreset;string player=selectedMpv;
                        await RunOperation(delegate{VideoSupport.PlayEnhanced(player,movie,preset);return VideoSupport.LastPlaybackStatus+"\r\n播放日志："+VideoSupport.LastPlaybackLogPath;});
                    }
                }
            };
            Button pot=Button("PotPlayer",false);pot.Click+=delegate{Open("potplayer");};
            Button nv=Button("RTX Video 设置",false);nv.Click+=delegate{Open("nvidia");};
            Button web=Button("打开浏览器",false);web.Click+=delegate{Open("browser");};
            Button guide=Button("详细设置",false);guide.Click+=delegate{ShowGuide();};
            actions.Controls.AddRange(new Control[]{play,pot,nv,web,guide});c.Controls.Add(actions,0,4);
            Label note=Label("独立播放器按本次预设启动。网页与 PotPlayer 需手动配置，按钮不会自动开启它们的 RTX 功能。",9,Muted,false);note.Dock=DockStyle.Fill;note.Padding=new Padding(0,7,0,0);c.Controls.Add(note,0,5);
            UpdatePreset();
        }
        void AddPresets(TableLayoutPanel content,bool video)
        {
            TableLayoutPanel row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};
            for(int i=0;i<3;i++)row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.3333f));
            string[] titles=video?new string[]{"RTX 超分","精细缩放","轻量流畅"}:new string[]{"画质优先","均衡体验","流畅优先"};
            string[] desc=video?new string[]{"NVIDIA 视频超分辨率\r\n适合低分辨率片源","高质量常规缩放\r\n保留原片帧率","硬件解码 + 轻量缩放\r\n降低播放处理负担"}:new string[]{"DLSS · Quality\r\n优先保留画面细节","DLSS · Balanced\r\n清晰度与帧率折中","DLSS · Performance\r\n降低内部渲染分辨率"};
            for(int i=0;i<3;i++){int choice=i;tiles[i]=new PresetTile("0"+(i+1),titles[i],desc[i]);tiles[i].Click+=delegate{if(busy)return;if(video)videoPreset=choice;else gamePreset=choice;UpdatePreset();};row.Controls.Add(tiles[i],i,0);}
            content.Controls.Add(row,0,2);
        }
        void UpdatePreset()
        {
            int preset=activePage==1?videoPreset:gamePreset;
            for(int i=0;i<3;i++){tiles[i].Selected=i==preset;tiles[i].Invalidate();}
            if(activePage==1)
            {
                string[] text={"本次独立播放：D3D11 硬件解码 + NVIDIA RTX 视频超分（2×）。\r\n建议用低于屏幕分辨率的 SDR 影片测试；不补帧、不自动开启 HDR。\r\n网页：NVIDIA app → 系统 → 视频 → 超分辨率；开启活动指示器验证。\r\nPotPlayer：偏好设置 → 视频 → 内置 D3D11 渲染器 → GPU 超分辨率。", "本次独立播放：high-quality 画质方案与常规高质量缩放。\r\n保留片源帧率；适合对比纹理、字幕与高对比边缘。\r\n网页可在 NVIDIA app 选择自动质量，平衡 GPU 使用率。\r\nPotPlayer 的增强需要单独在播放器内选择，并观察效果。", "本次独立播放：D3D11 硬件解码 + 双线性缩放。\r\n减少画面处理负担，保留原片帧率，不会把 24 帧电影变成 60 帧。\r\n网页与 PotPlayer：优先硬件解码，按需关闭高开销增强。\r\n若原片已接近屏幕分辨率，超分带来的提升可能很小。"};
                detail.Text=text[preset];
            }
            else
            {
                string[] modes={"Quality：细节优先，内部渲染分辨率较高。","Balanced：在清晰度与渲染负担之间折中。","Performance：优先提高渲染帧率，细节可能减少。"};
                detail.Text="当前方案 · "+modes[preset]+"\r\n实际写入：为所选程序启用 DLSS 超分模式覆盖，不替换游戏文件。\r\n在《燕云十六声》中先启用 DLSS；帧生成与 Reflex 需在游戏内另行设置。\r\n首次应用前保存原配置，可随时恢复；具体效果取决于游戏兼容性。";
            }
        }
        void BuildSystem()
        {
            TableLayoutPanel c=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};
            c.RowStyles.Add(new RowStyle(SizeType.Absolute,66));c.RowStyles.Add(new RowStyle(SizeType.Percent,100));c.RowStyles.Add(new RowStyle(SizeType.Absolute,65));c.RowStyles.Add(new RowStyle(SizeType.Absolute,104));c.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            Label title=Label("设备、支持范围与恢复",17,TextColor,true);title.Dock=DockStyle.Fill;c.Controls.Add(title,0,0);
            TextBox facts=InformationBox("RTX 4090 Laptop GPU 的支持范围\r\n\r\n可用：DLSS 超分辨率、DLAA、帧生成、光线重建，需游戏支持。\r\n未正式支持：DLSS 5 神经渲染、RTX 50 系列多帧生成。\r\n2.0 另提供社区兼容 NR 实验模式，具体运行需验证。\r\n\r\n此工具的游戏预设通过 NVIDIA 驱动接口保存，生效取决于游戏。\r\n视频增强使用独立 mpv 进程；PotPlayer 与网页设置由各自应用管理。\r\n\r\n首次应用保存原有配置，反复切换不会覆盖最初备份。\r\n恢复操作只处理本工具管理的设置，不重置整个驱动。",10,TextColor,false);
            facts.BackColor=Surface;facts.Dock=DockStyle.Fill;facts.Padding=new Padding(18);facts.Margin=new Padding(0,0,12,15);c.Controls.Add(facts,0,1);
            FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
            Button probe=Button("重新检测设备",true);probe.Click+=async delegate{await RunOperation(delegate{lastProbe=DriverProfiles.Probe();return lastProbe;});};
            Button backups=Button("打开备份目录",false);backups.Click+=delegate{try{Directory.CreateDirectory(DriverProfiles.BackupDirectory);Process.Start(new ProcessStartInfo(DriverProfiles.BackupDirectory){UseShellExecute=true});}catch(Exception ex){WriteLog(ex.Message);}};
            Button graphics=Button("Windows 图形设置",false);graphics.Click+=delegate{Open("graphics");};
            Button support=Button("RTX Video 说明",false);support.Click+=delegate{Open("support");};
            actions.Controls.AddRange(new Control[]{probe,backups,graphics,support});c.Controls.Add(actions,0,2);
            Label recovery=Label("恢复游戏：回到“游戏画面”，选择同一个游戏程序，点击“恢复此游戏”。\r\n备份保存在本机用户目录中，不会上传。独立播放器关闭即结束本次视频预设。\r\nHDR 需屏幕与 Windows 同时支持，本工具不会自动开启。\r\n界面中的预设不会推断或显示未经测量的 FPS 提升。",9,Muted,false);recovery.Dock=DockStyle.Fill;c.Controls.Add(recovery,0,3);
            Button readme=Button("使用说明与播放器日志",false);readme.Click+=delegate{try{Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"README.md")){UseShellExecute=true});}catch(Exception ex){WriteLog(ex.Message);}};c.Controls.Add(readme,0,4);page.Controls.Add(c);
        }
        static TextBox InformationBox(string text,float size,Color color,bool bold){return new TextBox{Text=text,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Surface,ForeColor=color,Font=new Font("Microsoft YaHei UI",size),TabStop=false};}
        static TextBox DetailsBox(){return new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Surface,ForeColor=TextColor,Font=new Font("Microsoft YaHei UI",9.5f),Dock=DockStyle.Fill,Margin=new Padding(0,12,12,8),TabStop=false};}
        bool RequireGame(bool requireExists=true){if(String.IsNullOrWhiteSpace(selectedGame)||(requireExists&&!File.Exists(selectedGame))){WriteLog("请先选择游戏实际运行的 .exe 主程序。");return false;}return true;}
        async Task RunOperation(Func<string> operation)
        {
            if(busy)return;
            busy=true;page.Enabled=false;UseWaitCursor=true;
            WriteLog("正在处理，请稍候…");
            try{string result=await Task.Run(operation);WriteLog(result);}
            catch(Exception ex){WriteLog("未完成："+ex.Message);}
            finally{busy=false;page.Enabled=true;UseWaitCursor=false;}
        }
        void ShowGuide(){using(Form guide=new Form{Text="视频设置步骤",Size=new Size(850,710),StartPosition=FormStartPosition.CenterParent,BackColor=Background,ForeColor=TextColor}){TextBox text=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Surface,ForeColor=TextColor,BorderStyle=BorderStyle.None,Font=new Font("Microsoft YaHei UI",11),Text=VideoSupport.GetGuide(videoPreset)};guide.Controls.Add(text);guide.ShowDialog(this);}}
        void Open(string target){try{VideoSupport.OpenTarget(target);}catch(Exception ex){WriteLog(ex.Message);}}
        void WriteLog(string text){if(log!=null){log.AppendText((log.TextLength==0?"":"\r\n")+DateTime.Now.ToString("HH:mm:ss")+"  "+text+"\r\n");}}
        void LoadPreferences(){try{string file=Path.Combine(LocalDir,"last-game.txt");if(File.Exists(file))selectedGame=File.ReadAllText(file,Encoding.UTF8);string player=Path.Combine(LocalDir,"last-mpv.txt");if(File.Exists(player)){string saved=File.ReadAllText(player,Encoding.UTF8);if(File.Exists(saved))selectedMpv=saved;}}catch{}}
        void SavePreferences(){try{Directory.CreateDirectory(LocalDir);File.WriteAllText(Path.Combine(LocalDir,"last-game.txt"),selectedGame,Encoding.UTF8);if(!String.IsNullOrEmpty(selectedMpv))File.WriteAllText(Path.Combine(LocalDir,"last-mpv.txt"),selectedMpv,Encoding.UTF8);}catch(Exception ex){WriteLog("无法记住所选路径："+ex.Message);}}
    }
}






