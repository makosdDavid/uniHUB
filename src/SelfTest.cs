using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbMapper
{
    // UsbMapper.exe --selftest: configuration, mapping engine, macros, recording and HID decoding.
    // All output goes to a recorder, never to Windows. Results: selftest.log, exit code 0 = all passed.
    static class SelfTest
    {
        sealed class Rec : IOutput
        {
            readonly List<string> ev = new List<string>();
            void A(string s) { lock (ev) ev.Add(s); }
            public bool Key(int code, bool down) { A(Keys.Name(code) + (down ? "↓" : "↑")); return true; }
            public bool Vk(int vk, bool down) { A(Keys.VkName(vk) + (down ? "↓" : "↑")); return true; }
            public void Text(string t) { A("szöveg:" + t); }
            public bool Mouse(string b, bool down) { A("egér:" + b + (down ? "↓" : "↑")); return true; }
            public void Wheel(string d, int t) { A("görgő:" + d); }
            public void Move(int dx, int dy) { A("mozgás:" + dx + "," + dy); }
            public bool Pad(string c, bool down) { A("pad:" + c + (down ? "↓" : "↑")); return true; }
            public void PadAxis(string a, int v) { A("tengely:" + a + "=" + v); }
            public void Exec(string kind, string a, string b, string c) { if (!(kind == "sound" && a == "stopall")) A(kind + ":" + a); }
            public string Take() { lock (ev) { string s = string.Join(" ", ev.ToArray()); ev.Clear(); return s; } }
            public int Count(string s) { lock (ev) { int n = 0; foreach (string e in ev) if (e == s) n++; return n; } }
        }

        static readonly StringBuilder report = new StringBuilder();
        static int passed, failed;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            report.AppendLine((ok ? "OK    " : "HIBA  ") + name + (ok || detail == null ? "" : "  →  " + detail));
        }
        static void Eq(string name, string expected, string actual) { Check(name, expected == actual, "várt: [" + expected + "]  kapott: [" + actual + "]"); }

        const string Dev = "044F:B10A";

        static string B(string input, string rest) { return "{\"device\":\"044F:B10A\",\"input\":\"" + input + "\"," + rest + "}"; }

        static string Sample()
        {
            string macros = "["
                + "{\"id\":\"m-once\",\"name\":\"Egyszeri\",\"mode\":\"once\",\"interruptible\":false,\"useDefaultDelay\":true,\"defaultDelay\":10,"
                + "\"simple\":[{\"t\":\"tap\",\"keys\":[30]},{\"t\":\"delay\",\"ms\":5000},{\"t\":\"key\",\"code\":29,\"down\":true},{\"t\":\"tap\",\"keys\":[48]},{\"t\":\"key\",\"code\":29,\"down\":false}],\"press\":[],\"held\":[],\"release\":[]},"
                + "{\"id\":\"m-rep\",\"name\":\"Ismétlő\",\"mode\":\"repeat\",\"interruptible\":false,\"useDefaultDelay\":false,\"defaultDelay\":50,"
                + "\"simple\":[{\"t\":\"key\",\"code\":30,\"down\":true},{\"t\":\"delay\",\"ms\":120},{\"t\":\"key\",\"code\":30,\"down\":false},{\"t\":\"delay\",\"ms\":120}],\"press\":[],\"held\":[],\"release\":[]},"
                + "{\"id\":\"m-rep-int\",\"name\":\"Megszakítható\",\"mode\":\"repeat\",\"interruptible\":true,\"useDefaultDelay\":false,\"defaultDelay\":50,"
                + "\"simple\":[{\"t\":\"key\",\"code\":30,\"down\":true},{\"t\":\"delay\",\"ms\":300},{\"t\":\"key\",\"code\":30,\"down\":false},{\"t\":\"delay\",\"ms\":300}],\"press\":[],\"held\":[],\"release\":[]},"
                + "{\"id\":\"m-tog\",\"name\":\"Kapcsoló\",\"mode\":\"toggle\",\"interruptible\":false,\"useDefaultDelay\":true,\"defaultDelay\":40,"
                + "\"simple\":[{\"t\":\"tap\",\"keys\":[31]}],\"press\":[],\"held\":[],\"release\":[]},"
                + "{\"id\":\"m-seq\",\"name\":\"Sorozat\",\"mode\":\"sequence\",\"interruptible\":false,\"useDefaultDelay\":true,\"defaultDelay\":20,"
                + "\"simple\":[],\"press\":[{\"t\":\"tap\",\"keys\":[48]}],\"held\":[{\"t\":\"tap\",\"keys\":[30]}],\"release\":[{\"t\":\"tap\",\"keys\":[46]}]},"
                + "{\"id\":\"m-all\",\"name\":\"Mindenféle\",\"mode\":\"once\",\"interruptible\":false,\"useDefaultDelay\":false,\"defaultDelay\":0,"
                + "\"simple\":[{\"t\":\"text\",\"text\":\"Árvíztűrő 👍\"},{\"t\":\"mbtn\",\"button\":\"right\",\"down\":true},{\"t\":\"mbtn\",\"button\":\"right\",\"down\":false},"
                + "{\"t\":\"wheel\",\"dir\":\"down\",\"ticks\":2},{\"t\":\"move\",\"dx\":5,\"dy\":-3},{\"t\":\"media\",\"action\":\"vol_up\"},{\"t\":\"pad\",\"control\":\"A\",\"down\":true},"
                + "{\"t\":\"padaxis\",\"axis\":\"LX\",\"value\":-32768},{\"t\":\"pad\",\"control\":\"A\",\"down\":false},{\"t\":\"web\",\"url\":\"https://example.com/\"},"
                + "{\"t\":\"key\",\"code\":42,\"down\":false}],\"press\":[],\"held\":[],\"release\":[]}"
                + "]";
            string x4 = "["
                + B("b5", "\"type\":\"keys\",\"keys\":[42,3]") + "," + B("b6", "\"type\":\"keys\",\"keys\":[42,4]") + ","
                + B("h1U", "\"type\":\"keys\",\"keys\":[17]") + "," + B("h1R", "\"type\":\"keys\",\"keys\":[32]") + ","
                + B("h1D", "\"type\":\"keys\",\"keys\":[31]") + "," + B("h1DL", "\"type\":\"keys\",\"keys\":[44]") + ","
                + B("b7", "\"type\":\"macro\",\"macro\":\"m-once\"") + "," + B("b8", "\"type\":\"keys\",\"keys\":[200]") + ","
                + B("b9", "\"type\":\"macro\",\"macro\":\"m-rep\"") + "," + B("b10", "\"type\":\"macro\",\"macro\":\"m-rep-int\"") + ","
                + B("b11", "\"type\":\"macro\",\"macro\":\"m-tog\"") + "," + B("b12", "\"type\":\"macro\",\"macro\":\"m-seq\"") + ","
                + B("b13", "\"type\":\"macro\",\"macro\":\"m-all\"") + ","
                + B("b14", "\"type\":\"mouse\",\"action\":\"left\",\"repeat\":\"once\"") + "," + B("b15", "\"type\":\"mouse\",\"action\":\"wheel_down\",\"repeat\":\"hold\"") + ","
                + B("b16", "\"type\":\"mouse\",\"action\":\"wheel_up\",\"repeat\":\"once\"") + ","
                + B("b17", "\"type\":\"media\",\"action\":\"play_pause\"") + "," + B("b18", "\"type\":\"pad\",\"control\":\"RT\"") + ","
                + B("b19", "\"type\":\"text\",\"text\":\"gg\\nwp\"") + "," + B("b20", "\"type\":\"system\",\"action\":\"copy\"") + ","
                + B("b21", "\"type\":\"system\",\"action\":\"lock\"") + "," + B("b22", "\"type\":\"app\",\"path\":\"C:\\\\Windows\\\\notepad.exe\",\"args\":\"\",\"dir\":\"\"") + ","
                + B("b23", "\"type\":\"web\",\"url\":\"https://example.com/\"") + "," + B("b24", "\"type\":\"open\",\"path\":\"C:\\\\Users\"") + ","
                + B("b25", "\"type\":\"audio\",\"action\":\"mute_mic\"") + "," + B("b26", "\"type\":\"sound\",\"path\":\"C:\\\\Windows\\\\Media\\\\tada.wav\",\"play\":\"hold\"") + ","
                + B("b27", "\"type\":\"mouse\",\"action\":\"double_left\",\"repeat\":\"once\"")
                + "]";
            return "{\"version\":2,\"port\":47640,\"deviceNames\":{\"044F:B10A\":\"T.16000M\"},\"macros\":" + macros + ",\"profiles\":["
                + "{\"id\":\"default\",\"name\":\"Alap\",\"exes\":[],\"bindings\":[]},"
                + "{\"id\":\"x4\",\"name\":\"X4\",\"exes\":[\"X4\"],\"bindings\":" + x4 + "}]}";
        }

        public static int Run(string dir)
        {
            report.AppendLine("USB Mapper " + App.Version + " önteszt, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine();
            try { ConfigTests(); } catch (Exception e) { Check("beállítás-tesztek", false, e.ToString()); }
            try { MapperTests(); } catch (Exception e) { Check("hozzárendelés-tesztek", false, e.ToString()); }
            try { MacroTests(); } catch (Exception e) { Check("makrótesztek", false, e.ToString()); }
            try { RecorderTests(); } catch (Exception e) { Check("felvétel-tesztek", false, e.ToString()); }
            try { OutputTests(); } catch (Exception e) { Check("kimenet-tesztek", false, e.ToString()); }
            try { HidTests(); } catch (Exception e) { Check("HID-tesztek", false, e.ToString()); }
            report.AppendLine();
            report.AppendLine("Összesen: " + passed + " jó, " + failed + " hibás.");
            File.WriteAllText(Path.Combine(dir, "selftest.log"), report.ToString(), new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }

        static AppConfig Parse(string json) { return AppConfig.FromJson(Json.Parse(json)); }

        static void ConfigTests()
        {
            AppConfig c = Parse(Sample());
            Eq("programnév kiegészül .exe-re", "X4.exe", c.Profiles[1].Exes[0]);
            Eq("profil a programhoz (kis-nagybetű mindegy)", "x4", c.ForExe("x4.EXE").Id);
            Eq("ismeretlen program → alap profil", "default", c.ForExe("notepad.exe").Id);
            Eq("makrók betöltve", "6", c.Macros.Count.ToString());
            Eq("hozzárendelések betöltve (12 fajta)", "27", c.Profiles[1].Bindings.Count.ToString());
            string w1 = Json.Write(c.ToJson(), true);
            string w2 = Json.Write(Parse(w1).ToJson(), true);
            Eq("JSON oda-vissza változatlan", w1, w2);
            Check("emoji a szövegben megmarad", w1.Contains("👍") || w1.Contains("\\ud83d"), null);

            string v1 = "{\"version\":1,\"port\":47640,\"deviceNames\":{},\"profiles\":[{\"id\":\"default\",\"name\":\"Alap\",\"exes\":[],\"bindings\":["
                + B("b7", "\"type\":\"macro\",\"gap\":20,\"hold\":30,\"steps\":[{\"type\":\"tap\",\"keys\":[30]},{\"type\":\"wait\",\"ms\":50}]") + "]}]}";
            AppConfig cv = Parse(v1);
            Eq("1-es verzió: a beágyazott makró névvel mentett makró lesz", "1 once tap,delay", cv.Macros.Count + " " + cv.Macros[0].Mode + " " + cv.Macros[0].Simple[0].T + "," + cv.Macros[0].Simple[1].T);
            Eq("1-es verzió: a hozzárendelés a makróra mutat", cv.Macros[0].Id, cv.Profiles[0].Bindings[0].MacroId);

            string s = Sample();
            string[][] bad =
            {
                new[] { "ismeretlen billentyűkód", "\"keys\":[42,3]", "\"keys\":[42,84]" },
                new[] { "egy billentyű kétszer", "\"keys\":[42,3]", "\"keys\":[42,42]" },
                new[] { "kétszer ugyanaz a bemenet", "\"input\":\"b6\"", "\"input\":\"b5\"" },
                new[] { "129. gomb", "\"input\":\"b6\"", "\"input\":\"b129\"" },
                new[] { "alap profilhoz program", "\"exes\":[],", "\"exes\":[\"game.exe\"]," },
                new[] { "ismeretlen mező", "\"type\":\"keys\",\"keys\":[42,3]", "\"type\":\"keys\",\"keys\":[42,3],\"x\":1" },
                new[] { "nem létező makróra mutat", "\"macro\":\"m-once\"", "\"macro\":\"m-nincs\"" },
                new[] { "ismétlő makró késleltetés nélkül", "{\"t\":\"key\",\"code\":30,\"down\":true},{\"t\":\"delay\",\"ms\":120},{\"t\":\"key\",\"code\":30,\"down\":false},{\"t\":\"delay\",\"ms\":120}", "{\"t\":\"tap\",\"keys\":[30]}" },
                new[] { "üres egyszeri makró", "\"simple\":[{\"t\":\"tap\",\"keys\":[31]}]", "\"simple\":[]" },
                new[] { "ismeretlen makrótípus", "\"mode\":\"toggle\"", "\"mode\":\"turbo\"" },
                new[] { "ismeretlen lépéstípus", "{\"t\":\"tap\",\"keys\":[31]}", "{\"t\":\"teleport\"}" },
                new[] { "nem http(s) cím", "\"url\":\"https://example.com/\"}", "\"url\":\"file:///C:/x\"}" },
                new[] { "relatív programútvonal", "\"path\":\"C:\\\\Windows\\\\notepad.exe\"", "\"path\":\"notepad.exe\"" },
                new[] { "nem hangfájl", "tada.wav", "tada.exe" },
                new[] { "ismeretlen egérművelet", "\"action\":\"wheel_up\"", "\"action\":\"teleport\"" },
                new[] { "ismeretlen gamepad-gomb", "\"control\":\"RT\"", "\"control\":\"Z9\"" },
                new[] { "tengelyérték tartományon kívül", "\"value\":-32768", "\"value\":-40000" },
                new[] { "ismeretlen rendszerművelet", "\"action\":\"lock\"", "\"action\":\"format_c\"" },
            };
            foreach (string[] b in bad)
            {
                int at = s.IndexOf(b[1], StringComparison.Ordinal);
                if (at < 0) { Check("elutasítás: " + b[0], false, "a tesztminta nem található"); continue; }
                string json = s.Substring(0, at) + b[2] + s.Substring(at + b[1].Length);
                string msg = null;
                try { Parse(json); } catch (ConfigException e) { msg = e.Message; }
                Check("elutasítás: " + b[0], msg != null, "elfogadta");
                if (msg != null) report.AppendLine("        üzenet: " + msg);
            }
        }

        static Mapper NewMapper(Rec rec)
        {
            Mapper m = new Mapper(new object(), rec, new Logger(null), Parse(Sample()));
            m.SetForeground("X4.exe");
            return m;
        }

        static void Press(Mapper m, string input) { m.Button(Dev, input, true); }
        static void Up(Mapper m, string input) { m.Button(Dev, input, false); }

        static void MapperTests()
        {
            Rec rec = new Rec();
            Mapper m = new Mapper(new object(), rec, new Logger(null), Parse(Sample()));
            m.SetForeground("notepad.exe");
            Press(m, "b5"); Up(m, "b5");
            Eq("alap profil, nincs kötés → nem küld semmit", "", rec.Take());
            m.SetForeground("X4.exe");
            Eq("aktív profil X4-nél", "x4", m.Active.Id);

            Press(m, "b5"); Press(m, "b6"); Up(m, "b5"); Up(m, "b6");
            Eq("kombinációk közös Shifttel (számlálás)", "Bal Shift↓ 2↓ 3↓ 2↑ 3↑ Bal Shift↑", rec.Take());
            Press(m, "b5"); Press(m, "b5"); Up(m, "b5");
            Eq("ismételt lenyomásjelzés nem nyom kétszer", "Bal Shift↓ 2↓ 2↑ Bal Shift↑", rec.Take());
            m.Hat(Dev, 1, 0); m.Hat(Dev, 1, 1); m.Hat(Dev, 1, 2); m.Hat(Dev, 1, -1);
            Eq("kalap: átló kötés nélkül = két szomszédos irány", "W↓ D↓ W↑ D↑", rec.Take());
            m.Hat(Dev, 1, 5); m.Hat(Dev, 1, 4); m.Hat(Dev, 1, -1);
            Eq("kalap: kötött átló csak a sajátját nyomja", "Z↓ Z↑ S↓ S↑", rec.Take());
            Press(m, "b8"); Up(m, "b8");
            Eq("bővített billentyű (Fel nyíl)", "Fel nyíl↓ Fel nyíl↑", rec.Take());

            Press(m, "b14"); Up(m, "b14");
            Eq("egérgomb nyomva tartva", "egér:left↓ egér:left↑", rec.Take());
            Press(m, "b16"); Up(m, "b16");
            Eq("görgetés: egy kattanás nyomásonként", "görgő:up", rec.Take());
            Press(m, "b15"); Thread.Sleep(560); Up(m, "b15"); Thread.Sleep(120);
            int wheels = rec.Count("görgő:down");
            rec.Take();
            Check("görgetés: ismétel, amíg nyomod (≥ 3 kattanás 0,56 s alatt)", wheels >= 3 && wheels <= 8, wheels + " kattanás");
            Thread.Sleep(150);
            Eq("görgetés: felengedés után megáll", "", rec.Take());
            Press(m, "b27"); Thread.Sleep(250);
            Eq("dupla kattintás", "egér:left↓ egér:left↑ egér:left↓ egér:left↑", rec.Take());
            Press(m, "b17"); Up(m, "b17");
            Eq("médiabillentyű", "Lejátszás/szünet↓ Lejátszás/szünet↑", rec.Take());
            Press(m, "b18"); Up(m, "b18");
            Eq("gamepad-ravasz nyomva tartva", "pad:RT↓ pad:RT↑", rec.Take());
            Press(m, "b19"); Thread.Sleep(100);
            Eq("szöveg beírása", "szöveg:gg\nwp", rec.Take());
            Press(m, "b20"); Thread.Sleep(150);
            Eq("rendszer: másolás (kiosztásfüggetlen Ctrl+C)", "Ctrl↓ C↓ C↑ Ctrl↑", rec.Take());
            Press(m, "b21"); Thread.Sleep(100);
            Eq("rendszer: zárolás", "lock:", rec.Take());
            Press(m, "b22"); Thread.Sleep(100);
            Eq("program indítása", "app:C:\\Windows\\notepad.exe", rec.Take());
            Press(m, "b23"); Thread.Sleep(100);
            Eq("weboldal", "web:https://example.com/", rec.Take());
            Press(m, "b24"); Thread.Sleep(100);
            Eq("mappa megnyitása", "open:C:\\Users", rec.Take());
            Press(m, "b25"); Thread.Sleep(100);
            Eq("mikrofon némítása", "audio:mute_mic", rec.Take());
            Press(m, "b26"); Up(m, "b26");
            Eq("hangminta: szól, amíg nyomod", "sound:hold-start sound:hold-stop", rec.Take());

            Press(m, "b5"); m.SetForeground("notepad.exe"); Up(m, "b5");
            Eq("profilváltás felenged mindent, a későbbi felengedés nem küld", "Bal Shift↓ 2↓ Bal Shift↑ 2↑", rec.Take());
            m.SetForeground("X4.exe");
            Press(m, "b18"); m.SetPaused(true); m.SetPaused(false); Up(m, "b18");
            Eq("szünet felengedi a gamepadet is", "pad:RT↓ pad:RT↑", rec.Take());
            m.SetPaused(true); Press(m, "b5"); Up(m, "b5"); m.SetPaused(false);
            Eq("szünetben nem küld", "", rec.Take());
        }

        static void WaitIdle(Mapper m, int maxMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (m.RunningMacros > 0 && sw.ElapsedMilliseconds < maxMs) Thread.Sleep(10);
        }

        static void MacroTests()
        {
            Rec rec = new Rec();
            Mapper m = NewMapper(rec);

            Stopwatch sw = Stopwatch.StartNew();
            Press(m, "b7"); Up(m, "b7"); WaitIdle(m, 2000);
            Eq("egyszeri makró, alap késleltetés: a felvett várakozás kimarad", "A↓ A↑ Bal Ctrl↓ B↓ B↑ Bal Ctrl↑", rec.Take());
            Check("egyszeri makró: nem várt 5 s-ot", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + " ms");

            Press(m, "b9"); Thread.Sleep(50); Up(m, "b9");
            Thread.Sleep(30);
            Eq("ismétlés, nem megszakítható: felengedés után is fut a kör", "A↓", rec.Take());
            WaitIdle(m, 2000);
            Eq("ismétlés, nem megszakítható: a kör végigfut, új nem indul", "A↑", rec.Take());

            Press(m, "b10"); Thread.Sleep(50); Up(m, "b10");
            Thread.Sleep(120);
            Eq("ismétlés, megszakítható: felengedéskor azonnal megáll és felenged", "A↓ A↑", rec.Take());
            Eq("ismétlés, megszakítható: nem fut tovább", "0", m.RunningMacros.ToString());

            Press(m, "b9"); Thread.Sleep(600); Up(m, "b9"); WaitIdle(m, 2000);
            int passes = rec.Count("A↓");
            rec.Take();
            Check("ismétlés: nyomva tartva többször lefut", passes >= 2, passes + " kör");

            Press(m, "b11"); Up(m, "b11"); Thread.Sleep(300);
            Check("kapcsoló: felengedés után is fut", m.RunningMacros == 1, m.RunningMacros + " futó makró");
            Press(m, "b11"); Up(m, "b11"); WaitIdle(m, 1000);
            int taps = rec.Count("S↓");
            rec.Take();
            Check("kapcsoló: újabb nyomásra leáll, közben többször lefutott", m.RunningMacros == 0 && taps >= 3, taps + " kör, " + m.RunningMacros + " futó");

            Press(m, "b12"); Thread.Sleep(250); Up(m, "b12"); WaitIdle(m, 2000);
            string seq = rec.Take();
            Check("sorozat: lenyomás → tartás alatt ismétel → felengedés", seq.StartsWith("B↓ B↑ A↓ A↑ A↓ A↑", StringComparison.Ordinal) && seq.EndsWith("C↓ C↑", StringComparison.Ordinal), seq);

            Press(m, "b13"); WaitIdle(m, 2000);
            Eq("makró minden lépésfajtával", "szöveg:Árvíztűrő 👍 egér:right↓ egér:right↑ görgő:down mozgás:5,-3 Hangerő fel↓ Hangerő fel↑ pad:A↓ tengely:LX=-32768 pad:A↑ web:https://example.com/", rec.Take());
            m.SetForeground("notepad.exe");
            Eq("profilváltás a gamepad-tengelyt is alapra állítja", "tengely:LX=0 tengely:LY=0 tengely:RX=0 tengely:RY=0 tengely:LT=0 tengely:RT=0", rec.Take());
            m.SetForeground("X4.exe");

            Press(m, "b9"); Thread.Sleep(50);
            m.SetForeground("notepad.exe"); WaitIdle(m, 1000);
            Eq("profilváltás megszakítja a makrót és felenged", "A↓ A↑", rec.Take());
            Eq("profilváltás után nem fut makró", "0", m.RunningMacros.ToString());
        }

        static void RecorderTests()
        {
            Recorder r = new Recorder(new object(), new Logger(null));
            Recorder.Options o = new Recorder.Options();
            o.Keyboard = o.Mouse = o.Move = false; o.Pads = true;
            r.Start(o);
            r.OnButton(Dev, "b1", true); r.OnButton(Dev, "b1", false);
            r.OnHat(Dev, 1, 1); r.OnHat(Dev, 1, -1);
            r.OnAxis(Dev, "X", 1.0); Thread.Sleep(20); r.OnAxis(Dev, "SLIDER", 0.5);
            r.OnButton(Dev, "b16", true);
            List<Step> steps = r.Stop();
            List<string> got = new List<string>();
            foreach (Step s in steps)
            {
                if (s.T == "pad") got.Add(s.Control + (s.Down ? "↓" : "↑"));
                else if (s.T == "padaxis") got.Add(s.Axis + "=" + s.Value);
            }
            Eq("felvétel: vezérlő → virtuális Xbox (gomb, kalap, tengelyek)", "A↓ A↑ UP↓ RIGHT↓ UP↑ RIGHT↑ LX=32767 RT=128", string.Join(" ", got.ToArray()));
            Check("felvétel: a lépések közé várakozás kerül", steps.Exists(delegate (Step s) { return s.T == "delay"; }), null);
        }

        static void OutputTests()
        {
            Eq("INPUT szerkezet mérete x64-en", "40", Out.InputSize.ToString());
            Native.INPUT up = Out.KeyInput(200, false);
            Eq("Fel nyíl felengedés: scancode és jelzők", "72/11", up.ki.wScan + "/" + up.ki.dwFlags);
            Native.INPUT v = Out.VkInput(0xB3, true);
            Eq("médiabillentyű: bővített jelző", "179/1", v.ki.wVk + "/" + v.ki.dwFlags);
            Native.INPUT c = Out.VkInput(0x43, true);
            Eq("C betű virtuális kódja nem bővített", "67/0", c.ki.wVk + "/" + c.ki.dwFlags);
            Eq("gamepad-vezérlők száma", "25", VirtualPad.Controls.Length.ToString());
        }

        static void HidTests()
        {
            DeviceManager dm = new DeviceManager();
            dm.Enumerate();
            HidDevice d = dm.FindByKey(Dev);
            if (d == null) { report.AppendLine("--    HID-tesztek kihagyva: a T.16000M (044F:B10A) nincs csatlakoztatva."); return; }
            List<string> axes = new List<string>();
            foreach (AxisCap a in d.Axes) axes.Add(a.Name);
            report.AppendLine("      eszköz: " + d.Name + ", " + d.ButtonCount + " gomb, " + d.Hats.Count + " kalap, tengelyek: " + string.Join(",", axes.ToArray()) + ", jelentés " + d.ReportLength + " bájt");
            Eq("T.16000M: 16 gomb, 1 kalap, 10 bájt", "16/1/10", d.ButtonCount + "/" + d.Hats.Count + "/" + d.ReportLength);
            Eq("T.16000M: tengelyek (a leíró sorrendjében)", "X,Y,SLIDER,RZ", string.Join(",", axes.ToArray()));
            if (d.ReportLength != 10 || d.Hats.Count != 1) return;

            List<string> ev = new List<string>();
            dm.OnButton = delegate (HidDevice x, string input, bool down) { ev.Add(input + (down ? "↓" : "↑")); };
            dm.OnHat = delegate (HidDevice x, int hat, int dir) { ev.Add("h" + hat + ":" + dir); };
            HidDevice.OnAxis = delegate (HidDevice x, string axis, double val) { ev.Add(axis + "=" + val.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); };
            byte[] idle = { 0x00, 0x00, 0x00, 0x0F, 0x00, 0x20, 0x00, 0x20, 0x80, 0x80 };
            byte[] r1 = { 0x00, 0x10, 0x80, 0x01, 0xFF, 0x3F, 0x00, 0x00, 0x80, 0xFF };   // button 5 + 16, hat up-right, X right, Y forward, slider max
            dm.Feed(d, idle);
            ev.Clear();
            dm.Feed(d, r1);
            Eq("kitalált jelentés: gombok, kalap, tengelyek", "b5↓ b16↓ h1:1 X=1.00 Y=-1.00 SLIDER=1.00", string.Join(" ", ev.ToArray()));
            Eq("élő nézet: nyomva", "b5 b16 h1UR", string.Join(" ", d.Pressed().ToArray()));
            ev.Clear();
            dm.Feed(d, idle);
            Eq("kitalált jelentés: minden elengedve", "b5↑ b16↑ h1:-1 X=0.00 Y=0.00 SLIDER=0.50", string.Join(" ", ev.ToArray()));
            HidDevice.OnAxis = delegate { };

            Rec rec = new Rec();
            Mapper m = NewMapper(rec);
            dm.OnButton = delegate (HidDevice x, string input, bool down) { m.Button(x.Key, input, down); };
            dm.OnHat = delegate (HidDevice x, int hat, int dir) { m.Hat(x.Key, hat, dir); };
            dm.Feed(d, new byte[] { 0x00, 0x10, 0x00, 0x01, 0x00, 0x20, 0x00, 0x20, 0x80, 0x80 });
            dm.Feed(d, idle);
            Eq("teljes lánc: jelentés → billentyűk", "Bal Shift↓ 2↓ W↓ D↓ 2↑ Bal Shift↑ W↑ D↑", rec.Take());
        }
    }
}
