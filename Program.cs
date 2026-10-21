// MiniThemeSwitch — 极简的浅色/深色自动切换（常驻托盘，单个 exe，几十 KB）
// 编译：csc /target:winexe /optimize+ /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Program.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniThemeSwitch
{
    internal static class Log
    {
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Settings.FolderPath);
                string path = Path.Combine(Settings.FolderPath, "app.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 200 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
            }
            catch
            {
            }
        }
    }

    internal sealed class Settings
    {
        public TimeSpan Light;
        public TimeSpan Dark;
        public bool Autostart;
        public bool Paused;

        public Settings()
        {
            Light = new TimeSpan(7, 0, 0);
            Dark = new TimeSpan(18, 0, 0);
            Autostart = true;
            Paused = false;
        }

        public static string FolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiniThemeSwitch");
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(FolderPath, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(ConfigPath))
                    return s;

                string[] lines = File.ReadAllLines(ConfigPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    if (key == "light")
                    {
                        TimeSpan t;
                        if (TimeSpan.TryParse(val, out t)) s.Light = t;
                    }
                    else if (key == "dark")
                    {
                        TimeSpan t;
                        if (TimeSpan.TryParse(val, out t)) s.Dark = t;
                    }
                    else if (key == "autostart")
                    {
                        s.Autostart = (val == "1");
                    }
                    else if (key == "paused")
                    {
                        s.Paused = (val == "1");
                    }
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(ConfigPath, string.Format(
                    "light={0:hh\\:mm}\r\ndark={1:hh\\:mm}\r\nautostart={2}\r\npaused={3}\r\n",
                    Light, Dark, Autostart ? 1 : 0, Paused ? 1 : 0));
            }
            catch (Exception ex)
            {
                Log.Write("save config failed: " + ex.Message);
            }
        }

        public string Summary()
        {
            return string.Format("浅色 {0:hh\\:mm} → 深色 {1:hh\\:mm}", Light, Dark);
        }
    }

    internal static class Themes
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_THEMECHANGED = 0x031A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_FRAME = 0x0400;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        public static bool? ReadCurrent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false))
                {
                    if (key == null) return null;
                    object value = key.GetValue("AppsUseLightTheme");
                    if (value == null) return null;
                    return Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool Apply(bool light)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
                {
                    if (key == null) return false;
                    int value = light ? 1 : 0;
                    key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
                }

                Refresh();

                // 副屏任务栏经常慢半拍，隔 0.7 秒再戳一次
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Thread.Sleep(700);
                        Refresh();
                    }
                    catch
                    {
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Write("apply theme failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>通知所有窗口换色，并额外戳一下每块屏幕的任务栏。</summary>
        public static void Refresh()
        {
            try
            {
                IntPtr result;
                SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero,
                    "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out result);
                SendMessageTimeout(new IntPtr(0xFFFF), WM_THEMECHANGED, IntPtr.Zero,
                    null, SMTO_ABORTIFHUNG, 1000, out result);
                PokeTaskbars();
            }
            catch (Exception ex)
            {
                Log.Write("refresh failed: " + ex.Message);
            }
        }

        /// <summary>强制刷新：瞬间翻转一下透明效果，逼 DWM 重画所有任务栏。</summary>
        public static void ForceRefresh()
        {
            int started = Environment.TickCount;
            int originalValue = 1;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, true))
                {
                    if (key != null)
                    {
                        object original = key.GetValue("EnableTransparency");
                        originalValue = original == null ? 1 : Convert.ToInt32(original);
                        key.SetValue("EnableTransparency", originalValue == 1 ? 0 : 1, RegistryValueKind.DWord);
                    }
                }

                PokeTaskbars();
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                Log.Write("force refresh step failed: " + ex.Message);
            }
            finally
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, true))
                    {
                        if (key != null)
                            key.SetValue("EnableTransparency", originalValue, RegistryValueKind.DWord);
                    }
                    PokeTaskbars();
                }
                catch (Exception ex)
                {
                    Log.Write("force refresh restore failed: " + ex.Message);
                }
            }

            Log.Write("force refresh performed in " + (Environment.TickCount - started) + " ms");
        }

        private static void PokeTaskbars()
        {
            int count = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
            {
                StringBuilder name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                string cls = name.ToString();
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                {
                    count++;
                    IntPtr result;
                    SendMessageTimeout(hwnd, WM_SETTINGCHANGE, IntPtr.Zero,
                        "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out result);
                    SendMessageTimeout(hwnd, WM_THEMECHANGED, IntPtr.Zero,
                        null, SMTO_ABORTIFHUNG, 200, out result);
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
                }
                return true;
            }, IntPtr.Zero);
            Log.Write("poked " + count + " taskbar window(s)");
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MiniThemeSwitch";

        public static void Sync(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    else if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("autostart sync failed: " + ex.Message);
            }
        }
    }

    internal static class Scheduler
    {
        public static bool? Desired(Settings s, DateTime now)
        {
            if (s.Light == s.Dark)
                return null;

            TimeSpan t = now.TimeOfDay;
            if (s.Light < s.Dark)
                return (t >= s.Light && t < s.Dark);
            return (t >= s.Light || t < s.Dark);
        }

        public static DateTime NextBoundary(Settings s, DateTime now)
        {
            DateTime today = now.Date;
            DateTime[] candidates = new DateTime[]
            {
                today + s.Light,
                today + s.Dark,
                today + s.Light + TimeSpan.FromDays(1),
                today + s.Dark + TimeSpan.FromDays(1)
            };

            DateTime best = DateTime.MaxValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] > now && candidates[i] < best)
                    best = candidates[i];
            }
            return best;
        }
    }

    internal static class Icons
    {
        public static Icon Sun()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 190, 40)))
                using (Pen pen = new Pen(brush, 1.5f))
                {
                    g.FillEllipse(brush, 4.5f, 4.5f, 7f, 7f);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4.0;
                        float x1 = 8f + (float)(Math.Cos(a) * 6.1);
                        float y1 = 8f + (float)(Math.Sin(a) * 6.1);
                        float x2 = 8f + (float)(Math.Cos(a) * 7.5);
                        float y2 = 8f + (float)(Math.Sin(a) * 7.5);
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public static Icon Moon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(70, 130, 220)))
                {
                    g.FillEllipse(brush, 2f, 2f, 12f, 12f);
                }
                g.CompositingMode = CompositingMode.SourceCopy;
                using (SolidBrush cut = new SolidBrush(Color.Transparent))
                {
                    g.FillEllipse(cut, 6.5f, 1f, 11f, 11f);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly DateTimePicker _lightPicker;
        private readonly DateTimePicker _darkPicker;
        private readonly CheckBox _autostartCheck;

        public SettingsForm(Settings settings)
        {
            Text = "MiniThemeSwitch 设置";
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18, 14, 18, 14);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.RowCount = 5;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label lightLabel = new Label();
            lightLabel.Text = "浅色模式开始时间";
            lightLabel.AutoSize = true;
            lightLabel.Anchor = AnchorStyles.Left;
            lightLabel.Margin = new Padding(3, 9, 16, 9);

            _lightPicker = MakePicker(settings.Light);

            Label darkLabel = new Label();
            darkLabel.Text = "深色模式开始时间";
            darkLabel.AutoSize = true;
            darkLabel.Anchor = AnchorStyles.Left;
            darkLabel.Margin = new Padding(3, 9, 16, 9);

            _darkPicker = MakePicker(settings.Dark);

            grid.Controls.Add(lightLabel, 0, 0);
            grid.Controls.Add(_lightPicker, 1, 0);
            grid.Controls.Add(darkLabel, 0, 1);
            grid.Controls.Add(_darkPicker, 1, 1);

            Label hint = new Label();
            hint.Text = "到点自动切换；两个时间相同则不切换。";
            hint.AutoSize = true;
            hint.ForeColor = SystemColors.GrayText;
            hint.Margin = new Padding(3, 10, 3, 10);
            grid.Controls.Add(hint, 0, 2);
            grid.SetColumnSpan(hint, 2);

            _autostartCheck = new CheckBox();
            _autostartCheck.Text = "开机自动启动（登录后常驻托盘）";
            _autostartCheck.AutoSize = true;
            _autostartCheck.Checked = settings.Autostart;
            _autostartCheck.Margin = new Padding(3, 3, 3, 10);
            grid.Controls.Add(_autostartCheck, 0, 3);
            grid.SetColumnSpan(_autostartCheck, 2);

            Button okButton = new Button();
            okButton.Text = "保存并生效";
            okButton.AutoSize = true;
            okButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            okButton.MinimumSize = new Size(104, 32);
            okButton.Padding = new Padding(10, 0, 10, 0);
            okButton.Margin = new Padding(8, 4, 0, 0);
            okButton.DialogResult = DialogResult.OK;

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.AutoSize = true;
            cancelButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            cancelButton.MinimumSize = new Size(76, 32);
            cancelButton.Padding = new Padding(8, 0, 8, 0);
            cancelButton.Margin = new Padding(8, 4, 0, 0);
            cancelButton.DialogResult = DialogResult.Cancel;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 8, 0, 0);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            grid.Controls.Add(buttons, 0, 4);
            grid.SetColumnSpan(buttons, 2);

            Controls.Add(grid);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static DateTimePicker MakePicker(TimeSpan value)
        {
            DateTimePicker picker = new DateTimePicker();
            picker.Format = DateTimePickerFormat.Custom;
            picker.CustomFormat = "HH:mm";
            picker.ShowUpDown = true;
            picker.Value = DateTime.Today + value;
            picker.Width = 100;
            picker.Margin = new Padding(3, 5, 3, 5);
            return picker;
        }

        public TimeSpan LightTime
        {
            get
            {
                DateTime v = _lightPicker.Value;
                return new TimeSpan(v.Hour, v.Minute, 0);
            }
        }

        public TimeSpan DarkTime
        {
            get
            {
                DateTime v = _darkPicker.Value;
                return new TimeSpan(v.Hour, v.Minute, 0);
            }
        }

        public bool AutostartEnabled
        {
            get { return _autostartCheck.Checked; }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _clock;
        private readonly Icon _sunIcon;
        private readonly Icon _moonIcon;
        private readonly HiddenWindow _hidden;

        private PowerModeChangedEventHandler _onPower;
        private SessionSwitchEventHandler _onSession;

        private string _lastTrayText;
        private bool _lastTrayLight = true;
        private bool _paused;
        private bool _manualActive;
        private bool _manualLight;
        private DateTime _manualUntil;

        public TrayContext(Settings settings)
        {
            _settings = settings;
            _paused = settings.Paused;

            Log.Write("tray: creating icons");

            _sunIcon = Icons.Sun();
            _moonIcon = Icons.Moon();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Opening += delegate { BuildMenu(); };

            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = _menu;
            _tray.Text = "MiniThemeSwitch";
            _tray.Icon = _moonIcon;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(); };

            Log.Write("tray: notify icon visible");

            _clock = new System.Windows.Forms.Timer();
            _clock.Interval = 60000;
            _clock.Tick += delegate { Tick(); };

            _hidden = new HiddenWindow();
            _hidden.CreateHandle(new CreateParams());
            _hidden.ShowRequested += delegate { ShowSettings(); };
            _hidden.TickRequested += delegate { Tick(); };

            _onPower = delegate(object sender, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Resume) _hidden.Post(HiddenWindow.WmTick);
            };
            _onSession = delegate(object sender, SessionSwitchEventArgs e)
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock) _hidden.Post(HiddenWindow.WmTick);
            };
            SystemEvents.PowerModeChanged += _onPower;
            SystemEvents.SessionSwitch += _onSession;

            StartShowListener();
            Autostart.Sync(_settings.Autostart);

            bool firstRun = !File.Exists(Settings.ConfigPath);
            if (firstRun)
                _settings.Save();

            Tick();

            if (firstRun)
                _hidden.Post(HiddenWindow.WmShow);

            Log.Write("tray: ready, config=" + Settings.ConfigPath + " firstRun=" + firstRun);
        }

        /// <summary>隐藏窗口：用来把后台线程的事件投递到 UI 线程，避免轮询。</summary>
        private sealed class HiddenWindow : NativeWindow
        {
            public const int WmShow = 0x8000 + 33;
            public const int WmTick = 0x8000 + 34;

            public event EventHandler ShowRequested;
            public event EventHandler TickRequested;

            public void Post(int msg)
            {
                if (Handle != IntPtr.Zero)
                    PostMessage(Handle, msg, IntPtr.Zero, IntPtr.Zero);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmShow)
                {
                    EventHandler handler = ShowRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                    return;
                }
                if (m.Msg == WmTick)
                {
                    EventHandler handler = TickRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void ScheduleNext()
        {
            DateTime now = DateTime.Now;
            DateTime next = Scheduler.NextBoundary(_settings, now);

            double ms = (next - now).TotalMilliseconds;
            if (ms < 15000) ms = 15000;
            if (ms > 3600000) ms = 3600000;   // 最长睡 1 小时，防止时钟调整/休眠后错过

            _clock.Stop();
            _clock.Interval = (int)ms;
            _clock.Start();
        }

        private void StartShowListener()
        {
            Thread thread = new Thread(delegate ()
            {
                try
                {
                    using (EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset,
                        @"Local\MiniThemeSwitch.Show"))
                    {
                        while (true)
                        {
                            ev.WaitOne();
                            _hidden.Post(HiddenWindow.WmShow);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("listener stopped: " + ex.Message);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void BuildMenu()
        {
            _menu.Items.Clear();

            ToolStripMenuItem header = new ToolStripMenuItem(_settings.Summary());
            header.Enabled = false;
            _menu.Items.Add(header);

            ToolStripMenuItem state = new ToolStripMenuItem(StateText());
            state.Enabled = false;
            _menu.Items.Add(state);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem lightItem = new ToolStripMenuItem("立即切换为浅色");
            lightItem.Click += delegate { SwitchManual(true); };
            _menu.Items.Add(lightItem);

            ToolStripMenuItem darkItem = new ToolStripMenuItem("立即切换为深色");
            darkItem.Click += delegate { SwitchManual(false); };
            _menu.Items.Add(darkItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem pauseItem = new ToolStripMenuItem("暂停自动切换");
            pauseItem.Checked = _paused;
            pauseItem.Click += delegate { TogglePause(); };
            _menu.Items.Add(pauseItem);

            ToolStripMenuItem autostartItem = new ToolStripMenuItem("开机自动启动");
            autostartItem.Checked = _settings.Autostart;
            autostartItem.Click += delegate { ToggleAutostart(); };
            _menu.Items.Add(autostartItem);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置时间...");
            settingsItem.Click += delegate { ShowSettings(); };
            _menu.Items.Add(settingsItem);

            ToolStripMenuItem fixItem = new ToolStripMenuItem("修复任务栏颜色（强制刷新）");
            fixItem.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate { Themes.ForceRefresh(); });
            };
            _menu.Items.Add(fixItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };
            _menu.Items.Add(exitItem);
        }

        private string StateText()
        {
            bool? current = Themes.ReadCurrent();
            string mode = current.HasValue ? (current.Value ? "当前：浅色" : "当前：深色") : "当前：未知";
            if (_paused)
                return mode + "（已暂停自动切换）";
            if (_manualActive && DateTime.Now < _manualUntil)
                return mode + "（手动切换，到点恢复自动）";
            return mode;
        }

        private void Tick()
        {
            DateTime now = DateTime.Now;
            bool? want;

            if (_paused)
            {
                want = null;
            }
            else if (_manualActive && now < _manualUntil)
            {
                want = _manualLight;
            }
            else
            {
                _manualActive = false;
                want = Scheduler.Desired(_settings, now);
            }

            if (want.HasValue)
            {
                bool? current = Themes.ReadCurrent();
                if (!current.HasValue || current.Value != want.Value)
                    Themes.Apply(want.Value);
            }

            RefreshTray();
            ScheduleNext();
        }

        private void RefreshTray()
        {
            bool? current = Themes.ReadCurrent();
            string mode = current.HasValue ? (current.Value ? "浅色" : "深色") : "未知";
            string text;
            if (_paused)
                text = "MiniThemeSwitch - 已暂停（" + mode + "）";
            else
                text = string.Format("MiniThemeSwitch - {0} | 浅 {1:hh\\:mm} / 深 {2:hh\\:mm}",
                    mode, _settings.Light, _settings.Dark);

            if (text.Length > 63)
                text = text.Substring(0, 63);

            bool light = (current.HasValue && current.Value);
            if (text == _lastTrayText && light == _lastTrayLight)
                return;

            _lastTrayText = text;
            _lastTrayLight = light;
            _tray.Text = text;
            _tray.Icon = light ? _sunIcon : _moonIcon;
        }

        private void SwitchManual(bool light)
        {
            if (Themes.Apply(light))
            {
                _manualActive = true;
                _manualLight = light;
                _manualUntil = Scheduler.NextBoundary(_settings, DateTime.Now);
            }
            RefreshTray();
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _settings.Paused = _paused;
            _settings.Save();
            Tick();
        }

        private void ToggleAutostart()
        {
            _settings.Autostart = !_settings.Autostart;
            Autostart.Sync(_settings.Autostart);
            _settings.Save();
        }

        private void ShowSettings()
        {
            using (SettingsForm form = new SettingsForm(_settings))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return;

                _settings.Light = form.LightTime;
                _settings.Dark = form.DarkTime;
                _settings.Autostart = form.AutostartEnabled;
                _settings.Save();
                Autostart.Sync(_settings.Autostart);
                _manualActive = false;
                Tick();
            }
        }

        private void ExitApp()
        {
            DialogResult answer = MessageBox.Show(
                "退出后就不再自动切换主题了。\n下次登录仍会自动启动，确定现在退出吗？",
                "MiniThemeSwitch",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            Log.Write("exit requested from tray menu");
            _clock.Stop();
            SystemEvents.PowerModeChanged -= _onPower;
            SystemEvents.SessionSwitch -= _onSession;
            if (_hidden != null)
                _hidden.DestroyHandle();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch { }

            string mode = "";
            if (args != null && args.Length > 0)
                mode = args[0].Trim().ToLowerInvariant();

            if (mode == "--light")
            {
                Themes.Apply(true);
                Thread.Sleep(900);
                return;
            }
            if (mode == "--dark")
            {
                Themes.Apply(false);
                Thread.Sleep(900);
                return;
            }
            if (mode == "--fix")
            {
                Themes.ForceRefresh();
                Thread.Sleep(300);
                return;
            }
            if (mode == "--auto")
            {
                Settings autoCfg = Settings.Load();
                bool? want = Scheduler.Desired(autoCfg, DateTime.Now);
                if (want.HasValue)
                {
                    Themes.Apply(want.Value);
                    Thread.Sleep(900);
                }
                return;
            }

            Log.Write("start exe=" + Application.ExecutablePath + " args=" + string.Join(" ", args ?? new string[0]));

            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\MiniThemeSwitch.SingleInstance", out created))
            {
                if (!created)
                {
                    try
                    {
                        EventWaitHandle ev;
                        if (EventWaitHandle.TryOpenExisting(@"Local\MiniThemeSwitch.Show", out ev))
                            ev.Set();
                    }
                    catch
                    {
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    Log.Write("UI exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Write("fatal exception: " + e.ExceptionObject);
                };

                Settings settings = Settings.Load();
                TrayContext context = new TrayContext(settings);
                try
                {
                    Application.Run(context);
                }
                catch (Exception ex)
                {
                    Log.Write("message loop crashed: " + ex);
                    throw;
                }
                Log.Write("message loop ended");
            }
        }
    }
}
