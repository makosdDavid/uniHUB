using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsbMapper
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool dryRun = Array.IndexOf(args, "--dry-run") >= 0;
            bool noBrowser = Array.IndexOf(args, "--no-browser") >= 0;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (Array.IndexOf(args, "--selftest") >= 0) return SelfTest.Run(dir);
            if (Array.IndexOf(args, "--livetest") >= 0) { Application.EnableVisualStyles(); return LiveTest.Run(dir); }

            bool created;
            using (Mutex single = new Mutex(true, @"Local\UsbMapper.Instance", out created))
            {
                if (!created)
                {
                    // already running: ask that instance to open its settings page
                    try { EventWaitHandle.OpenExisting(@"Local\UsbMapper.Show").Set(); } catch (Exception) { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                App app = new App(dir, dryRun);
                // --token=<32 hex>: fixed session token, for scripted tests of the local API
                foreach (string a in args)
                    if (a.StartsWith("--token=", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(a.Substring(8), "^[0-9a-f]{32}$"))
                        app.Token = a.Substring(8);
                try { app.Start(!noBrowser); }
                catch (Exception e)
                {
                    app.Shutdown();
                    MessageBox.Show(e.Message, "USB Mapper – nem indult el", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                Application.Run();
                app.Shutdown();
                GC.KeepAlive(single);
            }
            return 0;
        }
    }

    sealed class App
    {
        public const string Version = "2.0.0";
        public readonly object Sync = new object();
        public readonly string Dir;
        public readonly bool DryRun;
        public readonly Logger Log;
        public readonly ConfigStore Store;
        public readonly DeviceManager Devices = new DeviceManager();
        public readonly List<string> RecentExes = new List<string>();
        public string Token = Guid.NewGuid().ToString("N");
        public AppConfig Config;
        public string Rev;
        public Mapper Mapper;
        public Recorder Recorder;
        public VirtualPad Pad;
        public SoundBank Sounds;
        public string Url;

        RawInputWindow raw;
        WebServer web;
        NotifyIcon tray;
        ToolStripMenuItem statusItem, pauseItem;
        System.Windows.Forms.Timer fgTimer;
        SynchronizationContext ui;
        EventWaitHandle showEvent;
        uint lastPid;
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        bool shutDown;

        public App(string dir, bool dryRun)
        {
            Dir = dir;
            DryRun = dryRun;
            Log = new Logger(Path.Combine(dir, "UsbMapper.log"));
            Store = new ConfigStore(dir);
        }

        public void Start(bool openBrowser)
        {
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            Log.Add("USB Mapper " + Version + " indul" + (DryRun ? " TESZT MÓDBAN (--dry-run): billentyűt nem küld, csak naplóz." : "."));
            try { Config = Store.Load(out Rev); }
            catch (ConfigException e) { throw new ConfigException("A beállításfájl hibás, ezért nem indultam el. A fájlhoz nem nyúltam.\n\n" + Store.FilePath + "\n\n" + e.Message); }

            Pad = new VirtualPad(Log);
            Sounds = new SoundBank(Log);
            IOutput output = DryRun ? (IOutput)new DryRunOutput(Log) : new RealOutput(Log, Pad, Sounds);
            Mapper = new Mapper(Sync, output, Log, Config);
            Recorder = new Recorder(Sync, Log);
            Mapper.DeviceName = DeviceName;
            Devices.Log = Log.Add;
            Devices.OnButton = delegate (HidDevice d, string input, bool down) { Recorder.OnButton(d.Key, input, down); Mapper.Button(d.Key, input, down); };
            Devices.OnHat = delegate (HidDevice d, int hat, int dir) { Recorder.OnHat(d.Key, hat, dir); Mapper.Hat(d.Key, hat, dir); };
            HidDevice.OnAxis = delegate (HidDevice d, string axis, double v) { Recorder.OnAxis(d.Key, axis, v); };
            lock (Sync) Devices.Enumerate();
            raw = new RawInputWindow(Devices, Sync);

            web = new WebServer(this, Path.Combine(Dir, "ui"));
            int port = web.Start(Config.Port);
            Url = "http://localhost:" + port + "/?t=" + Token;
            Log.Add("Felület: http://localhost:" + port + "/");

            BuildTray();
            fgTimer = new System.Windows.Forms.Timer();
            fgTimer.Interval = 400;
            fgTimer.Tick += delegate { CheckForeground(); };
            fgTimer.Start();
            CheckForeground();

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UsbMapper.Show");
            Thread t = new Thread(delegate ()
            {
                while (true)
                {
                    showEvent.WaitOne();
                    if (shutDown) return;
                    OpenUi();
                }
            });
            t.IsBackground = true;
            t.Name = "show";
            t.Start();

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try { Mapper.ReleaseAll(); Log.Add("VÉGZETES HIBA: " + e.ExceptionObject); } catch (Exception) { }
            };
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log.Add("HIBA: " + e.Exception.Message); };
            SystemEvents.SessionEnding += delegate { Mapper.ReleaseAll(); };
            if (openBrowser) OpenUi();
        }

        string DeviceName(string key)
        {
            HidDevice d = Devices.FindByKey(key);
            if (d != null) return d.Name;
            string n;
            return Config.DeviceNames.TryGetValue(key, out n) ? n : key;
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem("USB Mapper" + (DryRun ? " – teszt mód" : ""));
            title.Enabled = false;
            title.Font = new Font(title.Font, FontStyle.Bold);
            statusItem = new ToolStripMenuItem("");
            statusItem.Enabled = false;
            ToolStripMenuItem open = new ToolStripMenuItem("Beállítások megnyitása");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += delegate { OpenUi(); };
            pauseItem = new ToolStripMenuItem("Szünet");
            pauseItem.CheckOnClick = true;
            pauseItem.Click += delegate { Mapper.SetPaused(pauseItem.Checked); UpdateTray(); };
            ToolStripMenuItem quit = new ToolStripMenuItem("Kilépés");
            quit.Click += delegate { RequestExit(); };
            menu.Items.AddRange(new ToolStripItem[] { title, statusItem, new ToolStripSeparator(), open, pauseItem, new ToolStripSeparator(), quit });

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { tray.Icon = SystemIcons.Application; }
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OpenUi(); };
            tray.Visible = true;
            UpdateTray();
        }

        public void UpdateTray()
        {
            if (tray == null) return;
            string profile = Mapper.Active.Name;
            string state = Mapper.Paused ? "szünet" : DryRun ? "teszt mód" : "";
            statusItem.Text = "Profil: " + profile + (state != "" ? " (" + state + ")" : "");
            pauseItem.Checked = Mapper.Paused;
            string tip = "USB Mapper – " + profile + (state != "" ? " (" + state + ")" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip;
        }

        // Runs work on the UI thread and waits for it (hooks for macro recording live there).
        public T OnUi<T>(Func<T> work)
        {
            T result = default(T);
            Exception error = null;
            ui.Send(delegate { try { result = work(); } catch (Exception e) { error = e; } }, null);
            if (error != null) throw error;
            return result;
        }

        public void UpdateTrayLater()
        {
            if (ui != null) ui.Post(delegate { UpdateTray(); }, null);
        }

        public void OpenUi()
        {
            try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Add("A böngésző nem nyílt meg: " + e.Message); }
        }

        void CheckForeground()
        {
            IntPtr w = Native.GetForegroundWindow();
            if (w == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(w, out pid);
            if (pid == 0 || pid == lastPid || pid == ownPid) return;   // own tray menu: keep the current profile
            lastPid = pid;
            string exe = ExeOf(pid);
            lock (Sync)
            {
                if (exe != null)
                {
                    RecentExes.RemoveAll(delegate (string e) { return string.Equals(e, exe, StringComparison.OrdinalIgnoreCase); });
                    RecentExes.Insert(0, exe);
                    if (RecentExes.Count > 15) RecentExes.RemoveAt(RecentExes.Count - 1);
                }
            }
            Mapper.SetForeground(exe);
            UpdateTray();
        }

        static string ExeOf(uint pid)
        {
            IntPtr h = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return Path.GetFileName(sb.ToString());
            }
            finally { Native.CloseHandle(h); }
        }

        public void RequestExit()
        {
            if (ui != null) ui.Post(delegate { Application.ExitThread(); }, null);
        }

        public void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            try { if (Mapper != null) Mapper.ReleaseAll(); } catch (Exception) { }
            if (fgTimer != null) fgTimer.Stop();
            if (web != null) web.Stop();
            try { if (Recorder != null && Recorder.Recording) Recorder.Stop(); } catch (Exception) { }
            if (raw != null) raw.Close();
            try { if (Pad != null) Pad.Dispose(); } catch (Exception) { }
            try { if (Sounds != null) Sounds.Dispose(); } catch (Exception) { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (showEvent != null) { try { showEvent.Set(); } catch (Exception) { } }
            Log.Add("USB Mapper leállt.");
        }
    }
}
