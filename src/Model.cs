using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UsbMapper
{
    // The configuration model follows G HUB's own (logi.protocol.macros in lghub_agent.exe):
    // an input gets one assignment (keystroke, text, mouse, media, system, app, web page, file/folder,
    // audio, audio sample, gamepad, or a named macro). A macro has a type (no repeat, repeat while
    // holding, toggle, sequence with press / while-holding / release parts), standard or recorded
    // delays, and an "interruptible" flag (G HUB default: not interruptible).
    static class Vocab
    {
        public static readonly string[] BindingTypes = { "keys", "macro", "text", "mouse", "media", "system", "app", "web", "open", "audio", "sound", "pad" };
        public static readonly string[] StepTypes = { "key", "tap", "delay", "text", "mbtn", "click", "wheel", "move", "media", "system", "app", "web", "open", "audio", "sound", "pad", "padaxis" };
        public static readonly string[] MouseActions = { "left", "right", "middle", "x1", "x2", "double_left", "double_right", "double_middle", "wheel_up", "wheel_down", "wheel_left", "wheel_right" };
        public static readonly string[] MouseButtons = { "left", "right", "middle", "x1", "x2" };
        public static readonly string[] WheelDirs = { "up", "down", "left", "right" };
        public static readonly string[] Media = { "play_pause", "stop", "next", "prev", "vol_up", "vol_down", "mute" };
        public static readonly string[] SystemActions = { "close", "minimize", "maximize", "fullscreen", "copy", "cut", "paste", "undo", "redo", "select_all",
            "desktop", "task_view", "switch_apps", "shift_switch_apps", "snap_left", "snap_right", "desktop_next", "desktop_prev",
            "zoom_in", "zoom_out", "scroll_left", "scroll_right", "screen_capture", "lock", "app_menu",
            "file_client", "internet_client", "mail_client", "media_client", "calculator", "brightness_up", "brightness_down" };
        public static readonly string[] Audio = { "mute_speakers", "mute_mic", "mic_up", "mic_down", "out_cycle", "in_cycle" };
        public static readonly string[] Play = { "once", "hold", "toggle" };
        public static readonly string[] Modes = { "once", "repeat", "toggle", "sequence" };
        public static readonly string[] Repeat = { "once", "hold" };

        public static bool In(string[] set, string v) { return v != null && Array.IndexOf(set, v) >= 0; }
    }

    // One step of a macro (G HUB: Macro.Sequence.Component).
    sealed class Step
    {
        public string T;
        public int Code; public bool Down; public int[] Keys; public int Ms;
        public string Text; public string Button; public bool Double; public string Dir; public int Ticks;
        public int Dx, Dy; public string Action; public string Path, Args, WorkDir, Url; public string Control, Axis; public int Value;
    }

    sealed class Macro
    {
        public string Id, Name, Mode = "once";
        public bool Interruptible;
        public bool UseDefaultDelay = true;
        public int DefaultDelay = 50;
        public List<Step> Simple = new List<Step>(), Press = new List<Step>(), Held = new List<Step>(), Release = new List<Step>();
    }

    sealed class Binding
    {
        public string Device;   // "VVVV:PPPP"
        public string Input;
        public string Type;
        public int[] Keys;
        public string MacroId;
        public string Text, Action, Repeat = "once", Path, Args, WorkDir, Url, Play, Control;
    }

    sealed class Profile
    {
        public string Id;
        public string Name;
        public List<string> Exes = new List<string>();
        public List<Binding> Bindings = new List<Binding>();
        readonly Dictionary<string, Binding> index = new Dictionary<string, Binding>(StringComparer.Ordinal);

        public void Index()
        {
            index.Clear();
            foreach (Binding b in Bindings) index[b.Device + "|" + b.Input] = b;
        }

        public Binding Find(string device, string input)
        {
            Binding b;
            return index.TryGetValue(device + "|" + input, out b) ? b : null;
        }
    }

    sealed class AppConfig
    {
        public const int DefaultPort = 47640;
        public int Port = DefaultPort;
        public List<Profile> Profiles = new List<Profile>();
        public List<Macro> Macros = new List<Macro>();
        public SortedDictionary<string, string> DeviceNames = new SortedDictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, Macro> macroIndex = new Dictionary<string, Macro>(StringComparer.Ordinal);

        static readonly Regex deviceRe = new Regex(@"^[0-9A-F]{4}:[0-9A-F]{4}$", RegexOptions.CultureInvariant);
        static readonly Regex idRe = new Regex(@"^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
        static readonly Regex exeRe = new Regex("^[^\\\\/:*?\"<>|\\x00-\\x1f]{1,96}\\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly Regex pathRe = new Regex("^(?:[A-Za-z]:\\\\|\\\\\\\\[^\\\\/:*?\"<>|\\x00-\\x1f]+\\\\)[^/:*?\"<>|\\x00-\\x1f]{0,255}$", RegexOptions.CultureInvariant);
        static readonly Regex urlRe = new Regex("^https?://[^\\s<>\"\\x00-\\x1f]{1,2000}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly Regex soundRe = new Regex("\\.(wav|mp3|wma)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        const int MaxSteps = 20000;

        public static AppConfig CreateDefault()
        {
            AppConfig c = new AppConfig();
            Profile p = new Profile();
            p.Id = "default";
            p.Name = "Alap (minden más program)";
            p.Index();
            c.Profiles.Add(p);
            return c;
        }

        public Profile ForExe(string exe)
        {
            if (!string.IsNullOrEmpty(exe))
            {
                for (int i = 1; i < Profiles.Count; i++)
                    foreach (string e in Profiles[i].Exes)
                        if (string.Equals(e, exe, StringComparison.OrdinalIgnoreCase)) return Profiles[i];
            }
            return Profiles[0];
        }

        public Profile ById(string id)
        {
            foreach (Profile p in Profiles) if (p.Id == id) return p;
            return null;
        }

        public Macro MacroById(string id)
        {
            Macro m;
            return id != null && macroIndex.TryGetValue(id, out m) ? m : null;
        }

        void IndexMacros()
        {
            macroIndex.Clear();
            foreach (Macro m in Macros) macroIndex[m.Id] = m;
        }

        // ---------- JSON out ----------
        public JObj ToJson()
        {
            List<object> macros = new List<object>();
            foreach (Macro m in Macros)
                macros.Add(new JObj().Set("id", m.Id).Set("name", m.Name).Set("mode", m.Mode).Set("interruptible", m.Interruptible)
                    .Set("useDefaultDelay", m.UseDefaultDelay).Set("defaultDelay", m.DefaultDelay)
                    .Set("simple", StepsJson(m.Simple)).Set("press", StepsJson(m.Press)).Set("held", StepsJson(m.Held)).Set("release", StepsJson(m.Release)));
            List<object> profiles = new List<object>();
            foreach (Profile p in Profiles)
            {
                List<object> bindings = new List<object>();
                foreach (Binding b in p.Bindings) bindings.Add(BindingJson(b));
                profiles.Add(new JObj().Set("id", p.Id).Set("name", p.Name).Set("exes", new List<string>(p.Exes)).Set("bindings", bindings));
            }
            JObj names = new JObj();
            foreach (KeyValuePair<string, string> kv in DeviceNames) names.Set(kv.Key, kv.Value);
            return new JObj().Set("version", 2).Set("port", Port).Set("deviceNames", names).Set("macros", macros).Set("profiles", profiles);
        }

        static JObj BindingJson(Binding b)
        {
            JObj o = new JObj().Set("device", b.Device).Set("input", b.Input).Set("type", b.Type);
            switch (b.Type)
            {
                case "keys": o.Set("keys", new List<int>(b.Keys)); break;
                case "macro": o.Set("macro", b.MacroId); break;
                case "text": o.Set("text", b.Text); break;
                case "mouse": o.Set("action", b.Action).Set("repeat", b.Repeat); break;
                case "media": case "system": case "audio": o.Set("action", b.Action); break;
                case "app": o.Set("path", b.Path).Set("args", b.Args ?? "").Set("dir", b.WorkDir ?? ""); break;
                case "web": o.Set("url", b.Url); break;
                case "open": o.Set("path", b.Path); break;
                case "sound": o.Set("path", b.Path).Set("play", b.Play); break;
                case "pad": o.Set("control", b.Control); break;
            }
            return o;
        }

        public static List<object> StepsJson(List<Step> steps)
        {
            List<object> list = new List<object>();
            foreach (Step s in steps)
            {
                JObj o = new JObj().Set("t", s.T);
                switch (s.T)
                {
                    case "key": o.Set("code", s.Code).Set("down", s.Down); break;
                    case "tap": o.Set("keys", new List<int>(s.Keys)); break;
                    case "delay": o.Set("ms", s.Ms); break;
                    case "text": o.Set("text", s.Text); break;
                    case "mbtn": o.Set("button", s.Button).Set("down", s.Down); break;
                    case "click": o.Set("button", s.Button).Set("double", s.Double); break;
                    case "wheel": o.Set("dir", s.Dir).Set("ticks", s.Ticks); break;
                    case "move": o.Set("dx", s.Dx).Set("dy", s.Dy); break;
                    case "media": case "system": case "audio": o.Set("action", s.Action); break;
                    case "app": o.Set("path", s.Path).Set("args", s.Args ?? "").Set("dir", s.WorkDir ?? ""); break;
                    case "web": o.Set("url", s.Url); break;
                    case "open": case "sound": o.Set("path", s.Path); break;
                    case "pad": o.Set("control", s.Control).Set("down", s.Down); break;
                    case "padaxis": o.Set("axis", s.Axis).Set("value", s.Value); break;
                }
                list.Add(o);
            }
            return list;
        }

        // ---------- JSON in, with strict validation ----------
        public static AppConfig FromJson(object root)
        {
            Dictionary<string, object> r = Obj(root, "A beállítás");
            Only(r, "A beállítás", "version", "port", "deviceNames", "macros", "profiles");
            int version = Int(Need(r, "version", "A beállítás"), "A verzió", 1, 2);
            AppConfig c = new AppConfig();
            c.Port = Int(Need(r, "port", "A beállítás"), "A port", 1024, 65535);

            object namesObj;
            if (r.TryGetValue("deviceNames", out namesObj) && namesObj != null)
            {
                Dictionary<string, object> names = Obj(namesObj, "Az eszköznevek");
                if (names.Count > 100) throw new ConfigException("Túl sok eszköznév.");
                foreach (KeyValuePair<string, object> kv in names)
                {
                    if (!deviceRe.IsMatch(kv.Key)) throw new ConfigException("Érvénytelen eszközazonosító: " + kv.Key);
                    c.DeviceNames[kv.Key] = Text(kv.Value, "Az eszköz neve", 100);
                }
            }

            object macrosObj;
            if (r.TryGetValue("macros", out macrosObj) && macrosObj != null)
            {
                object[] ms = Arr(macrosObj, "A makrólista");
                if (ms.Length > 500) throw new ConfigException("Legfeljebb 500 makró lehet.");
                foreach (object mo in ms)
                {
                    Macro m = ParseMacro(mo);
                    if (c.MacroById(m.Id) != null) throw new ConfigException("Két makrónak ugyanaz az azonosítója: " + m.Id);
                    c.Macros.Add(m);
                    c.IndexMacros();
                }
            }

            object[] profiles = Arr(Need(r, "profiles", "A beállítás"), "A profillista");
            if (profiles.Length < 1 || profiles.Length > 50) throw new ConfigException("A profilok száma 1 és 50 között lehet.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> exeOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int pi = 0; pi < profiles.Length; pi++)
            {
                Dictionary<string, object> po = Obj(profiles[pi], "A profil");
                Only(po, "A profil", "id", "name", "exes", "bindings");
                Profile p = new Profile();
                p.Id = Str(Need(po, "id", "A profil"), "A profilazonosító");
                if (!idRe.IsMatch(p.Id)) throw new ConfigException("Érvénytelen profilazonosító: " + p.Id);
                if (!ids.Add(p.Id)) throw new ConfigException("Két profilnak ugyanaz az azonosítója: " + p.Id);
                p.Name = Text(Need(po, "name", "A profil"), "A profil neve", 60);
                string where = "„" + p.Name + "” profil";
                if (pi == 0 && p.Id != "default") throw new ConfigException("Az első profil az alap profil kell legyen.");
                if (pi > 0 && p.Id == "default") throw new ConfigException("Csak az első profil lehet az alap profil.");

                object[] exes = Arr(Need(po, "exes", where), where + " programlistája");
                if (exes.Length > 30) throw new ConfigException(where + ": legfeljebb 30 program adható meg.");
                if (pi == 0 && exes.Length > 0) throw new ConfigException("Az alap profilhoz nem tartozhat program: az mindenre érvényes, amihez nincs saját profil.");
                foreach (object eo in exes)
                {
                    string exe = Str(eo, where + " programneve").Trim();
                    if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                    if (!exeRe.IsMatch(exe)) throw new ConfigException(where + ": érvénytelen programnév: " + exe);
                    string owner;
                    if (exeOwner.TryGetValue(exe, out owner))
                        throw new ConfigException("A(z) " + exe + " két profilban is szerepel (" + owner + ", " + p.Name + "). Egy program csak egy profilhoz tartozhat.");
                    exeOwner[exe] = p.Name;
                    p.Exes.Add(exe);
                }

                object[] bindings = Arr(Need(po, "bindings", where), where + " hozzárendelései");
                if (bindings.Length > 2000) throw new ConfigException(where + ": túl sok hozzárendelés.");
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object bo in bindings) p.Bindings.Add(ParseBinding(bo, where, seen, c, version));
                p.Index();
                c.Profiles.Add(p);
            }
            c.IndexMacros();
            return c;
        }

        static Macro ParseMacro(object mo)
        {
            Dictionary<string, object> d = Obj(mo, "A makró");
            Only(d, "A makró", "id", "name", "mode", "interruptible", "useDefaultDelay", "defaultDelay", "simple", "press", "held", "release");
            Macro m = new Macro();
            m.Id = Str(Need(d, "id", "A makró"), "A makró azonosítója");
            if (!idRe.IsMatch(m.Id)) throw new ConfigException("Érvénytelen makróazonosító: " + m.Id);
            m.Name = Text(Need(d, "name", "A makró"), "A makró neve", 80);
            string where = "„" + m.Name + "” makró";
            m.Mode = Str(Need(d, "mode", where), where + " típusa");
            if (!Vocab.In(Vocab.Modes, m.Mode)) throw new ConfigException(where + ": ismeretlen típus: " + m.Mode);
            m.Interruptible = Bool(Need(d, "interruptible", where), where + ": megszakítható");
            m.UseDefaultDelay = Bool(Need(d, "useDefaultDelay", where), where + ": alap késleltetés");
            m.DefaultDelay = Int(Need(d, "defaultDelay", where), where + ": az alap késleltetés", 0, 60000);
            m.Simple = ParseSteps(Need(d, "simple", where), where + ", lépések");
            m.Press = ParseSteps(Need(d, "press", where), where + ", lenyomáskor");
            m.Held = ParseSteps(Need(d, "held", where), where + ", nyomva tartás alatt");
            m.Release = ParseSteps(Need(d, "release", where), where + ", felengedéskor");
            if (m.Mode == "sequence") { if (m.Press.Count + m.Held.Count + m.Release.Count == 0) throw new ConfigException(where + ": a sorozatban legalább egy lépés kell."); }
            else if (m.Simple.Count == 0) throw new ConfigException(where + ": legalább egy lépés kell.");
            if (m.Mode == "repeat" || m.Mode == "toggle")
            {
                bool anyWait = m.UseDefaultDelay && m.DefaultDelay > 0;
                foreach (Step s in m.Simple) if (s.T == "delay" && s.Ms > 0) anyWait = true;
                if (!anyWait) throw new ConfigException(where + ": ismétlődő makróban kell késleltetés (alap késleltetés vagy várakozás lépés), különben folyamatosan pörögne.");
            }
            return m;
        }

        static List<Step> ParseSteps(object o, string where)
        {
            object[] a = Arr(o, where);
            if (a.Length > MaxSteps) throw new ConfigException(where + ": legfeljebb " + MaxSteps + " lépés lehet.");
            List<Step> list = new List<Step>(a.Length);
            for (int i = 0; i < a.Length; i++) list.Add(ParseStep(a[i], where + " " + (i + 1) + "."));
            return list;
        }

        static Step ParseStep(object o, string w)
        {
            Dictionary<string, object> d = Obj(o, w);
            Step s = new Step();
            s.T = Str(Need(d, "t", w), w + " típusa");
            switch (s.T)
            {
                case "key": Only(d, w, "t", "code", "down"); s.Code = KeyCode(Need(d, "code", w), w); s.Down = Bool(Need(d, "down", w), w); break;
                case "tap": Only(d, w, "t", "keys"); s.Keys = KeyList(Need(d, "keys", w), w); break;
                case "delay": Only(d, w, "t", "ms"); s.Ms = Int(Need(d, "ms", w), w + ": a várakozás", 0, 3600000); break;
                case "text": Only(d, w, "t", "text"); s.Text = LongText(Need(d, "text", w), w); break;
                case "mbtn": Only(d, w, "t", "button", "down"); s.Button = OneOf(Need(d, "button", w), Vocab.MouseButtons, w); s.Down = Bool(Need(d, "down", w), w); break;
                case "click": Only(d, w, "t", "button", "double"); s.Button = OneOf(Need(d, "button", w), Vocab.MouseButtons, w); s.Double = Bool(Need(d, "double", w), w); break;
                case "wheel": Only(d, w, "t", "dir", "ticks"); s.Dir = OneOf(Need(d, "dir", w), Vocab.WheelDirs, w); s.Ticks = Int(Need(d, "ticks", w), w + ": kattanás", 1, 100); break;
                case "move": Only(d, w, "t", "dx", "dy"); s.Dx = Int(Need(d, "dx", w), w + ": dx", -10000, 10000); s.Dy = Int(Need(d, "dy", w), w + ": dy", -10000, 10000); break;
                case "media": Only(d, w, "t", "action"); s.Action = OneOf(Need(d, "action", w), Vocab.Media, w); break;
                case "system": Only(d, w, "t", "action"); s.Action = OneOf(Need(d, "action", w), Vocab.SystemActions, w); break;
                case "audio": Only(d, w, "t", "action"); s.Action = OneOf(Need(d, "action", w), Vocab.Audio, w); break;
                case "app": Only(d, w, "t", "path", "args", "dir"); s.Path = PathStr(Need(d, "path", w), w); s.Args = Opt(d, "args", w, 2000); s.WorkDir = OptPath(d, "dir", w); break;
                case "web": Only(d, w, "t", "url"); s.Url = Url(Need(d, "url", w), w); break;
                case "open": Only(d, w, "t", "path"); s.Path = PathStr(Need(d, "path", w), w); break;
                case "sound": Only(d, w, "t", "path"); s.Path = SoundPath(Need(d, "path", w), w); break;
                case "pad": Only(d, w, "t", "control", "down"); s.Control = OneOf(Need(d, "control", w), VirtualPad.Controls, w); s.Down = Bool(Need(d, "down", w), w); break;
                case "padaxis":
                    Only(d, w, "t", "axis", "value");
                    s.Axis = OneOf(Need(d, "axis", w), VirtualPad.Axes, w);
                    s.Value = (s.Axis == "LT" || s.Axis == "RT") ? Int(Need(d, "value", w), w + ": érték", 0, 255) : Int(Need(d, "value", w), w + ": érték", -32768, 32767);
                    break;
                default: throw new ConfigException(w + ": ismeretlen lépéstípus: " + s.T);
            }
            return s;
        }

        static Binding ParseBinding(object bo, string where, HashSet<string> seen, AppConfig c, int version)
        {
            Dictionary<string, object> d = Obj(bo, where + " hozzárendelése");
            Binding b = new Binding();
            b.Device = Str(Need(d, "device", where), "Az eszközazonosító");
            if (!deviceRe.IsMatch(b.Device)) throw new ConfigException(where + ": érvénytelen eszközazonosító: " + b.Device);
            b.Input = Str(Need(d, "input", where), "A bemenet");
            if (!Inputs.Valid(b.Input)) throw new ConfigException(where + ": érvénytelen bemenet: " + b.Input);
            string dev;
            string w = where + ", " + (c.DeviceNames.TryGetValue(b.Device, out dev) ? dev : b.Device) + ", " + Inputs.Name(b.Input);
            if (!seen.Add(b.Device + "|" + b.Input)) throw new ConfigException(w + ": kétszer szerepel.");
            b.Type = Str(Need(d, "type", w), "A típus");
            switch (b.Type)
            {
                case "keys": Only(d, w, "device", "input", "type", "keys"); b.Keys = KeyList(Need(d, "keys", w), w); break;
                case "macro":
                    if (version == 1 && d.ContainsKey("steps")) { b.MacroId = ConvertV1Macro(d, w, c); break; }
                    Only(d, w, "device", "input", "type", "macro");
                    b.MacroId = Str(Need(d, "macro", w), w + ": makró");
                    if (c.MacroById(b.MacroId) == null) throw new ConfigException(w + ": nincs ilyen makró: " + b.MacroId);
                    break;
                case "text": Only(d, w, "device", "input", "type", "text"); b.Text = LongText(Need(d, "text", w), w); break;
                case "mouse":
                    Only(d, w, "device", "input", "type", "action", "repeat");
                    b.Action = OneOf(Need(d, "action", w), Vocab.MouseActions, w);
                    b.Repeat = OneOf(Need(d, "repeat", w), Vocab.Repeat, w);
                    break;
                case "media": Only(d, w, "device", "input", "type", "action"); b.Action = OneOf(Need(d, "action", w), Vocab.Media, w); break;
                case "system": Only(d, w, "device", "input", "type", "action"); b.Action = OneOf(Need(d, "action", w), Vocab.SystemActions, w); break;
                case "audio": Only(d, w, "device", "input", "type", "action"); b.Action = OneOf(Need(d, "action", w), Vocab.Audio, w); break;
                case "app": Only(d, w, "device", "input", "type", "path", "args", "dir"); b.Path = PathStr(Need(d, "path", w), w); b.Args = Opt(d, "args", w, 2000); b.WorkDir = OptPath(d, "dir", w); break;
                case "web": Only(d, w, "device", "input", "type", "url"); b.Url = Url(Need(d, "url", w), w); break;
                case "open": Only(d, w, "device", "input", "type", "path"); b.Path = PathStr(Need(d, "path", w), w); break;
                case "sound": Only(d, w, "device", "input", "type", "path", "play"); b.Path = SoundPath(Need(d, "path", w), w); b.Play = OneOf(Need(d, "play", w), Vocab.Play, w); break;
                case "pad": Only(d, w, "device", "input", "type", "control"); b.Control = OneOf(Need(d, "control", w), VirtualPad.Controls, w); break;
                default: throw new ConfigException(w + ": ismeretlen típus: " + b.Type);
            }
            return b;
        }

        // Version 1 kept macros inside the binding; they become named macros.
        static string ConvertV1Macro(Dictionary<string, object> d, string w, AppConfig c)
        {
            Only(d, w, "device", "input", "type", "steps", "gap", "hold");
            Macro m = new Macro();
            m.Id = "m-v1-" + (c.Macros.Count + 1);
            m.Name = ("Makró: " + w).Length > 80 ? ("Makró: " + w).Substring(0, 80) : "Makró: " + w;
            m.UseDefaultDelay = true;
            m.DefaultDelay = Int(Need(d, "gap", w), w + ": szünet", 0, 5000);
            foreach (object so in Arr(Need(d, "steps", w), w))
            {
                Dictionary<string, object> sd = Obj(so, w);
                Step s = new Step();
                if (Str(Need(sd, "type", w), w) == "tap") { s.T = "tap"; s.Keys = KeyList(Need(sd, "keys", w), w); }
                else { s.T = "delay"; s.Ms = Int(Need(sd, "ms", w), w, 0, 60000); }
                m.Simple.Add(s);
            }
            c.Macros.Add(m);
            c.IndexMacros();
            return m.Id;
        }

        // ---------- typed accessors ----------
        static int KeyCode(object o, string w)
        {
            int k = Int(o, w + ": billentyűkód", 1, 255);
            if (!Keys.IsKnown(k)) throw new ConfigException(w + ": ismeretlen billentyűkód: " + k);
            return k;
        }
        static int[] KeyList(object o, string label)
        {
            object[] a = Arr(o, label + " billentyűi");
            if (a.Length < 1) throw new ConfigException(label + ": legalább egy billentyű kell.");
            if (a.Length > 6) throw new ConfigException(label + ": legfeljebb 6 billentyű lehet egyszerre.");
            int[] keys = new int[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                keys[i] = Int(a[i], label + ": billentyűkód", 1, 255);
                if (!Keys.IsKnown(keys[i])) throw new ConfigException(label + ": ismeretlen billentyűkód: " + keys[i]);
                for (int j = 0; j < i; j++) if (keys[j] == keys[i]) throw new ConfigException(label + ": egy billentyű kétszer szerepel.");
            }
            return keys;
        }
        static string OneOf(object o, string[] set, string w)
        {
            string s = Str(o, w);
            if (!Vocab.In(set, s)) throw new ConfigException(w + ": ismeretlen érték: " + s);
            return s;
        }
        static string LongText(object o, string w)
        {
            string s = Str(o, w);
            if (s.Length < 1 || s.Length > 4000) throw new ConfigException(w + ": a szöveg 1 és 4000 karakter közötti lehet.");
            foreach (char ch in s) if (char.IsControl(ch) && ch != '\n' && ch != '\t' && ch != '\r') throw new ConfigException(w + ": a szövegben vezérlőkarakter van.");
            return s;
        }
        static string PathStr(object o, string w)
        {
            string s = Str(o, w).Trim();
            if (!pathRe.IsMatch(s)) throw new ConfigException(w + ": teljes elérési út kell (pl. C:\\Games\\jatek.exe): " + s);
            return s;
        }
        static string SoundPath(object o, string w)
        {
            string s = PathStr(o, w);
            if (!soundRe.IsMatch(s)) throw new ConfigException(w + ": csak .wav, .mp3 vagy .wma fájl lehet.");
            return s;
        }
        static string OptPath(Dictionary<string, object> d, string key, string w)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null || (v is string && ((string)v).Trim() == "")) return "";
            return PathStr(v, w + " (munkakönyvtár)");
        }
        static string Opt(Dictionary<string, object> d, string key, string w, int max)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            string s = Str(v, w);
            if (s.Length > max) throw new ConfigException(w + ": legfeljebb " + max + " karakter lehet.");
            foreach (char ch in s) if (char.IsControl(ch)) throw new ConfigException(w + ": vezérlőkaraktert tartalmaz.");
            return s;
        }
        static string Url(object o, string w)
        {
            string s = Str(o, w).Trim();
            if (!urlRe.IsMatch(s)) throw new ConfigException(w + ": http:// vagy https:// kezdetű cím kell.");
            return s;
        }
        static bool Bool(object o, string w)
        {
            if (!(o is bool)) throw new ConfigException(w + ": igaz/hamis érték kell.");
            return (bool)o;
        }
        static Dictionary<string, object> Obj(object o, string what)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) throw new ConfigException(what + " nem objektum.");
            return d;
        }
        static object[] Arr(object o, string what)
        {
            object[] a = o as object[];
            if (a == null)
            {
                ArrayList al = o as ArrayList;
                if (al == null) throw new ConfigException(what + " nem lista.");
                a = al.ToArray();
            }
            return a;
        }
        static object Need(Dictionary<string, object> d, string key, string where)
        {
            object v;
            if (!d.TryGetValue(key, out v)) throw new ConfigException(where + ": hiányzik a(z) „" + key + "” mező.");
            return v;
        }
        static void Only(Dictionary<string, object> d, string where, params string[] allowed)
        {
            foreach (string k in d.Keys)
                if (Array.IndexOf(allowed, k) < 0) throw new ConfigException(where + ": ismeretlen mező: " + k);
        }
        static string Str(object o, string what)
        {
            string s = o as string;
            if (s == null) throw new ConfigException(what + " nem szöveg.");
            return s;
        }
        static string Text(object o, string what, int max)
        {
            string s = Str(o, what).Trim();
            if (s.Length < 1 || s.Length > max) throw new ConfigException(what + " 1 és " + max + " karakter közötti lehet.");
            foreach (char ch in s) if (char.IsControl(ch)) throw new ConfigException(what + " vezérlőkaraktert tartalmaz.");
            return s;
        }
        static int Int(object o, string what, int min, int max)
        {
            long v;
            if (o is int) v = (int)o;
            else if (o is long) v = (long)o;
            else if (o is decimal && decimal.Truncate((decimal)o) == (decimal)o && Math.Abs((decimal)o) < 1000000000m) v = (long)(decimal)o;
            else throw new ConfigException(what + " egész szám kell legyen.");
            if (v < min || v > max) throw new ConfigException(what + " " + min + " és " + max + " között lehet.");
            return (int)v;
        }
    }
}
