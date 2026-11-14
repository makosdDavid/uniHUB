using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbMapper
{
    // Everything the mapper can do to the outside world. The real implementation talks to Windows,
    // the dry-run one only logs, the self-test one records.
    interface IOutput
    {
        bool Key(int code, bool down);            // DirectInput-style code (scancode, +128 = extended)
        bool Vk(int vk, bool down);               // virtual key (media, launch, layout-independent shortcuts)
        void Text(string text);                   // Unicode characters
        bool Mouse(string button, bool down);     // left, right, middle, x1, x2
        void Wheel(string dir, int ticks);        // up, down, left, right
        void Move(int dx, int dy);
        bool Pad(string control, bool down);      // virtual Xbox 360 controller
        void PadAxis(string axis, int value);
        void Exec(string kind, string a, string b, string c);  // window, lock, brightness, app, web, open, audio, sound
    }

    static class Out
    {
        public static readonly IntPtr Marker = new IntPtr(0x55534D50);   // "USMP": our own injected events
        static readonly int Size = Marshal.SizeOf(typeof(Native.INPUT));
        public static int InputSize { get { return Size; } }

        public static Native.INPUT KeyInput(int code, bool down)
        {
            Native.INPUT inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            inp.ki.wScan = (ushort)(code & 0x7F);
            uint flags = Native.KEYEVENTF_SCANCODE;
            if (code >= 0x80) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (!down) flags |= Native.KEYEVENTF_KEYUP;
            inp.ki.dwFlags = flags;
            inp.ki.dwExtraInfo = Marker;
            return inp;
        }

        public static bool IsExtendedVk(int vk)
        {
            return (vk >= 0x21 && vk <= 0x2E) || (vk >= 0x5B && vk <= 0x5D) || (vk >= 0xA6 && vk <= 0xB7) || vk == 0xA3 || vk == 0xA5 || vk == 0x90 || vk == 0x6F;
        }

        public static Native.INPUT VkInput(int vk, bool down)
        {
            Native.INPUT inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            inp.ki.wVk = (ushort)vk;
            inp.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
            uint flags = 0;
            if (IsExtendedVk(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (!down) flags |= Native.KEYEVENTF_KEYUP;
            inp.ki.dwFlags = flags;
            inp.ki.dwExtraInfo = Marker;
            return inp;
        }

        public static bool Send(params Native.INPUT[] inputs)
        {
            return Native.SendInput((uint)inputs.Length, inputs, Size) == inputs.Length;
        }

        public static Native.INPUT MouseInput(uint flags, int data, int dx, int dy)
        {
            Native.INPUT inp = new Native.INPUT();
            inp.type = 0;   // INPUT_MOUSE
            inp.mi.dx = dx;
            inp.mi.dy = dy;
            inp.mi.mouseData = unchecked((uint)data);
            inp.mi.dwFlags = flags;
            inp.mi.dwExtraInfo = Marker;
            return inp;
        }
    }

    sealed class RealOutput : IOutput
    {
        readonly Logger log;
        readonly VirtualPad pad;
        readonly SoundBank sounds;

        public RealOutput(Logger log, VirtualPad pad, SoundBank sounds) { this.log = log; this.pad = pad; this.sounds = sounds; }

        public bool Key(int code, bool down) { return Out.Send(Out.KeyInput(code, down)); }
        public bool Vk(int vk, bool down) { return Out.Send(Out.VkInput(vk, down)); }

        public void Text(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n') { Out.Send(Out.KeyInput(0x1C, true), Out.KeyInput(0x1C, false)); continue; }
                if (ch == '\t') { Out.Send(Out.KeyInput(0x0F, true), Out.KeyInput(0x0F, false)); continue; }
                Native.INPUT d = new Native.INPUT(), u = new Native.INPUT();
                d.type = u.type = Native.INPUT_KEYBOARD;
                d.ki.wScan = u.ki.wScan = ch;
                d.ki.dwFlags = Native.KEYEVENTF_UNICODE;
                u.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
                d.ki.dwExtraInfo = u.ki.dwExtraInfo = Out.Marker;
                Out.Send(d, u);
            }
        }

        public bool Mouse(string button, bool down)
        {
            uint f; int data = 0;
            switch (button)
            {
                case "left": f = down ? 0x0002u : 0x0004u; break;
                case "right": f = down ? 0x0008u : 0x0010u; break;
                case "middle": f = down ? 0x0020u : 0x0040u; break;
                case "x1": f = down ? 0x0080u : 0x0100u; data = 1; break;
                case "x2": f = down ? 0x0080u : 0x0100u; data = 2; break;
                default: return false;
            }
            return Out.Send(Out.MouseInput(f, data, 0, 0));
        }

        public void Wheel(string dir, int ticks)
        {
            bool h = dir == "left" || dir == "right";
            int sign = (dir == "up" || dir == "right") ? 1 : -1;
            Out.Send(Out.MouseInput(h ? 0x1000u : 0x0800u, sign * 120 * Math.Max(1, ticks), 0, 0));
        }

        public void Move(int dx, int dy) { Out.Send(Out.MouseInput(0x0001u, 0, dx, dy)); }

        public bool Pad(string control, bool down) { return pad.Set(control, down); }
        public void PadAxis(string axis, int value) { pad.SetAxis(axis, value); }

        public void Exec(string kind, string a, string b, string c)
        {
            try
            {
                switch (kind)
                {
                    case "window": WindowOp(a); break;
                    case "lock": Native.LockWorkStation(); break;
                    case "brightness": Brightness(int.Parse(a)); break;
                    case "app":
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(a);
                            psi.Arguments = b ?? "";
                            psi.UseShellExecute = true;
                            if (!string.IsNullOrEmpty(c)) psi.WorkingDirectory = c;
                            else { string dir = Path.GetDirectoryName(a); if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir; }
                            Process.Start(psi);
                            break;
                        }
                    case "web":
                    case "open":
                        Process.Start(new ProcessStartInfo(a) { UseShellExecute = true });
                        break;
                    case "audio": log.Add("   hang: " + CoreAudio.Run(a)); break;
                    case "sound": sounds.Run(a, b, c); break;
                }
            }
            catch (Exception e) { log.Add("HIBA (" + kind + "): " + e.Message); }
        }

        static void WindowOp(string op)
        {
            IntPtr w = Native.GetForegroundWindow();
            if (w == IntPtr.Zero) return;
            if (op == "minimize") Native.ShowWindow(w, 6);
            else if (op == "maximize") Native.ShowWindow(w, Native.IsZoomed(w) ? 9 : 3);
        }

        static void Brightness(int delta)
        {
            int cur = -1;
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightness")))
                foreach (ManagementObject o in s.Get()) { cur = Convert.ToInt32(o["CurrentBrightness"]); break; }
            if (cur < 0) throw new InvalidOperationException("ennek a kijelzőnek a fényereje nem állítható.");
            int target = Math.Max(0, Math.Min(100, cur + delta));
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods")))
                foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)target }); break; }
        }
    }

    // Test mode: nothing reaches Windows, every event goes to the log.
    sealed class DryRunOutput : IOutput
    {
        readonly Logger log;
        public DryRunOutput(Logger log) { this.log = log; }
        void L(string s) { log.Add("   teszt mód, nem küldve: " + s); }
        public bool Key(int code, bool down) { L(Keys.Name(code) + (down ? " le" : " fel")); return true; }
        public bool Vk(int vk, bool down) { L(Keys.VkName(vk) + (down ? " le" : " fel")); return true; }
        public void Text(string text) { L("szöveg „" + (text.Length > 60 ? text.Substring(0, 57) + "…" : text) + "”"); }
        public bool Mouse(string button, bool down) { L("egér " + button + (down ? " le" : " fel")); return true; }
        public void Wheel(string dir, int ticks) { L("görgetés " + dir + " ×" + ticks); }
        public void Move(int dx, int dy) { L("egérmozgás " + dx + ", " + dy); }
        public bool Pad(string control, bool down) { L("gamepad " + control + (down ? " le" : " fel")); return true; }
        public void PadAxis(string axis, int value) { L("gamepad " + axis + " = " + value); }
        public void Exec(string kind, string a, string b, string c) { L(kind + " " + a + (string.IsNullOrEmpty(b) ? "" : " " + b)); }
    }
}
