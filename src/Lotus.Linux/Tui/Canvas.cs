using System;
using System.Collections.Generic;
using System.Text;

namespace Lotus.Linux.Tui
{
    /// <summary>The colours of LOTUS, the same ones the Windows app uses.</summary>
    internal static class Palette
    {
        public const int Ink = 0x08080C;
        public const int Panel = 0x111118;
        public const int Panel2 = 0x1C1C28;
        public const int Line = 0x2B2B3A;
        public const int Text = 0xF4F4F6;
        public const int Dim = 0x8D8DA3;
        public const int Cyan = 0x00F0FF;
        public const int Pink = 0xFF2D95;
        public const int Yellow = 0xFCEE0A;
        public const int Green = 0x3DFF8B;
    }

    /// <summary>
    /// One screen of characters with their colours. The pages draw into it, and it
    /// turns itself into the escape sequences a terminal understands, or into
    /// plain lines for the tests.
    /// </summary>
    internal sealed class Canvas
    {
        private struct Cell
        {
            public char Ch;
            public int Fg;
            public int Bg;
            public bool Bold;
        }

        private readonly Cell[] cells;

        public int Width { get; }
        public int Height { get; }

        public Canvas(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            cells = new Cell[Width * Height];
            Fill(0, 0, Width, Height, Palette.Ink);
        }

        public void Fill(int x, int y, int width, int height, int bg)
        {
            for (int row = y; row < y + height; row++)
            {
                for (int column = x; column < x + width; column++)
                {
                    if (column < 0 || row < 0 || column >= Width || row >= Height) continue;
                    cells[row * Width + column] = new Cell { Ch = ' ', Fg = Palette.Text, Bg = bg };
                }
            }
        }

        /// <summary>Writes text at a position. What does not fit is cut off. A background of -1 keeps what is there.</summary>
        public void Text(int x, int y, string text, int fg, int bg = -1, bool bold = false)
        {
            if (text == null || y < 0 || y >= Height) return;
            for (int i = 0; i < text.Length; i++)
            {
                int column = x + i;
                if (column < 0) continue;
                if (column >= Width) break;
                int index = y * Width + column;
                char c = text[i];
                // Nothing a device or a file says may move the cursor.
                if (c < ' ' || c == 0x7F) c = ' ';
                cells[index] = new Cell { Ch = c, Fg = fg, Bg = bg < 0 ? cells[index].Bg : bg, Bold = bold };
            }
        }

        /// <summary>Text that ends at the given column.</summary>
        public void TextRight(int right, int y, string text, int fg, int bg = -1, bool bold = false)
        {
            Text(right - (text ?? "").Length + 1, y, text, fg, bg, bold);
        }

        public void TextCentered(int x, int width, int y, string text, int fg, int bg = -1, bool bold = false)
        {
            Text(x + Math.Max(0, (width - (text ?? "").Length) / 2), y, text, fg, bg, bold);
        }

        public void HLine(int x, int y, int width, int fg, char line)
        {
            Text(x, y, new string(line, Math.Max(0, width)), fg);
        }

        /// <summary>A frame with a filled inside.</summary>
        public void Box(int x, int y, int width, int height, int fg, int bg, bool unicode)
        {
            if (width < 2 || height < 2) return;
            Fill(x, y, width, height, bg);
            char h = unicode ? '─' : '-';
            char v = unicode ? '│' : '|';
            Text(x, y, (unicode ? "┌" : "+") + new string(h, width - 2) + (unicode ? "┐" : "+"), fg);
            Text(x, y + height - 1, (unicode ? "└" : "+") + new string(h, width - 2) + (unicode ? "┘" : "+"), fg);
            for (int row = y + 1; row < y + height - 1; row++)
            {
                Text(x, row, v.ToString(), fg);
                Text(x + width - 1, row, v.ToString(), fg);
            }
        }

        /// <summary>Breaks text into lines of at most the given width, at spaces where it can.</summary>
        public static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            if (width < 1) return lines;
            foreach (string paragraph in (text ?? "").Split('\n'))
            {
                string rest = paragraph.Trim();
                if (rest.Length == 0)
                {
                    lines.Add("");
                    continue;
                }
                while (rest.Length > width)
                {
                    int cut = rest.LastIndexOf(' ', width);
                    if (cut <= 0) cut = width;
                    lines.Add(rest.Substring(0, cut).TrimEnd());
                    rest = rest.Substring(cut).TrimStart();
                }
                lines.Add(rest);
            }
            return lines;
        }

        public static string Shorten(string text, int length, bool unicode = true)
        {
            text = text ?? "";
            if (length < 1) return "";
            if (text.Length <= length) return text;
            return unicode ? text.Substring(0, length - 1) + "…" : text.Substring(0, Math.Max(0, length - 2)) + "..";
        }

        /// <summary>The screen as plain lines, without colours and without trailing blanks.</summary>
        public string ToPlain()
        {
            var text = new StringBuilder();
            for (int row = 0; row < Height; row++)
            {
                var line = new StringBuilder(Width);
                for (int column = 0; column < Width; column++) line.Append(cells[row * Width + column].Ch);
                text.Append(line.ToString().TrimEnd()).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>The whole screen as escape sequences, each row placed by its position.</summary>
        public string ToAnsi(bool position = true)
        {
            var text = new StringBuilder(Width * Height * 4);
            for (int row = 0; row < Height; row++)
            {
                if (position) text.Append("\u001b[").Append(row + 1).Append(";1H");
                int fg = -2, bg = -2;
                bool bold = false;
                for (int column = 0; column < Width; column++)
                {
                    Cell cell = cells[row * Width + column];
                    if (cell.Fg != fg || cell.Bg != bg || cell.Bold != bold)
                    {
                        fg = cell.Fg;
                        bg = cell.Bg;
                        bold = cell.Bold;
                        text.Append("\u001b[0");
                        if (bold) text.Append(";1");
                        text.Append(";38;2;").Append((fg >> 16) & 255).Append(';').Append((fg >> 8) & 255).Append(';').Append(fg & 255);
                        text.Append(";48;2;").Append((bg >> 16) & 255).Append(';').Append((bg >> 8) & 255).Append(';').Append(bg & 255);
                        text.Append('m');
                    }
                    text.Append(cell.Ch);
                }
                text.Append("\u001b[0m");
                if (!position) text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>The colours of one cell, for the tests.</summary>
        public int ForegroundAt(int x, int y)
        {
            return cells[y * Width + x].Fg;
        }

        public int BackgroundAt(int x, int y)
        {
            return cells[y * Width + x].Bg;
        }
    }
}
