using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace UsbMapper
{
    // Local settings UI on http://localhost:<port>/ (this machine only). Every /api/ call needs the
    // session token in the X-Token header; POST bodies must be JSON; foreign origins and hosts are refused.
    sealed class WebServer
    {
        readonly App app;
        readonly string uiDir;
        HttpListener listener;
        Thread thread;
        string origin;
        int port;
        static readonly UTF8Encoding utf8 = new UTF8Encoding(false);
        const int MaxBody = 8 * 1024 * 1024;

        static readonly Dictionary<string, string[]> statics = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "/", new[] { "index.html", "text/html; charset=utf-8" } },
            { "/index.html", new[] { "index.html", "text/html; charset=utf-8" } },
            { "/app.js", new[] { "app.js", "text/javascript; charset=utf-8" } },
            { "/app.css", new[] { "app.css", "text/css; charset=utf-8" } },
        };

        public WebServer(App app, string uiDir) { this.app = app; this.uiDir = uiDir; }

        public int Start(int firstPort)
        {
            for (int i = 0; i < 20; i++)
            {
                HttpListener l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + (firstPort + i) + "/");
                try { l.Start(); listener = l; port = firstPort + i; break; }
                catch (HttpListenerException) { l.Close(); }
            }
            if (listener == null) throw new InvalidOperationException("Nem sikerült helyi portot nyitni (" + firstPort + "-től).");
            origin = "http://localhost:" + port;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "web";
            thread.Start();
            return port;
        }

        public void Stop()
        {
            try { if (listener != null) { listener.Stop(); listener.Close(); } } catch (Exception) { }
        }

        void Loop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                try { Handle(ctx); }
                catch (Exception e)
                {
                    try { ctx.Response.Abort(); } catch (Exception) { }
                    app.Log.Add("HIBA a felület kiszolgálásában: " + e.Message);
                }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string host = req.Headers["Host"] ?? "";
            if (host != "localhost:" + port && host != "127.0.0.1:" + port) { SendText(ctx, 403, "403"); return; }
            string path = req.Url.AbsolutePath;
            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (req.Headers["X-Token"] != app.Token) { SendJson(ctx, 403, Err("Érvénytelen munkamenet. Nyisd meg újra a felületet a tálcaikonról.")); return; }
                string reqOrigin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(reqOrigin) && reqOrigin != origin) { SendJson(ctx, 403, Err("Idegen eredetű kérés.")); return; }
                if (req.HttpMethod == "POST" && !(req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { SendJson(ctx, 415, Err("JSON kérés kell.")); return; }
                object result;
                try { result = Api(req.HttpMethod + " " + path, req); }
                catch (ConfigException e) { result = Err(e.Message); }
                catch (IOException e) { result = Err("Fájlhiba: " + e.Message); }
                catch (UnauthorizedAccessException e) { result = Err("Fájlhiba: " + e.Message); }
                if (result == null) { SendJson(ctx, 404, Err("Nincs ilyen művelet.")); return; }
                SendJson(ctx, 200, result);
                if (path == "/api/quit") app.RequestExit();
                return;
            }
            string[] f;
            if ((req.HttpMethod == "GET" || req.HttpMethod == "HEAD") && statics.TryGetValue(path, out f))
            {
                Send(ctx, 200, f[1], File.ReadAllBytes(Path.Combine(uiDir, f[0])));
                return;
            }
            SendText(ctx, 404, "404");
        }

        object Api(string route, HttpListenerRequest req)
        {
            switch (route)
            {
                case "GET /api/state": return State();
                case "GET /api/live":
                    {
                        long since;
                        long.TryParse(req.QueryString["since"] ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out since);
                        return Live(since);
                    }
                case "POST /api/save": return Save(Body(req));
                case "POST /api/pause":
                    {
                        Dictionary<string, object> b = Body(req);
                        object v;
                        if (!b.TryGetValue("paused", out v) || !(v is bool)) throw new ConfigException("Hiányzik a paused mező.");
                        app.Mapper.SetPaused((bool)v);
                        app.UpdateTrayLater();
                        return new JObj().Set("ok", true).Set("paused", app.Mapper.Paused);
                    }
                case "POST /api/record/start":
                    {
                        Dictionary<string, object> b = Body(req);
                        Recorder.Options o = new Recorder.Options();
                        o.Keyboard = Flag(b, "keyboard"); o.Mouse = Flag(b, "mouse"); o.Move = Flag(b, "move"); o.Pads = Flag(b, "pads");
                        if (!o.Keyboard && !o.Mouse && !o.Move && !o.Pads) throw new ConfigException("Válassz legalább egy felvehető bemenetet.");
                        app.OnUi(delegate { app.Recorder.Start(o); return true; });
                        return new JObj().Set("ok", true);
                    }
                case "POST /api/record/stop":
                    {
                        List<Step> steps = app.OnUi(delegate { return app.Recorder.Stop(); });
                        return new JObj().Set("ok", true).Set("steps", AppConfig.StepsJson(steps));
                    }
                case "POST /api/test/input": return TestInput(Body(req));
                case "POST /api/test/report": return TestReport(Body(req));
                case "POST /api/quit": return new JObj().Set("ok", true);
            }
            return null;
        }

        JObj State()
        {
            List<object> keys = new List<object>();
            foreach (KeyDef k in Keys.All) keys.Add(new JObj().Set("code", k.Code).Set("dom", k.Dom).Set("name", k.Name).Set("group", k.Group));
            lock (app.Sync)
            {
                Profile a = app.Mapper.Active;
                return new JObj().Set("ok", true).Set("version", App.Version).Set("dryRun", app.DryRun).Set("paused", app.Mapper.Paused)
                    .Set("configPath", app.Store.FilePath).Set("rev", app.Rev).Set("config", app.Config.ToJson())
                    .Set("keys", keys).Set("devices", Devices()).Set("padConnected", app.Pad != null && app.Pad.Connected).Set("devicesRev", app.Devices.Revision)
                    .Set("active", new JObj().Set("id", a.Id).Set("name", a.Name)).Set("exe", app.Mapper.Exe)
                    .Set("recentExes", new List<string>(app.RecentExes)).Set("log", app.Log.Since(0));
            }
        }

        List<object> Devices()
        {
            List<object> list = new List<object>();
            foreach (HidDevice d in app.Devices.All)
                list.Add(new JObj().Set("key", d.Key).Set("name", d.Name).Set("buttons", d.ButtonCount).Set("hats", d.Hats.Count));
            return list;
        }

        JObj Live(long since)
        {
            lock (app.Sync)
            {
                JObj pressed = new JObj();
                foreach (HidDevice d in app.Devices.All) pressed.Set(d.Key, d.Pressed());
                Profile a = app.Mapper.Active;
                return new JObj().Set("ok", true).Set("paused", app.Mapper.Paused).Set("dryRun", app.DryRun)
                    .Set("active", new JObj().Set("id", a.Id).Set("name", a.Name)).Set("exe", app.Mapper.Exe)
                    .Set("devicesRev", app.Devices.Revision).Set("pressed", pressed)
                    .Set("recentExes", new List<string>(app.RecentExes)).Set("log", app.Log.Since(since))
                    .Set("recording", app.Recorder.Recording).Set("recordCount", app.Recorder.Count).Set("macrosRunning", app.Mapper.RunningMacros);
            }
        }

        JObj Save(Dictionary<string, object> body)
        {
            object rev, cfgObj;
            if (!body.TryGetValue("baseRev", out rev) || !(rev is string)) throw new ConfigException("Hiányzik a baseRev mező.");
            if (!body.TryGetValue("config", out cfgObj)) throw new ConfigException("Hiányzik a config mező.");
            AppConfig c = AppConfig.FromJson(cfgObj);
            lock (app.Sync)
            {
                if (c.Port != app.Config.Port) throw new ConfigException("A port a felületről nem módosítható.");
                string newRev;
                string backup = app.Store.Save(c, (string)rev, out newRev);
                app.Config = c;
                app.Rev = newRev;
                app.Mapper.SetConfig(c);
                app.Log.Add("Beállítás mentve" + (backup != "" ? ", az előző állapot: backups\\" + backup : "") + ".");
                app.UpdateTrayLater();
                return new JObj().Set("ok", true).Set("rev", newRev).Set("backup", backup);
            }
        }

        static bool Flag(Dictionary<string, object> b, string key)
        {
            object v;
            return b.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        void RequireDryRun()
        {
            if (!app.DryRun) throw new ConfigException("Ez a próbafunkció csak teszt módban (--dry-run) érhető el.");
        }

        JObj TestInput(Dictionary<string, object> body)
        {
            RequireDryRun();
            string device = body.ContainsKey("device") ? body["device"] as string : null;
            string input = body.ContainsKey("input") ? body["input"] as string : null;
            if (device == null || input == null || !Inputs.Valid(input)) throw new ConfigException("Érvénytelen eszköz vagy bemenet.");
            bool down = body.ContainsKey("down") && body["down"] is bool && (bool)body["down"];
            if (input[0] == 'b') app.Mapper.Button(device, input, down);
            else app.Mapper.Hat(device, input[1] - '0', down ? Inputs.DirIndex(input.Substring(2)) : -1);
            return new JObj().Set("ok", true);
        }

        JObj TestReport(Dictionary<string, object> body)
        {
            RequireDryRun();
            string device = body.ContainsKey("device") ? body["device"] as string : null;
            string hex = body.ContainsKey("hex") ? body["hex"] as string : null;
            if (device == null || hex == null || hex.Length % 2 != 0 || hex.Length > 1024) throw new ConfigException("Érvénytelen kérés.");
            byte[] report = new byte[hex.Length / 2];
            for (int i = 0; i < report.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out report[i])) throw new ConfigException("Érvénytelen hex.");
            }
            lock (app.Sync)
            {
                HidDevice d = app.Devices.FindByKey(device);
                if (d == null) throw new ConfigException("Nincs ilyen csatlakoztatott eszköz: " + device);
                if (report.Length != d.ReportLength) throw new ConfigException("A jelentés hossza " + d.ReportLength + " bájt kell legyen.");
                app.Devices.Feed(d, report);
                return new JObj().Set("ok", true).Set("pressed", d.Pressed());
            }
        }

        // ---------- plumbing ----------
        static JObj Err(string message) { return new JObj().Set("ok", false).Set("error", message); }

        static Dictionary<string, object> Body(HttpListenerRequest req)
        {
            if (req.ContentLength64 > MaxBody) throw new ConfigException("Túl nagy kérés.");
            string text;
            using (StreamReader sr = new StreamReader(req.InputStream, utf8))
            {
                char[] buf = new char[MaxBody + 1];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                if (n > MaxBody) throw new ConfigException("Túl nagy kérés.");
                text = new string(buf, 0, n);
            }
            object o;
            try { o = Json.Parse(text); }
            catch (Exception) { throw new ConfigException("A kérés nem érvényes JSON."); }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) throw new ConfigException("A kérés nem JSON-objektum.");
            return d;
        }

        static void Send(HttpListenerContext ctx, int status, string type, byte[] body)
        {
            HttpListenerResponse r = ctx.Response;
            r.StatusCode = status;
            r.ContentType = type;
            r.Headers["Cache-Control"] = "no-store";
            r.Headers["X-Content-Type-Options"] = "nosniff";
            r.Headers["Referrer-Policy"] = "no-referrer";
            r.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
            r.ContentLength64 = body.Length;
            if (ctx.Request.HttpMethod != "HEAD") r.OutputStream.Write(body, 0, body.Length);
            r.OutputStream.Close();
        }

        static void SendJson(HttpListenerContext ctx, int status, object o) { Send(ctx, status, "application/json; charset=utf-8", utf8.GetBytes(Json.Write(o, false))); }
        static void SendText(HttpListenerContext ctx, int status, string s) { Send(ctx, status, "text/plain; charset=utf-8", utf8.GetBytes(s)); }
    }
}
