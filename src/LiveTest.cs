using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace UsbMapper
{
    // UsbMapper.exe --livetest: real output, but keyboard input only into its own test window.
    // Before every step it checks that the test window is in the foreground; if not, it releases
    // everything and stops. The virtual Xbox controller is checked through XInput.
    // Results: livetest.log, exit code 0 = passed.
    static class LiveTest
    {
        sealed class TestForm : Form
        {
            public readonly List<string> Got = new List<string>();
            public readonly StringBuilder Chars = new StringBuilder();
            public TestForm()
            {
                Text = "USB Mapper – élő teszt (ne kattints el innen)";
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(520, 140);
                TopMost = true;
                Controls.Add(new Label { Text = "Élő teszt fut, néhány másodperc.\nNe kattints el erről az ablakról.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f) });
                foreach (Control c in Controls) c.TabStop = false;
            }
            protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
            {
                if (msg.WParam.ToInt32() == 0xE7) return false;   // VK_PACKET must reach TranslateMessage to become WM_CHAR
                Record(msg);
                return true;
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x101 || m.Msg == 0x105) { Record(m); return; }
                if (m.Msg == 0x102) { Chars.Append((char)m.WParam.ToInt32()); return; }   // WM_CHAR
                base.WndProc(ref m);
            }
            void Record(Message m)
            {
                if (m.Msg != 0x100 && m.Msg != 0x101 && m.Msg != 0x104 && m.Msg != 0x105) return;
                long lp = m.LParam.ToInt64();
                bool up = m.Msg == 0x101 || m.Msg == 0x105;
                if (!up && ((lp >> 30) & 1) == 1) return;
                if (m.WParam.ToInt32() == 0xE7) return;   // VK_PACKET: Unicode text, checked through WM_CHAR
                int sc = (int)((lp >> 16) & 0xFF);
                bool ext = ((lp >> 24) & 1) == 1;
                Got.Add(sc.ToString("X2") + (ext ? "e" : "") + (up ? "↑" : "↓"));
            }
        }

        // Keyboard output only while our window is in the foreground; the rest goes to the real output.
        sealed class GuardedOutput : IOutput
        {
            readonly RealOutput inner;
            public IntPtr Window;
            public bool Aborted;
            public GuardedOutput(RealOutput inner) { this.inner = inner; }
            bool Safe(bool down) { if (Native.GetForegroundWindow() != Window) Aborted = true; return !Aborted || !down; }
            public bool Key(int code, bool down) { return !Safe(down) || inner.Key(code, down); }
            public bool Vk(int vk, bool down) { return !Safe(down) || inner.Vk(vk, down); }
            public void Text(string t) { if (Safe(true)) inner.Text(t); }
            public bool Mouse(string b, bool down) { return true; }
            public void Wheel(string d, int t) { }
            public void Move(int dx, int dy) { }
            public bool Pad(string c, bool down) { return inner.Pad(c, down); }
            public void PadAxis(string a, int v) { inner.PadAxis(a, v); }
            public void Exec(string kind, string a, string b, string c) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

        [DllImport("xinput1_4.dll")]
        static extern int XInputGetState(int user, out XINPUT_STATE state);

        static string XInputAll()
        {
            List<string> s = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                XINPUT_STATE st;
                if (XInputGetState(i, out st) == 0) s.Add(i + ":" + st.wButtons.ToString("X4") + "/" + st.bRightTrigger + "/" + st.sThumbLX);
            }
            return string.Join(" ", s.ToArray());
        }

        const string Dev = "044F:B10A";

        public static int Run(string dir)
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("USB Mapper " + App.Version + " élő teszt, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int result = 1;
            Logger lg = new Logger(null);
            VirtualPad pad = new VirtualPad(lg);
            SoundBank sounds = new SoundBank(lg);
            GuardedOutput output = new GuardedOutput(new RealOutput(lg, pad, sounds));
            TestForm f = new TestForm();
            Mapper m = null;
            f.Shown += delegate
            {
                f.Activate();
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 500;
                t.Tick += delegate
                {
                    t.Stop();
                    try
                    {
                        output.Window = f.Handle;
                        if (Native.GetForegroundWindow() != f.Handle)
                        {
                            log.AppendLine("KIHAGYVA: a tesztablak nem került előtérbe, ezért semmit nem küldtem.");
                            result = 2;
                            return;
                        }
                        m = new Mapper(new object(), output, lg, AppConfig.FromJson(Json.Parse(Sample)));
                        m.SetForeground("X4.exe");
                        Action pump = delegate { DateTime end = DateTime.UtcNow.AddMilliseconds(80); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); } };
                        Action<int> pumpFor = delegate (int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); } };
                        Action[] steps =
                        {
                            delegate { m.Button(Dev, "b5", true); }, delegate { m.Button(Dev, "b5", false); },
                            delegate { m.Hat(Dev, 1, 1); }, delegate { m.Hat(Dev, 1, -1); },
                            delegate { m.Button(Dev, "b8", true); }, delegate { m.Button(Dev, "b8", false); },
                            delegate { m.Button(Dev, "b7", true); pumpFor(400); }, delegate { m.Button(Dev, "b7", false); },
                            delegate { m.Button(Dev, "b9", true); pumpFor(300); }, delegate { m.Button(Dev, "b9", false); },
                        };
                        foreach (Action s in steps)
                        {
                            if (Native.GetForegroundWindow() != f.Handle) { output.Aborted = true; break; }
                            s();
                            pump();
                        }
                        pumpFor(200);
                        string expected = "2A↓ 03↓ 03↑ 2A↑ 11↓ 20↓ 11↑ 20↑ 48e↓ 48e↑ 1E↓ 1E↑ 1D↓ 30↓ 30↑ 1D↑";
                        string got = string.Join(" ", f.Got.ToArray());
                        log.AppendLine("billentyűk, várt:   " + expected);
                        log.AppendLine("billentyűk, kapott: " + got);
                        log.AppendLine("szöveg, várt:   „Árvíztűrő ü!”");
                        log.AppendLine("szöveg, kapott: „" + f.Chars + "”");
                        bool keysOk = got == expected && f.Chars.ToString() == "Árvíztűrő ü!";

                        // virtual Xbox controller, read back through XInput
                        string before = XInputAll();
                        m.Button(Dev, "b10", true); pumpFor(300);
                        string pressed = XInputAll();
                        m.Button(Dev, "b10", false); pumpFor(200);
                        string after = XInputAll();
                        log.AppendLine("XInput előtte:   " + (before == "" ? "(nincs vezérlő)" : before));
                        log.AppendLine("XInput A+RT+LX:  " + pressed);
                        log.AppendLine("XInput utána:    " + after);
                        bool padOk = pressed.Contains(":1000/255/32767") && after.Contains(":0000/0/0") && pad.Connected;

                        if (output.Aborted) { log.AppendLine("MEGSZAKÍTVA: a tesztablak elvesztette a fókuszt; minden felengedve."); result = 3; }
                        else if (keysOk && padOk) { log.AppendLine("OK: billentyűk (bővítettel), Unicode szöveg, makró és virtuális Xbox-vezérlő rendben."); result = 0; }
                        else log.AppendLine("HIBA: eltérés (billentyűk/szöveg: " + (keysOk ? "jó" : "rossz") + ", gamepad: " + (padOk ? "jó" : "rossz") + ").");
                    }
                    catch (Exception e) { log.AppendLine("KIVÉTEL: " + e); }
                    finally
                    {
                        if (m != null) m.ReleaseAll();
                        f.Close();
                    }
                };
                t.Start();
            };
            Application.Run(f);
            pad.Dispose();
            sounds.Dispose();
            File.WriteAllText(Path.Combine(dir, "livetest.log"), log.ToString(), new UTF8Encoding(false));
            return result;
        }

        const string Sample = @"{""version"":2,""port"":47640,""deviceNames"":{},""macros"":[
 {""id"":""m1"",""name"":""teszt"",""mode"":""once"",""interruptible"":false,""useDefaultDelay"":true,""defaultDelay"":20,
  ""simple"":[{""t"":""tap"",""keys"":[30]},{""t"":""key"",""code"":29,""down"":true},{""t"":""tap"",""keys"":[48]},{""t"":""key"",""code"":29,""down"":false}],""press"":[],""held"":[],""release"":[]},
 {""id"":""m2"",""name"":""pad"",""mode"":""sequence"",""interruptible"":false,""useDefaultDelay"":true,""defaultDelay"":20,""simple"":[],
  ""press"":[{""t"":""pad"",""control"":""A"",""down"":true},{""t"":""padaxis"",""axis"":""RT"",""value"":255},{""t"":""padaxis"",""axis"":""LX"",""value"":32767}],
  ""held"":[],""release"":[{""t"":""pad"",""control"":""A"",""down"":false},{""t"":""padaxis"",""axis"":""RT"",""value"":0},{""t"":""padaxis"",""axis"":""LX"",""value"":0}]}
],""profiles"":[
 {""id"":""default"",""name"":""Alap"",""exes"":[],""bindings"":[]},
 {""id"":""x4"",""name"":""X4"",""exes"":[""X4.exe""],""bindings"":[
  {""device"":""044F:B10A"",""input"":""b5"",""type"":""keys"",""keys"":[42,3]},
  {""device"":""044F:B10A"",""input"":""h1U"",""type"":""keys"",""keys"":[17]},
  {""device"":""044F:B10A"",""input"":""h1R"",""type"":""keys"",""keys"":[32]},
  {""device"":""044F:B10A"",""input"":""b7"",""type"":""macro"",""macro"":""m1""},
  {""device"":""044F:B10A"",""input"":""b8"",""type"":""keys"",""keys"":[200]},
  {""device"":""044F:B10A"",""input"":""b9"",""type"":""text"",""text"":""Árvíztűrő ü!""},
  {""device"":""044F:B10A"",""input"":""b10"",""type"":""macro"",""macro"":""m2""}
 ]}]}";
    }
}
