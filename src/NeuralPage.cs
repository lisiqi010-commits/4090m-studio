using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace RtxHub
{
 public partial class HubForm
 {
  int neuralPreset=1;
  TextBox neuralExe;
  TextBox neuralDetail;
  CheckBox neuralOffline;
  PresetTile[] neuralTiles=new PresetTile[3];
  void BuildNeural()
  {
   TableLayoutPanel c=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Margin=Padding.Empty};
   c.RowStyles.Add(new RowStyle(SizeType.Absolute,55));c.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
   c.RowStyles.Add(new RowStyle(SizeType.Absolute,125));c.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   c.RowStyles.Add(new RowStyle(SizeType.Absolute,30));c.RowStyles.Add(new RowStyle(SizeType.Absolute,49));c.RowStyles.Add(new RowStyle(SizeType.Absolute,49));
   Panel heading=new Panel{Dock=DockStyle.Fill};Label title=Label("DLSS 5 神经渲染 · 实验",17,TextColor,true);title.SetBounds(0,0,850,31);heading.Controls.Add(title);
   Label sub=Label("NR Nexus + 社区兼容运行库。准备组件不会修改游戏；安装后仍需核实实际运行。",9,Muted,false);sub.SetBounds(0,32,900,23);heading.Controls.Add(sub);c.Controls.Add(heading,0,0);
   TableLayoutPanel picker=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};picker.RowStyles.Add(new RowStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,129));
   neuralExe=new TextBox{Text=selectedGame,Dock=DockStyle.Fill,BackColor=Surface,ForeColor=TextColor,Margin=new Padding(0,9,10,0),AccessibleName="离线游戏实际 EXE 路径"};
   Button browse=Button("选择离线游戏",false);browse.Dock=DockStyle.Fill;browse.Margin=new Padding(0,2,0,6);
   browse.Click+=delegate{using(OpenFileDialog d=new OpenFileDialog{Title="选择允许插件的离线 DX11 / DX12 x64 游戏主程序",Filter="Windows 程序|*.exe",CheckFileExists=true}){if(d.ShowDialog(this)==DialogResult.OK){neuralExe.Text=d.FileName;selectedGame=d.FileName;SavePreferences();}}};
   picker.Controls.Add(neuralExe,0,0);picker.Controls.Add(browse,1,0);c.Controls.Add(picker,0,1);
   TableLayoutPanel cards=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};
   string[] titles={"轻量 NR","均衡 NR","画质 NR"};string[] desc={"单层模型 B · 50%\r\n降低神经渲染工作量","单层模型 B · 67%\r\n建议先从这里体验","单层模型 B · 100%\r\n处理成本较高"};
   for(int i=0;i<3;i++){cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.3333f));int choice=i;neuralTiles[i]=new PresetTile("0"+(i+1),titles[i],desc[i]);neuralTiles[i].Click+=delegate{if(!busy){neuralPreset=choice;UpdateNeural();}};cards.Controls.Add(neuralTiles[i],i,0);}c.Controls.Add(cards,0,2);
   neuralDetail=DetailsBox();neuralDetail.Margin=new Padding(0,8,12,4);c.Controls.Add(neuralDetail,0,3);
   neuralOffline=new CheckBox{Text="游戏安装：我已确认目标为允许插件的离线单机，且游戏已关闭。",ForeColor=Muted,Dock=DockStyle.Fill,AutoSize=false,Font=new Font("Microsoft YaHei UI",9)};c.Controls.Add(neuralOffline,0,4);
   neuralExe.TextChanged+=delegate{neuralOffline.Checked=false;};
   FlowLayoutPanel row1=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
   Button prepare=Button("准备 / 校验组件",true);prepare.Click+=async delegate{await RunOperation(delegate{return NeuralPackages.Prepare(NeuralProgress);});};
   Button preflight=Button("预检游戏",false);preflight.Click+=async delegate{string exe=neuralExe.Text;await RunOperation(delegate{NeuralPackages.Verify();return NeuralInstaller.Preflight(exe,NeuralPackages.BundleDirectory);});};
   Button install=Button("安装 NR 到游戏",false);install.Click+=async delegate{if(!neuralOffline.Checked){WriteLog("请先确认目标允许插件且属于离线单机，再勾选上方确认项。网页和 PotPlayer 不使用此游戏安装按钮。");return;}string exe=neuralExe.Text;int preset=neuralPreset;await RunOperation(delegate{NeuralPackages.Verify();return NeuralInstaller.Install(exe,NeuralPackages.BundleDirectory,NeuralPresets.BuildGameIni(preset))+"\r\n文件安装不代表 NR 已执行；运行后检查日志，F6 对比、F11 查看开销。";});};
   Button restore=Button("撤回游戏 NR",false);restore.Click+=async delegate{string exe=neuralExe.Text;await RunOperation(delegate{return NeuralInstaller.Restore(exe);});};
   row1.Controls.AddRange(new Control[]{prepare,preflight,install,restore});c.Controls.Add(row1,0,5);
   FlowLayoutPanel row2=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
   Button play=Button("选择影片并用 NR 播放",true);play.Click+=async delegate{using(OpenFileDialog d=new OpenFileDialog{Title="用独立 NR 播放器试播本地影片",Filter="视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.m4v|所有文件|*.*",CheckFileExists=true}){if(d.ShowDialog(this)==DialogResult.OK){string movie=d.FileName;string player=selectedMpv;int preset=neuralPreset;await RunOperation(delegate{return NeuralPlayback.Play(player,movie,preset);});}}};
   Button check=Button("检查运行日志",false);check.Click+=async delegate{string file=NeuralPlayback.LastLog;string game=neuralExe.Text;using(OpenFileDialog d=new OpenFileDialog{Title="选择本次 ReShade.log（插件加载与 NR 执行分别检查）",Filter="ReShade 日志|*.log|所有文件|*.*",CheckFileExists=true}){if(!String.IsNullOrEmpty(game)&&Path.IsPathRooted(game)){string gameFolder=Path.GetDirectoryName(game);if(Directory.Exists(gameFolder))d.InitialDirectory=gameFolder;}if(!String.IsNullOrEmpty(file)&&File.Exists(file))d.FileName=file;if(d.ShowDialog(this)==DialogResult.OK){string chosen=d.FileName;await RunOperation(delegate{return NeuralPresets.AnalyzeLog(chosen);});}}};
   Button folder=Button("打开组件目录",false);folder.Click+=delegate{try{Directory.CreateDirectory(NeuralPackages.BundleDirectory);Process.Start(new ProcessStartInfo(NeuralPackages.BundleDirectory){UseShellExecute=true});}catch(Exception ex){WriteLog(ex.Message);}};
   Button info=Button("NR 使用说明",false);info.Click+=delegate{try{Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"DLSS5-NR-使用说明.txt")){UseShellExecute=true});}catch(Exception ex){WriteLog(ex.Message);}};
   row2.Controls.AddRange(new Control[]{play,check,folder,info});c.Controls.Add(row2,0,6);page.Controls.Add(c);UpdateNeural();
  }
  void UpdateNeural(){for(int i=0;i<3;i++){neuralTiles[i].Selected=i==neuralPreset;neuralTiles[i].Invalidate();}neuralDetail.Text=NeuralPackages.Status()+"\r\n当前："+new[]{"轻量 50%","均衡 67%","画质 100%"}[neuralPreset]+" · Present / 模型 B / 单层。视频用 SDR，游戏自动颜色。F6 开关对比，F11 查看实际成本。\r\n最终画面处理会影响 HUD 和字幕；中性深度／运动信息有画质限制。NR 增加 GPU 工作量。\r\n仅直接支持 DX11 / DX12 x64。检测到反作弊或已有注入文件会停止游戏安装。\r\n独立 NR 播放器使用单独目录，保留现有 mpv、网页和 PotPlayer 配置。";}
  void NeuralProgress(string message){if(!IsDisposed&&IsHandleCreated)BeginInvoke(new Action<string>(WriteLog),message);}
 }
}



