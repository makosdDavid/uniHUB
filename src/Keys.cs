using System;
using System.Collections.Generic;

namespace UsbMapper
{
    // Keyboard keys the mapper can press. Code = DirectInput-style key code:
    // below 128 it is the set-1 scancode, from 128 the key is an extended (E0) key
    // with scancode = code - 128. Dom = KeyboardEvent.code in the browser, used by
    // the UI to record keys. Names follow the US key positions; the UI relabels
    // letters and symbols with the real keyboard layout when the browser allows it.
    sealed class KeyDef
    {
        public readonly int Code; public readonly string Dom; public readonly string Name; public readonly string Group;
        public KeyDef(int code, string dom, string name, string group) { Code = code; Dom = dom; Name = name; Group = group; }
    }

    static class Keys
    {
        public static readonly List<KeyDef> All = new List<KeyDef>();
        static readonly Dictionary<int, KeyDef> byCode = new Dictionary<int, KeyDef>();

        static void A(int code, string dom, string name, string group)
        {
            KeyDef k = new KeyDef(code, dom, name, group);
            All.Add(k);
            byCode[code] = k;
        }

        static Keys()
        {
            const string G1 = "Betűk és számok", G2 = "Írásjelek", G3 = "Módosítók", G4 = "Funkcióbillentyűk", G5 = "Vezérlés és nyilak", G6 = "Számbillentyűzet";
            string row1 = "QWERTYUIOP", row2 = "ASDFGHJKL", row3 = "ZXCVBNM";
            for (int i = 0; i < row1.Length; i++) A(0x10 + i, "Key" + row1[i], row1[i].ToString(), G1);
            for (int i = 0; i < row2.Length; i++) A(0x1E + i, "Key" + row2[i], row2[i].ToString(), G1);
            for (int i = 0; i < row3.Length; i++) A(0x2C + i, "Key" + row3[i], row3[i].ToString(), G1);
            for (int d = 1; d <= 9; d++) A(0x01 + d, "Digit" + d, d.ToString(), G1);
            A(0x0B, "Digit0", "0", G1);

            A(0x29, "Backquote", "`", G2);
            A(0x0C, "Minus", "-", G2);
            A(0x0D, "Equal", "=", G2);
            A(0x1A, "BracketLeft", "[", G2);
            A(0x1B, "BracketRight", "]", G2);
            A(0x27, "Semicolon", ";", G2);
            A(0x28, "Quote", "'", G2);
            A(0x2B, "Backslash", "\\", G2);
            A(0x33, "Comma", ",", G2);
            A(0x34, "Period", ".", G2);
            A(0x35, "Slash", "/", G2);
            A(0x56, "IntlBackslash", "<> (a bal Shift melletti)", G2);

            A(0x2A, "ShiftLeft", "Bal Shift", G3);
            A(0x36, "ShiftRight", "Jobb Shift", G3);
            A(0x1D, "ControlLeft", "Bal Ctrl", G3);
            A(0x9D, "ControlRight", "Jobb Ctrl", G3);
            A(0x38, "AltLeft", "Bal Alt", G3);
            A(0xB8, "AltRight", "Jobb Alt (AltGr)", G3);
            A(0xDB, "MetaLeft", "Bal Windows", G3);
            A(0xDC, "MetaRight", "Jobb Windows", G3);

            for (int f = 1; f <= 10; f++) A(0x3A + f, "F" + f, "F" + f, G4);
            A(0x57, "F11", "F11", G4);
            A(0x58, "F12", "F12", G4);
            for (int f = 13; f <= 23; f++) A(0x64 + (f - 13), "F" + f, "F" + f, G4);
            A(0x76, "F24", "F24", G4);

            A(0x01, "Escape", "Esc", G5);
            A(0x0F, "Tab", "Tab", G5);
            A(0x3A, "CapsLock", "Caps Lock", G5);
            A(0x39, "Space", "Szóköz", G5);
            A(0x1C, "Enter", "Enter", G5);
            A(0x0E, "Backspace", "Backspace", G5);
            A(0xD2, "Insert", "Insert", G5);
            A(0xD3, "Delete", "Delete", G5);
            A(0xC7, "Home", "Home", G5);
            A(0xCF, "End", "End", G5);
            A(0xC9, "PageUp", "Page Up", G5);
            A(0xD1, "PageDown", "Page Down", G5);
            A(0xC8, "ArrowUp", "Fel nyíl", G5);
            A(0xD0, "ArrowDown", "Le nyíl", G5);
            A(0xCB, "ArrowLeft", "Balra nyíl", G5);
            A(0xCD, "ArrowRight", "Jobbra nyíl", G5);
            A(0xB7, "PrintScreen", "Print Screen", G5);
            A(0x46, "ScrollLock", "Scroll Lock", G5);
            A(0xDD, "ContextMenu", "Helyi menü", G5);

            for (int n = 0; n <= 9; n++)
            {
                int[] sc = { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
                A(sc[n], "Numpad" + n, "Numpad " + n, G6);
            }
            A(0x53, "NumpadDecimal", "Numpad ,", G6);
            A(0x4E, "NumpadAdd", "Numpad +", G6);
            A(0x4A, "NumpadSubtract", "Numpad -", G6);
            A(0x37, "NumpadMultiply", "Numpad *", G6);
            A(0xB5, "NumpadDivide", "Numpad /", G6);
            A(0x9C, "NumpadEnter", "Numpad Enter", G6);
        }

        public static bool IsKnown(int code) { return byCode.ContainsKey(code); }

        public static string Name(int code)
        {
            KeyDef k;
            return byCode.TryGetValue(code, out k) ? k.Name : ("kód " + code);
        }

        static readonly Dictionary<int, string> vkNames = new Dictionary<int, string>
        {
            { 0x10, "Shift" }, { 0x11, "Ctrl" }, { 0x12, "Alt" }, { 0x5B, "Win" }, { 0x5D, "Helyi menü" }, { 0x09, "Tab" },
            { 0x25, "Balra" }, { 0x27, "Jobbra" }, { 0x73, "F4" }, { 0x7A, "F11" },
            { 0xAD, "Némítás" }, { 0xAE, "Hangerő le" }, { 0xAF, "Hangerő fel" }, { 0xB0, "Következő szám" }, { 0xB1, "Előző szám" },
            { 0xB2, "Leállítás" }, { 0xB3, "Lejátszás/szünet" }, { 0xAC, "Böngésző" }, { 0xB4, "Levelezés" }, { 0xB5, "Médialejátszó" },
            { 0xB6, "Fájlkezelő" }, { 0xB7, "Számológép" },
        };

        public static string VkName(int vk)
        {
            string n;
            if (vkNames.TryGetValue(vk, out n)) return n;
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            return "VK " + vk.ToString("X2");
        }

        public static string Combo(int[] codes)
        {
            if (codes == null || codes.Length == 0) return "(üres)";
            string[] parts = new string[codes.Length];
            for (int i = 0; i < codes.Length; i++) parts[i] = Name(codes[i]);
            return string.Join("+", parts);
        }
    }
}
