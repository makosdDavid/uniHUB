using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsbMapper
{
    // Records a macro with real timing (G HUB "RECORD KEYSTROKES", extended to every input):
    // keyboard and mouse through low-level hooks, game controllers through Raw Input. Controller input is
    // stored as virtual Xbox 360 steps so it can be played back through ViGEm.
    // Start/Stop must run on the UI thread (the hooks need its message loop).
    sealed class Recorder
    {
        public sealed class Options { public bool Keyboard = true, Mouse = true, Move = true, Pads = true; }

        // Controller → virtual Xbox 360 controller.
        static readonly string[] buttonMap = { null, "A", "B", "X", "Y", "LB", "RB", "BACK", "START", "LS", "RS", "GUIDE" };

        readonly object sync;
        readonly Logger log;
        readonly List<Step> steps = new List<Step>();
        readonly Stopwatch clock = new Stopwatch();
        readonly HashSet<int> keysDown = new HashSet<int>();
        readonly Dictionary<string, HashSet<string>> dpad = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, long> axisLast = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<string, int> axisPending = new Dictionary<string, int>(StringComparer.Ordinal);
        Options opt = new Options();
        Native.HookProc kbProc, msProc;
        IntPtr kbHook, msHook;
        long lastEventMs;
        long lastMoveMs;
        int moveX, moveY, lastX, lastY;
        bool havePoint;
        int skipped;
        volatile bool on;

        public Recorder(object sync, Logger log) { this.sync = sync; this.log = log; kbProc = KbHook; msProc = MsHook; }

        public bool Recording { get { return on; } }
        public int Count { get { lock (sync) return steps.Count; } }

        public void Start(Options o)
        {
            lock (sync)
            {
                if (on) return;
                opt = o;
                steps.Clear(); keysDown.Clear(); dpad.Clear(); axisLast.Clear(); axisPending.Clear();
                moveX = moveY = 0; havePoint = false; skipped = 0;
                clock.Restart();
                lastEventMs = -1;
                lastMoveMs = 0;
                on = true;
            }
            IntPtr mod = Native.GetModuleHandle(null);
            if (opt.Keyboard) kbHook = Native.SetWindowsHookEx(13, kbProc, mod, 0);
            if (opt.Mouse || opt.Move) msHook = Native.SetWindowsHookEx(14, msProc, mod, 0);
            log.Add("Makrófelvétel indul (" + Describe(opt) + ").");
        }

        static string Describe(Options o)
        {
            List<string> p = new List<string>();
            if (o.Keyboard) p.Add("billentyűzet");
            if (o.Mouse) p.Add("egérgombok és görgő");
            if (o.Move) p.Add("egérmozgás");
            if (o.Pads) p.Add("játékvezérlők");
            return string.Join(", ", p.ToArray());
        }

        public List<Step> Stop()
        {
            if (kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; }
            if (msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
            lock (sync)
            {
                if (!on) return new List<Step>();
                on = false;
                FlushMove();
                foreach (KeyValuePair<string, int> kv in new List<KeyValuePair<string, int>>(axisPending)) AddAxis(kv.Key, kv.Value);
                List<Step> result = Trim(new List<Step>(steps));
                log.Add("Makrófelvétel kész: " + result.Count + " lépés" + (skipped > 0 ? ", " + skipped + " nem visszajátszható bemenet kihagyva" : "") + ".");
                steps.Clear();
                return result;
            }
        }

        // The clicks on the "start" and "stop" buttons of the settings page are not part of the macro.
        static List<Step> Trim(List<Step> s)
        {
            while (s.Count > 0 && (s[0].T == "move" || s[0].T == "delay" || (s[0].T == "mbtn" && !s[0].Down))) s.RemoveAt(0);
            int end = s.Count;
            int lastDown = -1;
            for (int i = s.Count - 1; i >= 0; i--)
            {
                if (s[i].T == "mbtn" && s[i].Button == "left" && s[i].Down) { lastDown = i; break; }
                if (s[i].T != "mbtn" && s[i].T != "move" && s[i].T != "delay") break;
            }
            if (lastDown >= 0) end = lastDown;
            while (end > 0 && (s[end - 1].T == "move" || s[end - 1].T == "delay")) end--;
            return s.GetRange(0, end);
        }

        long Now() { return clock.ElapsedMilliseconds; }

        void Add(Step s)
        {
            long now = Now();
            if (lastEventMs >= 0)
            {
                long gap = now - lastEventMs;
                if (gap > 0) steps.Add(new Step { T = "delay", Ms = (int)Math.Min(gap, 3600000) });
            }
            lastEventMs = now;
            if (steps.Count < 20000) steps.Add(s);
        }

        void FlushMove()
        {
            if (moveX == 0 && moveY == 0) return;
            int dx = moveX, dy = moveY;
            moveX = moveY = 0;
            Add(new Step { T = "move", Dx = Math.Max(-10000, Math.Min(10000, dx)), Dy = Math.Max(-10000, Math.Min(10000, dy)) });
        }

        IntPtr KbHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && on)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo != Out.Marker)
                {
                    bool up = (k.flags & 0x80) != 0;
                    int c = (int)(k.scanCode & 0x7F) | ((k.flags & 1) != 0 ? 0x80 : 0);
                    lock (sync)
                    {
                        if (on)
                        {
                            if (!Keys.IsKnown(c)) skipped++;
                            else if (up ? keysDown.Remove(c) : keysDown.Add(c))   // key repeat is not recorded
                            {
                                FlushMove();
                                Add(new Step { T = "key", Code = c, Down = !up });
                            }
                        }
                    }
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        IntPtr MsHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && on)
            {
                Native.MSLLHOOKSTRUCT m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if (m.dwExtraInfo != Out.Marker)
                {
                    int msg = wParam.ToInt32();
                    lock (sync) { if (on) Mouse(msg, m); }
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        void Mouse(int msg, Native.MSLLHOOKSTRUCT m)
        {
            if (msg == 0x0200)   // move
            {
                if (havePoint) { moveX += m.x - lastX; moveY += m.y - lastY; }
                lastX = m.x; lastY = m.y; havePoint = true;
                if (opt.Move && Now() - lastMoveMs >= 15) { lastMoveMs = Now(); FlushMove(); }
                if (!opt.Move) moveX = moveY = 0;
                return;
            }
            if (!opt.Mouse) return;
            string btn = null; bool down = false;
            switch (msg)
            {
                case 0x0201: btn = "left"; down = true; break;
                case 0x0202: btn = "left"; break;
                case 0x0204: btn = "right"; down = true; break;
                case 0x0205: btn = "right"; break;
                case 0x0207: btn = "middle"; down = true; break;
                case 0x0208: btn = "middle"; break;
                case 0x020B: btn = ((m.mouseData >> 16) & 0xFFFF) == 1 ? "x1" : "x2"; down = true; break;
                case 0x020C: btn = ((m.mouseData >> 16) & 0xFFFF) == 1 ? "x1" : "x2"; break;
                case 0x020A:
                case 0x020E:
                    {
                        short delta = (short)((m.mouseData >> 16) & 0xFFFF);
                        int ticks = Math.Max(1, Math.Abs(delta) / 120);
                        string dir = msg == 0x020A ? (delta > 0 ? "up" : "down") : (delta > 0 ? "right" : "left");
                        if (opt.Move) FlushMove(); else moveX = moveY = 0;
                        Add(new Step { T = "wheel", Dir = dir, Ticks = ticks });
                        return;
                    }
            }
            if (btn == null) return;
            if (opt.Move) FlushMove(); else moveX = moveY = 0;
            Add(new Step { T = "mbtn", Button = btn, Down = down });
        }

        // ---------- controllers (called under the shared lock from the Raw Input thread) ----------
        public void OnButton(string device, string input, bool down)
        {
            if (!on || !opt.Pads) return;
            int n = int.Parse(input.Substring(1));
            if (n >= buttonMap.Length || buttonMap[n] == null) { skipped++; return; }
            FlushMove();
            Add(new Step { T = "pad", Control = buttonMap[n], Down = down });
        }

        public void OnHat(string device, int hat, int dir)
        {
            if (!on || !opt.Pads) return;
            if (hat != 1) { skipped++; return; }
            HashSet<string> now = new HashSet<string>(StringComparer.Ordinal);
            if (dir >= 0)
            {
                if (dir == 7 || dir == 0 || dir == 1) now.Add("UP");
                if (dir >= 1 && dir <= 3) now.Add("RIGHT");
                if (dir >= 3 && dir <= 5) now.Add("DOWN");
                if (dir >= 5 && dir <= 7) now.Add("LEFT");
            }
            HashSet<string> before;
            if (!dpad.TryGetValue(device, out before)) before = new HashSet<string>(StringComparer.Ordinal);
            FlushMove();
            foreach (string c in before) if (!now.Contains(c)) Add(new Step { T = "pad", Control = c, Down = false });
            foreach (string c in now) if (!before.Contains(c)) Add(new Step { T = "pad", Control = c, Down = true });
            dpad[device] = now;
        }

        public void OnAxis(string device, string axis, double v)
        {
            if (!on || !opt.Pads) return;
            string target; int value;
            switch (axis)
            {
                case "X": target = "LX"; value = (int)Math.Round(v * 32767); break;
                case "Y": target = "LY"; value = (int)Math.Round(-v * 32767); break;
                case "RX": target = "RX"; value = (int)Math.Round(v * 32767); break;
                case "RY": target = "RY"; value = (int)Math.Round(-v * 32767); break;
                case "RZ": target = "RX"; value = (int)Math.Round(v * 32767); break;
                case "Z": target = "LT"; value = (int)Math.Round((v + 1) / 2 * 255); break;
                case "SLIDER": target = "RT"; value = (int)Math.Round(v * 255); break;
                case "DIAL": target = "LT"; value = (int)Math.Round(v * 255); break;
                default: return;
            }
            long last;
            if (axisLast.TryGetValue(target, out last) && Now() - last < 15) { axisPending[target] = value; return; }
            AddAxis(target, value);
        }

        void AddAxis(string target, int value)
        {
            axisPending.Remove(target);
            axisLast[target] = Now();
            FlushMove();
            Add(new Step { T = "padaxis", Axis = target, Value = value });
        }
    }
}
