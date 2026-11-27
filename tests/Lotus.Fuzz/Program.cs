using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Lotus.Core;
using Lotus.Linux;

namespace Lotus.Fuzz
{
    /// <summary>
    /// Feeds the parsers of LOTUS broken input, millions of times, and checks
    /// what must hold whatever comes in. These are the places where LOTUS reads
    /// bytes it did not write: what a USB device says about itself, the files of
    /// the policy store, and the requests on the control channel.
    ///
    ///     dotnet run -c Release --project tests/Lotus.Fuzz -- --seconds 60
    ///     ... --target json --seed 7 --seconds 600
    ///     ... --trace out/        writes every input before it is tried, to find one that kills the process
    ///
    /// It starts from valid samples and changes them: flipped bits, cut and
    /// doubled pieces, lengths set to their limits, two samples spliced. It is
    /// not guided by code coverage. An input that breaks a rule is written to
    /// the folder "fuzz-findings" and the exit code is 1.
    /// </summary>
    internal static class Program
    {
        private sealed class Target
        {
            public string Name;
            public List<byte[]> Seeds;
            public Action<byte[]> Run;
            public bool Text;
        }

        private sealed class Finding : Exception
        {
            public Finding(string message, Exception inner = null) : base(message, inner)
            {
            }
        }

        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();
        private static string sysfsDir;
        private static volatile string current = "";
        private static long currentStarted;
        private static volatile byte[] currentInput;

        private static int Main(string[] args)
        {
            int seconds = 30;
            int seed = 20261127;
            string only = null;
            string trace = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seconds" when i + 1 < args.Length: seconds = int.Parse(args[++i]); break;
                    case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
                    case "--target" when i + 1 < args.Length: only = args[++i]; break;
                    case "--trace" when i + 1 < args.Length: trace = args[++i]; break;
                    default:
                        Console.Error.WriteLine("Unknown option " + args[i] + ". Options: --seconds N, --seed N, --target NAME, --trace FOLDER");
                        return 2;
                }
            }

            sysfsDir = Path.Combine(Path.GetTempPath(), "lotus-fuzz-" + Guid.NewGuid().ToString("N"));
            List<Target> targets = Targets();
            if (only != null)
            {
                targets = targets.Where(t => t.Name == only).ToList();
                if (targets.Count == 0)
                {
                    Console.Error.WriteLine("No such target. There are: " + string.Join(", ", Targets().Select(t => t.Name)));
                    return 2;
                }
            }
            if (trace != null) Directory.CreateDirectory(trace);

            // A parser that never comes back is a finding too. This thread notices.
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    long started = Interlocked.Read(ref currentStarted);
                    if (started != 0 && Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
                    {
                        Save(current, currentInput, "It did not finish within 20 seconds.");
                        Environment.Exit(1);
                    }
                }
            }) { IsBackground = true };
            watchdog.Start();

            int failed = 0;
            long total = 0;
            double each = Math.Max(0.5, seconds / (double)targets.Count);
            try
            {
                foreach (Target target in targets)
                {
                    var random = new Random(seed ^ StableHash(target.Name));
                    var clock = Stopwatch.StartNew();
                    long runs = 0;
                    // First every sample as it is: a sample that breaks a rule is a broken harness.
                    var queue = new Queue<byte[]>(target.Seeds);
                    string problem = null;
                    while (problem == null && (queue.Count > 0 || clock.Elapsed.TotalSeconds < each))
                    {
                        byte[] input = queue.Count > 0 ? queue.Dequeue() : Mutate(target, random);
                        if (trace != null) File.WriteAllBytes(Path.Combine(trace, target.Name + ".bin"), input);
                        current = target.Name;
                        currentInput = input;
                        Interlocked.Exchange(ref currentStarted, Stopwatch.GetTimestamp());
                        try
                        {
                            target.Run(input);
                        }
                        catch (Finding finding)
                        {
                            problem = finding.Message + (finding.InnerException != null ? " " + finding.InnerException : "");
                        }
                        catch (Exception ex)
                        {
                            problem = "It threw something it never should: " + ex;
                        }
                        Interlocked.Exchange(ref currentStarted, 0);
                        runs++;
                        if (problem != null) Save(target.Name, input, problem);
                    }
                    total += runs;
                    Console.WriteLine((problem == null ? "ok    " : "FOUND ") + target.Name.PadRight(12) +
                                      runs.ToString("N0").PadLeft(12) + " inputs in " + clock.Elapsed.TotalSeconds.ToString("F1") + " s");
                    if (problem != null) failed++;
                }
            }
            finally
            {
                try { if (Directory.Exists(sysfsDir)) Directory.Delete(sysfsDir, true); } catch (IOException) { }
            }
            Console.WriteLine(failed == 0
                ? total.ToString("N0") + " inputs, no rule broken. Seed " + seed + "."
                : failed + " target(s) broke a rule. See the folder fuzz-findings. Seed " + seed + ".");
            return failed == 0 ? 0 : 1;
        }

        private static int StableHash(string text)
        {
            int hash = 17;
            foreach (char c in text) hash = unchecked(hash * 31 + c);
            return hash;
        }

        private static void Save(string target, byte[] input, string problem)
        {
            try
            {
                Directory.CreateDirectory("fuzz-findings");
                string name = target + "-" + Convert.ToHexString(SHA256.HashData(input ?? new byte[0])).Substring(0, 12);
                File.WriteAllBytes(Path.Combine("fuzz-findings", name + ".bin"), input ?? new byte[0]);
                File.WriteAllText(Path.Combine("fuzz-findings", name + ".txt"), problem + Environment.NewLine);
                Console.WriteLine("FOUND " + target + ": " + problem.Split('\n')[0]);
                Console.WriteLine("      input saved as fuzz-findings/" + name + ".bin (" + (input?.Length ?? 0) + " bytes)");
            }
            catch (IOException)
            {
            }
        }

        // ================= changing a sample =================

        private static readonly byte[] Edges = { 0x00, 0x01, 0x02, 0x04, 0x08, 0x09, 0x10, 0x20, 0x3F, 0x40, 0x7F, 0x80, 0xC0, 0xFE, 0xFF };

        private static readonly string[] Tokens =
        {
            "{", "}", "[", "]", "\"", ":", ",", "\\", "\\u", "\\uD800", "\\u0000", "null", "true", "false", "-", "1e999",
            "-0", "0.0000000000000000000001", "99999999999999999999999999", "\n", "\t", "\u0000", "\uFEFF", "\u202E",
            "\"until\":", "\"trusted\":[", "\"kind\":\"vendor\"", "\"id\":\"", "USB\\\\VID_", "&PID_", "&MI_00",
            "\"armed\":", "\"storage\":\"readonly\"", "\"functions\":[", "2026-12-04T17:00:00Z", "9999-99-99"
        };

        private static byte[] Mutate(Target target, Random random)
        {
            byte[] data = (byte[])target.Seeds[random.Next(target.Seeds.Count)].Clone();
            int rounds = 1 + random.Next(random.Next(4) == 0 ? 12 : 4);
            for (int round = 0; round < rounds; round++)
            {
                var bytes = new List<byte>(data);
                int at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
                switch (random.Next(target.Text ? 13 : 11))
                {
                    case 0: // one bit
                        if (bytes.Count > 0) bytes[at] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1: // one byte to a value at an edge
                        if (bytes.Count > 0) bytes[at] = Edges[random.Next(Edges.Length)];
                        break;
                    case 2: // one byte to anything
                        if (bytes.Count > 0) bytes[at] = (byte)random.Next(256);
                        break;
                    case 3: // cut the end off
                        bytes.RemoveRange(at, bytes.Count - at);
                        break;
                    case 4: // cut a piece out
                        if (bytes.Count > 0) bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + random.Next(8)));
                        break;
                    case 5: // say a piece twice
                    {
                        int length = Math.Min(bytes.Count - at, 1 + random.Next(32));
                        if (length > 0) bytes.InsertRange(at, bytes.GetRange(at, length));
                        break;
                    }
                    case 6: // random bytes in the middle
                    {
                        var extra = new byte[1 + random.Next(16)];
                        random.NextBytes(extra);
                        bytes.InsertRange(at, extra);
                        break;
                    }
                    case 7: // a four-byte number at its limits, both byte orders
                    {
                        uint[] numbers = { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFF, 0x10000, (uint)bytes.Count, (uint)bytes.Count + 1 };
                        byte[] number = BitConverter.GetBytes(numbers[random.Next(numbers.Length)]);
                        if (random.Next(2) == 0) Array.Reverse(number);
                        for (int i = 0; i < 4 && at + i < bytes.Count; i++) bytes[at + i] = number[i];
                        break;
                    }
                    case 8: // the front of this sample, the back of another
                    {
                        byte[] other = target.Seeds[random.Next(target.Seeds.Count)];
                        int from = other.Length == 0 ? 0 : random.Next(other.Length);
                        bytes.RemoveRange(at, bytes.Count - at);
                        bytes.AddRange(other.Skip(from));
                        break;
                    }
                    case 9: // the same byte many times
                    {
                        byte value = bytes.Count > 0 ? bytes[at] : (byte)'[';
                        bytes.InsertRange(at, Enumerable.Repeat(value, 1 + random.Next(random.Next(8) == 0 ? 4000 : 200)));
                        break;
                    }
                    case 10: // swap two bytes
                        if (bytes.Count > 1)
                        {
                            int other = random.Next(bytes.Count);
                            byte keep = bytes[at];
                            bytes[at] = bytes[other];
                            bytes[other] = keep;
                        }
                        break;
                    case 11: // a word that means something to a parser
                        bytes.InsertRange(at, Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]));
                        break;
                    case 12: // nested deeper than anything real
                    {
                        string open = random.Next(2) == 0 ? "[" : "{\"a\":";
                        bytes.InsertRange(at, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(open, 1 + random.Next(300)))));
                        break;
                    }
                }
                // Nothing LOTUS reads is larger than this, and a hostile size is tried on purpose below.
                if (bytes.Count > 1 << 20) bytes.RemoveRange(1 << 20, bytes.Count - (1 << 20));
                data = bytes.ToArray();
            }
            return data;
        }

        // ================= the rules =================

        private static void Require(bool condition, string rule)
        {
            if (!condition) throw new Finding("Rule broken: " + rule);
        }

        /// <summary>Runs something that may refuse its input in exactly one way.</summary>
        private static bool Accepts<TRefusal>(Action action) where TRefusal : Exception
        {
            try
            {
                action();
                return true;
            }
            catch (TRefusal)
            {
                return false;
            }
        }

        private static string AsText(byte[] input)
        {
            // Bytes that are not UTF-8 become the replacement character, as they would when a file is read.
            return Encoding.UTF8.GetString(input);
        }

        private static List<Target> Targets()
        {
            return new List<Target>
            {
                new Target { Name = "json", Text = true, Seeds = JsonSeeds(), Run = FuzzJson },
                new Target { Name = "policy", Text = true, Seeds = PolicySeeds(), Run = FuzzPolicy },
                new Target { Name = "ledger", Text = true, Seeds = LedgerSeeds(), Run = FuzzLedger },
                new Target { Name = "logline", Text = true, Seeds = LogSeeds(), Run = FuzzLogLine },
                new Target { Name = "typed", Text = true, Seeds = TypedSeeds(), Run = FuzzTyped },
                new Target { Name = "codec", Seeds = CodecSeeds(), Run = FuzzCodec },
                new Target { Name = "sealed", Seeds = SealedSeeds(), Run = FuzzSealed },
                new Target { Name = "descriptor", Seeds = DescriptorSeeds(), Run = FuzzDescriptor },
                new Target { Name = "sysfs", Seeds = SysfsSeeds(), Run = FuzzSysfs }
            };
        }

        // ---------- JSON ----------

        private static void FuzzJson(byte[] input)
        {
            string text = AsText(input);
            object value = null;
            if (!Accepts<FormatException>(() => value = Json.Parse(text))) return;

            // What was read can be written, and what was written reads back to the same thing.
            string written = Json.Write(value, false);
            object again = null;
            Require(Accepts<FormatException>(() => again = Json.Parse(written)), "JSON that LOTUS wrote can be read by LOTUS");
            Require(Json.Write(again, false) == written, "reading and writing JSON a second time changes nothing");
            Require(Json.TryParse(text, out _), "TryParse agrees with Parse");
        }

        private static List<byte[]> JsonSeeds()
        {
            return Texts(
                "{}", "[]", "null", "true", "-12.5e3", "\"text with \\\"quotes\\\" and \\u00e9 and \\n\"",
                "{\"a\":[1,2,{\"b\":null,\"c\":[true,false]}],\"d\":\"e\",\"f\":0.1}",
                "[[[[[[[[1]]]]]]]]", " \t\r\n{ \"k\" : \"v\" } \n", "\uFEFF{\"bom\":1}",
                "{\"op\":\"apply\",\"policy\":{\"version\":1,\"armed\":true}}",
                "{\"dup\":1,\"dup\":2}", "[1,2,3,", "{\"a\":}", "\"\\uD83D\\uDE00\"");
        }

        // ---------- the policy ----------

        private static void FuzzPolicy(byte[] input)
        {
            string text = AsText(input);
            Policy policy = null;
            if (!Accepts<FormatException>(() => policy = Policy.FromJson(text))) return;

            Require(policy.Trusted.Count <= Policy.MaxTrusted, "the trusted list is never longer than its limit");
            foreach (TrustEntry entry in policy.Trusted)
            {
                Require(entry.Id.Length > 0 && entry.Id == Policy.NormalizeId(entry.Id), "a trust entry has a normalized ID");
                Require(entry.Name.Length <= TextSafety.MaxNameLength, "a name is never longer than its limit");
                Require(entry.Name == TextSafety.Clean(entry.Name), "a name carries no control characters");
                Require(entry.Functions.Count <= 64, "an entry never holds more than 64 functions");
            }
            // The two switches for keyboards and mice are never both on.
            Require(!(policy.ApproveInput && policy.AllowInput), "approving new keyboards and always allowing them do not go together");
            // A policy that was accepted survives being saved and loaded, unchanged.
            string saved = policy.ToJson();
            Policy again = null;
            Require(Accepts<FormatException>(() => again = Policy.FromJson(saved)), "a policy LOTUS saved can be loaded");
            Require(again.ToJson() == saved, "saving and loading a policy a second time changes nothing");
            Require(policy.Clone().ToJson() == saved, "a copy of a policy is the same policy");
            // Judging a device under any accepted policy gives an answer.
            PolicyEngine.Evaluate(policy, SampleDevice, TimeSpan.MaxValue);
            GuardWorker.Summarize(policy);
        }

        private static readonly UsbDevice SampleDevice = new UsbDevice
        {
            InstanceId = @"USB\VID_0781&PID_5567\4C530001230505116353", Name = "Stick", Vid = "0781", Pid = "5567",
            HasSerial = true, Serial = "4C530001230505116353", Started = true, Present = true
        };

        private static List<byte[]> PolicySeeds()
        {
            var full = new Policy
            {
                Armed = true, Storage = StorageMode.ReadOnly, Pnp = PnpMode.Block, AllowInput = false, Strict = true
            };
            full.AddTrust(new TrustEntry
            {
                Kind = TrustKind.Device, Id = @"USB\VID_0781&PID_5567\4C530001230505116353", Name = "SanDisk Cruzer",
                Functions = new List<string> { "080650" }, UntilUtc = new DateTime(2027, 1, 15, 17, 0, 0, DateTimeKind.Utc)
            });
            full.AddTrust(new TrustEntry { Kind = TrustKind.Model, Id = "VID_046D&PID_C52B", Name = "Receiver" });
            full.AddTrust(new TrustEntry { Kind = TrustKind.Vendor, Id = "VID_0403", Name = "Adapters" });
            var waiting = new Policy { Armed = true, Input = InputRule.Approve, Strict = true };
            waiting.AddTrust(new TrustEntry { Kind = TrustKind.Device, Id = @"USB\VID_046D&PID_C542&CC3F949&0&5", Name = "Receiver", LetIn = true });
            return Texts(
                new Policy().ToJson(), full.ToJson(), waiting.ToJson(),
                "{\"version\":1,\"armed\":true,\"storage\":\"block\",\"pnp\":\"allow\",\"trusted\":[]}",
                "{\"armed\":false}",
                "{\"version\":1,\"armed\":true,\"storage\":\"block\",\"pnp\":\"block\",\"allowInput\":true,\"trusted\":[" +
                "{\"kind\":\"device\",\"id\":\"usb\\\\vid_1a2c&pid_2d23\\\\5&cc3f949&0&6\",\"name\":\"Keyboard\",\"added\":\"2026-11-26T10:00:00Z\"}]}");
        }

        // ---------- the list of blocked devices ----------

        private static void FuzzLedger(byte[] input)
        {
            string text = AsText(input);
            Ledger ledger = null;
            if (!Accepts<FormatException>(() => ledger = Ledger.FromJson(text))) return;
            string saved = ledger.ToJson();
            Ledger again = null;
            Require(Accepts<FormatException>(() => again = Ledger.FromJson(saved)), "a list of blocked devices LOTUS saved can be loaded");
            Require(again.ToJson() == saved, "saving and loading that list a second time changes nothing");
        }

        private static List<byte[]> LedgerSeeds()
        {
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(SampleDevice, Reason.StorageBlocked, new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc)));
            return Texts(new Ledger().ToJson(), ledger.ToJson());
        }

        // ---------- one line of the activity log ----------

        private static void FuzzLogLine(byte[] input)
        {
            LotusEvent e = LotusEvent.FromJsonLine(AsText(input));
            if (e == null) return;
            // Whatever is in the log, showing it and passing it on never fails.
            string line = e.ToJsonLine();
            Require(!line.Contains('\n') && !line.Contains('\r'), "a log entry is one line");
            Require(LotusEvent.FromJsonLine(line) != null, "a log line LOTUS wrote can be read");
            string shown = EventIds.Describe(e);
            Require(shown.Split(new[] { "\r\n" }, StringSplitOptions.None).Length <= 4, "the text for the event log has at most four lines");
            string journal = Encoding.UTF8.GetString(JournalLog.Format(e));
            Require(journal.Split('\n').Length <= 8, "a journal entry has at most seven fields");
            Require(journal.Contains("\nLOTUS_EVENT_ID=" + EventIds.For(e.Type) + "\n"), "a journal entry carries the number of its event");
        }

        private static List<byte[]> LogSeeds()
        {
            return Texts(
                new LotusEvent { Type = EventTypes.Blocked, Message = "Stick was blocked.", DeviceId = SampleDevice.InstanceId, DeviceName = "Stick" }.ToJsonLine(),
                new LotusEvent { Type = EventTypes.Tamper, Message = "The policy store was damaged." }.ToJsonLine(),
                "{\"t\":\"2026-01-01T00:00:00.000Z\",\"type\":\"blocked\",\"msg\":\"line one\\nPRIORITY=0\\nLOTUS_EVENT=released\",\"name\":\"a\\r\\nb\"}",
                "{\"t\":42,\"type\":[],\"msg\":{}}");
        }

        // ---------- what a person types ----------

        private static void FuzzTyped(byte[] input)
        {
            string text = AsText(input);
            foreach (TrustKind kind in new[] { TrustKind.Vendor, TrustKind.Model, TrustKind.Device })
            {
                bool ok = TrustEntry.TryParse(kind, text, text, out TrustEntry entry, out string error);
                Require(ok == (entry != null) && ok == (error == null), "typed trust is either an entry or a reason, never both");
                if (!ok) continue;
                Require(entry.Kind == kind && entry.Id == Policy.NormalizeId(entry.Id) && entry.Id.Length <= 200, "a typed entry has a normalized ID of sane length");
                foreach (char c in entry.Id) Require(c > ' ' && c <= '~', "a typed ID holds only printable ASCII");
                var policy = new Policy();
                Require(policy.AddTrust(entry), "a typed entry can be put on the list");
                Require(Policy.FromJson(policy.ToJson()).Trusted.Single().Id == entry.Id, "a typed entry survives being saved");
            }
            DateTime now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            bool timed = TrustEntry.TryParseTerm(text, now, out DateTime? until, out string problem);
            Require(timed == (problem == null), "a typed time is either a time or a reason");
            if (timed && until.HasValue)
            {
                Require(until.Value > now && until.Value - now <= TimeSpan.FromDays(3661), "an end is in the future and at most ten years away");
                Require(until.Value.Kind == DateTimeKind.Utc, "an end is kept in universal time");
            }
            Require(TextSafety.Clean(text).Length <= TextSafety.MaxNameLength, "cleaned text is never longer than its limit");
            Require(TextSafety.Clean(TextSafety.Clean(text)) == TextSafety.Clean(text), "cleaning text twice changes nothing");
            LinuxDeviceBackend.CleanSerial(text);
            UsbClassifier.ParseInstanceId(text, out _, out _, out _);
            UsbClassifier.IsWholeDeviceId(text);
            UsbClassifier.IsPartOfDevice(text);
            UsbClassifier.KindFromFunction(text);
        }

        private static List<byte[]> TypedSeeds()
        {
            return Texts("046D", "vid_046d", "0x046D", "046D:C52B", "VID_046D&PID_C52B", "USB\\VID_046D&PID_C52B",
                "USB\\VID_0781&PID_5567\\4C530001230505116353", "USB\\VID_0781&PID_5567&MI_00\\6&1234", "7d", "12h", "90m", "2w",
                "2026-12-04", "2026-12-04 17:00", "never", "", "  ", "Caf\u00e9 \u202Egnp.exe");
        }

        // ---------- the binary form inside the store ----------

        private static void FuzzCodec(byte[] input)
        {
            object value = null;
            if (!Accepts<FormatException>(() => value = ObjectCodec.Decode(input))) return;
            // A record that was accepted has exactly one binary form.
            byte[] encoded = ObjectCodec.Encode(value);
            object again = null;
            Require(Accepts<FormatException>(() => again = ObjectCodec.Decode(encoded)), "a record LOTUS wrote can be read");
            Require(ObjectCodec.Encode(again).AsSpan().SequenceEqual(encoded), "reading and writing a record a second time changes nothing");
        }

        private static List<byte[]> CodecSeeds()
        {
            var seeds = new List<byte[]>
            {
                ObjectCodec.Encode(null),
                ObjectCodec.Encode(true),
                ObjectCodec.Encode("text"),
                ObjectCodec.Encode(123456789L),
                ObjectCodec.Encode(new List<object> { 1L, "two", null, new List<object>() }),
                ObjectCodec.Encode(Json.Parse(PolicySeedText()))
            };
            seeds.Add(ObjectCodec.Encode(new Dictionary<string, object> { ["policy"] = Json.Parse(PolicySeedText()), ["serial"] = 7L }));
            return seeds;
        }

        private static string PolicySeedText()
        {
            return Encoding.UTF8.GetString(PolicySeeds()[1]);
        }

        // ---------- the sealed file ----------

        private static readonly List<byte[]> SealedOriginals = new List<byte[]>();

        private static void FuzzSealed(byte[] input)
        {
            Require(SealedBox.LooksSealed(input) || !Accepts<FormatException>(() => SealedBox.Open(Key, 1, input, out _)),
                "a file that does not look like a store is never opened");
            foreach (byte kind in new byte[] { 1, 2 })
            {
                byte[] plain = null;
                long serial = 0;
                if (!Accepts<FormatException>(() => plain = SealedBox.Open(Key, kind, input, out serial))) continue;
                // The whole point of the seal: only a file LOTUS wrote with this key opens.
                Require(SealedOriginals.Any(original => original.AsSpan().SequenceEqual(input)),
                    "a store file that was changed in any way does not open");
                Require(plain != null, "an opened store has content");
            }
            // With another key nothing opens.
            byte[] otherKey = (byte[])Key.Clone();
            otherKey[5] ^= 1;
            Require(!Accepts<FormatException>(() => SealedBox.Open(otherKey, 1, input, out _)) || !SealedBox.LooksSealed(input) ||
                    !SealedOriginals.Any(original => original.AsSpan().SequenceEqual(input)),
                "a store file does not open with another key");
        }

        private static List<byte[]> SealedSeeds()
        {
            SealedOriginals.Clear();
            byte[] content = CodecSeeds().Last();
            SealedOriginals.Add(SealedBox.Seal(Key, 1, 1, content));
            SealedOriginals.Add(SealedBox.Seal(Key, 1, 99, new byte[] { 1, 2, 3 }));
            SealedOriginals.Add(SealedBox.Seal(Key, 2, 7, Encoding.UTF8.GetBytes(new Ledger().ToJson())));
            SealedOriginals.Add(SealedBox.Seal(Key, 1, long.MaxValue, new byte[4096]));
            return new List<byte[]>(SealedOriginals);
        }

        // ---------- what a USB device says about itself ----------

        private static void FuzzDescriptor(byte[] input)
        {
            var found = new List<UsbInterfaceInfo>();
            UsbDescriptors.ParseConfiguration(input, found);
            Require(found.Count <= 64, "a device never has more than 64 kinds of function");
            Require(found.Count <= input.Length / 9, "every function that is found has a descriptor behind it");
            var codes = new HashSet<string>();
            foreach (UsbInterfaceInfo info in found)
            {
                Require(codes.Add(info.Class + "/" + info.SubClass + "/" + info.Protocol), "a function is listed once");
            }
            // The same bytes always give the same answer, and a second walk adds nothing.
            int before = found.Count;
            UsbDescriptors.ParseConfiguration(input, found);
            Require(found.Count == before, "reading the same descriptor twice finds nothing new");
            LinuxDeviceBackend.LooksLikeMediaTransfer(input);
            foreach (UsbInterfaceInfo info in found)
            {
                UsbClassifier.Labels(UsbClassifier.KindFromFunction(info.Class.ToString("X2") + info.SubClass.ToString("X2") + info.Protocol.ToString("X2")));
            }
        }

        private static byte[] Configuration(params byte[][] interfaces)
        {
            var bytes = new List<byte> { 9, 2, 0, 0, (byte)interfaces.Length, 1, 0, 0x80, 50 };
            byte number = 0;
            foreach (byte[] code in interfaces)
            {
                bytes.AddRange(new byte[] { 9, 4, number++, 0, 2, code[0], code[1], code[2], 0 });
                bytes.AddRange(new byte[] { 7, 5, 0x81, 2, 0, 2, 0 });
                bytes.AddRange(new byte[] { 7, 5, 0x02, 2, 0, 2, 0 });
            }
            bytes[2] = (byte)(bytes.Count & 0xFF);
            bytes[3] = (byte)(bytes.Count >> 8);
            return bytes.ToArray();
        }

        private static List<byte[]> DescriptorSeeds()
        {
            return new List<byte[]>
            {
                Configuration(new byte[] { 8, 6, 0x50 }),
                Configuration(new byte[] { 3, 1, 1 }, new byte[] { 3, 1, 2 }),
                Configuration(new byte[] { 0xE, 1, 0 }, new byte[] { 0xE, 2, 0 }, new byte[] { 1, 1, 0 }, new byte[] { 1, 2, 0 }),
                Configuration(new byte[] { 0xFF, 0xFF, 0 }),
                Configuration(new byte[] { 6, 1, 1 }),
                new byte[0],
                new byte[] { 0, 0 }
            };
        }

        // ---------- the same, read the way Linux hands it over ----------

        private static void FuzzSysfs(byte[] input)
        {
            // The kernel shows a device's descriptors as one file: the device descriptor, then every configuration.
            string device = Path.Combine(sysfsDir, "1-1");
            Directory.CreateDirectory(device);
            File.WriteAllBytes(Path.Combine(device, "descriptors"), input);
            // The first bytes double as the text files a device fills in itself.
            string text = AsText(input.Length > 300 ? input.AsSpan(0, 300).ToArray() : input);
            File.WriteAllText(Path.Combine(device, "idVendor"), "0781\n");
            File.WriteAllText(Path.Combine(device, "idProduct"), "5567\n");
            File.WriteAllText(Path.Combine(device, "product"), text);
            File.WriteAllText(Path.Combine(device, "manufacturer"), text);
            File.WriteAllText(Path.Combine(device, "serial"), text);
            File.WriteAllText(Path.Combine(device, "authorized"), "1\n");

            var backend = new LinuxDeviceBackend(sysfsDir, () => new HashSet<string>());
            List<UsbDevice> devices = backend.Scan();
            Require(devices.Count <= 1, "one folder is at most one device");
            foreach (UsbDevice found in devices)
            {
                Require(found.InstanceId.StartsWith(@"USB\VID_0781&PID_5567\", StringComparison.Ordinal), "the ID starts with the vendor and product the kernel read");
                Require(found.InstanceId == Policy.NormalizeId(found.InstanceId), "the ID is normalized");
                foreach (char c in found.InstanceId) Require(c > ' ' && c <= '~', "an ID holds only printable ASCII, whatever the serial number said");
                Require(found.InstanceId.Length <= 120, "an ID has a sane length");
                Require(found.Name == TextSafety.Clean(found.Name) && found.Name.Length <= TextSafety.MaxNameLength, "a name from a device is cleaned and cut");
                Require(found.Functions.Count <= 64, "a device never has more than 64 functions");
                // A device whatever it claims can be judged and logged.
                var policy = new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block };
                Decision decision = PolicyEngine.Evaluate(policy, found, TimeSpan.MaxValue);
                PolicyEngine.Explain(decision, policy);
                TrustEntry trust = TrustEntry.ForDevice(found);
                Require(Policy.FromJson(new Policy { Trusted = { trust } }.ToJson()).Trusted.Count == 1, "a device that was read can be trusted and saved");
            }
        }

        private static List<byte[]> SysfsSeeds()
        {
            var seeds = new List<byte[]>();
            foreach (byte[] configuration in DescriptorSeeds())
            {
                var bytes = new List<byte> { 18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x81, 0x07, 0x67, 0x55, 0, 1, 1, 2, 3, 1 };
                bytes.AddRange(configuration);
                seeds.Add(bytes.ToArray());
            }
            // Two configurations, as a phone has them.
            var two = new List<byte> { 18, 1, 0x00, 0x02, 0xEF, 2, 1, 64, 0xD1, 0x18, 0xE0, 0x4E, 0, 1, 1, 2, 3, 2 };
            two.AddRange(Configuration(new byte[] { 0xFF, 0xFF, 0 }));
            two.AddRange(Configuration(new byte[] { 6, 1, 1 }, new byte[] { 8, 6, 0x50 }));
            seeds.Add(two.ToArray());
            return seeds;
        }

        private static List<byte[]> Texts(params string[] texts)
        {
            return texts.Select(text => Encoding.UTF8.GetBytes(text)).ToList();
        }
    }
}
