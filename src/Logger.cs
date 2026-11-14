using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UsbMapper
{
    sealed class Logger
    {
        sealed class Entry { public long Seq; public string Time; public string Text; }
        readonly object gate = new object();
        readonly List<Entry> entries = new List<Entry>();
        readonly string file;
        long seq;
        const int Keep = 300;
        const long MaxFile = 1024 * 1024;

        public Logger(string file) { this.file = file; }

        public long Seq { get { lock (gate) return seq; } }

        public void Add(string text)
        {
            lock (gate)
            {
                DateTime now = DateTime.Now;
                Entry e = new Entry();
                e.Seq = ++seq;
                e.Time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                e.Text = text;
                entries.Add(e);
                if (entries.Count > Keep) entries.RemoveRange(0, entries.Count - Keep);
                if (file == null) return;
                try
                {
                    FileInfo fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > MaxFile)
                    {
                        string old = file + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file, now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public List<object> Since(long after)
        {
            List<object> list = new List<object>();
            lock (gate)
            {
                foreach (Entry e in entries)
                    if (e.Seq > after) list.Add(new JObj().Set("seq", e.Seq).Set("time", e.Time).Set("text", e.Text));
            }
            return list;
        }
    }

    // Turns controller events into key events according to the active profile.
    // Every public method takes the shared lock; macro threads take it around each key event.
}
