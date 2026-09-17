using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ExtraVisualDLC
{
    public class SiteAccount
    {
        public string UserId = "";
        public string Email = "";
        public string Username = "";
        public string Role = "Пользователь";
        public long Uid;
        public bool Frozen;
        public string BoundHwid = "";
        public string AccessToken = "";
        public string RefreshToken = "";
    }

    /// <summary>
    /// Вход по аккаунту сайта (Supabase Auth через REST).
    /// HWID привязывается к профилю при первом входе и проверяется дальше.
    /// </summary>
    public static class SiteAuth
    {
        public const string ProjectUrl = "https://vzfjwchkdaafnsoxzddt.supabase.co";
        // Публичный ключ — его можно хранить в клиенте
        public const string AnonKey = "sb_publishable_sqljifUjv-s140nxHWZ5rw_yWJpg_eo";

        private static HttpClient MakeHttp(string token = null)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("apikey", AnonKey);
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            return http;
        }

        private static string ReadError(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("msg", out var m)) return m.GetString() ?? "";
                if (r.TryGetProperty("error_description", out var d)) return d.GetString() ?? "";
                if (r.TryGetProperty("error", out var e)) return e.GetString() ?? "";
            }
            catch { }
            return "";
        }

        private static string FriendlyError(string raw)
        {
            string low = (raw ?? "").ToLowerInvariant();
            if (low.Contains("banned")) return "Аккаунт заблокирован. Обратись в поддержку.";
            if (low.Contains("invalid login") || low.Contains("invalid_grant") || low.Contains("email not confirmed") == false && low.Contains("credentials"))
                return "Неверная почта или пароль.";
            if (low.Contains("email not confirmed")) return "Подтверди почту (ссылка из письма), потом входи.";
            return string.IsNullOrWhiteSpace(raw) ? "Ошибка входа." : raw;
        }

        public static async Task<SiteAccount> LoginAsync(string email, string password)
        {
            using var http = MakeHttp();
            var body = new StringContent(
                JsonSerializer.Serialize(new { email, password }),
                Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(ProjectUrl + "/auth/v1/token?grant_type=password", body);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception(FriendlyError(ReadError(json)));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var acc = new SiteAccount
            {
                Email = email,
                AccessToken = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                UserId = root.GetProperty("user").GetProperty("id").GetString() ?? ""
            };
            await FillProfileAsync(acc);
            await CheckHwidAsync(acc);
            return acc;
        }

        public static async Task<SiteAccount> RefreshAsync(string email, string refreshToken)
        {
            using var http = MakeHttp();
            var body = new StringContent(
                JsonSerializer.Serialize(new { refresh_token = refreshToken }),
                Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(ProjectUrl + "/auth/v1/token?grant_type=refresh_token", body);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new Exception("Сессия истекла, войди заново.");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var acc = new SiteAccount
            {
                Email = email,
                AccessToken = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : refreshToken,
                UserId = root.GetProperty("user").GetProperty("id").GetString() ?? ""
            };
            await FillProfileAsync(acc);
            return acc;
        }

        private static async Task FillProfileAsync(SiteAccount acc)
        {
            using var http = MakeHttp(acc.AccessToken);
            string url = ProjectUrl + "/rest/v1/profiles?user_id=eq." + Uri.EscapeDataString(acc.UserId)
                + "&select=id,username,role,frozen,hwid";
            using var resp = await http.GetAsync(url);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return; // профиль создастся триггером, ник возьмём из почты
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0) return;
            var p = doc.RootElement[0];
            acc.Uid = p.TryGetProperty("id", out var id) ? id.GetInt64() : 0;
            acc.Username = p.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            acc.Role = p.TryGetProperty("role", out var r) ? r.GetString() ?? "Пользователь" : "Пользователь";
            acc.Frozen = p.TryGetProperty("frozen", out var f) && f.GetBoolean();
            acc.BoundHwid = p.TryGetProperty("hwid", out var h) ? h.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(acc.Username))
                acc.Username = acc.Email.Split('@')[0];
        }

        private static async Task CheckHwidAsync(SiteAccount acc)
        {
            string hwid = GetHwid();
            if (string.IsNullOrEmpty(acc.BoundHwid))
            {
                // первый вход с этого аккаунта — привязываем ПК
                using var http = MakeHttp(acc.AccessToken);
                var body = new StringContent(
                    JsonSerializer.Serialize(new { hwid }),
                    Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                    ProjectUrl + "/rest/v1/profiles?user_id=eq." + Uri.EscapeDataString(acc.UserId));
                req.Content = body;
                req.Headers.Add("Prefer", "return=minimal");
                using var resp = await http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                acc.BoundHwid = hwid;
                return;
            }
            if (!string.Equals(acc.BoundHwid, hwid, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Аккаунт привязан к другому ПК (HWID). Обратись в поддержку для сброса.");
        }

        public static string GetHwid()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var v = key?.GetValue("MachineGuid") as string;
                if (!string.IsNullOrWhiteSpace(v)) return "GUID-" + v;
            }
            catch { }
            // запасной вариант: стабильный хеш имени ПК + пользователя
            string raw = Environment.MachineName + "|" + Environment.UserName;
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return "PC-" + BitConverter.ToString(h).Replace("-", "").Substring(0, 24);
        }

        public static string ToNick(string username, long uid)
        {
            var sb = new StringBuilder();
            foreach (char c in (username ?? ""))
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                if (sb.Length >= 16) break;
            }
            if (sb.Length < 3) sb.Clear().Append("Player").Append(uid > 0 ? uid.ToString() : "");
            if (sb.Length > 16) sb.Length = 16;
            return sb.ToString();
        }
    }
}
