using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExtraVisualDLC
{
    public class UpdateInfo
    {
        public Version Version { get; set; }
        public string Url { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// Проверка обновлений через https://www.extravisualdlc.online/launcher/version.json
    /// и установка без батников: запущенный exe можно переименовать, а новый встать на его место.
    /// </summary>
    public static class Updater
    {
        public const string VersionUrl = "https://www.extravisualdlc.online/launcher/version.json";

        public static Version Current() =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

        public static async Task<UpdateInfo> CheckAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string json = await http.GetStringAsync(VersionUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var v = Version.Parse(root.GetProperty("version").GetString() ?? "0.0");
            if (v <= Current()) return null;

            string notes = "";
            if (root.TryGetProperty("notes", out var n)) notes = n.GetString() ?? "";
            return new UpdateInfo
            {
                Version = v,
                Url = root.GetProperty("url").GetString() ?? "",
                Notes = notes
            };
        }

        public static async Task DownloadAsync(string url, string dest, IProgress<int> progress)
        {
            if (File.Exists(dest)) File.Delete(dest);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var net = await resp.Content.ReadAsStreamAsync();
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[1024 * 256];
            long done = 0;
            int read;
            while ((read = await net.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                await fs.WriteAsync(buf, 0, read);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        public static void ApplyAndRestart()
        {
            string exe = Application.ExecutablePath;
            string upd = exe + ".upd";
            string bak = exe + ".bak";
            if (!File.Exists(upd)) throw new FileNotFoundException("Файл обновления не найден");
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            File.Move(exe, bak);   // запущенный файл переименовать можно
            File.Move(upd, exe);   // новый встаёт на его место
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Exit();
        }

        public static void CleanupBackup()
        {
            try
            {
                string bak = Application.ExecutablePath + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
            }
            catch { }
        }
    }
}
