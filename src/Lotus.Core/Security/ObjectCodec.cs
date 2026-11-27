using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// The compact binary form in which LOTUS keeps its own records. It holds the
    /// same kinds of value the rest of the code works with (text, numbers, yes or
    /// no, lists and named fields) as tagged, length-prefixed bytes. Nothing in a
    /// LOTUS store is text a person could read or edit, even before encryption.
    /// </summary>
    public static class ObjectCodec
    {
        private const byte TagNull = 0, TagFalse = 1, TagTrue = 2, TagNumber = 3, TagText = 4, TagList = 5, TagMap = 6;
        private const int MaxDepth = 32;
        /// <summary>A record of LOTUS is a few kilobytes. This only stops a hostile file from eating memory.</summary>
        private const int MaxBytes = 8 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(object value)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                Write(writer, value, 0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void Write(BinaryWriter writer, object value, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The record is nested too deeply.");
            switch (value)
            {
                case null:
                    writer.Write(TagNull);
                    return;
                case bool flag:
                    writer.Write(flag ? TagTrue : TagFalse);
                    return;
                case string text:
                    writer.Write(TagText);
                    WriteText(writer, text);
                    return;
                case Dictionary<string, object> map:
                    writer.Write(TagMap);
                    writer.Write(map.Count);
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        WriteText(writer, pair.Key);
                        Write(writer, pair.Value, depth + 1);
                    }
                    return;
                case List<object> list:
                    writer.Write(TagList);
                    writer.Write(list.Count);
                    foreach (object item in list) Write(writer, item, depth + 1);
                    return;
                case double _:
                case float _:
                case int _:
                case long _:
                case uint _:
                case short _:
                case byte _:
                case decimal _:
                    writer.Write(TagNumber);
                    writer.Write(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                default:
                    throw new FormatException("A value of type " + value.GetType().Name + " cannot be stored.");
            }
        }

        private static void WriteText(BinaryWriter writer, string text)
        {
            byte[] bytes = Utf8.GetBytes(text ?? "");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Reads a record back. Throws FormatException for anything that is not one.</summary>
        public static object Decode(byte[] bytes)
        {
            if (bytes == null) throw new FormatException("There is no record.");
            if (bytes.Length > MaxBytes) throw new FormatException("The record is far too large.");
            int at = 0;
            object value = Read(bytes, ref at, 0);
            if (at != bytes.Length) throw new FormatException("There is data after the end of the record.");
            return value;
        }

        private static object Read(byte[] bytes, ref int at, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The record is nested too deeply.");
            if (at >= bytes.Length) throw new FormatException("The record ends too early.");
            byte tag = bytes[at++];
            switch (tag)
            {
                case TagNull:
                    return null;
                case TagFalse:
                    return false;
                case TagTrue:
                    return true;
                case TagNumber:
                    if (bytes.Length - at < 8) throw new FormatException("The record ends too early.");
                    double number = BitConverter.Int64BitsToDouble(ReadInt64(bytes, at));
                    at += 8;
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        throw new FormatException("The record holds a number that is not a number.");
                    }
                    return number;
                case TagText:
                    return ReadText(bytes, ref at);
                case TagList:
                {
                    int count = ReadCount(bytes, ref at);
                    var list = new List<object>(Math.Min(count, 1024));
                    for (int i = 0; i < count; i++) list.Add(Read(bytes, ref at, depth + 1));
                    return list;
                }
                case TagMap:
                {
                    int count = ReadCount(bytes, ref at);
                    var map = new Dictionary<string, object>(Math.Min(count, 1024));
                    for (int i = 0; i < count; i++)
                    {
                        if (at >= bytes.Length || bytes.Length - at < 4) throw new FormatException("The record ends too early.");
                        string key = ReadText(bytes, ref at);
                        if (map.ContainsKey(key)) throw new FormatException("The record names the same field twice.");
                        map[key] = Read(bytes, ref at, depth + 1);
                    }
                    return map;
                }
                default:
                    throw new FormatException("The record holds a value of an unknown kind.");
            }
        }

        /// <summary>Every item takes at least one byte, so a count larger than what is left is a lie.</summary>
        private static int ReadCount(byte[] bytes, ref int at)
        {
            if (bytes.Length - at < 4) throw new FormatException("The record ends too early.");
            int count = ReadInt32(bytes, at);
            at += 4;
            if (count < 0 || count > bytes.Length - at) throw new FormatException("The record claims more items than it holds.");
            return count;
        }

        private static string ReadText(byte[] bytes, ref int at)
        {
            if (bytes.Length - at < 4) throw new FormatException("The record ends too early.");
            int length = ReadInt32(bytes, at);
            at += 4;
            if (length < 0 || length > bytes.Length - at) throw new FormatException("The record claims more text than it holds.");
            try
            {
                string text = Utf8.GetString(bytes, at, length);
                at += length;
                return text;
            }
            catch (ArgumentException)
            {
                throw new FormatException("The record holds text that is not valid.");
            }
        }

        private static int ReadInt32(byte[] bytes, int at)
        {
            return bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;
        }

        private static long ReadInt64(byte[] bytes, int at)
        {
            return (uint)ReadInt32(bytes, at) | (long)ReadInt32(bytes, at + 4) << 32;
        }
    }
}
