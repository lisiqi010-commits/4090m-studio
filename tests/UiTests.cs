using System;
using System.Drawing;
using System.Windows.Forms;
using RtxHub;
internal static class UiTests
{
 [STAThread] static int Main()
 {
  try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
  int checks=0;
  using(HubForm f=new HubForm(true))
  {
   f.ShowInTaskbar=false;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-32000,-32000);f.Show();Application.DoEvents();
   for(int i=0;i<4;i++){f.SwitchPage(i);Application.DoEvents();Check(f,ref checks);}
   var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
   f.SwitchPage(2);Application.DoEvents();
   var ack=(CheckBox)typeof(HubForm).GetField("neuralOffline",flags).GetValue(f);
   var pathBox=(TextBox)typeof(HubForm).GetField("neuralExe",flags).GetValue(f);
   ack.Checked=true;pathBox.Text=@"C:\another-game\different.exe";if(ack.Checked)throw new Exception("Offline confirmation must reset when target changes.");checks++;
   typeof(HubForm).GetField("selectedGame",flags).SetValue(f,@"C:\removed-game\game.exe");
   var guard=typeof(HubForm).GetMethod("RequireGame",flags);
   if(guard.GetParameters().Length!=1)throw new Exception("Restore path validation still requires a live executable.");
   if(!(bool)guard.Invoke(f,new object[]{false}))throw new Exception("Missing executable blocks original profile restoration.");checks++;
  }
  Console.WriteLine("PASS UI layout checks: "+checks);return 0; } catch(Exception ex) { Console.WriteLine("FAIL: "+ex.Message); return 1; }
 }
 static void Check(Control parent,ref int checks)
 {
  foreach(Control c in parent.Controls)
  {
   if(c is Button && c.Visible){checks++;if(c.Bottom>parent.ClientSize.Height+1||c.Right>parent.ClientSize.Width+1)throw new Exception("Clipped button: "+c.Text+" bounds="+c.Bounds+" parent="+parent.ClientSize);}
   Check(c,ref checks);
  }
 }
}




