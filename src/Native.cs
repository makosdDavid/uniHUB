using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbMapper
{
    // Win32 declarations. The program is built for x64 only (see build.ps1); the
    // struct layouts below assume 64-bit pointers.
    static class Native
    {
        public const int WM_INPUT = 0x00FF;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        public const int GIDC_ARRIVAL = 1;
        public const int GIDC_REMOVAL = 2;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_PREPARSEDDATA = 0x20000005;
        public const uint RIDI_DEVICENAME = 0x20000007;
        public const uint RIDI_DEVICEINFO = 0x2000000b;
        public const uint RIM_TYPEHID = 2;

        public const int HIDP_INPUT = 0;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

        // RID_DEVICE_INFO with the HID member of the union; the two pad fields bring it to sizeof(RID_DEVICE_INFO) = 32.
        [StructLayout(LayoutKind.Sequential)]
        public struct RID_DEVICE_INFO_HID
        {
            public uint cbSize; public uint dwType;
            public uint dwVendorId; public uint dwProductId; public uint dwVersionNumber;
            public ushort usUsagePage; public ushort usUsage;
            public uint pad1; public uint pad2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint cbSize);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint numDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
        public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfoString(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
        public static extern uint GetRawInputDeviceInfoStruct(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO_HID pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public const uint KEYEVENTF_UNICODE = 0x0004;

        // ViGEmBus access
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle device, IntPtr overlapped, out uint transferred, bool wait);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        // low-level hooks (macro recording)
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x; public int y; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        // HIDP_CAPS is read from a 64-byte buffer (see HidDevice), the caps arrays from 72-byte records.
        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, [Out] byte[] caps);

        [DllImport("hid.dll")]
        public static extern int HidP_GetButtonCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetProductString(SafeFileHandle device, [Out] byte[] buffer, uint length);
    }
}
