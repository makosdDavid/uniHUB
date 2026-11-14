using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace UsbMapper
{
    // An object whose keys keep their order when written out.
    sealed class JObj : List<KeyValuePair<string, object>>
    {
        public JObj Set(string key, object value) { Add(new KeyValuePair<string, object>(key, value)); return this; }
    }

    static class Json
    {
        public static object Parse(string text)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 16 * 1024 * 1024;
            js.RecursionLimit = 32;
            return js.DeserializeObject(text);
        }

        public static string Write(object value, bool pretty)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int level)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        // Short arrays of plain numbers stay on one line in the pretty form (key lists).
        static bool IsFlat(IList list)
        {
            if (list.Count > 12) return false;
            foreach (object o in list) if (!(o is int || o is long)) return false;
            return true;
        }

        static void WriteValue(StringBuilder sb, object v, bool pretty, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is uint || v is ushort || v is byte) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is decimal || v is float) { sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is JObj)
            {
                JObj o = (JObj)v;
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int i = 0; i < o.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, pretty, level + 1);
                    WriteString(sb, o[i].Key);
                    sb.Append(pretty ? ": " : ":");
                    WriteValue(sb, o[i].Value, pretty, level + 1);
                }
                Indent(sb, pretty, level);
                sb.Append('}');
                return;
            }
            if (v is IDictionary)
            {
                IDictionary d = (IDictionary)v;
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, pretty, level + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(pretty ? ": " : ":");
                    WriteValue(sb, e.Value, pretty, level + 1);
                }
                Indent(sb, pretty, level);
                sb.Append('}');
                return;
            }
            if (v is IEnumerable)
            {
                List<object> items = new List<object>();
                foreach (object o in (IEnumerable)v) items.Add(o);
                if (items.Count == 0) { sb.Append("[]"); return; }
                bool flat = !pretty || IsFlat(items);
                sb.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(flat && pretty ? ", " : ",");
                    if (!flat) Indent(sb, pretty, level + 1);
                    WriteValue(sb, items[i], pretty, level + 1);
                }
                if (!flat) Indent(sb, pretty, level);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029 || c == '<' || c == '>' || c == '&')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
