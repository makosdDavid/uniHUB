using System;
using System.Collections.Generic;
using System.Threading;

namespace UsbMapper
{
    // Turns controller events into output according to the active profile.
    // Everything that is held down (keys, mouse buttons, media keys, gamepad controls) goes through a
    // reference-counted hold table, so a profile switch, pause or exit can release all of it at once.
    sealed class Mapper
    {
        readonly object sync;
        readonly IOutput o;
        readonly Logger log;
        AppConfig cfg;
        Profile active;
        string exe;
        bool paused;
        bool warnedBlocked;
        bool padAxesUsed, soundUsed;
        int generation;
        readonly Dictionary<string, int> holdRef = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> hatActive = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, MacroRun> runs = new Dictionary<string, MacroRun>(StringComparer.Ordinal);
        readonly Dictionary<string, object> wheelRepeat = new Dictionary<string, object>(StringComparer.Ordinal);
        readonly HashSet<string> soundHeld = new HashSet<string>(StringComparer.Ordinal);

        public Func<string, string> DeviceName = delegate (string key) { return key; };
        public int TapMs = 30;

        public Mapper(object sync, IOutput output, Logger log, AppConfig cfg)
        {
            this.sync = sync; o = output; this.log = log; this.cfg = cfg;
            active = cfg.ForExe(null);
        }

        public Profile Active { get { lock (sync) return active; } }
        public string Exe { get { lock (sync) return exe; } }
        public bool Paused { get { lock (sync) return paused; } }
        public int RunningMacros { get { lock (sync) return runs.Count; } }
        int Gen { get { return Thread.VolatileRead(ref generation); } }

        public void SetConfig(AppConfig c)
        {
            lock (sync)
            {
                ReleaseAllLocked();
                cfg = c;
                active = cfg.ForExe(exe);
                log.Add("Új beállítás betöltve. Aktív profil: " + active.Name);
            }
        }

        public void SetForeground(string exeName)
        {
            lock (sync)
            {
                exe = exeName;
                Profile p = cfg.ForExe(exeName);
                if (p == active) return;
                ReleaseAllLocked();
                active = p;
                log.Add("Profilváltás: " + p.Name + (exeName != null ? " (" + exeName + ")" : ""));
            }
        }

        public void SetPaused(bool value)
        {
            lock (sync)
            {
                if (paused == value) return;
                paused = value;
                if (paused) ReleaseAllLocked();
                log.Add(paused ? "Szünet: a USB Mapper nem küld semmit." : "Folytatás: a USB Mapper újra működik.");
            }
        }

        public void ReleaseAll() { lock (sync) ReleaseAllLocked(); }

        public void Button(string device, string input, bool down)
        {
            lock (sync) { if (down) Press(device, input); else Release(device, input); }
        }

        // dir: -1 = centre, 0..7 = U, UR, R, DR, D, DL, L, UL.
        // A diagonal without its own assignment presses its two neighbours (like W+D).
        public void Hat(string device, int hat, int dir)
        {
            lock (sync)
            {
                HashSet<string> now = new HashSet<string>(StringComparer.Ordinal);
                if (dir >= 0)
                {
                    string own = Inputs.Hat(hat, dir);
                    if (dir % 2 == 0 || (active != null && active.Find(device, own) != null)) now.Add(own);
                    else { now.Add(Inputs.Hat(hat, (dir + 7) % 8)); now.Add(Inputs.Hat(hat, (dir + 1) % 8)); }
                }
                string hk = device + "|h" + hat;
                HashSet<string> before;
                if (!hatActive.TryGetValue(hk, out before)) before = new HashSet<string>(StringComparer.Ordinal);
                foreach (string i in before) if (!now.Contains(i)) Release(device, i);
                foreach (string i in now) if (!before.Contains(i)) Press(device, i);
                hatActive[hk] = now;
            }
        }

        // ---------- press / release ----------
        static readonly Dictionary<string, int> mediaVk = new Dictionary<string, int>
        {
            { "play_pause", 0xB3 }, { "stop", 0xB2 }, { "next", 0xB0 }, { "prev", 0xB1 }, { "vol_up", 0xAF }, { "vol_down", 0xAE }, { "mute", 0xAD },
        };

        void Press(string device, string input)
        {
            if (paused || active == null) return;
            Binding b = active.Find(device, input);
            if (b == null) return;
            string k = device + "|" + input;
            string who = DeviceName(device) + " · " + Inputs.Name(input);
            switch (b.Type)
            {
                case "keys":
                    if (held.ContainsKey(k)) return;
                    log.Add(who + " → " + Keys.Combo(b.Keys));
                    HoldAll(k, Ids("k", b.Keys));
                    return;
                case "media":
                    if (held.ContainsKey(k)) return;
                    log.Add(who + " → média: " + b.Action);
                    HoldAll(k, new List<string> { "v" + mediaVk[b.Action] });
                    return;
                case "pad":
                    if (held.ContainsKey(k)) return;
                    log.Add(who + " → gamepad " + b.Control);
                    HoldAll(k, new List<string> { "p" + b.Control });
                    return;
                case "mouse":
                    log.Add(who + " → egér: " + b.Action + (b.Repeat == "hold" ? " (ismétel, amíg nyomod)" : ""));
                    MousePress(k, b);
                    return;
                case "sound":
                    log.Add(who + " → hangminta (" + b.Play + "): " + b.Path);
                    soundUsed = true;
                    if (b.Play == "once") o.Exec("sound", "once", k, b.Path);
                    else if (b.Play == "hold") { o.Exec("sound", "hold-start", k, b.Path); soundHeld.Add(k); }
                    else o.Exec("sound", "toggle", k, b.Path);
                    return;
                case "macro":
                    {
                        Macro m = cfg.MacroById(b.MacroId);
                        if (m == null) return;
                        MacroRun run;
                        if (runs.TryGetValue(k, out run))
                        {
                            if (m.Mode == "toggle") { if (!run.ToggleOff) { run.ToggleOff = true; log.Add(who + " → makró kikapcsolva: " + m.Name); } }
                            else if (m.Mode != "once") run.Released = false;
                            return;
                        }
                        log.Add(who + " → makró: " + m.Name);
                        run = new MacroRun(this, m, k, Gen);
                        runs[k] = run;
                        run.Start();
                        return;
                    }
                default:
                    log.Add(who + " → " + Describe(b));
                    OneShot(b);
                    return;
            }
        }

        void Release(string device, string input)
        {
            string k = device + "|" + input;
            List<string> ids;
            if (held.TryGetValue(k, out ids))
            {
                held.Remove(k);
                for (int i = ids.Count - 1; i >= 0; i--) Unhold(ids[i]);
            }
            wheelRepeat.Remove(k);
            if (soundHeld.Remove(k)) o.Exec("sound", "hold-stop", k, null);
            MacroRun run;
            if (runs.TryGetValue(k, out run)) run.Released = true;
        }

        static List<string> Ids(string prefix, int[] codes)
        {
            List<string> l = new List<string>();
            foreach (int c in codes) l.Add(prefix + c);
            return l;
        }

        void HoldAll(string k, List<string> ids)
        {
            foreach (string id in ids) Hold(id);
            held[k] = ids;
        }

        void MousePress(string k, Binding b)
        {
            string a = b.Action;
            if (Vocab.In(Vocab.MouseButtons, a)) { if (!held.ContainsKey(k)) HoldAll(k, new List<string> { "m" + a }); return; }
            int gen = Gen;
            if (a.StartsWith("double_", StringComparison.Ordinal))
            {
                string btn = a.Substring(7);
                Async(gen, delegate { Click(btn, true, gen); });
                return;
            }
            string dir = a.Substring(6);   // wheel_*
            o.Wheel(dir, 1);
            if (b.Repeat != "hold") return;
            object token = new object();
            wheelRepeat[k] = token;
            Async(gen, delegate
            {
                // like a held key: first repeat after 400 ms, then every 50 ms
                if (!WaitWhile(400, gen, delegate { return KeepWheel(k, token); })) return;
                while (KeepWheel(k, token) && Gen == gen)
                {
                    o.Wheel(dir, 1);
                    if (!WaitWhile(50, gen, delegate { return KeepWheel(k, token); })) return;
                }
            });
        }

        bool KeepWheel(string k, object token)
        {
            lock (sync) { object t; return wheelRepeat.TryGetValue(k, out t) && t == token; }
        }

        // Fire-and-forget assignments (text, system, app, web, file/folder, audio).
        void OneShot(Binding b)
        {
            int gen = Gen;
            switch (b.Type)
            {
                case "text": { string t = b.Text; Async(gen, delegate { o.Text(t); }); break; }
                case "system": { string a = b.Action; Async(gen, delegate { RunSystem(a, gen); }); break; }
                case "app": { string p = b.Path, ar = b.Args, d = b.WorkDir; Async(gen, delegate { o.Exec("app", p, ar, d); }); break; }
                case "web": { string u = b.Url; Async(gen, delegate { o.Exec("web", u, null, null); }); break; }
                case "open": { string p = b.Path; Async(gen, delegate { o.Exec("open", p, null, null); }); break; }
                case "audio": { string a = b.Action; Async(gen, delegate { o.Exec("audio", a, null, null); }); break; }
            }
        }

        public static string Describe(Binding b)
        {
            switch (b.Type)
            {
                case "text": return "szöveg (" + b.Text.Length + " karakter)";
                case "system": return "rendszer: " + b.Action;
                case "app": return "program: " + b.Path;
                case "web": return "weboldal: " + b.Url;
                case "open": return "megnyitás: " + b.Path;
                case "audio": return "hang: " + b.Action;
            }
            return b.Type;
        }

        void Async(int gen, ThreadStart work)
        {
            Thread t = new Thread(delegate ()
            {
                if (Gen != gen) return;
                try { work(); }
                catch (Exception e) { log.Add("HIBA: " + e.Message); }
            });
            t.IsBackground = true;
            t.Name = "action";
            t.Start();
        }

        // ---------- system actions (G HUB Macro.System), layout-independent virtual keys ----------
        static readonly Dictionary<string, int[]> systemVk = new Dictionary<string, int[]>
        {
            { "close", new[] { 0x12, 0x73 } }, { "fullscreen", new[] { 0x7A } },
            { "copy", new[] { 0x11, 0x43 } }, { "cut", new[] { 0x11, 0x58 } }, { "paste", new[] { 0x11, 0x56 } },
            { "undo", new[] { 0x11, 0x5A } }, { "redo", new[] { 0x11, 0x59 } }, { "select_all", new[] { 0x11, 0x41 } },
            { "desktop", new[] { 0x5B, 0x44 } }, { "task_view", new[] { 0x5B, 0x09 } },
            { "switch_apps", new[] { 0x12, 0x09 } }, { "shift_switch_apps", new[] { 0x12, 0x10, 0x09 } },
            { "snap_left", new[] { 0x5B, 0x25 } }, { "snap_right", new[] { 0x5B, 0x27 } },
            { "desktop_next", new[] { 0x5B, 0x11, 0x27 } }, { "desktop_prev", new[] { 0x5B, 0x11, 0x25 } },
            { "screen_capture", new[] { 0x5B, 0x10, 0x53 } }, { "app_menu", new[] { 0x5D } },
            { "file_client", new[] { 0xB6 } }, { "internet_client", new[] { 0xAC } }, { "mail_client", new[] { 0xB4 } },
            { "media_client", new[] { 0xB5 } }, { "calculator", new[] { 0xB7 } },
        };

        // Runs on a worker thread.
        void RunSystem(string action, int gen)
        {
            int[] vks;
            if (systemVk.TryGetValue(action, out vks)) { TapIds(Ids("v", vks), gen); return; }
            switch (action)
            {
                case "minimize": case "maximize": o.Exec("window", action, null, null); break;
                case "lock": o.Exec("lock", null, null, null); break;
                case "brightness_up": o.Exec("brightness", "10", null, null); break;
                case "brightness_down": o.Exec("brightness", "-10", null, null); break;
                case "scroll_left": o.Wheel("left", 1); break;
                case "scroll_right": o.Wheel("right", 1); break;
                case "zoom_in":
                case "zoom_out":
                    lock (sync) { if (Gen != gen) return; Hold("v17"); }
                    o.Wheel(action == "zoom_in" ? "up" : "down", 1);
                    Thread.Sleep(TapMs);
                    lock (sync) { if (Gen == gen) Unhold("v17"); }
                    break;
            }
        }

        void TapIds(List<string> ids, int gen)
        {
            lock (sync) { if (Gen != gen) return; foreach (string id in ids) Hold(id); }
            Thread.Sleep(TapMs);
            lock (sync) { if (Gen != gen) return; for (int i = ids.Count - 1; i >= 0; i--) Unhold(ids[i]); }
        }

        void Click(string btn, bool dbl, int gen)
        {
            for (int n = 0; n < (dbl ? 2 : 1); n++)
            {
                if (n > 0) Thread.Sleep(60);
                TapIds(new List<string> { "m" + btn }, gen);
            }
        }

        bool WaitWhile(int ms, int gen, Func<bool> keep)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                if (Gen != gen || !keep()) return false;
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return true;
                Thread.Sleep(Math.Min(left, 15));
            }
        }

        // ---------- hold table ----------
        void Hold(string id)
        {
            int n;
            holdRef.TryGetValue(id, out n);
            holdRef[id] = n + 1;
            if (n == 0) Emit(id, true);
        }

        void Unhold(string id)
        {
            int n;
            if (!holdRef.TryGetValue(id, out n) || n == 0) return;
            if (n == 1) { holdRef.Remove(id); Emit(id, false); }
            else holdRef[id] = n - 1;
        }

        void Emit(string id, bool down)
        {
            bool ok;
            string rest = id.Substring(1);
            switch (id[0])
            {
                case 'k': ok = o.Key(int.Parse(rest), down); break;
                case 'v': ok = o.Vk(int.Parse(rest), down); break;
                case 'm': ok = o.Mouse(rest, down); break;
                case 'p': ok = o.Pad(rest, down); break;
                default: return;
            }
            if (ok || warnedBlocked) return;
            warnedBlocked = true;
            log.Add(id[0] == 'p'
                ? "HIBA: a virtuális gamepad nem fogadta a jelet."
                : "HIBA: a Windows nem engedte a bemenet küldését. Ha a játék rendszergazdaként fut, a USB Mappert is rendszergazdaként kell indítani.");
        }

        void ReleaseAllLocked()
        {
            Interlocked.Increment(ref generation);
            List<string> ids = new List<string>(holdRef.Keys);
            ids.Sort(StringComparer.Ordinal);
            for (int i = ids.Count - 1; i >= 0; i--) Emit(ids[i], false);
            holdRef.Clear();
            held.Clear();
            wheelRepeat.Clear();
            soundHeld.Clear();
            if (soundUsed) { soundUsed = false; o.Exec("sound", "stopall", null, null); }
            if (padAxesUsed)
            {
                padAxesUsed = false;
                foreach (string a in VirtualPad.Axes) o.PadAxis(a, 0);
            }
        }

        // ---------- macros ----------
        sealed class MacroRun
        {
            readonly Mapper mp;
            readonly Macro m;
            readonly string key;
            readonly int gen;
            readonly List<string> own = new List<string>();
            volatile bool released, toggleOff;
            public bool Released { get { return released; } set { released = value; } }
            public bool ToggleOff { get { return toggleOff; } set { toggleOff = value; } }

            public MacroRun(Mapper mp, Macro m, string key, int gen) { this.mp = mp; this.m = m; this.key = key; this.gen = gen; }

            bool Alive { get { return mp.Gen == gen; } }

            public void Start()
            {
                Thread t = new Thread(Run);
                t.IsBackground = true;
                t.Name = "macro";
                t.Start();
            }

            void Run()
            {
                try
                {
                    Func<bool> never = delegate { return false; };
                    Func<bool> onRelease = delegate { return m.Interruptible && released; };
                    Func<bool> onToggle = delegate { return m.Interruptible && toggleOff; };
                    switch (m.Mode)
                    {
                        case "once": Play(m.Simple, never); break;
                        case "repeat": do { Play(m.Simple, onRelease); } while (!released && Alive); break;
                        case "toggle": do { Play(m.Simple, onToggle); } while (!toggleOff && Alive); break;
                        case "sequence":
                            Play(m.Press, never);
                            if (m.Held.Count == 0) { while (!released && Alive) Thread.Sleep(10); }
                            else while (!released && Alive) Play(m.Held, onRelease);
                            if (Alive) Play(m.Release, never);
                            break;
                    }
                }
                catch (Exception e) { mp.log.Add("HIBA a makróban („" + m.Name + "”): " + e.Message); }
                finally
                {
                    lock (mp.sync)
                    {
                        if (Alive) for (int i = own.Count - 1; i >= 0; i--) mp.Unhold(own[i]);
                        own.Clear();
                        MacroRun cur;
                        if (mp.runs.TryGetValue(key, out cur) && cur == this) mp.runs.Remove(key);
                    }
                }
            }

            void Play(List<Step> steps, Func<bool> cut)
            {
                DateTime start = DateTime.UtcNow;
                foreach (Step s in steps)
                {
                    if (!Alive || cut()) return;
                    if (s.T == "delay")
                    {
                        if (!m.UseDefaultDelay && !Wait(s.Ms, cut)) return;
                        continue;
                    }
                    Exec(s);
                    if (m.UseDefaultDelay && m.DefaultDelay > 0 && !Wait(m.DefaultDelay, cut)) return;
                }
                // safety: a pass that took no time at all must not spin the CPU
                if ((DateTime.UtcNow - start).TotalMilliseconds < 5) Thread.Sleep(10);
            }

            bool Wait(int ms, Func<bool> cut)
            {
                DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
                while (true)
                {
                    if (!Alive || cut()) return false;
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return true;
                    Thread.Sleep(Math.Min(left, 15));
                }
            }

            void Down(string id)
            {
                lock (mp.sync) { if (!Alive) return; mp.Hold(id); own.Add(id); }
            }

            void Up(string id)
            {
                lock (mp.sync)
                {
                    if (!Alive) return;
                    int i = own.LastIndexOf(id);
                    if (i < 0) return;      // an "up" without our own "down" (e.g. recording started with a key held)
                    own.RemoveAt(i);
                    mp.Unhold(id);
                }
            }

            void Tap(List<string> ids)
            {
                foreach (string id in ids) Down(id);
                Thread.Sleep(mp.TapMs);
                for (int i = ids.Count - 1; i >= 0; i--) Up(ids[i]);
            }

            void Exec(Step s)
            {
                switch (s.T)
                {
                    case "key": if (s.Down) Down("k" + s.Code); else Up("k" + s.Code); break;
                    case "tap": Tap(Ids("k", s.Keys)); break;
                    case "mbtn": if (s.Down) Down("m" + s.Button); else Up("m" + s.Button); break;
                    case "click":
                        Tap(new List<string> { "m" + s.Button });
                        if (s.Double) { Thread.Sleep(60); Tap(new List<string> { "m" + s.Button }); }
                        break;
                    case "wheel": mp.o.Wheel(s.Dir, s.Ticks); break;
                    case "move": mp.o.Move(s.Dx, s.Dy); break;
                    case "text": mp.o.Text(s.Text); break;
                    case "media": Tap(new List<string> { "v" + mediaVk[s.Action] }); break;
                    case "system": mp.RunSystem(s.Action, gen); break;
                    case "app": mp.o.Exec("app", s.Path, s.Args, s.WorkDir); break;
                    case "web": mp.o.Exec("web", s.Url, null, null); break;
                    case "open": mp.o.Exec("open", s.Path, null, null); break;
                    case "audio": mp.o.Exec("audio", s.Action, null, null); break;
                    case "sound": lock (mp.sync) mp.soundUsed = true; mp.o.Exec("sound", "once", key + "|macro", s.Path); break;
                    case "pad": if (s.Down) Down("p" + s.Control); else Up("p" + s.Control); break;
                    case "padaxis": lock (mp.sync) { if (!Alive) return; mp.padAxesUsed = true; } mp.o.PadAxis(s.Axis, s.Value); break;
                }
            }
        }
    }
}
