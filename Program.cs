using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AndroidPermissionGranter
{
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // =====================================================================
    //  Цветовая палитра и графические утилиты (современный плоский стиль)
    // =====================================================================
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(238, 240, 244);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 229, 235);
        public static readonly Color Primary = Color.FromArgb(79, 70, 229);   // indigo
        public static readonly Color Success = Color.FromArgb(16, 185, 129);  // emerald
        public static readonly Color Info = Color.FromArgb(59, 130, 246);     // blue
        public static readonly Color Purple = Color.FromArgb(139, 92, 246);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color Warn = Color.FromArgb(217, 119, 6);
        public static readonly Color Slate = Color.FromArgb(100, 116, 139);
        public static readonly Color TextDark = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
    }

    internal static class Ui
    {
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void ApplyRound(Control c, int radius)
        {
            void Apply()
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                using (var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                    c.Region = new Region(path);
            }
            Apply();
            c.SizeChanged += (s, e) => Apply();
        }

        public static Color Lighten(Color c, float f) => ControlPaint.Light(c, f);
        public static Color Darken(Color c, float f) => ControlPaint.Dark(c, f);
    }

    /// <summary>Панель-«карточка»: скруглённые углы, светлая заливка, тонкая рамка.</summary>
    internal sealed class Card : Panel
    {
        public int Radius { get; set; } = 12;
        public Color BorderColor { get; set; } = Theme.Border;
        public Color FillColor { get; set; } = Theme.Card;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; // углы сливаются с фоном формы
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.RoundedRect(rect, Radius))
            {
                using (var b = new SolidBrush(FillColor)) g.FillPath(b, path);
                using (var p = new Pen(BorderColor)) g.DrawPath(p, path);
            }
        }
    }

    // =====================================================================
    //  Обёртка над ADB
    // =====================================================================
    public static class AdbHelper
    {
        public static string AdbPath { get; private set; }
        public static bool IsAvailable => !string.IsNullOrEmpty(AdbPath) && File.Exists(AdbPath);

        public static void Initialize(string basePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iSpaceADB");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            string adbExe = Path.Combine(tempDir, "adb.exe");
            string dll1 = Path.Combine(tempDir, "AdbWinApi.dll");
            string dll2 = Path.Combine(tempDir, "AdbWinUsbApi.dll");

            void ExtractResource(string resourceName, string destPath)
            {
                if (File.Exists(destPath)) return;
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string fullName = $"iSpacePermission.adb.{resourceName}";
                using (var stream = assembly.GetManifestResourceStream(fullName))
                {
                    if (stream == null)
                        throw new FileNotFoundException($"Ресурс не найден: {fullName}. Проверьте Build Action = Embedded Resource");
                    using (var file = new FileStream(destPath, FileMode.Create))
                        stream.CopyTo(file);
                }
            }

            try
            {
                ExtractResource("adb.exe", adbExe);
                ExtractResource("AdbWinApi.dll", dll1);
                ExtractResource("AdbWinUsbApi.dll", dll2);
                AdbPath = adbExe;
            }
            catch (Exception ex)
            {
                throw new Exception("Не удалось распаковать ADB. Убедитесь, что файлы добавлены как Embedded Resource.", ex);
            }
        }

        /// <summary>
        /// Запуск adb с тайм-аутом БЕЗ зависаний.
        /// Ключевой момент: оба потока (stdout/stderr) читаются асинхронно СРАЗУ после старта.
        /// Если читать их только после выхода процесса (как было раньше), буфер канала (~4 КБ)
        /// переполняется на большом выводе (dumpsys), adb блокируется на записи, событие Exited
        /// не наступает — и приложение зависает до тайм-аута. Теперь дедлок исключён.
        /// </summary>
        public static async Task<string> RunAsync(string args, int timeoutSec = 10)
        {
            if (!IsAvailable) throw new InvalidOperationException("ADB не инициализирован.");

            var psi = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();

                // Начинаем вычитывать оба потока немедленно — это и предотвращает дедлок.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var exitTask = WaitForExitAsync(proc);

                var finished = await Task.WhenAny(exitTask, Task.Delay(timeoutSec * 1000));
                if (finished != exitTask)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    throw new TimeoutException($"Команда ADB не ответила за {timeoutSec} с: adb {args}");
                }

                string outText = await outTask;
                string errText = await errTask;

                if (string.IsNullOrWhiteSpace(errText)) return outText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(outText)) return errText;
                return outText + "\n" + errText;
            }
        }

        private static Task WaitForExitAsync(Process process)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => tcs.TrySetResult(true);
            if (process.HasExited) tcs.TrySetResult(true); // на случай мгновенного завершения
            return tcs.Task;
        }

        public static async Task<bool> IsDeviceConnectedAsync()
        {
            try
            {
                var res = await RunAsync("devices", 6);
                // строка вида "<serial>\tdevice" — именно авторизованное устройство
                return res.Split('\n').Any(l => l.TrimEnd().EndsWith("\tdevice"));
            }
            catch { return false; }
        }

        public static async Task<string> DevicesRawAsync()
        {
            try { return (await RunAsync("devices", 6)).Trim(); }
            catch (Exception ex) { return "[ОШИБКА] " + ex.Message; }
        }

        public static async Task<List<string>> GetPackagesAsync(bool thirdOnly = true)
        {
            var cmd = thirdOnly ? "shell pm list packages -3" : "shell pm list packages";
            var res = await RunAsync(cmd, 20);

            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(res)) return list;

            foreach (var line in res.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("package:"))
                {
                    var pkg = trimmed.Substring(8).Trim();
                    if (pkg.Length > 0) list.Add(pkg);
                }
            }
            return list;
        }

        // -----------------------------------------------------------------
        //  Разбор запрашиваемых разрешений (как в grant_permissions.sh):
        //  берём ВСЕ разрешения из блока "requested permissions:",
        //  а если распарсить не удалось — используем резервный набор.
        // -----------------------------------------------------------------
        public static async Task<List<string>> GetRequestedPermissionsAsync(string pkg)
        {
            var res = await RunAsync($"shell dumpsys package {pkg}", 20);
            var perms = ParseRequestedPermissions(res);
            if (perms.Count == 0) perms = FallbackPermissions();
            return perms;
        }

        private static List<string> ParseRequestedPermissions(string dump)
        {
            var perms = new List<string>();
            if (string.IsNullOrWhiteSpace(dump)) return perms;

            bool inBlock = false;
            foreach (var raw in dump.Split('\n'))
            {
                var line = raw.Trim();

                if (!inBlock)
                {
                    if (line.StartsWith("requested permissions:")) inBlock = true;
                    continue;
                }

                if (line.Length == 0) break;        // пустая строка — конец блока
                if (line.EndsWith(":")) break;      // следующий раздел ("install permissions:" и т.п.)

                var parts = line.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var token = parts[0];
                if (IsPermissionToken(token) && !perms.Contains(token))
                    perms.Add(token);
            }
            return perms;
        }

        private static bool IsPermissionToken(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('.') < 0) return false;
            foreach (var ch in s)
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_')) return false;
            return true;
        }

        private static List<string> FallbackPermissions() => new List<string>
        {
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.POST_NOTIFICATIONS",
            "android.permission.ACCESS_FINE_LOCATION",
            "android.permission.ACCESS_COARSE_LOCATION",
            "android.permission.ACCESS_BACKGROUND_LOCATION",
            "android.permission.READ_PHONE_STATE",
            "android.permission.GET_ACCOUNTS",
            "android.permission.REQUEST_INSTALL_PACKAGES"
        };

        public static async Task<string> GrantAsync(string pkg, string perm)
        {
            try { return await RunAsync($"shell pm grant {pkg} {perm}", 6); }
            catch (Exception ex) { return "[ERR] " + ex.Message; }
        }

        public static async Task<string> SetAppOpAsync(string pkg, string op, string mode = "allow")
        {
            try { return await RunAsync($"shell appops set {pkg} {op} {mode}", 6); }
            catch (Exception ex) { return "[ERR] " + ex.Message; }
        }

        public static async Task<string> GetAppOpAsync(string pkg, string op)
        {
            try
            {
                var r = await RunAsync($"shell appops get {pkg} {op}", 6);
                var first = r?.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                return string.IsNullOrWhiteSpace(first) ? "—" : first.Trim();
            }
            catch { return "—"; }
        }

        public static async Task<string> ConnectAsync(string ip)
        {
            try { return (await RunAsync($"connect {ip}", 12)).Trim(); }
            catch (Exception ex) { return "[ОШИБКА] " + ex.Message; }
        }

        public static async Task ForceStopAsync(string pkg)
        {
            try { await RunAsync($"shell am force-stop {pkg}", 6); } catch { }
        }

        public static async Task LaunchAsync(string pkg)
        {
            try { await RunAsync($"shell monkey -p {pkg} -c android.intent.category.LAUNCHER 1", 6); } catch { }
        }
    }

    // =====================================================================
    //  Главное окно
    // =====================================================================
    public class MainForm : Form
    {
        private const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const int LogRow = 2;
        private const float LogHeight = 200F;

        private TableLayoutPanel _mainLayout;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel, _progressLabel;
        private ToolStripProgressBar _progressBar;
        private TextBox _searchBox;
        private ListBox _pkgList, _permList;
        private Button _btnRefresh, _btnConnect, _btnLoad, _btnGrant, _btnLaunch, _btnLog, _btnThanks;
        private Panel _logPanel;
        private RichTextBox _logBox;
        private bool _busy = false;
        private List<string> _packages = new List<string>();

        // Цвета строк в тёмном логе
        private static readonly Color LogText = Color.FromArgb(203, 213, 225);
        private static readonly Color LogOk = Color.FromArgb(52, 211, 153);
        private static readonly Color LogInfo = Color.FromArgb(34, 211, 238);
        private static readonly Color LogWarn = Color.FromArgb(251, 191, 36);
        private static readonly Color LogErr = Color.FromArgb(248, 113, 113);
        private static readonly Color LogDim = Color.FromArgb(148, 163, 184);

        private readonly Dictionary<string, string> _appOpsMap = new Dictionary<string, string>
        {
            { "android.permission.SYSTEM_ALERT_WINDOW", "SYSTEM_ALERT_WINDOW" },
            { "android.permission.WRITE_SETTINGS", "WRITE_SETTINGS" },
            { "android.permission.REQUEST_INSTALL_PACKAGES", "REQUEST_INSTALL_PACKAGES" },
            { "android.permission.MANAGE_EXTERNAL_STORAGE", "MANAGE_EXTERNAL_STORAGE" },
            { "android.permission.GET_USAGE_STATS", "GET_USAGE_STATS" }
        };

        private readonly string[] _criticalOps = new[]
        {
            "SYSTEM_ALERT_WINDOW", "WRITE_SETTINGS", "MANAGE_EXTERNAL_STORAGE",
            "REQUEST_INSTALL_PACKAGES", "GET_USAGE_STATS"
        };

        public MainForm()
        {
            this.Text = "iSpace — Выдача разрешений Android";
            this.Font = new Font("Segoe UI", 9.75F);
            this.Size = new Size(1000, 720);
            this.MinimumSize = new Size(880, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.Bg;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            InitUI();
            this.Load += async (s, e) => await InitAsync();
            this.FormClosing += (s, e) => KillAdb();
        }

        private void InitUI()
        {
            _mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(14, 14, 14, 8),
                BackColor = Theme.Bg
            };
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));   // 0 шапка
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 1 контент
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));    // 2 лог (скрыт)
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));   // 3 кнопки
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // 4 статус

            // Лог — строка внутри сетки (между контентом и кнопками), поэтому кнопки
            // действий ВСЕГДА остаются видимыми и доступными при открытом логе.
            _logPanel = new Card
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(12, 10, 12, 12),
                FillColor = Color.FromArgb(15, 23, 42),
                BorderColor = Color.FromArgb(30, 41, 59),
                Radius = 10,
                Visible = false
            };
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = LogText,
                Font = new Font("Consolas", 9F),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };
            _logPanel.Controls.Add(_logBox);

            _mainLayout.Controls.Add(BuildHeader(), 0, 0);
            _mainLayout.Controls.Add(BuildContent(), 0, 1);
            _mainLayout.Controls.Add(_logPanel, 0, 2);
            _mainLayout.Controls.Add(BuildActions(), 0, 3);
            _mainLayout.Controls.Add(BuildStatus(), 0, 4);

            this.Controls.Add(_mainLayout);
        }

        // ---- Шапка -------------------------------------------------------
        private Control BuildHeader()
        {
            var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(20, 0, 16, 0) };

            var title = new Label
            {
                Text = "Выдача разрешений",
                Font = new Font("Segoe UI Semibold", 17F),
                ForeColor = Theme.Primary,
                BackColor = Theme.Card,
                AutoSize = true,
                Location = new Point(20, 18)
            };
            var subtitle = new Label
            {
                Text = "ADB-менеджер разрешений Android · iSpace",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Card,
                AutoSize = true,
                Location = new Point(22, 52)
            };

            var headerButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                BackColor = Theme.Card,
                Padding = new Padding(0, 26, 6, 0)
            };

            _btnRefresh = CreateBtn("🔄  Обновить устройства", Theme.Info, async (s, e) => await RefreshDevicesAsync());
            _btnRefresh.Width = 186;
            _btnConnect = CreateBtn("🔗  Подключить по IP", Theme.Success, (s, e) => ConnectIPAsync());
            _btnConnect.Width = 176;

            headerButtons.Controls.Add(_btnConnect);
            headerButtons.Controls.Add(_btnRefresh);

            card.Controls.Add(title);
            card.Controls.Add(subtitle);
            card.Controls.Add(headerButtons);
            return card;
        }

        // ---- Контент (две карточки) -------------------------------------
        private Control BuildContent()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Bg,
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            // ----- Левая карточка: приложения -----
            var pkgCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 7, 0), Padding = new Padding(14, 12, 14, 14) };

            var pkgHeader = SectionLabel("📦  Установленные приложения");

            _searchBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 30,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10F),
                Margin = new Padding(0, 6, 0, 8)
            };
            _searchBox.TextChanged += (s, e) => FilterPkgs();

            var searchHost = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 6, 0, 4), BackColor = Theme.Card };
            searchHost.Controls.Add(_searchBox);

            _pkgList = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ItemHeight = 20,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                BackColor = Color.FromArgb(250, 250, 252)
            };
            _pkgList.SelectedIndexChanged += (s, e) => PreviewPermsAsync();

            _btnLoad = CreateBtn("📋  Загрузить список приложений", Theme.Purple, async (s, e) => await LoadPkgsAsync());
            _btnLoad.Dock = DockStyle.Bottom;
            _btnLoad.Height = 42;
            _btnLoad.Margin = new Padding(0, 10, 0, 0);

            pkgCard.Controls.Add(_pkgList);
            pkgCard.Controls.Add(_btnLoad);
            pkgCard.Controls.Add(searchHost);
            pkgCard.Controls.Add(pkgHeader);

            // ----- Правая карточка: разрешения -----
            var permCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(7, 0, 0, 0), Padding = new Padding(14, 12, 14, 14) };

            var permHeader = SectionLabel("🔑  Запрашиваемые разрешения");
            _permList = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                BackColor = Color.FromArgb(250, 250, 252)
            };

            permCard.Controls.Add(_permList);
            permCard.Controls.Add(permHeader);

            grid.Controls.Add(pkgCard, 0, 0);
            grid.Controls.Add(permCard, 1, 0);
            return grid;
        }

        // ---- Нижняя панель действий -------------------------------------
        private Control BuildActions()
        {
            var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(16, 8, 10, 8) };

            // Сетка: слева подсказка (сжимается с многоточием), справа кнопки — без перекрытий.
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Card
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var hint = new Label
            {
                Text = "Выберите приложение слева и выдайте все разрешения",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Card,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(2, 0, 8, 0)
            };

            var footerButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Card,
                Margin = new Padding(0),
                Anchor = AnchorStyles.Right
            };

            _btnGrant = CreateBtn("✨  Выдать ВСЕ разрешения", Theme.Primary, async (s, e) => await GrantAllAsync());
            _btnGrant.Width = 244; _btnGrant.Height = 46; _btnGrant.Font = new Font("Segoe UI Semibold", 11F);

            _btnLaunch = CreateBtn("🚀  Запустить", Theme.Success, async (s, e) => await LaunchAppAsync());
            _btnLaunch.Width = 148; _btnLaunch.Height = 46;

            _btnLog = CreateBtn("📜  Лог", Theme.Slate, (s, e) => ToggleLog());
            _btnLog.Width = 100; _btnLog.Height = 46;

            _btnThanks = CreateBtn("❤  О программе", Theme.Danger, (s, e) => ShowThanks());
            _btnThanks.Width = 146; _btnThanks.Height = 46;

            footerButtons.Controls.Add(_btnGrant);
            footerButtons.Controls.Add(_btnLaunch);
            footerButtons.Controls.Add(_btnLog);
            footerButtons.Controls.Add(_btnThanks);

            row.Controls.Add(hint, 0, 0);
            row.Controls.Add(footerButtons, 1, 0);
            card.Controls.Add(row);
            return card;
        }

        // ---- Статус-бар --------------------------------------------------
        private Control BuildStatus()
        {
            _statusStrip = new StatusStrip
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                SizingGrip = false,
                Margin = new Padding(0, 4, 0, 0)
            };
            _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Готово", ForeColor = Theme.TextDark };
            _progressLabel = new ToolStripStatusLabel { Text = "Ожидание", TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.TextMuted };
            _progressBar = new ToolStripProgressBar { Width = 160, Visible = false };
            _statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _progressLabel, _progressBar });
            return _statusStrip;
        }

        private Label SectionLabel(string text) => new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = Theme.TextDark,
            BackColor = Theme.Card,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        };

        private Button CreateBtn(string text, Color bg, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9.75F),
                Height = 38,
                Width = 140,
                Margin = new Padding(6, 0, 0, 0),
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Ui.Lighten(bg, 0.12f);
            b.FlatAppearance.MouseDownBackColor = Ui.Darken(bg, 0.08f);
            b.Click += click;
            Ui.ApplyRound(b, 9);
            return b;
        }

        // =================================================================
        //  Логика
        // =================================================================
        private async Task InitAsync()
        {
            try
            {
                AdbHelper.Initialize(Application.StartupPath);
                if (_searchBox.IsHandleCreated)
                    SendMessage(_searchBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "🔍  Поиск приложения…");

                _statusLabel.Text = "Готово · ADB загружен";
                _statusLabel.ForeColor = Theme.Success;
                Log("Приложение инициализировано. ADB распакован и готов к работе.", LogOk);
                Log("Подключите устройство по USB (с отладкой) или нажмите «Подключить по IP».", LogDim);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Ошибка: " + ex.Message;
                _statusLabel.ForeColor = Theme.Danger;
                Log(ex.Message, LogErr);
                MessageBox.Show(ex.Message, "Ошибка инициализации", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task RefreshDevicesAsync()
        {
            SetBusy(true);
            _statusLabel.Text = "Проверка USB-устройств…";
            try
            {
                var raw = await AdbHelper.DevicesRawAsync();
                Log("\n== adb devices ==\n" + raw, LogDim);

                var ok = await AdbHelper.IsDeviceConnectedAsync();
                _statusLabel.Text = ok ? "Устройство подключено" : "Устройство не найдено";
                _statusLabel.ForeColor = ok ? Theme.Success : Theme.Danger;
                Log(ok ? "Устройство обнаружено и авторизовано." : "Авторизованное устройство не найдено. Подтвердите отладку на телефоне или подключитесь по Wi-Fi.",
                    ok ? LogOk : LogWarn);
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] " + ex.Message, LogErr);
            }
            finally { SetBusy(false); }
        }

        private async void ConnectIPAsync()
        {
            var ipInput = PromptIP();
            if (string.IsNullOrWhiteSpace(ipInput)) return;
            await ConnectToAddressAsync(ipInput.Trim());
        }


        private async Task ConnectToAddressAsync(string address)
        {
            // adb connect <ip> подключается к порту 5555 по умолчанию; добавляем порт,
            // только если пользователь не указал свой.
            string fullAddress = address;
            if (!fullAddress.Contains(":")) fullAddress += ":5555";

            SetBusy(true);
            _statusLabel.Text = "Подключение к " + fullAddress + "…";
            try
            {
                var raw = await AdbHelper.ConnectAsync(fullAddress);
                Log("\n== adb connect " + fullAddress + " ==", LogDim);
                Log(string.IsNullOrWhiteSpace(raw) ? "(пустой ответ)" : raw, LogText);

                // adb печатает "connected to ..." или "already connected to ..." при успехе.
                bool connected =
                    raw.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    raw.IndexOf("cannot", StringComparison.OrdinalIgnoreCase) < 0 &&
                    raw.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0 &&
                    raw.IndexOf("ОШИБКА", StringComparison.OrdinalIgnoreCase) < 0;

                // Дополнительно убеждаемся, что устройство реально видно как авторизованное.
                bool ready = connected && await AdbHelper.IsDeviceConnectedAsync();

                if (ready)
                {
                    _statusLabel.Text = "Подключено к " + fullAddress;
                    _statusLabel.ForeColor = Theme.Success;
                    Log("Устройство подключено: " + fullAddress, LogOk);
                }
                else if (connected)
                {
                    _statusLabel.Text = "Подключено, требуется авторизация";
                    _statusLabel.ForeColor = Theme.Warn;
                    Log("Соединение установлено, но устройство не авторизовано. Подтвердите запрос отладки на экране устройства и нажмите «Обновить устройства».", LogWarn);
                }
                else
                {
                    _statusLabel.Text = "Ошибка подключения";
                    _statusLabel.ForeColor = Theme.Danger;
                    Log("Не удалось подключиться к " + fullAddress + ". Убедитесь, что устройство и компьютер в одной Wi-Fi сети.", LogErr);
                }
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] " + ex.Message, LogErr);
            }
            finally { SetBusy(false); }
        }

        private async Task LoadPkgsAsync()
        {
            SetBusy(true);
            _statusLabel.Text = "Загрузка списка приложений…";
            _pkgList.Items.Clear();
            _permList.Items.Clear();
            _packages.Clear();

            try
            {
                _packages = await AdbHelper.GetPackagesAsync(true);
                if (_packages.Count == 0)
                {
                    Log("Сторонние приложения не найдены, загружаю полный список…", LogWarn);
                    _packages = await AdbHelper.GetPackagesAsync(false);
                }

                _packages = _packages.OrderBy(p => p).ToList();
                FilterPkgs();

                _statusLabel.Text = "Загружено приложений: " + _packages.Count;
                _statusLabel.ForeColor = _packages.Count > 0 ? Theme.Success : Theme.Danger;
                Log(_packages.Count > 0
                        ? "Найдено приложений: " + _packages.Count
                        : "Список пуст. Проверьте подключение устройства.",
                    _packages.Count > 0 ? LogOk : LogWarn);
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] " + ex.Message, LogErr);
                _statusLabel.Text = "Ошибка загрузки списка";
                _statusLabel.ForeColor = Theme.Danger;
            }
            finally { SetBusy(false); }
        }

        private void FilterPkgs()
        {
            var f = _searchBox.Text.Trim().ToLowerInvariant();
            _pkgList.BeginUpdate();
            _pkgList.Items.Clear();
            foreach (var p in _packages.Where(p => f.Length == 0 || p.ToLowerInvariant().Contains(f)))
                _pkgList.Items.Add(p);
            _pkgList.EndUpdate();
        }

        private async void PreviewPermsAsync()
        {
            var pkg = _pkgList.SelectedItem as string;
            if (pkg == null) return;

            _permList.Items.Clear();
            _permList.Items.Add("⏳  Загрузка разрешений…");

            try
            {
                var perms = await AdbHelper.GetRequestedPermissionsAsync(pkg);

                // Если выбор изменился, пока шёл запрос — не перетираем актуальный предпросмотр.
                if ((_pkgList.SelectedItem as string) != pkg) return;

                _permList.BeginUpdate();
                _permList.Items.Clear();

                if (perms.Count == 0)
                {
                    _permList.Items.Add("ℹ  Разрешения не найдены.");
                }
                else
                {
                    foreach (var p in perms)
                    {
                        var icon = _appOpsMap.ContainsKey(p) ? "⚙  " : "•  ";
                        _permList.Items.Add(icon + p);
                    }
                    _permList.Items.Add("");
                    _permList.Items.Add("─── Критические AppOps (будут включены) ───");
                    foreach (var o in _criticalOps)
                        _permList.Items.Add("🔒  " + o);
                }
                _permList.EndUpdate();
            }
            catch (TimeoutException)
            {
                if ((_pkgList.SelectedItem as string) != pkg) return;
                _permList.Items.Clear();
                _permList.Items.Add("⏱  Время ожидания истекло.");
                _permList.Items.Add("Устройство отвечает слишком медленно — попробуйте USB.");
                Log("Тайм-аут загрузки разрешений для " + pkg, LogWarn);
            }
            catch (Exception ex)
            {
                if ((_pkgList.SelectedItem as string) != pkg) return;
                _permList.Items.Clear();
                _permList.Items.Add("❌  Ошибка: " + ex.Message);
                Log("Ошибка загрузки разрешений: " + ex.Message, LogErr);
            }
        }

        private async Task GrantAllAsync()
        {
            var pkg = _pkgList.SelectedItem as string;
            if (pkg == null)
            {
                MessageBox.Show("Сначала выберите приложение из списка.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "Выдать ВСЕ разрешения для:\n\n" + pkg +
                    "\n\nВключая чувствительные (наложение поверх окон, установка из неизвестных источников и т.д.).\n\nПродолжить?",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            SetBusy(true);
            _statusLabel.Text = "Выдача разрешений…";
            _statusLabel.ForeColor = Theme.TextDark;
            _progressBar.Visible = true;
            _progressBar.Value = 0;

            int granted = 0, skipped = 0, appops = 0;

            try
            {
                var perms = await AdbHelper.GetRequestedPermissionsAsync(pkg);
                var total = perms.Count + _criticalOps.Length;
                var cur = 0;

                Log("\n══════════════════════════════════════", LogInfo);
                Log("Выдача разрешений для: " + pkg, LogInfo);
                Log("Найдено запрашиваемых разрешений: " + perms.Count, LogDim);
                Log("(через adb выдаются runtime-разрешения и appops; обычные и signature Android назначает сам)", LogDim);

                foreach (var p in perms)
                {
                    _statusLabel.Text = "Выдача: " + p;
                    if (total > 0) _progressBar.Value = Math.Min(100, (int)(++cur * 100.0 / total));

                    if (_appOpsMap.TryGetValue(p, out string op))
                    {
                        await AdbHelper.SetAppOpAsync(pkg, op);
                        appops++;
                        Log("  [✓] AppOp: " + op, LogOk);
                    }
                    else
                    {
                        var res = await AdbHelper.GrantAsync(pkg, p);
                        if (GrantFailed(res))
                        {
                            skipped++;
                            Log("  [·] " + p + "  — не runtime, управляется системой", LogDim);
                        }
                        else
                        {
                            granted++;
                            Log("  [✓] " + p, LogOk);
                        }
                    }
                }

                Log("\n── Включение критических AppOps ──", LogInfo);
                foreach (var o in _criticalOps)
                {
                    _statusLabel.Text = "Включение: " + o;
                    if (total > 0) _progressBar.Value = Math.Min(100, (int)(++cur * 100.0 / total));

                    await AdbHelper.SetAppOpAsync(pkg, o);
                    Log("  [✓] " + o + " → allow", LogOk);
                }

                // Верификация (как в скрипте: appops get ...)
                Log("\n── Проверка статуса AppOps ──", LogInfo);
                foreach (var o in _criticalOps)
                {
                    var st = await AdbHelper.GetAppOpAsync(pkg, o);
                    Log("  " + o + ": " + st, LogDim);
                }

                _statusLabel.Text = "Перезапуск приложения…";
                Log("\nПерезапуск приложения…", LogWarn);
                await AdbHelper.ForceStopAsync(pkg);
                await Task.Delay(900);
                await AdbHelper.LaunchAsync(pkg);
                Log("Приложение перезапущено.", LogOk);

                _progressBar.Value = 100;
                _statusLabel.Text = $"Готово · выдано runtime: {granted}, AppOps: {appops}";
                _statusLabel.ForeColor = Theme.Success;
                Log($"Готово. Выдано runtime-разрешений: {granted}, спец. AppOps: {appops}; критические AppOps включены.", LogOk);
                Log($"Ещё {skipped} — не runtime (обычные/системные): их назначает сам Android, через adb они не выдаются. Это штатно, не ошибка.", LogDim);

                MessageBox.Show(
                    "Готово — приложение перезапущено.\n\n" +
                    $"Выдано через adb:\n   • runtime-разрешений: {granted}\n   • спец. AppOps: {appops}\n   • критические AppOps: включены\n\n" +
                    $"Ещё {skipped} разрешений — обычные/системные: их назначает сам Android (через adb не выдаются). Это нормально, не ошибка.",
                    "Успешно", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] " + ex.Message, LogErr);
                _statusLabel.Text = "Произошла ошибка";
                _statusLabel.ForeColor = Theme.Danger;
            }
            finally
            {
                SetBusy(false);
                _progressBar.Visible = false;
            }
        }

        private static bool GrantFailed(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            var o = output.ToLowerInvariant();
            return o.Contains("exception")
                || o.Contains("not a changeable")
                || o.Contains("not allowed")
                || o.Contains("unknown permission")
                || o.Contains("[err]")
                || o.Contains("failure")
                || o.Contains("error:");
        }

        private async Task LaunchAppAsync()
        {
            var pkg = _pkgList.SelectedItem as string;
            if (pkg == null)
            {
                MessageBox.Show("Сначала выберите приложение из списка.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true);
            _statusLabel.Text = "Запуск…";
            try
            {
                await AdbHelper.ForceStopAsync(pkg);
                await Task.Delay(400);
                await AdbHelper.LaunchAsync(pkg);
                Log("Запущено: " + pkg, LogOk);
                _statusLabel.Text = "Приложение запущено";
                _statusLabel.ForeColor = Theme.Success;
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] " + ex.Message, LogErr);
            }
            finally { SetBusy(false); }
        }

        private void ToggleLog()
        {
            bool show = !_logPanel.Visible;
            _logPanel.Visible = show;
            _mainLayout.RowStyles[LogRow].Height = show ? LogHeight : 0F;
            _btnLog.Text = show ? "📜  Скрыть лог" : "📜  Лог";
        }

        private void Log(string msg, Color c)
        {
            if (_logBox.InvokeRequired)
            {
                _logBox.Invoke(new Action(() => Log(msg, c)));
                return;
            }

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionColor = c;
            _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "]  " + msg + Environment.NewLine);
            _logBox.SelectionColor = LogText;
            _logBox.ScrollToCaret();
        }

        private void SetBusy(bool b)
        {
            _busy = b;
            this.Cursor = b ? Cursors.WaitCursor : Cursors.Default;

            var btns = new[] { _btnRefresh, _btnConnect, _btnLoad, _btnGrant, _btnLaunch, _btnLog, _btnThanks };
            foreach (var btn in btns)
                if (btn != null) btn.Enabled = !b;
            // Никаких Application.DoEvents() — операции по-настоящему асинхронны, UI не блокируется.
        }

        private string PromptIP()
        {
            using (var f = new Form
            {
                Text = "Подключение по Wi-Fi",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Theme.Card,
                Font = new Font("Segoe UI", 9.75F),
                ClientSize = new Size(430, 168),
                AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var lbl = new Label
                {
                    Text = "Введите IP-адрес устройства\n(например, 192.168.31.135):",
                    Location = new Point(20, 18),
                    AutoSize = true,
                    ForeColor = Theme.TextDark
                };
                var tb = new TextBox
                {
                    Location = new Point(22, 62),
                    Width = 386,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Segoe UI", 11F)
                };

                // Кнопки — в нижней панели с авто-раскладкой справа: не обрезаются при любом DPI.
                var bar = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.Card };
                var flow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Padding = new Padding(0, 12, 16, 0),
                    BackColor = Theme.Card
                };
                var btnOk = CreateBtn("Подключить", Theme.Primary, null);
                btnOk.Width = 150; btnOk.Height = 40; btnOk.Margin = new Padding(8, 0, 0, 0); btnOk.DialogResult = DialogResult.OK;
                var btnCancel = CreateBtn("Отмена", Theme.Slate, null);
                btnCancel.Width = 110; btnCancel.Height = 40; btnCancel.Margin = new Padding(8, 0, 0, 0); btnCancel.DialogResult = DialogResult.Cancel;

                flow.Controls.Add(btnOk);
                flow.Controls.Add(btnCancel);
                bar.Controls.Add(flow);

                f.Controls.Add(lbl);
                f.Controls.Add(tb);
                f.Controls.Add(bar);
                f.AcceptButton = btnOk;
                f.CancelButton = btnCancel;

                return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        private void ShowThanks()
        {
            using (var dlg = new Form
            {
                Text = "О программе",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Theme.Card,
                Font = new Font("Segoe UI", 9.75F),
                ClientSize = new Size(540, 356),
                AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var made = new Label
                {
                    Text = "Сделано @di_dream специально для",
                    Font = new Font("Segoe UI", 12F),
                    ForeColor = Theme.Primary,
                    AutoSize = true,
                    Location = new Point(26, 26)
                };
                var tg = MakeLink("https://t.me/ISPACE_NEW", "https://t.me/ISPACE_NEW", new Point(28, 58),
                                  new Font("Segoe UI", 12F, FontStyle.Bold | FontStyle.Underline), Theme.Info);

                var donateHdr = new Label
                {
                    Text = "Поддержать разработчика:",
                    Font = new Font("Segoe UI Semibold", 11F),
                    ForeColor = Theme.TextDark,
                    AutoSize = true,
                    Location = new Point(26, 128)
                };
                var donate = MakeLink("tbank.ru/cf/5j4cUHc9WMy", "https://tbank.ru/cf/5j4cUHc9WMy", new Point(28, 158),
                                      new Font("Segoe UI", 11F, FontStyle.Bold | FontStyle.Underline), Theme.Success);

                var scanHint = new Label
                {
                    Text = "Сканируйте QR-код для доната  →",
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = Theme.TextMuted,
                    AutoSize = true,
                    Location = new Point(28, 196)
                };

                var qr = new PictureBox
                {
                    Size = new Size(196, 196),
                    Location = new Point(318, 54),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Theme.Card
                };
                var qrImg = LoadEmbeddedImage("iSpacePermission.qr_donate.png");
                if (qrImg != null) qr.Image = qrImg;
                else { qr.Visible = false; scanHint.Visible = false; }

                var closeBtn = CreateBtn("Закрыть", Theme.Primary, null);
                closeBtn.Width = 140; closeBtn.Height = 40;
                closeBtn.Location = new Point((dlg.ClientSize.Width - closeBtn.Width) / 2, 296);
                closeBtn.DialogResult = DialogResult.OK;

                dlg.Controls.AddRange(new Control[] { made, tg, donateHdr, donate, scanHint, qr, closeBtn });
                dlg.AcceptButton = closeBtn;
                dlg.CancelButton = closeBtn;
                dlg.FormClosed += (s, e) =>
                {
                    if (qr.Image != null) { var im = qr.Image; qr.Image = null; im.Dispose(); }
                };
                dlg.ShowDialog(this);
            }
        }

        private LinkLabel MakeLink(string text, string url, Point loc, Font font, Color color)
        {
            var ll = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Location = loc,
                Font = font,
                LinkColor = color,
                ActiveLinkColor = Theme.Primary,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            ll.LinkClicked += (s, e) =>
            {
                try { Process.Start(url); } catch { }
                ll.LinkVisited = true;
            };
            return ll;
        }

        private static Image LoadEmbeddedImage(string resourceName)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return null;
                    using (var tmp = Image.FromStream(s))
                        return new Bitmap(tmp);
                }
            }
            catch { return null; }
        }

        // -----------------------------------------------------------------
        //  Завершение работы: гасим adb-демон и убиваем наш adb.exe.
        // -----------------------------------------------------------------
        private void KillAdb()
        {
            // 1) Корректно останавливаем сервер adb.
            try
            {
                if (AdbHelper.IsAvailable)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = AdbHelper.AdbPath,
                        Arguments = "kill-server",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                        if (p != null && !p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                }
            }
            catch { }

            // 2) Подчищаем процессы adb.exe, запущенные из нашей временной папки.
            try
            {
                string ourAdb = AdbHelper.AdbPath;
                if (!string.IsNullOrEmpty(ourAdb))
                {
                    foreach (var p in Process.GetProcessesByName("adb"))
                    {
                        try
                        {
                            string path = null;
                            try { path = p.MainModule != null ? p.MainModule.FileName : null; } catch { }
                            if (path != null && string.Equals(path, ourAdb, StringComparison.OrdinalIgnoreCase))
                                p.Kill();
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }
        }
    }
}
