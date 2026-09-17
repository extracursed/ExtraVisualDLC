using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExtraVisualDLC
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--test-logo")
            {
                RunLogoTest();
                return;
            }
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // Самодиагностика картинок: пишет logo_test.txt/png и bg_test.png рядом с exe
        static void RunLogoTest()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            var lines = new List<string>();
            try
            {
                var viaLoader = MainForm.LoadLogo();
                lines.Add("loader=" + (viaLoader == null ? "NULL" : viaLoader.Width + "x" + viaLoader.Height));
                if (viaLoader != null)
                {
                    using var t = new Bitmap(viaLoader, new Size(256, 256));
                    t.Save(Path.Combine(dir, "logo_test.png"), ImageFormat.Png);
                    lines.Add("logo_test.png=saved");
                }
                var emb = LogoData.Image;
                lines.Add("embedded=" + (emb == null ? "NULL" : emb.Width + "x" + emb.Height));
                var bg = BgData.Image;
                lines.Add("bg=" + (bg == null ? "NULL" : bg.Width + "x" + bg.Height));
                if (bg != null)
                {
                    try
                    {
                        using var art = MainForm.BuildBackdrop(bg, 572, 310);
                        art.Save(Path.Combine(dir, "art_test.png"), ImageFormat.Png);
                        lines.Add("art_test.png=saved");
                    }
                    catch (Exception ex2) { lines.Add("ART-EX: " + ex2.GetType().Name + ": " + ex2.Message); }
                    try
                    {
                        bg.Save(Path.Combine(dir, "bg_direct.png"), ImageFormat.Png);
                        lines.Add("bg_direct.png=saved");
                    }
                    catch (Exception ex3) { lines.Add("BG-EX: " + ex3.GetType().Name + ": " + ex3.Message); }
                }
                lines.Add("OK");
            }
            catch (Exception ex)
            {
                string full = ex.ToString();
                if (full.Length > 800) full = full.Substring(0, 800);
                lines.Add("EX: " + full.Replace("\r", " ").Replace("\n", " "));
            }
            try { File.WriteAllLines(Path.Combine(dir, "logo_test.txt"), lines); } catch { }
        }
    }
}
