using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbMapper
{
    sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    // Input ids: "b<n>" = button n (HID button usage n, the Windows button number),
    // "h<k><dir>" = direction of hat k (U, UR, R, DR, D, DL, L, UL).
    static class Inputs
    {
        public static readonly string[] Dirs = { "U", "UR", "R", "DR", "D", "DL", "L", "UL" };
        static readonly string[] DirHu = { "fel", "jobbra fel", "jobbra", "jobbra le", "le", "balra le", "balra", "balra fel" };
        static readonly Regex re = new Regex(@"^(?:b([1-9][0-9]{0,2})|h([1-4])(U|UR|R|DR|D|DL|L|UL))$", RegexOptions.CultureInvariant);
        public const int MaxButton = 128;

        public static bool Valid(string s)
        {
            if (s == null) return false;
            Match m = re.Match(s);
            if (!m.Success) return false;
            if (m.Groups[1].Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > MaxButton) return false;
            return true;
        }

        public static string Hat(int hat, int dir) { return "h" + hat + Dirs[dir]; }

        public static int DirIndex(string d) { return Array.IndexOf(Dirs, d); }

        public static string Name(string s)
        {
            Match m = re.Match(s ?? "");
            if (!m.Success) return s;
            if (m.Groups[1].Success) return m.Groups[1].Value + ". gomb";
            string hat = m.Groups[2].Value == "1" ? "Kalap " : m.Groups[2].Value + ". kalap ";
            return hat + DirHu[DirIndex(m.Groups[3].Value)];
        }
    }

    // Reads and writes config.json next to the program. Every write keeps the previous file in backups\.
    sealed class ConfigStore
    {
        public readonly string FilePath;
        public readonly string BackupDir;
        static readonly UTF8Encoding utf8 = new UTF8Encoding(false);
        const int KeepBackups = 50;

        public ConfigStore(string dir)
        {
            FilePath = Path.Combine(dir, "config.json");
            BackupDir = Path.Combine(dir, "backups");
        }

        public static string Rev(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder();
                foreach (byte x in h) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        public string CurrentRev()
        {
            return File.Exists(FilePath) ? Rev(File.ReadAllBytes(FilePath)) : "";
        }

        // Missing file: a default configuration is written. Unreadable file: ConfigException, the file is left alone.
        public AppConfig Load(out string rev)
        {
            if (!File.Exists(FilePath))
            {
                AppConfig d = AppConfig.CreateDefault();
                Save(d, "", out rev);
                return d;
            }
            byte[] bytes = File.ReadAllBytes(FilePath);
            object root;
            try { root = Json.Parse(utf8.GetString(bytes).TrimStart('﻿')); }
            catch (Exception e) { throw new ConfigException("A config.json nem érvényes JSON: " + e.Message); }
            AppConfig c = AppConfig.FromJson(root);
            rev = Rev(bytes);
            return c;
        }

        // Returns the backup file name ("" when there was no previous file); newRev receives the new revision.
        public string Save(AppConfig c, string baseRev, out string newRev)
        {
            string cur = CurrentRev();
            if (cur != baseRev) throw new ConfigException("A config.json megváltozott a betöltés óta. Töltsd újra az oldalt, és végezd el újra a módosítást.");
            byte[] bytes = utf8.GetBytes(Json.Write(c.ToJson(), true) + "\n");
            string backup = "";
            if (File.Exists(FilePath))
            {
                Directory.CreateDirectory(BackupDir);
                backup = "config_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json";
                File.Copy(FilePath, Path.Combine(BackupDir, backup), false);
                Prune();
            }
            string tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Copy(tmp, FilePath, true);
            File.Delete(tmp);
            newRev = Rev(File.ReadAllBytes(FilePath));
            if (newRev != Rev(bytes)) throw new ConfigException("A kiírás utáni ellenőrzés nem egyezik. Az előző állapot: backups\\" + backup);
            return backup;
        }

        void Prune()
        {
            string[] files = Directory.GetFiles(BackupDir, "config_*.json");
            if (files.Length <= KeepBackups) return;
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - KeepBackups; i++)
            {
                try { File.Delete(files[i]); } catch (IOException) { }
            }
        }
    }
}
