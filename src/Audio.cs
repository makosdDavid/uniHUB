using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UsbMapper
{
    // Windows Core Audio: mute, microphone level and default-device switching (G HUB "Audio" actions).
    static class CoreAudio
    {
        const int eRender = 0, eCapture = 1, DEVICE_STATE_ACTIVE = 1;

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int count);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(int channel, float level, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(int channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Undocumented but long-stable interface used by the Windows sound panel to change the default device.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr defPeriod, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pv);

        static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }

        static IAudioEndpointVolume Volume(IMMDevice dev)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            Check(dev.Activate(ref iid, 23, IntPtr.Zero, out o));   // CLSCTX_ALL
            return (IAudioEndpointVolume)o;
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        static string Name(IMMDevice dev)
        {
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) < 0) return "?";
            PROPERTYKEY key = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
            PROPVARIANT v;
            if (store.GetValue(ref key, out v) < 0) return "?";
            string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "?";
            PropVariantClear(ref v);
            return s;
        }

        public static string Run(string action)
        {
            IMMDeviceEnumerator en = Enumerator();
            IMMDevice dev;
            Guid ctx = Guid.Empty;
            switch (action)
            {
                case "mute_speakers":
                case "mute_mic":
                    {
                        Check(en.GetDefaultAudioEndpoint(action == "mute_mic" ? eCapture : eRender, 0, out dev));
                        IAudioEndpointVolume vol = Volume(dev);
                        bool m;
                        Check(vol.GetMute(out m));
                        Check(vol.SetMute(!m, ref ctx));
                        return (action == "mute_mic" ? "mikrofon " : "hangszóró ") + (m ? "némítás ki" : "némítva") + " (" + Name(dev) + ")";
                    }
                case "mic_up":
                case "mic_down":
                    {
                        Check(en.GetDefaultAudioEndpoint(eCapture, 0, out dev));
                        IAudioEndpointVolume vol = Volume(dev);
                        float l;
                        Check(vol.GetMasterVolumeLevelScalar(out l));
                        l = Math.Max(0f, Math.Min(1f, l + (action == "mic_up" ? 0.05f : -0.05f)));
                        Check(vol.SetMasterVolumeLevelScalar(l, ref ctx));
                        return "mikrofonszint " + Math.Round(l * 100) + "% (" + Name(dev) + ")";
                    }
                case "out_cycle":
                case "in_cycle":
                    {
                        int flow = action == "in_cycle" ? eCapture : eRender;
                        IMMDeviceCollection col;
                        Check(en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out col));
                        int n;
                        Check(col.GetCount(out n));
                        if (n < 2) return "csak egy aktív eszköz van, nincs mire váltani";
                        string cur = null;
                        if (en.GetDefaultAudioEndpoint(flow, 0, out dev) >= 0) dev.GetId(out cur);
                        List<IMMDevice> list = new List<IMMDevice>();
                        int at = -1;
                        for (int i = 0; i < n; i++)
                        {
                            IMMDevice d;
                            Check(col.Item(i, out d));
                            string id;
                            d.GetId(out id);
                            if (id == cur) at = i;
                            list.Add(d);
                        }
                        IMMDevice next = list[(at + 1) % n];
                        string nextId;
                        next.GetId(out nextId);
                        IPolicyConfig pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")));
                        for (int role = 0; role < 3; role++) Check(pc.SetDefaultEndpoint(nextId, role));
                        return (flow == eCapture ? "bemenet: " : "kimenet: ") + Name(next);
                    }
            }
            return "ismeretlen hangművelet: " + action;
        }
    }

    // Plays sound files (wav, mp3, wma) through MCI on one dedicated thread (G HUB "audio sample").
    sealed class SoundBank : IDisposable
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr callback);

        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly Dictionary<string, string> open = new Dictionary<string, string>();   // key -> alias
        readonly HashSet<string> looping = new HashSet<string>();
        readonly Logger log;
        int next;

        public SoundBank(Logger log)
        {
            this.log = log;
            Thread t = new Thread(delegate () { foreach (Action a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception e) { this.log.Add("HIBA (hang): " + e.Message); } } });
            t.IsBackground = true;
            t.Name = "sound";
            t.Start();
        }

        void Mci(string cmd)
        {
            StringBuilder sb = new StringBuilder(256);
            int err = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            if (err != 0) throw new InvalidOperationException("MCI hiba " + err + ": " + cmd);
        }

        void Close(string key)
        {
            string alias;
            if (!open.TryGetValue(key, out alias)) return;
            try { mciSendString("close " + alias, null, 0, IntPtr.Zero); } catch (Exception) { }
            open.Remove(key);
            looping.Remove(key);
        }

        void Start(string key, string path, bool loop)
        {
            Close(key);
            string alias = "umsnd" + (++next);
            Mci("open \"" + path + "\" type mpegvideo alias " + alias);
            open[key] = alias;
            Mci("play " + alias + (loop ? " repeat" : ""));
            if (loop) looping.Add(key);
        }

        // op: once | hold-start | hold-stop | toggle | stopall
        public void Run(string op, string key, string path)
        {
            queue.Add(delegate
            {
                switch (op)
                {
                    case "once": Start(key, path, false); break;
                    case "hold-start": Start(key, path, true); break;
                    case "hold-stop": Close(key); break;
                    case "toggle": if (looping.Contains(key)) Close(key); else Start(key, path, true); break;
                    case "stopall": foreach (string k in new List<string>(open.Keys)) Close(k); break;
                }
            });
        }

        public void Dispose()
        {
            Run("stopall", null, null);
            queue.CompleteAdding();
        }
    }
}
