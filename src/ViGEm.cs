using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsbMapper
{
    // Virtual Xbox 360 controller through the ViGEmBus driver (1.22.0), spoken to directly with
    // DeviceIoControl, following the official ViGEmClient (MIT): open the bus interface, check the
    // version, plug in a target, wait until ready, submit reports, unplug on exit.
    sealed class VirtualPad : IDisposable
    {
        static readonly Guid BusInterface = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
        const uint IOCTL_PLUGIN = 0x002AA004, IOCTL_UNPLUG = 0x002AA008, IOCTL_CHECK_VERSION = 0x002AA00C,
                   IOCTL_WAIT_READY = 0x002AA010, IOCTL_XUSB_SUBMIT = 0x002AA808;

        public static readonly string[] Buttons = { "UP", "DOWN", "LEFT", "RIGHT", "START", "BACK", "LS", "RS", "LB", "RB", "GUIDE", "A", "B", "X", "Y" };
        static readonly Dictionary<string, ushort> bits = new Dictionary<string, ushort>
        {
            { "UP", 0x0001 }, { "DOWN", 0x0002 }, { "LEFT", 0x0004 }, { "RIGHT", 0x0008 }, { "START", 0x0010 }, { "BACK", 0x0020 },
            { "LS", 0x0040 }, { "RS", 0x0080 }, { "LB", 0x0100 }, { "RB", 0x0200 }, { "GUIDE", 0x0400 },
            { "A", 0x1000 }, { "B", 0x2000 }, { "X", 0x4000 }, { "Y", 0x8000 },
        };
        public static readonly string[] Controls = BuildControls();
        public static readonly string[] Axes = { "LX", "LY", "RX", "RY", "LT", "RT" };

        static string[] BuildControls()
        {
            List<string> c = new List<string>(Buttons);
            c.AddRange(new[] { "LT", "RT", "LS_UP", "LS_DOWN", "LS_LEFT", "LS_RIGHT", "RS_UP", "RS_DOWN", "RS_LEFT", "RS_RIGHT" });
            return c.ToArray();
        }

        readonly object gate = new object();
        readonly Logger log;
        SafeFileHandle bus;
        uint serial;
        bool failed;
        ushort buttons;
        byte lt, rt;
        short lx, ly, rx, ry;

        public VirtualPad(Logger log) { this.log = log; }

        public bool Connected { get { lock (gate) return serial != 0; } }

        public bool Set(string control, bool down)
        {
            lock (gate)
            {
                ushort b;
                if (bits.TryGetValue(control, out b)) { if (down) buttons |= b; else buttons &= (ushort)~b; }
                else switch (control)
                    {
                        case "LT": lt = down ? (byte)255 : (byte)0; break;
                        case "RT": rt = down ? (byte)255 : (byte)0; break;
                        case "LS_UP": ly = down ? short.MaxValue : (short)0; break;
                        case "LS_DOWN": ly = down ? short.MinValue : (short)0; break;
                        case "LS_LEFT": lx = down ? short.MinValue : (short)0; break;
                        case "LS_RIGHT": lx = down ? short.MaxValue : (short)0; break;
                        case "RS_UP": ry = down ? short.MaxValue : (short)0; break;
                        case "RS_DOWN": ry = down ? short.MinValue : (short)0; break;
                        case "RS_LEFT": rx = down ? short.MinValue : (short)0; break;
                        case "RS_RIGHT": rx = down ? short.MaxValue : (short)0; break;
                        default: return false;
                    }
                return Submit();
            }
        }

        public void SetAxis(string axis, int value)
        {
            lock (gate)
            {
                switch (axis)
                {
                    case "LX": lx = Clamp16(value); break;
                    case "LY": ly = Clamp16(value); break;
                    case "RX": rx = Clamp16(value); break;
                    case "RY": ry = Clamp16(value); break;
                    case "LT": lt = (byte)Math.Max(0, Math.Min(255, value)); break;
                    case "RT": rt = (byte)Math.Max(0, Math.Min(255, value)); break;
                    default: return;
                }
                Submit();
            }
        }

        static short Clamp16(int v) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v)); }

        public void Reset()
        {
            lock (gate)
            {
                buttons = 0; lt = rt = 0; lx = ly = rx = ry = 0;
                if (serial != 0) Submit();
            }
        }

        bool Submit()
        {
            if (!EnsureConnected()) return false;
            byte[] r = new byte[20];
            BitConverter.GetBytes(20u).CopyTo(r, 0);
            BitConverter.GetBytes(serial).CopyTo(r, 4);
            BitConverter.GetBytes(buttons).CopyTo(r, 8);
            r[10] = lt; r[11] = rt;
            BitConverter.GetBytes(lx).CopyTo(r, 12);
            BitConverter.GetBytes(ly).CopyTo(r, 14);
            BitConverter.GetBytes(rx).CopyTo(r, 16);
            BitConverter.GetBytes(ry).CopyTo(r, 18);
            return Ioctl(IOCTL_XUSB_SUBMIT, r);
        }

        bool EnsureConnected()
        {
            if (serial != 0) return true;
            if (failed) return false;
            try
            {
                string path = FindBus();
                if (path == null) throw new InvalidOperationException("a ViGEmBus illesztő nem található.");
                bus = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80u | 0x20000000u | 0x80000000u | 0x40000000u, IntPtr.Zero);
                if (bus.IsInvalid) throw new InvalidOperationException("a ViGEmBus nem nyitható meg (hibakód " + Marshal.GetLastWin32Error() + ").");
                if (!Ioctl(IOCTL_CHECK_VERSION, Pair(8, 1))) throw new InvalidOperationException("a ViGEmBus verziója nem egyezik.");
                for (uint s = 1; s <= 16; s++)
                {
                    byte[] plug = new byte[16];
                    BitConverter.GetBytes(16u).CopyTo(plug, 0);
                    BitConverter.GetBytes(s).CopyTo(plug, 4);
                    BitConverter.GetBytes(0u).CopyTo(plug, 8);          // Xbox360Wired
                    BitConverter.GetBytes((ushort)0x045E).CopyTo(plug, 12);
                    BitConverter.GetBytes((ushort)0x028E).CopyTo(plug, 14);
                    if (!Ioctl(IOCTL_PLUGIN, plug)) continue;
                    int err;
                    if (!Ioctl(IOCTL_WAIT_READY, Pair(8, s), out err) && err != 87)
                    {
                        Ioctl(IOCTL_UNPLUG, Pair(8, s));
                        throw new InvalidOperationException("a virtuális vezérlő nem lett kész (hibakód " + err + ").");
                    }
                    serial = s;
                    log.Add("Virtuális Xbox 360 vezérlő csatlakoztatva (ViGEm, " + s + ". hely).");
                    return true;
                }
                throw new InvalidOperationException("nincs szabad hely a ViGEmBuson.");
            }
            catch (Exception e)
            {
                failed = true;
                if (bus != null) { bus.Dispose(); bus = null; }
                log.Add("HIBA: a virtuális gamepad nem érhető el: " + e.Message);
                return false;
            }
        }

        static byte[] Pair(uint size, uint value)
        {
            byte[] b = new byte[8];
            BitConverter.GetBytes(size).CopyTo(b, 0);
            BitConverter.GetBytes(value).CopyTo(b, 4);
            return b;
        }

        bool Ioctl(uint code, byte[] input) { int err; return Ioctl(code, input, out err); }

        bool Ioctl(uint code, byte[] input, out int error)
        {
            error = 0;
            using (ManualResetEvent ev = new ManualResetEvent(false))
            {
                NativeOverlapped ov = new NativeOverlapped();
                ov.EventHandle = ev.SafeWaitHandle.DangerousGetHandle();
                IntPtr pov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                GCHandle pin = GCHandle.Alloc(input, GCHandleType.Pinned);
                try
                {
                    Marshal.StructureToPtr(ov, pov, false);
                    uint got;
                    Native.DeviceIoControl(bus, code, pin.AddrOfPinnedObject(), (uint)input.Length, IntPtr.Zero, 0, out got, pov);
                    if (Native.GetOverlappedResult(bus, pov, out got, true)) return true;
                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                finally { pin.Free(); Marshal.FreeHGlobal(pov); }
            }
        }

        static string FindBus()
        {
            Guid g = BusInterface;
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (set == new IntPtr(-1)) return null;
            try
            {
                Native.SP_DEVICE_INTERFACE_DATA data = new Native.SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
                {
                    uint need = 0;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need == 0 || need > 4096) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        Marshal.WriteInt32(buf, 8);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out need, IntPtr.Zero))
                            return Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return null;
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (serial != 0)
                {
                    try { buttons = 0; lt = rt = 0; lx = ly = rx = ry = 0; Submit(); Ioctl(IOCTL_UNPLUG, Pair(8, serial)); } catch (Exception) { }
                    serial = 0;
                }
                if (bus != null) { bus.Dispose(); bus = null; }
            }
        }
    }
}
