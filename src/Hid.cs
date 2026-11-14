using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace UsbMapper
{
    sealed class HatCap
    {
        public ushort Link;
        public int Min, Max;
    }

    sealed class AxisCap
    {
        public ushort Link, Usage;
        public int Min, Max, Bits;
        public string Name;
        public double Last = double.NaN;
    }

    // One game controller seen through Raw Input. Reports are decoded with the
    // device's own HID descriptor (preparsed data), so any HID controller works.
    sealed class HidDevice : IDisposable
    {
        public IntPtr Handle;
        public string Path;
        public string Key;      // "VVVV:PPPP"
        public string Name;
        public int ButtonCount;
        public readonly List<HatCap> Hats = new List<HatCap>();
        public readonly List<AxisCap> Axes = new List<AxisCap>();
        public static readonly string[] AxisNames = { "X", "Y", "Z", "RX", "RY", "RZ", "SLIDER", "DIAL" };
        public static Action<HidDevice, string, double> OnAxis = delegate { };
        public readonly bool[] Buttons = new bool[Inputs.MaxButton + 1];
        public int[] HatDir;
        public int ReportLength;
        IntPtr preparsed;
        ushort[] usageBuf;

        public static HidDevice TryOpen(IntPtr handle)
        {
            Native.RID_DEVICE_INFO_HID info = new Native.RID_DEVICE_INFO_HID();
            info.cbSize = 32;
            uint size = 32;
            if (Native.GetRawInputDeviceInfoStruct(handle, Native.RIDI_DEVICEINFO, ref info, ref size) == unchecked((uint)-1)) return null;
            if (info.dwType != Native.RIM_TYPEHID || info.usUsagePage != 1) return null;
            if (info.usUsage != 4 && info.usUsage != 5 && info.usUsage != 8) return null;

            HidDevice d = new HidDevice();
            d.Handle = handle;
            d.Key = ((int)info.dwVendorId).ToString("X4") + ":" + ((int)info.dwProductId).ToString("X4");

            uint chars = 0;
            Native.GetRawInputDeviceInfoString(handle, Native.RIDI_DEVICENAME, null, ref chars);
            if (chars > 0 && chars < 4096)
            {
                StringBuilder sb = new StringBuilder((int)chars + 1);
                if (Native.GetRawInputDeviceInfoString(handle, Native.RIDI_DEVICENAME, sb, ref chars) != unchecked((uint)-1)) d.Path = sb.ToString();
            }

            uint ppSize = 0;
            Native.GetRawInputDeviceInfo(handle, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
            if (ppSize == 0 || ppSize > 1024 * 1024) return null;
            d.preparsed = Marshal.AllocHGlobal((int)ppSize);
            if (Native.GetRawInputDeviceInfo(handle, Native.RIDI_PREPARSEDDATA, d.preparsed, ref ppSize) == unchecked((uint)-1)) { d.Dispose(); return null; }

            if (!d.ReadCaps()) { d.Dispose(); return null; }
            d.Name = d.ReadProductName() ?? ("USB-játékvezérlő " + d.Key);
            return d;
        }

        bool ReadCaps()
        {
            byte[] caps = new byte[64];
            if (Native.HidP_GetCaps(preparsed, caps) != Native.HIDP_STATUS_SUCCESS) return false;
            ReportLength = BitConverter.ToUInt16(caps, 4);
            int nButtonCaps = BitConverter.ToUInt16(caps, 46);
            int nValueCaps = BitConverter.ToUInt16(caps, 48);

            if (nButtonCaps > 0)
            {
                ushort n = (ushort)nButtonCaps;
                byte[] bc = new byte[72 * n];
                if (Native.HidP_GetButtonCaps(Native.HIDP_INPUT, bc, ref n, preparsed) == Native.HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 72;
                        if (BitConverter.ToUInt16(bc, o) != 9) continue;              // UsagePage: Button
                        bool isRange = bc[o + 12] != 0;
                        int max = isRange ? BitConverter.ToUInt16(bc, o + 58) : BitConverter.ToUInt16(bc, o + 56);
                        if (max > ButtonCount) ButtonCount = Math.Min(max, Inputs.MaxButton);
                    }
                }
            }
            if (nValueCaps > 0)
            {
                ushort n = (ushort)nValueCaps;
                byte[] vc = new byte[72 * n];
                if (Native.HidP_GetValueCaps(Native.HIDP_INPUT, vc, ref n, preparsed) == Native.HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < n && Hats.Count < 4; i++)
                    {
                        int o = i * 72;
                        if (BitConverter.ToUInt16(vc, o) != 1) continue;              // UsagePage: Generic Desktop
                        bool isRange = vc[o + 12] != 0;
                        int usageMin = BitConverter.ToUInt16(vc, o + 56);
                        int usageMax = isRange ? BitConverter.ToUInt16(vc, o + 58) : usageMin;
                        for (int u = Math.Max(usageMin, 0x30); u <= Math.Min(usageMax, 0x37) && Axes.Count < 8; u++)
                        {
                            AxisCap ac = new AxisCap();
                            ac.Link = BitConverter.ToUInt16(vc, o + 6);
                            ac.Usage = (ushort)u;
                            ac.Min = BitConverter.ToInt32(vc, o + 40);
                            ac.Max = BitConverter.ToInt32(vc, o + 44);
                            ac.Bits = BitConverter.ToUInt16(vc, o + 18);
                            ac.Name = AxisNames[u - 0x30];
                            if (ac.Max > ac.Min) Axes.Add(ac);
                        }
                        if (0x39 < usageMin || 0x39 > usageMax) continue;             // Hat switch
                        HatCap h = new HatCap();
                        h.Link = BitConverter.ToUInt16(vc, o + 6);
                        h.Min = BitConverter.ToInt32(vc, o + 40);
                        h.Max = BitConverter.ToInt32(vc, o + 44);
                        Hats.Add(h);
                    }
                }
            }
            HatDir = new int[Hats.Count];
            for (int i = 0; i < HatDir.Length; i++) HatDir[i] = -1;
            uint maxUsages = Native.HidP_MaxUsageListLength(Native.HIDP_INPUT, 9, preparsed);
            usageBuf = new ushort[Math.Max(1, (int)Math.Min(maxUsages, 4096))];
            return ButtonCount > 0 || Hats.Count > 0;
        }

        string ReadProductName()
        {
            if (string.IsNullOrEmpty(Path)) return null;
            // access 0: the name can be read without opening the device for input
            using (SafeFileHandle h = Native.CreateFile(Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                byte[] buf = new byte[256];
                if (!Native.HidD_GetProductString(h, buf, (uint)buf.Length)) return null;
                string s = Encoding.Unicode.GetString(buf);
                int z = s.IndexOf('\0');
                if (z >= 0) s = s.Substring(0, z);
                s = s.Trim();
                return s.Length > 0 ? s : null;
            }
        }

        static int HatToDir(uint raw, HatCap cap)
        {
            long rel = (long)raw - cap.Min;
            long positions = (long)cap.Max - cap.Min + 1;
            if (rel < 0 || rel >= positions) return -1;   // outside the logical range = centred
            if (positions == 8) return (int)rel;
            if (positions == 4) return (int)rel * 2;
            return -1;
        }

        // Decodes one input report and calls back for every change.
        public void Process(byte[] report, Action<HidDevice, string, bool> onButton, Action<HidDevice, int, int> onHat)
        {
            uint len = (uint)report.Length;
            if (ButtonCount > 0)
            {
                uint n = (uint)usageBuf.Length;
                // a report that does not carry the buttons (other report id) leaves their state alone
                if (Native.HidP_GetUsages(Native.HIDP_INPUT, 9, 0, usageBuf, ref n, preparsed, report, len) == Native.HIDP_STATUS_SUCCESS)
                {
                    bool[] now = new bool[Inputs.MaxButton + 1];
                    for (int i = 0; i < n; i++) if (usageBuf[i] >= 1 && usageBuf[i] <= Inputs.MaxButton) now[usageBuf[i]] = true;
                    for (int u = 1; u <= Inputs.MaxButton; u++)
                    {
                        if (now[u] == Buttons[u]) continue;
                        Buttons[u] = now[u];
                        onButton(this, "b" + u, now[u]);
                    }
                }
            }
            for (int i = 0; i < Hats.Count; i++)
            {
                uint v;
                if (Native.HidP_GetUsageValue(Native.HIDP_INPUT, 1, Hats[i].Link, 0x39, out v, preparsed, report, len) != Native.HIDP_STATUS_SUCCESS) continue;
                int dir = HatToDir(v, Hats[i]);
                if (dir == HatDir[i]) continue;
                HatDir[i] = dir;
                onHat(this, i + 1, dir);
            }
            foreach (AxisCap a in Axes)
            {
                uint v;
                if (Native.HidP_GetUsageValue(Native.HIDP_INPUT, 1, a.Link, a.Usage, out v, preparsed, report, len) != Native.HIDP_STATUS_SUCCESS) continue;
                long raw = v;
                if (a.Min < 0 && a.Bits > 0 && a.Bits < 32 && (raw & (1L << (a.Bits - 1))) != 0) raw -= 1L << a.Bits;   // signed field
                double n = ((double)raw - a.Min) / ((double)a.Max - a.Min);           // 0..1
                if (a.Usage < 0x36) n = n * 2 - 1;                                 // centred axes: -1..1
                n = Math.Max(-1, Math.Min(1, n));
                if (!double.IsNaN(a.Last) && Math.Abs(n - a.Last) < 0.01) continue;
                a.Last = n;
                OnAxis(this, a.Name, n);
            }
        }

        // Inputs held right now, for the live view of the UI.
        public List<string> Pressed()
        {
            List<string> list = new List<string>();
            for (int u = 1; u <= Inputs.MaxButton; u++) if (Buttons[u]) list.Add("b" + u);
            for (int i = 0; i < HatDir.Length; i++) if (HatDir[i] >= 0) list.Add(Inputs.Hat(i + 1, HatDir[i]));
            return list;
        }

        public void Dispose()
        {
            if (preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(preparsed); preparsed = IntPtr.Zero; }
        }
    }

    sealed class DeviceManager
    {
        readonly Dictionary<IntPtr, HidDevice> byHandle = new Dictionary<IntPtr, HidDevice>();
        public int Revision;   // grows on every arrival / removal, the UI reloads its device list on change
        public Action<HidDevice, string, bool> OnButton = delegate { };
        public Action<HidDevice, int, int> OnHat = delegate { };
        public Action<string> Log = delegate { };

        public IEnumerable<HidDevice> All { get { return byHandle.Values; } }

        public HidDevice FindByKey(string key)
        {
            foreach (HidDevice d in byHandle.Values) if (d.Key == key) return d;
            return null;
        }

        public void Enumerate()
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST));
            Native.GetRawInputDeviceList(null, ref count, size);
            if (count == 0) return;
            Native.RAWINPUTDEVICELIST[] list = new Native.RAWINPUTDEVICELIST[count];
            uint got = Native.GetRawInputDeviceList(list, ref count, size);
            if (got == unchecked((uint)-1)) return;
            for (int i = 0; i < got; i++)
                if (list[i].dwType == Native.RIM_TYPEHID) Add(list[i].hDevice);
        }

        public HidDevice Add(IntPtr handle)
        {
            HidDevice d;
            if (byHandle.TryGetValue(handle, out d)) return d;
            d = HidDevice.TryOpen(handle);
            if (d == null) return null;
            byHandle[handle] = d;
            Revision++;
            Log("Eszköz csatlakoztatva: " + d.Name + " (" + d.Key + ", " + d.ButtonCount + " gomb, " + d.Hats.Count + " kalap)");
            return d;
        }

        public void Remove(IntPtr handle)
        {
            HidDevice d;
            if (!byHandle.TryGetValue(handle, out d)) return;
            // let go of everything the device held
            for (int u = 1; u <= Inputs.MaxButton; u++) if (d.Buttons[u]) { d.Buttons[u] = false; OnButton(d, "b" + u, false); }
            for (int i = 0; i < d.HatDir.Length; i++) if (d.HatDir[i] >= 0) { d.HatDir[i] = -1; OnHat(d, i + 1, -1); }
            byHandle.Remove(handle);
            d.Dispose();
            Revision++;
            Log("Eszköz leválasztva: " + d.Name + " (" + d.Key + ")");
        }

        public void Feed(HidDevice d, byte[] report)
        {
            d.Process(report, OnButton, OnHat);
        }
    }

    // Hidden window that receives WM_INPUT for every joystick, gamepad and multi-axis controller,
    // also while another program (the game) is in the foreground.
    sealed class RawInputWindow : NativeWindow
    {
        readonly DeviceManager devices;
        readonly object sync;
        readonly int headerSize = Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
        IntPtr buffer = IntPtr.Zero;
        uint bufferSize;
        readonly Dictionary<int, byte[]> reportBufs = new Dictionary<int, byte[]>();

        public RawInputWindow(DeviceManager devices, object sync)
        {
            this.devices = devices;
            this.sync = sync;
            CreateHandle(new CreateParams());
            Native.RAWINPUTDEVICE[] rid = new Native.RAWINPUTDEVICE[3];
            ushort[] usages = { 4, 5, 8 };
            for (int i = 0; i < 3; i++)
            {
                rid[i].usUsagePage = 1;
                rid[i].usUsage = usages[i];
                rid[i].dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY;
                rid[i].hwndTarget = Handle;
            }
            if (!Native.RegisterRawInputDevices(rid, 3, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                throw new InvalidOperationException("A Raw Input regisztráció nem sikerült (hibakód " + Marshal.GetLastWin32Error() + ").");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT) { try { OnInput(m.LParam); } catch (Exception e) { devices.Log("HIBA a bemenet feldolgozásában: " + e.Message); } }
            else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE)
            {
                lock (sync)
                {
                    if ((int)m.WParam == Native.GIDC_ARRIVAL) devices.Add(m.LParam);
                    else if ((int)m.WParam == Native.GIDC_REMOVAL) devices.Remove(m.LParam);
                }
            }
            base.WndProc(ref m);
        }

        void OnInput(IntPtr hRawInput)
        {
            uint size = 0;
            Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
            if (size == 0 || size > 65536) return;
            if (size > bufferSize)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                bufferSize = size;
            }
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buffer, ref size, (uint)headerSize) != size) return;
            Native.RAWINPUTHEADER hdr = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(Native.RAWINPUTHEADER));
            if (hdr.dwType != Native.RIM_TYPEHID) return;
            int sizeHid = Marshal.ReadInt32(buffer, headerSize);
            int count = Marshal.ReadInt32(buffer, headerSize + 4);
            if (sizeHid <= 0 || count <= 0 || headerSize + 8 + (long)sizeHid * count > size) return;
            byte[] report;
            if (!reportBufs.TryGetValue(sizeHid, out report)) { report = new byte[sizeHid]; reportBufs[sizeHid] = report; }
            lock (sync)
            {
                HidDevice d = devices.Add(hdr.hDevice);
                if (d == null) return;
                for (int i = 0; i < count; i++)
                {
                    Marshal.Copy(IntPtr.Add(buffer, headerSize + 8 + i * sizeHid), report, 0, sizeHid);
                    devices.Feed(d, report);
                }
            }
        }

        public void Close()
        {
            DestroyHandle();
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
        }
    }
}
