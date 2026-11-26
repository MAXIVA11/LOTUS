using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// A small JSON reader and writer. LOTUS runs on the .NET Framework that ships
    /// with Windows, which has no built-in JSON library worth depending on, and the
    /// files it reads are small.
    /// Objects come back as Dictionary&lt;string, object&gt;, arrays as List&lt;object&gt;,
    /// numbers as double, and the rest as string, bool or null.
    /// </summary>
    public static class Json
    {
        private const int MaxDepth = 64;

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null.");
            int i = 0;
            // Skip a UTF-8 byte order mark if the file was saved by an editor that adds one.
            if (text.Length > 0 && text[0] == '﻿') i = 1;
            object value = ReadValue(text, ref i, 0);
            SkipWhite(text, ref i);
            if (i != text.Length) throw Error(text, i, "unexpected text after the end of the document");
            return value;
        }

        public static bool TryParse(string text, out object value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (FormatException)
            {
                value = null;
                return false;
            }
        }

        public static string Write(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indent, 0);
            return sb.ToString();
        }

        // ---------- typed accessors ----------

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback = null)
        {
            if (obj != null && obj.TryGetValue(key, out object v) && v is string s) return s;
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj != null && obj.TryGetValue(key, out object v) && v is bool b) return b;
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> obj, string key, int fallback)
        {
            if (obj != null && obj.TryGetValue(key, out object v) && v is double d &&
                d >= int.MinValue && d <= int.MaxValue)
            {
                return (int)d;
            }
            return fallback;
        }

        public static List<object> GetList(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out object v) && v is List<object> list) return list;
            return new List<object>();
        }

        // ---------- reader ----------

        private static FormatException Error(string s, int i, string message)
        {
            return new FormatException("Invalid JSON at position " + i + ": " + message + ".");
        }

        private static void SkipWhite(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw Error(s, i, "nesting is too deep");
            SkipWhite(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end of text");
            char c = s[i];
            switch (c)
            {
                case '{': return ReadObject(s, ref i, depth);
                case '[': return ReadArray(s, ref i, depth);
                case '"': return ReadString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref i);
                    throw Error(s, i, "unexpected character '" + c + "'");
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error(s, i, "expected " + word);
            i += word.Length;
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWhite(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Error(s, i, "expected a property name");
                string key = ReadString(s, ref i);
                SkipWhite(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Error(s, i, "expected ':'");
                i++;
                result[key] = ReadValue(s, ref i, depth + 1);
                SkipWhite(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unexpected end of text inside an object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw Error(s, i, "expected ',' or '}'");
            }
        }

        private static List<object> ReadArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // [
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ReadValue(s, ref i, depth + 1));
                SkipWhite(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unexpected end of text inside an array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw Error(s, i, "expected ',' or ']'");
            }
        }

        private static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                if (i >= s.Length) throw Error(s, i, "unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    if (c < ' ') throw Error(s, i - 1, "control character inside a string");
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length) throw Error(s, i, "unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Error(s, i, "incomplete \\u escape");
                        if (!ushort.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort code))
                        {
                            throw Error(s, i, "bad \\u escape");
                        }
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw Error(s, i - 1, "unknown escape '\\" + e + "'");
                }
            }
        }

        private static double ReadNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                else break;
            }
            string token = s.Substring(start, i - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                throw Error(s, start, "bad number '" + token + "'");
            }
            return d;
        }

        // ---------- writer ----------

        private static void WriteValue(StringBuilder sb, object value, bool indent, int level)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case DateTime dt:
                    WriteString(sb, FormatTime(dt));
                    return;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case IFormattable n when value is int || value is long || value is short || value is byte ||
                                         value is uint || value is ulong || value is ushort || value is sbyte ||
                                         value is decimal:
                    sb.Append(n.ToString(null, CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> obj:
                    WriteObject(sb, obj, indent, level);
                    return;
                case IEnumerable list:
                    WriteArray(sb, list, indent, level);
                    return;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> obj, bool indent, int level)
        {
            if (obj.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> pair in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indent, level + 1);
                WriteString(sb, pair.Key);
                sb.Append(indent ? ": " : ":");
                WriteValue(sb, pair.Value, indent, level + 1);
            }
            NewLine(sb, indent, level);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, bool indent, int level)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indent, level + 1);
                WriteValue(sb, item, indent, level + 1);
            }
            if (!first) NewLine(sb, indent, level);
            sb.Append(']');
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- time ----------

        public static string FormatTime(DateTime time)
        {
            return time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>The same with milliseconds, for the activity log, where the order of events matters.</summary>
        public static string FormatTimePrecise(DateTime time)
        {
            return time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseTime(string text, DateTime fallback)
        {
            if (!string.IsNullOrEmpty(text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            {
                return parsed;
            }
            return fallback;
        }
    }
}
