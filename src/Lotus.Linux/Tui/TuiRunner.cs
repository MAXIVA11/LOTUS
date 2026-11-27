using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Lotus.Core;

namespace Lotus.Linux.Tui
{
    /// <summary>The daemon behind the interface: the control socket, the kernel's device list and the log file.</summary>
    internal sealed class DaemonSession : ILotusSession
    {
        private readonly Func<Program.State> read;
        private readonly Func<Policy, GuardReply> apply;

        public DaemonSession(Func<Program.State> read, Func<Policy, GuardReply> apply)
        {
            this.read = read;
            this.apply = apply;
        }

        public Program.State Read()
        {
            return read();
        }

        public GuardReply Apply(Policy next)
        {
            return apply(next);
        }

        public List<LotusEvent> Log(int count)
        {
            return new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(count);
        }
    }

    /// <summary>Runs the interface on a real terminal: reads keys, redraws, and puts the terminal back when it ends.</summary>
    internal static class TuiRunner
    {
        /// <summary>Box drawing and block characters need a terminal that shows them. The plain Linux console does not.</summary>
        public static bool TerminalShowsUnicode()
        {
            if (Environment.GetEnvironmentVariable("LOTUS_ASCII") == "1") return false;
            if (OperatingSystem.IsWindows()) return true;
            if (Environment.GetEnvironmentVariable("TERM") == "linux") return false;
            foreach (string name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(value)) continue;
                return value.IndexOf("UTF-8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       value.IndexOf("UTF8", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        public static int Run(ILotusSession session, string version)
        {
            var app = new TuiApp(session, version, TerminalShowsUnicode());
            Stream raw = Console.OpenStandardOutput();
            var encoding = new UTF8Encoding(false);

            void Write(string text)
            {
                byte[] bytes = encoding.GetBytes(text);
                raw.Write(bytes, 0, bytes.Length);
                raw.Flush();
            }

            bool restored = false;
            void Restore()
            {
                if (restored) return;
                restored = true;
                // Colours off, cursor back, and the screen the person had before.
                Write("\u001b[0m\u001b[?25h\u001b[?1049l");
            }

            bool oldControlC = Console.TreatControlCAsInput;
            // The other screen buffer, so that what was in the terminal comes back afterwards.
            Write("\u001b[?1049h\u001b[?25l\u001b[2J");
            ConsoleCancelEventHandler onCancel = (s, e) => Restore();
            EventHandler onExit = (s, e) => Restore();
            Console.CancelKeyPress += onCancel;
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                Console.TreatControlCAsInput = true;
                DateTime nextRefresh = DateTime.MinValue;
                int width = 0, height = 0;
                bool dirty = true;
                while (!app.Quit)
                {
                    if (DateTime.UtcNow >= nextRefresh)
                    {
                        app.Refresh();
                        nextRefresh = DateTime.UtcNow.AddSeconds(1);
                        dirty = true;
                    }
                    int w = Console.WindowWidth, h = Console.WindowHeight;
                    if (w != width || h != height)
                    {
                        width = w;
                        height = h;
                        Write("\u001b[2J");
                        dirty = true;
                    }
                    while (Console.KeyAvailable)
                    {
                        app.Key(Console.ReadKey(true));
                        dirty = true;
                    }
                    if (dirty)
                    {
                        var canvas = new Canvas(width, height);
                        app.Render(canvas);
                        Write(canvas.ToAnsi());
                        dirty = false;
                    }
                    if (app.Deferred != null)
                    {
                        app.RunDeferred();
                        nextRefresh = DateTime.UtcNow.AddSeconds(1);
                        dirty = true;
                        continue;
                    }
                    Thread.Sleep(25);
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                AppDomain.CurrentDomain.ProcessExit -= onExit;
                try
                {
                    Console.TreatControlCAsInput = oldControlC;
                }
                catch (IOException)
                {
                }
                Restore();
            }
            return 0;
        }

        /// <summary>
        /// For the tests and the pictures in the documentation: presses the given
        /// keys one after the other and prints the screen that results, once,
        /// without taking over the terminal.
        /// </summary>
        public static int Frame(ILotusSession session, string version, int width, int height, string keys, bool plain, bool unicode)
        {
            var app = new TuiApp(session, version, unicode);
            app.Refresh();
            foreach (ConsoleKeyInfo key in ParseKeys(keys))
            {
                app.Key(key);
                if (app.Deferred != null) app.RunDeferred();
            }
            var canvas = new Canvas(width, height);
            app.Render(canvas);
            Stream raw = Console.OpenStandardOutput();
            byte[] bytes = new UTF8Encoding(false).GetBytes(plain ? canvas.ToPlain() : canvas.ToAnsi(false));
            raw.Write(bytes, 0, bytes.Length);
            raw.Flush();
            return 0;
        }

        /// <summary>
        /// Keys as words separated by spaces: "2 down t" presses 2, the down arrow
        /// and t. A word that starts with "text:" is typed letter by letter, with
        /// %20 for a space.
        /// </summary>
        public static List<ConsoleKeyInfo> ParseKeys(string keys)
        {
            var list = new List<ConsoleKeyInfo>();
            foreach (string word in (keys ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "up": list.Add(Named(ConsoleKey.UpArrow)); continue;
                    case "down": list.Add(Named(ConsoleKey.DownArrow)); continue;
                    case "left": list.Add(Named(ConsoleKey.LeftArrow)); continue;
                    case "right": list.Add(Named(ConsoleKey.RightArrow)); continue;
                    case "enter": list.Add(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); continue;
                    case "space": list.Add(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)); continue;
                    case "tab": list.Add(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); continue;
                    case "esc": list.Add(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)); continue;
                    case "del": list.Add(Named(ConsoleKey.Delete)); continue;
                    case "bksp": list.Add(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)); continue;
                    case "pgdn": list.Add(Named(ConsoleKey.PageDown)); continue;
                    case "pgup": list.Add(Named(ConsoleKey.PageUp)); continue;
                }
                string text = word.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ? word.Substring(5).Replace("%20", " ") : word;
                foreach (char c in text) list.Add(Letter(c));
            }
            return list;
        }

        private static ConsoleKeyInfo Named(ConsoleKey key)
        {
            return new ConsoleKeyInfo('\0', key, false, false, false);
        }

        public static ConsoleKeyInfo Letter(char c)
        {
            ConsoleKey key = ConsoleKey.NoName;
            char upper = char.ToUpperInvariant(c);
            if (upper >= 'A' && upper <= 'Z') key = ConsoleKey.A + (upper - 'A');
            else if (c >= '0' && c <= '9') key = ConsoleKey.D0 + (c - '0');
            else if (c == ' ') key = ConsoleKey.Spacebar;
            return new ConsoleKeyInfo(c, key, char.IsUpper(c), false, false);
        }
    }
}
