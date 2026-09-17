using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExtraVisualDLC
{
    public class LaunchResult
    {
        public bool Success;
        public string Message = "";
        public Process Process;
    }

    public static class MinecraftLauncher
    {
        private static readonly HttpClient http = new HttpClient();
        private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        // --- ExtraVisualDLC: полный клиент + визуалы (порт из sdfsdfsdf/launcher/bootstrap.py) ---
        public const string FabricLoaderVersion = "0.19.5";
        public static string ModUrl = ""; // задаётся из launcher/version.json (поле mod_url)
        private const string AdoptiumJre21Url = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";
        private static string FabricProfileUrl(string mc) =>
            $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{FabricLoaderVersion}/profile/json";
        private static string ModrinthFapiUrl(string mc) =>
            $"https://api.modrinth.com/v2/project/fabric-api/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%22{mc}%22%5D&limit=1";

        static MinecraftLauncher()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ExtraVisualDLC/1.0");
            http.Timeout = TimeSpan.FromMinutes(10);
        }

        public static async Task<List<string>> GetAvailableVersionsAsync()
        {
            var fallback = new List<string> { "1.20.1", "1.19.4", "1.18.2", "1.16.5", "1.12.2", "1.8.8", "1.7.10" };
            try
            {
                string json = await http.GetStringAsync(ManifestUrl);
                using var doc = JsonDocument.Parse(json);
                var list = new List<string>();
                foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                {
                    string type = v.GetProperty("type").GetString() ?? "";
                    if (type == "release")
                        list.Add(v.GetProperty("id").GetString() ?? "");
                }
                if (list.Count > 0) return list;
            }
            catch { }
            return fallback;
        }

        public static string FindJava(string customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                return customPath;

            // 1. java в PATH
            try
            {
                var psi = new ProcessStartInfo("java", "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) return "java";
                }
            }
            catch { }

            // 2. Частые пути
            var candidates = new List<string>();
            candidates.AddRange(Directory.Exists(@"C:\Program Files\Java")
                ? Directory.GetFiles(@"C:\Program Files\Java", "java.exe", SearchOption.AllDirectories).Take(3) : new string[0]);
            candidates.AddRange(Directory.Exists(@"C:\Program Files\Eclipse Adoptium")
                ? Directory.GetFiles(@"C:\Program Files\Eclipse Adoptium", "java.exe", SearchOption.AllDirectories).Take(3) : new string[0]);
            string mcRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
            if (Directory.Exists(mcRuntime))
            {
                try { candidates.AddRange(Directory.GetFiles(mcRuntime, "javaw.exe", SearchOption.AllDirectories).Take(2)); } catch { }
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return "java";
        }

        public static string OfflineUuid(string nickname)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + nickname));
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // version 3
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variant
            var guid = new Guid(hash);
            return guid.ToString("N");
        }

        // --- Java 21 / Fabric / mods (порт bootstrap.py) ---
        private static bool IsJava21OrNewer(string javaExe)
        {
            try
            {
                var psi = new ProcessStartInfo(javaExe, "-version")
                {
                    UseShellExecute = false, RedirectStandardError = true,
                    RedirectStandardOutput = true, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string err = p.StandardError.ReadToEnd();
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                string all = err + outp;
                return all.Contains(" 21") || all.Contains("\"21") || all.Contains(" 22") || all.Contains(" 23")
                    || all.Contains("openjdk 21") || all.Contains("openjdk version \"21");
            }
            catch { return false; }
        }

        public static async Task<string> EnsureJava21Async(string customPath, Action<string> log)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) && IsJava21OrNewer(customPath))
                return customPath;
            // 1) наш рантайм
            string rt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".extravisualdlc", "runtime", "java21");
            try
            {
                if (Directory.Exists(rt))
                    foreach (var f in Directory.GetFiles(rt, "java.exe", SearchOption.AllDirectories))
                        if (IsJava21OrNewer(f)) return f;
            }
            catch { }
            // 2) системная Java 21
            string sys = FindJava21System();
            if (sys != null) { log?.Invoke("[setup] Java уже есть: " + sys); return sys; }
            // 3) качаем Temurin JRE 21
            log?.Invoke("[setup] качаю Java 21 (Temurin, ~60 МБ)...");
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".extravisualdlc");
            string zip = Path.Combine(home, "runtime", "temurin21.zip");
            await DownloadFileAsync(AdoptiumJre21Url, zip);
            ZipFile.ExtractToDirectory(zip, rt, true);
            try { File.Delete(zip); } catch { }
            foreach (var f in Directory.GetFiles(rt, "java.exe", SearchOption.AllDirectories))
            { log?.Invoke("[setup] Java готова: " + f); return f; }
            throw new Exception("Java 21 скачалась, но java.exe не найден");
        }

        private static string FindJava21System()
        {
            var cands = new List<string>();
            try { string w = FindJava(""); if (w != null && IsJava21OrNewer(w)) return w; } catch { }
            string jh = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
            try
            {
                string j = Path.Combine(jh, "bin", "java.exe");
                if (File.Exists(j) && IsJava21OrNewer(j)) return j;
            }
            catch { }
            foreach (var b in new[] { @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium" })
            {
                try
                {
                    if (!Directory.Exists(b)) continue;
                    foreach (var f in Directory.GetFiles(b, "java.exe", SearchOption.AllDirectories).Take(5))
                        if (IsJava21OrNewer(f)) return f;
                }
                catch { }
            }
            return null;
        }

        private static (string dest, string url) MavenToPath(string libsDir, string name, string baseUrl)
        {
            var parts = name.Split(':');
            string g = parts[0], a = parts[1], v = parts[2];
            string clf = parts.Length > 3 ? parts[3] : null;
            string rel = $"{g.Replace('.', '/')}/{a}/{v}/{a}-{v}" + (clf != null ? "-" + clf : "") + ".jar";
            return (Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)), baseUrl + rel);
        }

        public static async Task<(List<string> jars, string mainClass)> EnsureFabricAsync(string versionId, string libsDir, Action<string> log)
        {
            log?.Invoke($"[setup] проверяю Fabric Loader {FabricLoaderVersion}...");
            string json = await http.GetStringAsync(FabricProfileUrl(versionId));
            using var doc = JsonDocument.Parse(json);
            string mainClass = doc.RootElement.TryGetProperty("mainClass", out var mc)
                ? mc.GetString() : "net.fabricmc.loader.impl.launch.knot.KnotClient";
            var jars = new List<string>();
            // новый формат профиля: libraries верхнего уровня (maven-координаты)
            if (doc.RootElement.TryGetProperty("libraries", out var topLibs))
            {
                foreach (var lib in topLibs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("name", out var n)) continue;
                    string url = lib.TryGetProperty("url", out var u) ? u.GetString() : "https://maven.fabricmc.net/";
                    var (dest, durl) = MavenToPath(libsDir, n.GetString(), url);
                    if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                    {
                        log?.Invoke("[setup] качаю " + n.GetString() + "...");
                        await DownloadFileAsync(durl, dest);
                    }
                    jars.Add(dest);
                }
            }
            // старый формат: launcherMeta.libraries
            if (jars.Count == 0
                && doc.RootElement.TryGetProperty("launcherMeta", out var lm) && lm.TryGetProperty("libraries", out var libs))
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("name", out var n)) continue;
                    string url = lib.TryGetProperty("url", out var u) ? u.GetString() : "https://maven.fabricmc.net/";
                    var (dest, durl) = MavenToPath(libsDir, n.GetString(), url);
                    if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                        await DownloadFileAsync(durl, dest);
                    jars.Add(dest);
                }
            }
            if (jars.Count == 0)
                throw new Exception("Fabric-профиль пуст (meta.fabricmc.net вернул 0 библиотек)");
            log?.Invoke("[setup] Fabric готов ✅");
            return (jars, mainClass);
        }

        public static async Task EnsureFabricApiAsync(string gameDir, string versionId, Action<string> log, Action<int, string> progress)
        {
            string mods = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(mods);
            if (Directory.GetFiles(mods, "fabric-api*.jar").Any()) return;
            try
            {
                log?.Invoke("[setup] качаю fabric-api...");
                progress?.Invoke(70, "Скачивание fabric-api...");
                string json = await http.GetStringAsync(ModrinthFapiUrl(versionId));
                using var doc = JsonDocument.Parse(json);
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Undefined) return;
                var files = first.GetProperty("files").EnumerateArray().ToList();
                var prim = files.FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
                if (prim.ValueKind == JsonValueKind.Undefined) prim = files[0];
                string url = prim.GetProperty("url").GetString();
                string name = prim.GetProperty("filename").GetString();
                await DownloadFileAsync(url, Path.Combine(mods, name));
            }
            catch (Exception ex) { log?.Invoke("[setup] fabric-api пропущен: " + ex.Message); }
        }

        public static async Task EnsureOurModAsync(string gameDir, Action<string> log, Action<int, string> progress)
        {
            string mods = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(mods);
            // Если целый визуал уже лежит — НЕ трогаем (игра могла быть запущена,
            // jar занят JVM; удаление/перекачивание на каждый запуск роняет ModDiscoverer
            // с "файл занят другим процессом"). Чистим только прочий мусор.
            FileInfo keep = null;
            try
            {
                keep = Directory.GetFiles(mods, "*.jar")
                    .Select(f => new FileInfo(f))
                    .Where(f =>
                    {
                        string n = f.Name.ToLowerInvariant();
                        return (n.Contains("extravisual") || n.Contains("plintus")) && f.Length > 50_000_000;
                    })
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { }
            foreach (var f in Directory.GetFiles(mods, "*.jar"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (!(n.Contains("extravisual") || n.Contains("plintus"))) continue;
                if (keep != null && string.Equals(f, keep.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { /* занят запущенной игрой — пропускаем */ }
            }
            if (keep != null)
            { log?.Invoke("[setup] визуал уже на месте: " + keep.Name); return; }
            if (string.IsNullOrWhiteSpace(ModUrl))
            { log?.Invoke("[setup] mod_url не задан — визуал пропущен (задай в launcher/version.json)"); return; }
            string fileName = "ExtraVisualDLC-mod.jar";
            try { fileName = Path.GetFileName(new Uri(ModUrl).LocalPath); } catch { }
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "ExtraVisualDLC-mod.jar";
            string dest = Path.Combine(mods, fileName);
            if (File.Exists(dest) && new FileInfo(dest).Length > 50_000_000)
            { log?.Invoke("[setup] визуал уже на месте: " + fileName); return; }
            if (File.Exists(dest))
            { try { File.Delete(dest); log?.Invoke("[setup] битый визуал удалён, качаю заново..."); } catch { } }
            log?.Invoke("[setup] качаю визуал (~100+ МБ)...");
            progress?.Invoke(72, "Скачивание визуалов...");
            await DownloadFileAsync(ModUrl, dest);
            log?.Invoke("[setup] визуал установлен: " + fileName);
        }

        public static void SetModUrlFromVersionJson(string versionJsonPath)
        {
            try
            {
                if (!File.Exists(versionJsonPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
                if (doc.RootElement.TryGetProperty("mod_url", out var m))
                    ModUrl = m.GetString() ?? "";
            }
            catch { }
        }

        public static async Task<LaunchResult> LaunchAsync(
            string versionId, string nickname, int ramGb,
            string gameDir, string javaPath,
            Action<string> log, Action<int, string> progress)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nickname) || nickname.Length < 3)
                    return new LaunchResult { Success = false, Message = "Ник должен быть от 3 символов!" };
                if (nickname.Length > 16)
                    return new LaunchResult { Success = false, Message = "Ник не длиннее 16 символов!" };

                Directory.CreateDirectory(gameDir);
                // mod_url из сайта, если не задан в коде (чтобы визуалы качались сами)
                if (string.IsNullOrWhiteSpace(ModUrl))
                {
                    try
                    {
                        string vjson = await http.GetStringAsync("https://www.extravisualdlc.online/launcher/version.json");
                        using var vdoc = JsonDocument.Parse(vjson);
                        if (vdoc.RootElement.TryGetProperty("mod_url", out var m)) ModUrl = m.GetString() ?? "";
                    }
                    catch { }
                }
                string versionsDir = Path.Combine(gameDir, "versions", versionId);
                string libsDir = Path.Combine(gameDir, "libraries");
                string assetsDir = Path.Combine(gameDir, "assets");
                string nativesDir = Path.Combine(gameDir, "natives", versionId);
                Directory.CreateDirectory(versionsDir);
                Directory.CreateDirectory(libsDir);
                Directory.CreateDirectory(assetsDir);
                Directory.CreateDirectory(nativesDir);

                log($"[ExtraVisualDLC] Запуск {versionId} для {nickname}...");
                progress(2, "Получение манифеста версий...");

                // --- manifest ---
                string manifestJson = await http.GetStringAsync(ManifestUrl);
                string versionUrl = null;
                using (var doc = JsonDocument.Parse(manifestJson))
                {
                    foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                    {
                        if (v.GetProperty("id").GetString() == versionId)
                        {
                            versionUrl = v.GetProperty("url").GetString();
                            break;
                        }
                    }
                }
                if (versionUrl == null)
                    return new LaunchResult { Success = false, Message = $"Версия {versionId} не найдена в Mojang!" };

                // --- version json ---
                string versionJsonPath = Path.Combine(versionsDir, versionId + ".json");
                string versionJson;
                if (File.Exists(versionJsonPath) && new FileInfo(versionJsonPath).Length > 1000)
                {
                    versionJson = await File.ReadAllTextAsync(versionJsonPath);
                    log("version.json уже есть, пропуск скачивания");
                }
                else
                {
                    progress(8, "Скачивание version.json...");
                    versionJson = await http.GetStringAsync(versionUrl);
                    await File.WriteAllTextAsync(versionJsonPath, versionJson);
                }

                using var ver = JsonDocument.Parse(versionJson);
                var root = ver.RootElement;

                // --- client jar ---
                string clientJar = Path.Combine(versionsDir, versionId + ".jar");
                if (!File.Exists(clientJar) || new FileInfo(clientJar).Length < 100000)
                {
                    progress(12, "Скачивание клиента...");
                    string clientUrl = root.GetProperty("downloads").GetProperty("client").GetProperty("url").GetString();
                    log("Скачивание client.jar...");
                    await DownloadFileAsync(clientUrl, clientJar);
                }
                else log("client.jar уже есть");

                // --- libraries ---
                var classpath = new List<string> { clientJar };
                var libElements = root.GetProperty("libraries").EnumerateArray().ToList();
                int libDone = 0;
                foreach (var lib in libElements)
                {
                    libDone++;
                    if (!CheckRules(lib))
                        continue;

                    if (!lib.TryGetProperty("downloads", out var downloads))
                        continue;

                    // artifact
                    if (downloads.TryGetProperty("artifact", out var artifact))
                    {
                        string path = artifact.GetProperty("path").GetString();
                        string url = artifact.GetProperty("url").GetString();
                        string full = Path.Combine(libsDir, path.Replace('/', Path.DirectorySeparatorChar));
                        classpath.Add(full);
                        if (!File.Exists(full) || new FileInfo(full).Length == 0)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            await DownloadFileAsync(url, full);
                        }
                    }

                    // natives (windows) — 1.21.4 использует natives-windows-x86_64
                    if (downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        string nativeKey = null;
                        if (classifiers.TryGetProperty("natives-windows-x86_64", out _)) nativeKey = "natives-windows-x86_64";
                        else if (classifiers.TryGetProperty("natives-windows-64", out _)) nativeKey = "natives-windows-64";
                        else if (classifiers.TryGetProperty("natives-windows", out _)) nativeKey = "natives-windows";
                        if (nativeKey != null)
                        {
                            var nat = classifiers.GetProperty(nativeKey);
                            string path = nat.GetProperty("path").GetString();
                            string url = nat.GetProperty("url").GetString();
                            string full = Path.Combine(libsDir, path.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(full) || new FileInfo(full).Length == 0)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(full));
                                await DownloadFileAsync(url, full);
                            }
                            ExtractNatives(full, nativesDir, log);
                            // natives jar обычно НЕ в classpath, только распаковка
                        }
                    }

                    if (libDone % 20 == 0)
                    {
                        int pct = 12 + (int)(libDone * 28.0 / Math.Max(1, libElements.Count));
                        progress(pct, $"Библиотеки {libDone}/{libElements.Count}...");
                    }
                }
                log($"Библиотек: {classpath.Count}");

                // кастомный client (Forge/Fabric): если рядом лежит custom-client.jar — добавить первым
                string customJar = Path.Combine(gameDir, "custom-client.jar");
                if (File.Exists(customJar))
                {
                    classpath.Insert(0, customJar);
                    log("Найден custom-client.jar, добавлен в classpath");
                }

                // --- assets ---
                progress(42, "Проверка ассетов...");
                string assetIndexId = root.GetProperty("assetIndex").GetProperty("id").GetString();
                string assetIndexUrl = root.GetProperty("assetIndex").GetProperty("url").GetString();
                string indexesDir = Path.Combine(assetsDir, "indexes");
                Directory.CreateDirectory(indexesDir);
                string indexPath = Path.Combine(indexesDir, assetIndexId + ".json");
                string indexJson;
                if (File.Exists(indexPath) && new FileInfo(indexPath).Length > 100)
                    indexJson = await File.ReadAllTextAsync(indexPath);
                else
                {
                    indexJson = await http.GetStringAsync(assetIndexUrl);
                    await File.WriteAllTextAsync(indexPath, indexJson);
                }

                using var idxDoc = JsonDocument.Parse(indexJson);
                var objects = idxDoc.RootElement.GetProperty("objects").EnumerateObject().ToList();
                string objectsDir = Path.Combine(assetsDir, "objects");
                int needCount = 0;
                foreach (var o in objects)
                {
                    string hash = o.Value.GetProperty("hash").GetString();
                    string local = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                    if (!File.Exists(local)) needCount++;
                }
                log($"Ассетов всего: {objects.Count}, докачать: {needCount}");

                if (needCount > 0)
                {
                    var sem = new SemaphoreSlim(8);
                    int done = 0;
                    var tasks = objects.Select(async kv =>
                    {
                        string hash = kv.Value.GetProperty("hash").GetString();
                        string local = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                        if (File.Exists(local)) return;
                        await sem.WaitAsync();
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(local));
                            string url = $"https://resources.download.minecraft.net/{hash.Substring(0, 2)}/{hash}";
                            await DownloadFileAsync(url, local);
                            int d = Interlocked.Increment(ref done);
                            if (d % 50 == 0 || d == needCount)
                            {
                                int pct = 42 + (int)(d * 30.0 / Math.Max(1, needCount));
                                progress(pct, $"Ассеты {d}/{needCount}...");
                            }
                        }
                        finally { sem.Release(); }
                    }).ToArray();
                    await Task.WhenAll(tasks);
                }

                // --- Fabric + визуалы (полный клиент как в bootstrap.py) ---
                progress(68, "Проверка Fabric...");
                var (fabJars, fabMain) = await EnsureFabricAsync(versionId, libsDir, log);
                await EnsureFabricApiAsync(gameDir, versionId, log, progress);
                await EnsureOurModAsync(gameDir, log, progress);

                // --- build args (Fabric KnotClient) ---
                progress(75, "Подготовка запуска...");
                string uuid = OfflineUuid(nickname);
                // classpath ТОЛЬКО из текущей version.json (список classpath) + Fabric.
                // Обход всей папки libraries запрещён: там лежат stale-версии
                // (напр. datafixerupper 6.0.8 рядом с 8.0.16) — дубли роняют игру
                // (NoSuchMethodError Codec.withAlternative). Ванильный ASM тоже мимо:
                // у Fabric свой 9.10.1, дубли роняют Knot.
                var fullCp = new List<string> { clientJar };
                fullCp.AddRange(fabJars);
                var seen = new HashSet<string>(fullCp, StringComparer.OrdinalIgnoreCase);
                foreach (var p in classpath)
                {
                    if (seen.Contains(p)) continue;
                    string norm = p.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
                    if (norm.Contains("/org/ow2/asm/")) continue; // ванильный ASM 9.6 — мимо
                    fullCp.Add(p);
                    seen.Add(p);
                }
                string cpSep = ";";
                string cp = string.Join(cpSep, fullCp.Select(p => "\"" + p + "\""));
                string mainClass = fabMain;
                int xmsGb = Math.Max(1, ramGb / 2);

                var jvmArgs = new List<string>
                {
                    $"-Xmx{ramGb}G",
                    $"-Xms{xmsGb}G",
                    $"-Djava.library.path=\"{nativesDir}\"",
                    "-Dminecraft.launcher.brand=ExtraVisualDLC",
                    "-Dminecraft.launcher.version=1.0",
                };
                jvmArgs.Add("-cp");
                jvmArgs.Add(cp);
                jvmArgs.Add(mainClass);
                string minecraftArgs = $"--username {nickname} --version fabric-{versionId} --gameDir \"{gameDir}\" --assetsDir \"{assetsDir}\" --assetIndex {assetIndexId} --uuid {uuid} --accessToken 0 --userType legacy";

                string fullArgs = string.Join(" ", jvmArgs) + " " + minecraftArgs;
                string javaExe = await EnsureJava21Async(javaPath, log);
                log("Java: " + javaExe);
                log("Запуск с Fabric + визуалами...");
                var psi = new ProcessStartInfo(javaExe, fullArgs)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = gameDir
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) log("[MC] " + e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) log("[MC-ERR] " + e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                progress(100, "Игра запущена!");
                return new LaunchResult { Success = true, Message = "Запущено!", Process = proc };
            }
            catch (Exception ex)
            {
                return new LaunchResult { Success = false, Message = "Ошибка: " + ex.Message };
            }
        }

        private static bool CheckRules(JsonElement el)
        {
            if (!el.TryGetProperty("rules", out var rules))
                return true;
            bool allow = false;
            foreach (var r in rules.EnumerateArray())
            {
                string action = r.GetProperty("action").GetString() ?? "allow";
                bool match = true;
                if (r.TryGetProperty("os", out var os))
                {
                    if (os.TryGetProperty("name", out var name))
                    {
                        string n = name.GetString() ?? "";
                        match = n == "windows" || n == "osx" && false; // мы только windows
                        if (n != "windows") match = false; else match = true;
                    }
                    if (os.TryGetProperty("arch", out var arch))
                    {
                        string a = arch.GetString() ?? "";
                        bool is64 = Environment.Is64BitOperatingSystem;
                        if (a == "x86" && is64) match = false;
                    }
                }
                if (match)
                    allow = (action == "allow");
            }
            return allow;
        }

        private static async Task DownloadFileAsync(string url, string dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            Exception last = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    await resp.Content.CopyToAsync(fs);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    if (attempt < 4) await Task.Delay(1500 * attempt);
                }
            }
            throw new Exception("Нет соединения с серверами Minecraft (" + GetHost(url) + "). Проверь интернет или включи VPN и попробуй снова. Причина: " + (last?.Message ?? "?"));
        }

        private static string GetHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        private static void ExtractNatives(string jarPath, string nativesDir, Action<string> log)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("META-INF")) continue;
                    string dest = Path.Combine(nativesDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!entry.FullName.EndsWith("/"))
                        entry.ExtractToFile(dest, true);
                }
            }
            catch (Exception ex)
            {
                log("Natives warn: " + ex.Message);
            }
        }
    }
}
