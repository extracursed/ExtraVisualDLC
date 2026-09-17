using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExtraVisualDLC
{
    public class AppSettings
    {
        public string Nick { get; set; } = "Player";
        public string Version { get; set; } = "1.20.1";
        public int RamGb { get; set; } = 4;
        public string GameDir { get; set; } = "";
        public string JavaPath { get; set; } = "";
        public string Server { get; set; } = "";
        public string Email { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
    }

    public class MainForm : Form
    {
        private static readonly Color ShadeBg = Color.FromArgb(10, 13, 24);
        private static readonly Color Card = Color.FromArgb(22, 26, 44);
        private static readonly Color InputBg = Color.FromArgb(32, 37, 62);
        private static readonly Color Accent = Color.FromArgb(0, 210, 255);      // cyan
        private static readonly Color AccentHover = Color.FromArgb(60, 225, 255);
        private static readonly Color AccentPress = Color.FromArgb(0, 160, 205);
        private static readonly Color Pink = Color.FromArgb(255, 92, 200);       // pink
        private static readonly Color TextC = Color.FromArgb(235, 238, 245);
        private static readonly Color Muted = Color.FromArgb(150, 158, 180);
        private static readonly Color Warn = Color.FromArgb(255, 200, 90);
        private static readonly Color Err = Color.FromArgb(255, 110, 130);

        private TextBox txtEmail;
        private TextBox txtPass;
        private Button btnLogin;
        private Label lblAccountTitle;
        private SiteAccount account;
        // Версия игры зафиксирована — выбор убран. Меняется одной строкой.
        private const string GAME_VERSION = "1.21.4";
        private TrackBar trackRam;
        private Label lblRamBadge;
        private Button btnPlay;
        private Button btnUpdate;
        private UpdateInfo pendingUpdate;
        private Panel progressBack;
        private Panel progressFill;
        private Panel shimmer;
        private Label lblStatus;
        private Panel glowPanel;
        private System.Windows.Forms.Timer pulseTimer;
        private System.Windows.Forms.Timer shimmerTimer;
        private System.Windows.Forms.Timer fadeTimer;
        private bool glowOn;
        private string settingsPath;
        private AppSettings settings = new AppSettings();
        private readonly List<string> logMemory = new List<string>();
        private System.Diagnostics.Process gameProcess;

        public MainForm()
        {
            Text = "ExtraVisualDLC — Minecraft Launcher";
            Size = new Size(1160, 660);
            MinimumSize = new Size(1160, 660);
            MaximumSize = new Size(1160, 660);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ShadeBg;
            ForeColor = TextC;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;
            Opacity = 0;

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            settingsPath = Path.Combine(exeDir, "settings.json");
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSettings();
            Updater.CleanupBackup();
            _ = CheckUpdatesAsync();
            _ = TryAutoLoginAsync();
        }

        private static void RoundControl(Control c, int radius)
        {
            try
            {
                int d = Math.Max(2, radius * 2);
                d = Math.Min(d, Math.Min(c.Width, c.Height));
                using var path = new GraphicsPath();
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(c.Width - d, 0, d, d, 270, 90);
                path.AddArc(c.Width - d, c.Height - d, d, d, 0, 90);
                path.AddArc(0, c.Height - d, d, d, 90, 90);
                path.CloseFigure();
                if (c.Region != null) c.Region.Dispose();
                c.Region = new Region(path);
            }
            catch { }
        }

        internal static Image LoadLogo()
        {
            try
            {
                // 1. файлы: папка exe + папка с логотипом на рабочем столе
                string desktopLogos = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "ExtraVisualDLC");
                foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, desktopLogos })
                {
                    string f = FindLogoFile(dir);
                    if (f != null) return Image.FromFile(f);
                }
                // 2. лого зашито в код
                var embedded = LogoData.Image;
                if (embedded != null) return embedded;
                // 3. старый встроенный ресурс
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("ExtraVisualDLC.logo.png");
                if (s != null) { using var tmp = new Bitmap(s); return new Bitmap(tmp); }
            }
            catch { }
            return null;
        }

        private static string FindLogoFile(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                foreach (var name in new[] { "квадрат.png", "logo.png" })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                string[] any = Directory.GetFiles(dir, "*.png");
                if (any.Length > 0) return any[0];
            }
            catch { }
            return null;
        }

        // фон на всё окно: cover-crop + затемнение + сине-розовый грейд +
        // тёмная шторка слева под управление + виньетка
        internal static Image BuildBackdrop(Image src, int w, int h)
        {
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sr = (float)src.Width / src.Height;
                float dr = (float)w / h;
                RectangleF srect;
                if (sr > dr)
                {
                    float sw = src.Height * dr;
                    srect = new RectangleF((src.Width - sw) / 2, 0, sw, src.Height);
                }
                else
                {
                    float sh = src.Width / dr;
                    srect = new RectangleF(0, (src.Height - sh) / 2, src.Width, sh);
                }
                g.DrawImage(src, new Rectangle(0, 0, w, h), srect, GraphicsUnit.Pixel);

                // затемнение для читаемости
                using (var dark = new SolidBrush(Color.FromArgb(85, 6, 10, 22)))
                    g.FillRectangle(dark, 0, 0, w, h);

                // сине-розовый тон по диагонали
                using (var grade = new LinearGradientBrush(
                    new Rectangle(0, 0, w, h),
                    Color.FromArgb(55, 0, 140, 255),
                    Color.FromArgb(65, 255, 40, 170), 45f))
                    g.FillRectangle(grade, 0, 0, w, h);

                // шторка слева под панель управления
                using (var shade = new LinearGradientBrush(
                    new Rectangle(0, 0, 440, h),
                    Color.FromArgb(255, ShadeBg),
                    Color.FromArgb(0, ShadeBg), 0f))
                    g.FillRectangle(shade, 0, 0, 440, h);

                // виньетка снизу
                using (var vg = new LinearGradientBrush(
                    new Rectangle(0, h - 150, w, 150),
                    Color.FromArgb(0, 5, 8, 18),
                    Color.FromArgb(150, 5, 8, 18), 90f))
                    g.FillRectangle(vg, 0, h - 150, w, 150);
            }
            return bmp;
        }

        private TextBox MakeField(Control parent, int x, int y, int w, int h, string text, float fontSize)
        {
            var wrap = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = InputBg
            };
            RoundControl(wrap, 11);
            var tb = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = InputBg,
                ForeColor = TextC,
                Font = new Font("Segoe UI", fontSize),
                Location = new Point(14, (h - (int)(fontSize * 2)) / 2),
                Width = w - 28,
                Text = text
            };
            wrap.Controls.Add(tb);
            parent.Controls.Add(wrap);
            return tb;
        }

        private void BuildUi()
        {
            int W = 1144;
            int H = 621;

            // фон на всё окно
            var art = new PictureBox
            {
                Location = new Point(0, 0),
                Size = new Size(W, H),
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = ShadeBg
            };
            try
            {
                var src = BgData.Image;
                if (src != null) art.Image = BuildBackdrop(src, W, H);
            }
            catch { }
            Controls.Add(art);

            // верхняя акцентная полоса
            var strip = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(W + 20, 3),
                BackColor = Accent
            };
            Controls.Add(strip);
            strip.BringToFront();

            const int colX = 44;
            const int colW = 362;
            int accX = W - colW - 44; // аккаунт — вправо вверх

            // квадратный логотип поверх фона
            glowPanel = new Panel
            {
                Location = new Point(colX, 27),
                Size = new Size(132, 132),
                BackColor = Color.FromArgb(24, 60, 110)
            };
            art.Controls.Add(glowPanel);

            var pic = new PictureBox
            {
                Location = new Point(colX + 6, 33),
                Size = new Size(120, 120),
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Transparent
            };
            try
            {
                var logo = LoadLogo();
                if (logo != null) pic.Image = logo;
            }
            catch { }
            art.Controls.Add(pic);

            var titleFont = new Font("Segoe UI", 22F, FontStyle.Bold);
            int w1 = TextRenderer.MeasureText("ExtraVisual", titleFont).Width;
            var t1 = new Label
            {
                Text = "ExtraVisual",
                Location = new Point(182, 52),
                AutoSize = true,
                Font = titleFont,
                ForeColor = Color.White,
                BackColor = Color.Transparent
            };
            var t2 = new Label
            {
                Text = "DLC",
                Location = new Point(182 + w1 - 12, 52),
                AutoSize = true,
                Font = titleFont,
                ForeColor = Pink,
                BackColor = Color.Transparent
            };
            art.Controls.Add(t1);
            art.Controls.Add(t2);
            art.Controls.Add(new Label
            {
                Text = "MINECRAFT LAUNCHER",
                Location = new Point(184, 100),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Muted,
                BackColor = Color.Transparent
            });

            // аккаунт сайта
            lblAccountTitle = new Label
            {
                Text = "Аккаунт ExtraVisualDLC",
                Location = new Point(accX, 27),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Muted,
                BackColor = Color.Transparent
            };
            art.Controls.Add(lblAccountTitle);
            txtEmail = MakeField(art, accX, 49, colW, 36, "Email с сайта", 11F);
            txtPass = MakeField(art, accX, 89, colW, 36, "Пароль", 11F);
            txtPass.UseSystemPasswordChar = true;
            btnLogin = new Button
            {
                Text = "ВОЙТИ",
                Location = new Point(accX, 129),
                Size = new Size(colW, 34),
                BackColor = InputBg,
                ForeColor = TextC,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnLogin.FlatAppearance.BorderSize = 0;
            RoundControl(btnLogin, 10);
            btnLogin.Click += async (s, e) => await OnLoginAsync();
            art.Controls.Add(btnLogin);

            // оперативка
            var ramCard = new Panel
            {
                Location = new Point(colX, 314),
                Size = new Size(colW, 100),
                BackColor = Card
            };
            RoundControl(ramCard, 14);
            ramCard.Controls.Add(new Label
            {
                Text = "ОПЕРАТИВНАЯ ПАМЯТЬ",
                Location = new Point(16, 12),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 170),
                BackColor = Color.Transparent
            });
            var badge = new Panel
            {
                Location = new Point(colW - 82, 10),
                Size = new Size(66, 22),
                BackColor = Color.FromArgb(52, 20, 52)
            };
            RoundControl(badge, 11);
            lblRamBadge = new Label
            {
                Text = "4 GB",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Pink,
                BackColor = Color.Transparent
            };
            badge.Controls.Add(lblRamBadge);
            ramCard.Controls.Add(badge);

            trackRam = new TrackBar
            {
                Location = new Point(12, 42),
                Size = new Size(colW - 24, 48),
                Minimum = 1,
                Maximum = 16,
                Value = 4,
                TickFrequency = 1,
                BackColor = Card
            };
            trackRam.ValueChanged += (s, e) => lblRamBadge.Text = $"{trackRam.Value} GB";
            ramCard.Controls.Add(trackRam);
            art.Controls.Add(ramCard);

            // играть
            btnPlay = new Button
            {
                Text = "▶  ИГРАТЬ",
                Location = new Point(colX, 426),
                Size = new Size(colW, 56),
                BackColor = Accent,
                ForeColor = Color.FromArgb(4, 32, 44),
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnPlay.FlatAppearance.BorderSize = 0;
            btnPlay.FlatAppearance.MouseOverBackColor = AccentHover;
            btnPlay.FlatAppearance.MouseDownBackColor = AccentPress;
            RoundControl(btnPlay, 16);
            btnPlay.Click += async (s, e) => await OnPlayAsync();
            art.Controls.Add(btnPlay);

            progressBack = new Panel
            {
                Location = new Point(colX, 490),
                Size = new Size(colW, 8),
                BackColor = Color.FromArgb(30, 36, 58)
            };
            RoundControl(progressBack, 4);
            progressFill = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(0, 8),
                BackColor = Accent
            };
            RoundControl(progressFill, 4);
            progressBack.Controls.Add(progressFill);
            art.Controls.Add(progressBack);

            lblStatus = new Label
            {
                Text = "Готов к запуску",
                Location = new Point(colX, 502),
                Size = new Size(colW, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.Transparent
            };
            art.Controls.Add(lblStatus);

            // обновление лаунчера (показывается только если есть новая версия)
            btnUpdate = new Button
            {
                Text = "⬇ ОБНОВИТЬ",
                Location = new Point(colX, 528),
                Size = new Size(colW, 40),
                BackColor = Color.FromArgb(60, 200, 130),
                ForeColor = Color.FromArgb(4, 40, 24),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Visible = false
            };
            btnUpdate.FlatAppearance.BorderSize = 0;
            btnUpdate.FlatAppearance.MouseOverBackColor = Color.FromArgb(90, 220, 150);
            RoundControl(btnUpdate, 12);
            btnUpdate.Click += async (s, e) => await OnUpdateAsync();
            art.Controls.Add(btnUpdate);

            // --- анимации ---
            fadeTimer = new System.Windows.Forms.Timer { Interval = 25 };
            fadeTimer.Tick += (s, e) =>
            {
                if (Opacity >= 1) { fadeTimer.Stop(); Opacity = 1; }
                else Opacity += 0.07;
            };
            fadeTimer.Start();

            pulseTimer = new System.Windows.Forms.Timer { Interval = 650 };
            pulseTimer.Tick += (s, e) =>
            {
                glowOn = !glowOn;
                if (glowPanel != null && !glowPanel.IsDisposed)
                    glowPanel.BackColor = glowOn
                        ? Color.FromArgb(30, 80, 140)
                        : Color.FromArgb(24, 60, 110);
            };
            pulseTimer.Start();

            shimmer = new Panel
            {
                Size = new Size(70, 8),
                Location = new Point(-70, 0),
                BackColor = Color.FromArgb(190, 240, 255),
                Visible = false
            };
            progressBack.Controls.Add(shimmer);
            shimmer.BringToFront();
            shimmerTimer = new System.Windows.Forms.Timer { Interval = 30 };
            shimmerTimer.Tick += (s, e) =>
            {
                if (shimmer == null || shimmer.IsDisposed) return;
                int x = shimmer.Left + 9;
                if (x > progressBack.Width) x = -shimmer.Width;
                shimmer.Left = x;
            };
        }

        private string DefaultGameDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".extravisualdlc");
        }

        private string GetGameDir()
        {
            string d = settings.GameDir;
            if (string.IsNullOrWhiteSpace(d)) d = DefaultGameDir();
            return d;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) settings = s;
                }
            }
            catch { }

            txtEmail.Text = settings.Email ?? "";
            trackRam.Value = Math.Max(1, Math.Min(16, settings.RamGb == 0 ? 4 : settings.RamGb));
            lblRamBadge.Text = $"{trackRam.Value} GB";
        }

        private void SaveSettings()
        {
            try
            {
                if (account == null)
                    settings.Email = txtEmail.Text.Trim();
                settings.RamGb = trackRam.Value;
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void SaveTokens()
        {
            try
            {
                settings.Email = account?.Email ?? settings.Email;
                settings.AccessToken = account?.AccessToken ?? "";
                settings.RefreshToken = account?.RefreshToken ?? "";
                settings.RamGb = trackRam.Value;
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void ApplyAccountUi()
        {
            if (btnLogin.IsDisposed) return;
            btnLogin.Invoke(new Action(() =>
            {
                bool logged = account != null;
                txtEmail.Enabled = !logged;
                txtPass.Enabled = !logged;
                if (logged) { txtPass.Text = ""; }
                btnLogin.Text = logged ? "ВЫЙТИ" : "ВОЙТИ";
                lblAccountTitle.Text = logged
                    ? $"Ник: {account.Username} · UID: {account.Uid}"
                    : "Аккаунт ExtraVisualDLC";
            }));
        }

        private async Task TryAutoLoginAsync()
        {
            if (string.IsNullOrWhiteSpace(settings.RefreshToken)) return;
            try
            {
                SetStatus("Восстановление сессии...", Warn);
                account = await SiteAuth.RefreshAsync(settings.Email, settings.RefreshToken);
                SaveTokens();
                ApplyAccountUi();
                SetStatus($"Вошёл: {account.Username} · UID {account.Uid}", Accent);
            }
            catch { SetStatus("Войди в аккаунт", Muted); }
        }

        private async Task OnLoginAsync()
        {
            if (account != null)
            {
                account = null;
                settings.AccessToken = "";
                settings.RefreshToken = "";
                SaveSettings();
                ApplyAccountUi();
                SetStatus("Вышел из аккаунта", Muted);
                return;
            }
            string email = txtEmail.Text.Trim();
            string pass = txtPass.Text;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("Введи email и пароль от сайта.", "ExtraVisualDLC");
                return;
            }
            try
            {
                btnLogin.Enabled = false;
                SetStatus("Вход...", Warn);
                account = await SiteAuth.LoginAsync(email, pass);
                if (account.Frozen)
                {
                    account = null;
                    SetStatus("Аккаунт заморожен. Обратись в поддержку.", Err);
                    MessageBox.Show("Аккаунт заморожен. Обратись в поддержку.", "ExtraVisualDLC",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    btnLogin.Enabled = true;
                    return;
                }
                SaveTokens();
                ApplyAccountUi();
                SetStatus($"Вошёл: {account.Username} · UID {account.Uid} · {account.Role}", Accent);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка входа", Err);
                MessageBox.Show(ex.Message, "ExtraVisualDLC — вход",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { btnLogin.Enabled = true; }
        }

        private async Task CheckUpdatesAsync()
        {
            try
            {
                pendingUpdate = await Updater.CheckAsync();
                if (pendingUpdate == null || btnUpdate.IsDisposed) return;
                btnUpdate.Invoke(new Action(() =>
                {
                    btnUpdate.Text = $"⬇ ОБНОВИТЬ ДО v{pendingUpdate.Version}";
                    btnUpdate.Visible = true;
                }));
                string what = string.IsNullOrEmpty(pendingUpdate.Notes)
                    ? "Доступно обновление лаунчера"
                    : "Доступно обновление: " + pendingUpdate.Notes;
                SetStatus(what, Warn);
            }
            catch { /* нет сети — просто молча работаем дальше */ }
        }

        private async Task OnUpdateAsync()
        {
            if (pendingUpdate == null) return;
            try
            {
                btnUpdate.Enabled = false;
                btnPlay.Enabled = false;
                var progress = new Progress<int>(p => SetProgress(p, $"Скачивание обновления... {p}%"));
                await Updater.DownloadAsync(pendingUpdate.Url, Application.ExecutablePath + ".upd", progress);
                SetStatus("Установка обновления, перезапуск...", Warn);
                await Task.Delay(400);
                Updater.ApplyAndRestart();
            }
            catch (Exception ex)
            {
                btnUpdate.Enabled = true;
                btnPlay.Enabled = true;
                SetStatus("Ошибка обновления: " + ex.Message, Err);
            }
        }

        private void Log(string msg)
        {
            lock (logMemory)
            {
                logMemory.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                if (logMemory.Count > 500) logMemory.RemoveRange(0, 100);
            }
            try
            {
                string lp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".extravisualdlc", "launch-log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(lp));
                File.AppendAllText(lp, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
            if (msg.StartsWith("[MC]") || msg.StartsWith("[MC-ERR]"))
            {
                string shortMsg = msg.Length > 90 ? msg.Substring(0, 90) + "..." : msg;
                SetStatus(shortMsg, TextC);
            }
        }

        private void SetStatus(string s, Color c)
        {
            if (lblStatus.InvokeRequired)
            {
                lblStatus.Invoke(new Action<string, Color>(SetStatus), s, c);
                return;
            }
            lblStatus.Text = s;
            lblStatus.ForeColor = c;
        }

        private void SetProgress(int pct, string s)
        {
            if (progressBack.InvokeRequired)
            {
                progressBack.Invoke(new Action<int, string>(SetProgress), pct, s);
                return;
            }
            pct = Math.Max(0, Math.Min(100, pct));
            progressFill.Width = progressBack.Width * pct / 100;
            RoundControl(progressFill, 4);
            lblStatus.Text = s;
            lblStatus.ForeColor = Muted;
        }

        private async Task OnPlayAsync()
        {
            SaveSettings();
            if (account == null)
            {
                MessageBox.Show("Сначала войди в аккаунт сайта (email + пароль).", "ExtraVisualDLC");
                return;
            }
            if (account.Frozen)
            {
                MessageBox.Show("Аккаунт заморожен. Обратись в поддержку.", "ExtraVisualDLC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (gameProcess != null && !gameProcess.HasExited)
            {
                MessageBox.Show("Игра уже запущена. Не жми Играть дважды — повторный запуск качает мод заново и роняет обе копии.", "ExtraVisualDLC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string nick = SiteAuth.ToNick(account.Username, account.Uid);
            string version = GAME_VERSION; // выбор версии убран, сборка фиксирована
            string gameDir = GetGameDir(); // %APPDATA%\.extravisualdlc у всех пользователей
            string java = settings.JavaPath ?? "";
            int ram = trackRam.Value;

            btnPlay.Enabled = false;
            btnPlay.Text = "ЗАПУСК...";
            if (shimmer != null) { shimmer.Visible = true; shimmer.Left = -shimmer.Width; }
            shimmerTimer.Start();
            SetStatus("Запуск...", Warn);
            SetProgress(0, "Подготовка...");

            var result = await Task.Run(() => MinecraftLauncher.LaunchAsync(
                version, nick, ram, gameDir, java,
                (msg) => Log(msg),
                (pct, st) => SetProgress(pct, st)).GetAwaiter().GetResult());

            shimmerTimer.Stop();
            if (shimmer != null) shimmer.Visible = false;

            if (!result.Success)
            {
                MessageBox.Show(result.Message, "ExtraVisualDLC — ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Ошибка запуска", Err);
                SetProgress(0, "Ошибка запуска");
                lblStatus.ForeColor = Err;
            }
            else
            {
                SetStatus("Игра запущена!", Accent);
                gameProcess = result.Process;
                if (gameProcess != null)
                {
                    gameProcess.EnableRaisingEvents = true;
                    gameProcess.Exited += (s, e) => SetStatus("Готов к запуску", Muted);
                    // Если игра упала в первые 15 сек — показать реальную причину из лога
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(15000);
                            if (gameProcess.HasExited)
                            {
                                int code = -1;
                                try { code = gameProcess.ExitCode; } catch { }
                                List<string> tail;
                                lock (logMemory) { tail = logMemory.Skip(Math.Max(0, logMemory.Count - 30)).ToList(); }
                                string txt = $"Игра закрылась сразу (код {code}). Последние строки лога:{Environment.NewLine}{Environment.NewLine}" + string.Join(Environment.NewLine, tail)
                                    + $"{Environment.NewLine}{Environment.NewLine}Полный лог: %APPDATA%\\.extravisualdlc\\launch-log.txt";
                                try { MessageBox.Show(txt, "ExtraVisualDLC — игра упала", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                                SetStatus($"Игра упала (код {code}) — смотри launch-log.txt", Err);
                            }
                        }
                        catch { }
                    });
                }
            }

            btnPlay.Enabled = true;
            btnPlay.Text = "▶  ИГРАТЬ";
        }
    }
}
