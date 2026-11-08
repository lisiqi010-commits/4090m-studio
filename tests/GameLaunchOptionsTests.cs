using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using RtxHub;

internal static class GameLaunchOptionsTests
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static List<T> Find<T>(Control parent) where T : Control
    {
        var found = new List<T>();
        foreach (Control child in parent.Controls)
        {
            if (child is T) found.Add((T)child);
            found.AddRange(Find<T>(child));
        }
        return found;
    }
    static void ShowOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
    }
    [STAThread] static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Form preview = GameLaunchOptions.CreatePreview())
            {
                Check(preview != null, "Pure preview is available without a game path.");
                ShowOffscreen(preview);
                var selectors = Find<ComboBox>(preview);
                Check(selectors.Count == 1, "One multiplier selector without technical switches.");
                var multiplier = selectors[0];
                Check(multiplier.DropDownStyle == ComboBoxStyle.DropDownList, "Users cannot type unsupported multipliers.");
                Check(multiplier.Items.Count == 6, "Six fixed choices are available.");
                for (int factor = 1; factor <= 6; factor++)
                    Check(multiplier.Items[factor - 1].ToString().StartsWith(factor + "×"), "Ordered choice " + factor);
                Check(multiplier.SelectedIndex == 0, "Default request is 1x.");
                Check(Find<CheckBox>(preview).Count == 0 && Find<TextBox>(preview).Count == 0, "No extra technical settings.");
                var text = new System.Text.StringBuilder();
                foreach (Label label in Find<Label>(preview)) text.AppendLine(label.Text);
                Check(text.ToString().Contains("游戏自身") && text.ToString().Contains("帧生成"), "1x explains independence from the game's own FG setting.");
                Check(text.ToString().Contains("实验") && text.ToString().Contains("DX12") && text.ToString().Contains("游戏内"), "Experimental game prerequisites are visible.");
                Check(text.ToString().Contains("官方") && text.ToString().Contains("验证"), "Community support and runtime evidence boundary are visible.");
                foreach (Control control in preview.Controls)
                    Check(preview.ClientRectangle.Contains(control.Bounds), "Layout stays inside the dialog.");
                string screenshot = Path.Combine(Path.GetTempPath(), "RtxHub-GameLaunchOptions-Preview.png");
                using (var bitmap = new Bitmap(preview.Width, preview.Height))
                {
                    preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
                    bitmap.Save(screenshot);
                }
                Console.WriteLine("Preview: " + screenshot);
            }
            using (var dialog = new GameLaunchOptionsDialog(@"D:\not-a-real-game-folder\private-parent\演示游戏.exe"))
            {
                ShowOffscreen(dialog);
                var labels = Find<Label>(dialog); bool filenameShown = false;
                foreach (Label label in labels)
                {
                    if (label.Text.Contains("演示游戏.exe")) filenameShown = true;
                    Check(!label.Text.Contains("private-parent"), "The dialog only displays the executable filename.");
                }
                Check(filenameShown, "The selected game is identifiable without reading the file.");
                Check(dialog.SelectedMultiplier == null, "No selection is committed before confirmation.");
                var multiplier = Find<ComboBox>(dialog)[0];
                multiplier.SelectedIndex = 5;
                Check(dialog.SelectedMultiplier == null, "Changing selection does not authorize a launch.");
                ((Button)dialog.AcceptButton).PerformClick();
                Check(dialog.DialogResult == DialogResult.OK && dialog.SelectedMultiplier == 6, "Confirm returns selected integer 6.");
            }
            using (var dialog = new GameLaunchOptionsDialog("game.exe"))
            {
                ShowOffscreen(dialog);
                Find<ComboBox>(dialog)[0].SelectedIndex = 3;
                ((Button)dialog.CancelButton).PerformClick();
                Check(dialog.SelectedMultiplier == null, "Back cancels a changed selection.");
            }
            using (var dialog = new GameLaunchOptionsDialog(null))
            {
                ShowOffscreen(dialog);
                dialog.Close();
                Check(dialog.SelectedMultiplier == null, "Closing a preview does not commit a request.");
            }
            Console.WriteLine("PASS: " + checks + " game launch option checks; no game, file discovery, hardware or backend operation.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
