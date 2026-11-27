using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Text;
using System.Threading;
using Lotus.Core;
using Xunit;

// LotusPaths points every test at one data folder at a time, so the tests take turns.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lotus.Tests
{
    public class ObjectCodecTests
    {
        private static Dictionary<string, object> Sample()
        {
            return new Dictionary<string, object>
            {
                ["armed"] = true,
                ["off"] = false,
                ["nothing"] = null,
                ["count"] = 3.0,
                ["name"] = "Ünïcode 日本 🔒",
                ["list"] = new List<object>
                {
                    "a", 1.5, false, null,
                    new Dictionary<string, object> { ["deep"] = new List<object> { "x" } }
                },
                ["empty"] = new Dictionary<string, object>()
            };
        }

        [Fact]
        public void A_record_comes_back_as_it_went_in()
        {
            Dictionary<string, object> before = Sample();
            object after = ObjectCodec.Decode(ObjectCodec.Encode(before));
            Assert.Equal(Json.Write(before), Json.Write(after));
        }

        [Fact]
        public void A_policy_survives_the_binary_form()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.ReadOnly, Pnp = PnpMode.Block, AllowInput = false };
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            policy.AddTrust(TrustEntry.ForModel(Devices.Stick()));

            object tree = ObjectCodec.Decode(ObjectCodec.Encode(Json.Parse(policy.ToJson())));
            Policy back = Policy.FromJson(Json.Write(tree, false));

            Assert.True(back.SettingsEqual(policy));
            Assert.Equal(2, back.Trusted.Count);
            Assert.Equal(policy.Trusted[0].Functions, back.Trusted[0].Functions);
        }

        [Fact]
        public void Whole_numbers_of_any_type_are_stored_as_numbers()
        {
            var record = new Dictionary<string, object> { ["a"] = 7, ["b"] = 7L, ["c"] = (byte)7 };
            var back = (Dictionary<string, object>)ObjectCodec.Decode(ObjectCodec.Encode(record));
            Assert.All(back.Values, value => Assert.Equal(7.0, value));
        }

        [Fact]
        public void The_binary_form_is_not_json()
        {
            byte[] bytes = ObjectCodec.Encode(Sample());
            string text = Encoding.UTF8.GetString(bytes);
            Assert.False(Json.TryParse(text, out object _));
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("\":", text);
        }

        [Fact]
        public void A_record_that_was_cut_short_is_refused_at_every_length()
        {
            byte[] bytes = ObjectCodec.Encode(Sample());
            for (int length = 0; length < bytes.Length; length++)
            {
                var cut = new byte[length];
                Buffer.BlockCopy(bytes, 0, cut, 0, length);
                Assert.Throws<FormatException>(() => ObjectCodec.Decode(cut));
            }
        }

        [Fact]
        public void Data_after_the_record_is_refused()
        {
            byte[] bytes = ObjectCodec.Encode(Sample());
            Array.Resize(ref bytes, bytes.Length + 1);
            Assert.Throws<FormatException>(() => ObjectCodec.Decode(bytes));
        }

        [Theory]
        [InlineData(new byte[] { 9 })] // a kind of value that does not exist
        [InlineData(new byte[] { 5, 0xFF, 0xFF, 0xFF, 0x7F })] // a list that claims two billion items
        [InlineData(new byte[] { 5, 0xFF, 0xFF, 0xFF, 0xFF })] // a negative count
        [InlineData(new byte[] { 4, 2, 0, 0, 0, 0xC3, 0x28 })] // text that is not valid UTF-8
        [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0xF8, 0x7F })] // not a number
        [InlineData(new byte[] { 6, 2, 0, 0, 0, 1, 0, 0, 0, 0x61, 0, 1, 0, 0, 0, 0x61, 0 })] // the same field twice
        public void Hostile_bytes_are_refused(byte[] bytes)
        {
            Assert.Throws<FormatException>(() => ObjectCodec.Decode(bytes));
        }

        [Fact]
        public void Nesting_without_end_is_refused()
        {
            var bytes = new byte[200 * 5];
            for (int i = 0; i < 200; i++)
            {
                bytes[i * 5] = 5; // a list
                bytes[i * 5 + 1] = 1; // with one item
            }
            Assert.Throws<FormatException>(() => ObjectCodec.Decode(bytes));
        }

        [Fact]
        public void Random_bytes_never_do_worse_than_being_refused()
        {
            var random = new Random(20261127);
            for (int round = 0; round < 3000; round++)
            {
                var bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
                try
                {
                    ObjectCodec.Decode(bytes);
                }
                catch (FormatException)
                {
                }
            }
        }
    }

    public class SealedBoxTests
    {
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        [Fact]
        public void What_is_sealed_comes_back_with_its_serial_number()
        {
            byte[] plain = Encoding.UTF8.GetBytes("the trusted list");
            byte[] file = SealedBox.Seal(Key, SealedBox.KindPolicy, 42, plain);

            byte[] back = SealedBox.Open(Key, SealedBox.KindPolicy, file, out long serial);
            Assert.Equal(plain, back);
            Assert.Equal(42, serial);
        }

        [Fact]
        public void The_file_starts_with_the_LOTUS_mark_and_shows_nothing_of_the_content()
        {
            byte[] plain = Encoding.UTF8.GetBytes(@"USB\VID_0781&PID_5567\4C530001230505116353 armed trusted");
            byte[] file = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, plain);

            Assert.True(SealedBox.LooksSealed(file));
            Assert.Equal(new byte[] { 0x4C, 0x4F, 0x54, 0x55, 0x53, 0x00, 2, SealedBox.KindPolicy }, file.Take(8).ToArray());
            foreach (string word in new[] { "USB", "VID_0781", "armed", "trusted", "4C53" })
            {
                Assert.False(Contains(file, Encoding.UTF8.GetBytes(word)), word);
                Assert.False(Contains(file, Encoding.Unicode.GetBytes(word)), word);
            }
        }

        [Fact]
        public void The_same_content_never_gives_the_same_file_twice()
        {
            byte[] plain = Encoding.UTF8.GetBytes("same");
            byte[] first = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, plain);
            byte[] second = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, plain);
            Assert.NotEqual(first, second);
            // Not even one encrypted block repeats.
            Assert.NotEqual(first.Skip(36).Take(16).ToArray(), second.Skip(36).Take(16).ToArray());
        }

        [Fact]
        public void The_size_of_the_file_says_little_about_the_content()
        {
            int small = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, new byte[10]).Length;
            int larger = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, new byte[700]).Length;
            int muchLarger = SealedBox.Seal(Key, SealedBox.KindPolicy, 1, new byte[3000]).Length;
            Assert.Equal(small, larger);
            Assert.True(muchLarger > small);
        }

        [Fact]
        public void Changing_any_single_byte_breaks_the_seal()
        {
            byte[] file = SealedBox.Seal(Key, SealedBox.KindPolicy, 7, Encoding.UTF8.GetBytes("content"));
            for (int at = 0; at < file.Length; at++)
            {
                var changed = (byte[])file.Clone();
                changed[at] ^= 0x01;
                Assert.Throws<FormatException>(() => SealedBox.Open(Key, SealedBox.KindPolicy, changed, out long _));
            }
        }

        [Fact]
        public void A_file_that_was_cut_short_or_made_longer_is_refused()
        {
            byte[] file = SealedBox.Seal(Key, SealedBox.KindPolicy, 7, Encoding.UTF8.GetBytes("content"));
            foreach (int length in new[] { 0, 1, 8, 35, 36, 67, 68, file.Length - 16, file.Length - 1, file.Length + 16 })
            {
                var other = new byte[length];
                Buffer.BlockCopy(file, 0, other, 0, Math.Min(length, file.Length));
                Assert.Throws<FormatException>(() => SealedBox.Open(Key, SealedBox.KindPolicy, other, out long _));
            }
        }

        [Fact]
        public void Another_key_does_not_open_it()
        {
            byte[] file = SealedBox.Seal(Key, SealedBox.KindPolicy, 7, Encoding.UTF8.GetBytes("content"));
            var other = (byte[])Key.Clone();
            other[31] ^= 0x80;
            FormatException refused = Assert.Throws<FormatException>(
                () => SealedBox.Open(other, SealedBox.KindPolicy, file, out long _));
            Assert.Contains("seal is broken", refused.Message);
        }

        [Fact]
        public void A_ledger_cannot_be_passed_off_as_a_policy()
        {
            byte[] ledger = SealedBox.Seal(Key, SealedBox.KindLedger, 0, Encoding.UTF8.GetBytes("content"));
            Assert.Throws<FormatException>(() => SealedBox.Open(Key, SealedBox.KindPolicy, ledger, out long _));

            // Relabelling the file does not help either: each kind has its own keys.
            ledger[7] = SealedBox.KindPolicy;
            Assert.Throws<FormatException>(() => SealedBox.Open(Key, SealedBox.KindPolicy, ledger, out long _));
        }

        [Fact]
        public void Empty_content_and_large_content_both_work()
        {
            Assert.Empty(SealedBox.Open(Key, SealedBox.KindLedger,
                SealedBox.Seal(Key, SealedBox.KindLedger, 0, new byte[0]), out long _));

            var large = new byte[300000];
            new Random(1).NextBytes(large);
            Assert.Equal(large, SealedBox.Open(Key, SealedBox.KindLedger,
                SealedBox.Seal(Key, SealedBox.KindLedger, 0, large), out long _));
        }

        [Fact]
        public void Text_files_and_the_old_store_do_not_look_sealed()
        {
            Assert.False(SealedBox.LooksSealed(Encoding.ASCII.GetBytes("LOTUS-STORE-1\nAAAA\n{}" + new string(' ', 100))));
            Assert.False(SealedBox.LooksSealed(Encoding.ASCII.GetBytes("{ \"armed\": false }" + new string(' ', 100))));
            Assert.False(SealedBox.LooksSealed(new byte[0]));
            Assert.False(SealedBox.LooksSealed(null));
        }
    }

    public class TextSafetyTests
    {
        [Fact]
        public void Control_characters_and_line_breaks_are_removed()
        {
            Assert.Equal("Evil Stick was allowed", TextSafety.Clean("Evil\r\nStick\twas\u0000 allowed"));
        }

        [Fact]
        public void Characters_that_reorder_or_hide_text_are_removed()
        {
            // U+202E turns the rest of the line around. U+200B is invisible.
            Assert.Equal("Keyboardtxt.exe", TextSafety.Clean("Keyboard‮txt​.exe"));
        }

        [Fact]
        public void Long_names_are_cut()
        {
            Assert.Equal(TextSafety.MaxNameLength, TextSafety.Clean(new string('A', 5000)).Length);
        }

        [Fact]
        public void Ordinary_names_pass_through()
        {
            Assert.Equal("SanDisk Cruzer Blade USB Device", TextSafety.Clean("  SanDisk Cruzer Blade  USB Device "));
            Assert.Equal("Müller Gerät", TextSafety.Clean("Müller Gerät"));
        }
    }

    public class DescriptorTests
    {
        private static byte[] Interface(byte cls, byte sub, byte protocol)
        {
            return new byte[] { 9, 4, 0, 0, 1, cls, sub, protocol, 0 };
        }

        private static byte[] Concat(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        [Fact]
        public void Interfaces_are_read_from_a_configuration()
        {
            byte[] config = Concat(new byte[] { 9, 2, 34, 0, 2, 1, 0, 0x80, 50 },
                Interface(0x08, 0x06, 0x50), new byte[] { 7, 5, 0x81, 2, 0, 2, 0 }, Interface(0x03, 0x01, 0x01));
            var found = new List<UsbInterfaceInfo>();
            UsbDescriptors.ParseConfiguration(config, found);

            Assert.Equal(2, found.Count);
            Assert.Equal(@"USB\CLASS_08&SUBCLASS_06&PROT_50", found[0].ToCompatibleId());
            Assert.Equal(@"USB\CLASS_03&SUBCLASS_01&PROT_01", found[1].ToCompatibleId());
        }

        [Fact]
        public void A_descriptor_with_a_zero_length_does_not_loop_for_ever()
        {
            var found = new List<UsbInterfaceInfo>();
            UsbDescriptors.ParseConfiguration(new byte[] { 9, 2, 20, 0, 1, 1, 0, 0x80, 50, 0, 4, 0, 0 }, found);
            Assert.Empty(found);
        }

        [Fact]
        public void A_truncated_descriptor_is_read_as_far_as_it_goes()
        {
            byte[] config = Concat(Interface(0x03, 0, 0), new byte[] { 9, 4, 1, 0 });
            var found = new List<UsbInterfaceInfo>();
            UsbDescriptors.ParseConfiguration(config, found);
            Assert.Single(found);
        }

        [Fact]
        public void Descriptor_class_codes_make_a_composite_device_known_at_once()
        {
            var device = new UsbDevice();
            UsbClassifier.Classify(device, "", "", new[] { @"USB\COMPOSITE" }, new DeviceNodeInfo[0],
                new[] { @"USB\CLASS_08&SUBCLASS_06&PROT_50", @"USB\CLASS_03&SUBCLASS_01&PROT_01" });

            Assert.True(device.FunctionsKnown);
            Assert.Equal(DeviceKinds.Storage | DeviceKinds.Input, device.Kinds);
        }

        [Fact]
        public void Without_descriptors_a_fresh_composite_device_is_still_unknown()
        {
            var device = new UsbDevice();
            UsbClassifier.Classify(device, "", "", new[] { @"USB\COMPOSITE" }, new DeviceNodeInfo[0]);
            Assert.False(device.FunctionsKnown);
        }

        [Theory]
        [InlineData(@"USB\VID_0781&PID_5567\4C530001230505116353", true)]
        [InlineData(@"USB\VID_1A2C&PID_2D23\5&CC3F949&0&6", true)]
        [InlineData(@"USB\VID_1A2C&PID_2D23&MI_00\6&C3A2486&0&0000", false)]
        [InlineData(@"USB\ROOT_HUB30\4&2FD48294&0&0", false)]
        [InlineData(@"HID\VID_1A2C&PID_2D23\7&1", false)]
        public void Whole_device_ids_are_told_apart_from_interface_ids(string id, bool whole)
        {
            Assert.Equal(whole, UsbClassifier.IsWholeDeviceId(id));
        }
    }

    public class TrustPinTests
    {
        private static Policy Strict()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block, AllowInput = false };
        }

        [Fact]
        public void A_trusted_device_keeps_working_while_it_does_what_it_did()
        {
            Policy policy = Strict();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Decision decision = PolicyEngine.Evaluate(policy, Devices.Headset(), TimeSpan.MaxValue);
            Assert.Equal(Reason.Trusted, decision.Reason);
        }

        [Fact]
        public void A_trusted_device_that_grows_a_keyboard_is_blocked()
        {
            Policy policy = Strict();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));

            UsbDevice changed = Devices.Headset();
            changed.Functions = new List<string> { "01", "03", "08" };
            changed.Kinds |= DeviceKinds.Storage;
            Decision decision = PolicyEngine.Evaluate(policy, changed, TimeSpan.MaxValue);

            Assert.True(decision.IsBlock);
            Assert.NotNull(decision.Outgrown);
            Assert.Contains("did not have when it was trusted", PolicyEngine.Explain(decision, policy));
        }

        [Fact]
        public void A_trusted_device_may_show_fewer_functions_than_before()
        {
            Policy policy = Strict();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            UsbDevice fewer = Devices.Headset();
            fewer.Functions = new List<string> { "01" };
            fewer.Kinds = DeviceKinds.Audio;
            Assert.Equal(Reason.Trusted, PolicyEngine.Evaluate(policy, fewer, TimeSpan.MaxValue).Reason);
        }

        [Fact]
        public void Model_trust_is_pinned_too()
        {
            Policy policy = Strict();
            policy.AddTrust(TrustEntry.ForModel(Devices.Mouse()));
            UsbDevice impostor = Devices.Mouse();
            impostor.InstanceId = @"USB\VID_046D&PID_C542\6&OTHER&0&1";
            impostor.Functions = new List<string> { "03", "08" };
            impostor.Kinds = DeviceKinds.Input | DeviceKinds.Storage;
            Assert.True(PolicyEngine.Evaluate(policy, impostor, TimeSpan.MaxValue).IsBlock);
        }

        [Fact]
        public void An_entry_without_recorded_functions_covers_everything()
        {
            Policy policy = Strict();
            policy.AddTrust(new TrustEntry { Kind = TrustKind.Device, Id = Devices.StickId });
            Assert.Equal(Reason.Trusted, PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.MaxValue).Reason);
        }

        [Fact]
        public void A_pinned_entry_waits_for_a_device_that_has_not_identified_itself()
        {
            Policy policy = Strict();
            var entry = new TrustEntry { Kind = TrustKind.Device, Id = Devices.ComboId };
            entry.Functions.Add("03");
            policy.AddTrust(entry);
            Decision decision = PolicyEngine.Evaluate(policy, Devices.FreshComposite(), TimeSpan.Zero);
            Assert.Equal(Verdict.Pending, decision.Verdict);
        }

        [Fact]
        public void The_recorded_functions_survive_a_round_trip_through_json()
        {
            Policy policy = Strict();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Policy back = Policy.FromJson(policy.ToJson());
            Assert.Equal(new[] { "01", "03" }, back.Trusted[0].Functions);
        }

        [Fact]
        public void With_everything_blocked_an_unknown_device_is_blocked_without_waiting()
        {
            Decision decision = PolicyEngine.Evaluate(Strict(), Devices.FreshComposite(), TimeSpan.Zero);
            Assert.True(decision.IsBlock);
        }

        [Fact]
        public void With_the_keyboard_exception_an_unknown_device_still_gets_its_moment()
        {
            Policy policy = Strict();
            policy.AllowInput = true;
            Assert.Equal(Verdict.Pending,
                PolicyEngine.Evaluate(policy, Devices.FreshComposite(), TimeSpan.Zero).Verdict);
        }

        [Fact]
        public void Names_from_a_policy_file_are_cleaned()
        {
            string json = "{\"armed\":false,\"trusted\":[{\"kind\":\"device\",\"id\":\"" +
                          Devices.StickId.Replace("\\", "\\\\") + "\",\"name\":\"Nice\\u202Egnp.exe\\nline two\"}]}";
            Policy policy = Policy.FromJson(json);
            Assert.Equal("Nicegnp.exe line two", policy.Trusted[0].Name);
        }
    }

    public class FastPathTests
    {
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly Ledger ledger = new Ledger();
        private readonly List<LotusEvent> events = new List<LotusEvent>();
        private DateTime now = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        private readonly Enforcer enforcer;

        public FastPathTests()
        {
            enforcer = new Enforcer(bus, new SimulatedReadOnlyPolicy(), ledger, null, events.Add, () => now);
        }

        private static Policy StorageBlocked()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block };
        }

        private static Policy Strict()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block, AllowInput = false };
        }

        [Fact]
        public void A_new_drive_is_switched_off_without_a_full_scan()
        {
            enforcer.RunOnce(StorageBlocked());
            int scans = bus.ScanCalls;

            bus.Plug(Devices.Stick());
            DeviceStatus status = enforcer.FastPass(StorageBlocked(), Devices.StickId);

            Assert.Equal(DeviceState.Blocked, status.State);
            Assert.True(bus.Scan().Single().IsOff);
            Assert.Equal(scans + 1, bus.ScanCalls); // only the Scan() in the line above
            Assert.Equal(1, events.Count(e => e.Type == EventTypes.Blocked));
        }

        [Fact]
        public void The_full_pass_that_follows_does_not_report_the_block_twice()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);
            enforcer.RunOnce(StorageBlocked());

            Assert.Equal(1, events.Count(e => e.Type == EventTypes.Blocked));
            Assert.Equal(1, events.Count(e => e.Type == EventTypes.Connected));
        }

        [Fact]
        public void An_allowed_device_is_left_alone()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Keyboard());
            DeviceStatus status = enforcer.FastPass(StorageBlocked(), Devices.KeyboardId);
            Assert.Equal(DeviceState.Allowed, status.State);
            Assert.Equal(0, bus.BlockCalls);
        }

        [Fact]
        public void An_id_that_is_not_a_connected_device_is_ignored()
        {
            enforcer.RunOnce(StorageBlocked());
            Assert.Null(enforcer.FastPass(StorageBlocked(), Devices.StickId));
        }

        [Fact]
        public void Nothing_is_decided_before_the_first_full_pass()
        {
            bus.Plug(Devices.Stick());
            Assert.Null(enforcer.FastPass(StorageBlocked(), Devices.StickId));
            Assert.Equal(0, bus.BlockCalls);
        }

        [Fact]
        public void A_blocked_drive_that_is_plugged_back_in_is_announced_again()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(StorageBlocked());
            enforcer.RunOnce(StorageBlocked());
            bus.Unplug(Devices.StickId);
            enforcer.RunOnce(StorageBlocked());
            events.Clear();

            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);

            Assert.Equal(1, bus.BlockCalls); // it came back already off
            Assert.Contains(events, e => e.Type == EventTypes.Blocked && e.Message.Contains("is blocked because"));
        }

        [Fact]
        public void Unplugged_drives_are_marked_so_they_come_back_switched_off()
        {
            bus.Remember(Devices.Stick());
            bus.Remember(Devices.Keyboard());
            enforcer.RunOnce(StorageBlocked());

            int marked = enforcer.MarkAbsent(StorageBlocked());

            Assert.Equal(1, marked);
            Assert.True(bus.HasDisabledMark(Devices.StickId));
            Assert.False(bus.HasDisabledMark(Devices.KeyboardId));
            Assert.True(ledger.Get(Devices.StickId).Premarked);

            bus.Plug(Devices.Stick());
            Assert.True(bus.Scan().Single().IsOff);
            Assert.Equal(0, bus.BlockCalls);
        }

        [Fact]
        public void A_marked_drive_that_arrives_is_reported_and_becomes_an_ordinary_blocked_device()
        {
            bus.Remember(Devices.Stick());
            enforcer.RunOnce(StorageBlocked());
            enforcer.MarkAbsent(StorageBlocked());
            events.Clear();

            bus.Plug(Devices.Stick());
            DeviceStatus status = enforcer.FastPass(StorageBlocked(), Devices.StickId);

            Assert.Equal(DeviceState.Blocked, status.State);
            Assert.False(ledger.Get(Devices.StickId).Premarked);
            Assert.Contains(events, e => e.Type == EventTypes.Blocked);
        }

        [Fact]
        public void Marks_are_taken_back_quietly_when_the_policy_relaxes()
        {
            bus.Remember(Devices.Stick());
            enforcer.RunOnce(StorageBlocked());
            enforcer.MarkAbsent(StorageBlocked());
            events.Clear();

            enforcer.RunOnce(new Policy());

            Assert.False(bus.HasDisabledMark(Devices.StickId));
            Assert.Equal(0, ledger.Count);
            Assert.DoesNotContain(events, e => e.Type == EventTypes.Released);
        }

        [Fact]
        public void Trusted_and_already_disabled_devices_are_not_marked()
        {
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            bus.Remember(Devices.Stick());
            UsbDevice other = Devices.Stick(@"USB\VID_0781&PID_5567\OTHERSERIAL");
            bus.Plug(other);
            bus.DisableByHand(other.InstanceId);
            bus.Unplug(other.InstanceId);
            enforcer.RunOnce(policy);

            Assert.Equal(0, enforcer.MarkAbsent(policy));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void An_unplugged_composite_device_is_only_marked_when_every_answer_is_a_block()
        {
            bus.Remember(Devices.StorageKeyboardCombo());
            enforcer.RunOnce(StorageBlocked());

            // Windows does not remember what an unplugged composite device does, and
            // with only storage blocked it might be harmless.
            Assert.Equal(0, enforcer.MarkAbsent(StorageBlocked()));
            Assert.Equal(1, enforcer.MarkAbsent(Strict()));
            Assert.False(ledger.Get(Devices.ComboId).KindsKnown);
        }

        [Fact]
        public void A_mark_made_without_knowing_the_device_goes_when_it_is_no_longer_certain()
        {
            bus.Remember(Devices.StorageKeyboardCombo());
            enforcer.RunOnce(Strict());
            enforcer.MarkAbsent(Strict());

            Policy relaxed = Strict();
            relaxed.AllowInput = true;
            enforcer.RunOnce(relaxed);

            Assert.False(bus.HasDisabledMark(Devices.ComboId));
        }

        [Fact]
        public void Nothing_is_marked_while_disarmed()
        {
            bus.Remember(Devices.Stick());
            enforcer.RunOnce(new Policy());
            Assert.Equal(0, enforcer.MarkAbsent(new Policy()));
        }

        [Fact]
        public void The_ledger_keeps_the_new_fields()
        {
            bus.Remember(Devices.StorageKeyboardCombo());
            enforcer.RunOnce(Strict());
            enforcer.MarkAbsent(Strict());

            Ledger back = Ledger.FromJson(ledger.ToJson());
            Assert.True(back.Get(Devices.ComboId).Premarked);
            Assert.False(back.Get(Devices.ComboId).KindsKnown);
        }
    }

#if NETFRAMEWORK
    public class FolderGuardTests
    {
        /// <summary>
        /// Setup puts the final rules on a folder it may only read by then. That
        /// works because the access list is written on its own, apart from the owner.
        /// </summary>
        [Fact]
        public void SetRules_replaces_the_access_list_of_a_folder_and_leaves_its_contents_alone()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lotus-acl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "inside.txt");
            File.WriteAllText(file, "kept");
            try
            {
                FolderGuard.SetRules(dir, FolderGuard.PublicFolderRules());

                DirectorySecurity now = Directory.GetAccessControl(dir, AccessControlSections.Access);
                Assert.True(now.AreAccessRulesProtected);
                List<FileSystemAccessRule> rules = now.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().ToList();
                Assert.Equal(3, rules.Count);
                Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));

                const FileSystemRights writing = FileSystemRights.WriteData | FileSystemRights.AppendData |
                                                 FileSystemRights.Delete | FileSystemRights.ChangePermissions |
                                                 FileSystemRights.TakeOwnership;
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                foreach (FileSystemAccessRule rule in rules)
                {
                    bool mayWrite = (rule.FileSystemRights & writing) != 0;
                    Assert.Equal(rule.IdentityReference.Equals(system), mayWrite);
                }

                // Setting the same rules twice is what an update does.
                FolderGuard.SetRules(dir, FolderGuard.PublicFolderRules());
                Assert.Equal(3, Directory.GetAccessControl(dir, AccessControlSections.Access)
                    .GetAccessRules(true, true, typeof(SecurityIdentifier)).Count);
                Assert.Equal("kept", File.ReadAllText(file));
            }
            finally
            {
                var open = new DirectorySecurity();
                open.SetAccessRuleProtection(false, false);
                Directory.SetAccessControl(dir, open);
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Working_rules_shut_ordinary_users_out_and_let_administrators_write()
        {
            DirectorySecurity rules = FolderGuard.WorkingFolderRules();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            List<FileSystemAccessRule> list = rules.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            Assert.True(rules.AreAccessRulesProtected);
            Assert.DoesNotContain(list, rule => rule.IdentityReference.Equals(users));
            Assert.Contains(list, rule => rule.IdentityReference.Equals(admins) &&
                                          rule.FileSystemRights == FileSystemRights.FullControl);
        }
    }
#endif

    public class SecureStoreTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-store-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        private SecureStore Open()
        {
            return new SecureStore(Path.Combine(folder, "secure"), Path.Combine(folder, "policy.json"));
        }

        private static StoreContent Armed()
        {
            var content = new StoreContent
            {
                Policy = new Policy { Armed = true, Storage = StorageMode.Block }
            };
            content.Policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            return content;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static void AssertUnreadable(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            foreach (string word in new[] { "armed", "trusted", "storage", "VID_", "USB", "HyperX", "blocked" })
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(word)), Path.GetFileName(path) + " shows " + word);
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(word)), Path.GetFileName(path) + " shows " + word);
            }
        }

        /// <summary>Writes a store the way LOTUS 1.1 did: readable JSON with a keyed checksum in front.</summary>
        private static void WriteOldStore(SecureStore store, Policy policy, bool breakSeal = false)
        {
            byte[] key = PlatformKeyWrap.Create().Unwrap(File.ReadAllBytes(store.KeyPath));
            string payload = Lotus.Core.Json.Write(new Dictionary<string, object>
            {
                ["serial"] = 5.0,
                ["policy"] = Lotus.Core.Json.Parse(policy.ToJson()),
                ["password"] = new Dictionary<string, object>
                {
                    ["algorithm"] = "pbkdf2-sha256", ["iterations"] = 210000.0, ["salt"] = "c2FsdA==", ["hash"] = "aGFzaA=="
                }
            });
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            byte[] mac;
            using (var hmac = new System.Security.Cryptography.HMACSHA256(key)) mac = hmac.ComputeHash(bytes);
            if (breakSeal) payload = payload.Replace("\"armed\": true", "\"armed\": false");
            File.WriteAllText(store.StorePath, "LOTUS-STORE-1\n" + Convert.ToBase64String(mac) + "\n" + payload,
                new UTF8Encoding(false));
            File.Delete(store.BackupPath);
        }

        [Fact]
        public void A_new_installation_has_no_store()
        {
            Assert.Equal(StoreState.Fresh, Open().Load().State);
        }

        [Fact]
        public void What_is_saved_comes_back()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Ok, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
            Assert.Equal(StorageMode.Block, loaded.Content.Policy.Storage);
            Assert.Single(loaded.Content.Policy.Trusted);
            Assert.Equal(1, loaded.Content.Serial);
        }

        [Fact]
        public void Nothing_in_the_store_folder_can_be_read_by_someone_who_finds_it()
        {
            SecureStore store = Open();
            store.Load();
            StoreContent content = Armed();
            store.Save(content);
            store.Save(content);
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            store.SaveLedger(ledger);
            store.SaveLedger(ledger);

            string[] files = Directory.GetFiles(Path.Combine(folder, "secure"));
            Assert.Contains(files, f => f.EndsWith("policy.dat"));
            Assert.Contains(files, f => f.EndsWith("policy.dat.bak"));
            Assert.Contains(files, f => f.EndsWith("ledger.dat"));
            Assert.Contains(files, f => f.EndsWith("seal.key"));
            foreach (string file in files) AssertUnreadable(file);
        }

        [Fact]
        public void Changing_one_byte_of_the_store_breaks_the_seal()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            byte[] bytes = File.ReadAllBytes(store.StorePath);
            bytes[bytes.Length / 2] ^= 0x10;
            File.WriteAllBytes(store.StorePath, bytes);

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Tampered, loaded.State);
            Assert.Contains("seal is broken", loaded.Problem);
        }

        [Fact]
        public void A_readable_policy_put_in_place_of_the_store_is_refused()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            File.WriteAllText(store.StorePath, new Policy().ToJson());

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Tampered, loaded.State);
            Assert.Contains("not a LOTUS store", loaded.Problem);
        }

        [Fact]
        public void A_damaged_store_falls_back_to_the_last_good_copy()
        {
            SecureStore store = Open();
            store.Load();
            StoreContent content = Armed();
            store.Save(content);
            content.Policy.Pnp = PnpMode.Block;
            store.Save(content);
            File.WriteAllText(store.StorePath, "garbage");

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Restored, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
            Assert.Equal(PnpMode.Allow, loaded.Content.Policy.Pnp); // the copy is one save behind
        }

        [Fact]
        public void A_deleted_store_is_not_mistaken_for_a_new_installation()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            File.Delete(store.StorePath);

            Assert.Equal(StoreState.Tampered, Open().Load().State);
        }

        [Fact]
        public void A_store_without_its_key_cannot_be_trusted()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            File.Delete(store.KeyPath);

            Assert.Equal(StoreState.Tampered, Open().Load().State);
        }

        [Fact]
        public void A_store_sealed_with_another_key_is_refused()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());

            // Someone builds their own store with their own key and swaps the store in.
            string other = Path.Combine(folder, "attacker");
            var forged = new SecureStore(other);
            forged.Load();
            forged.Save(new StoreContent());
            File.Copy(forged.StorePath, store.StorePath, true);
            File.Delete(store.BackupPath);

            Assert.Equal(StoreState.Tampered, Open().Load().State);
        }

        [Fact]
        public void A_save_that_was_cut_short_is_finished_on_the_next_start()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            File.Move(store.StorePath, store.PendingPath);

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Restored, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
        }

        [Fact]
        public void The_policy_file_of_version_1_0_is_taken_over()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "policy.json"),
                new Policy { Armed = true, Pnp = PnpMode.Block }.ToJson());

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Legacy, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
        }

        [Fact]
        public void The_store_of_version_1_1_is_taken_over_and_its_readable_copy_is_not_kept()
        {
            SecureStore first = Open();
            first.Load();
            first.Save(new StoreContent()); // gives the folder a key, as 1.1 did
            WriteOldStore(first, Armed().Policy);

            SecureStore store = Open();
            StoreLoad loaded = store.Load();
            Assert.Equal(StoreState.Legacy, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
            Assert.Single(loaded.Content.Policy.Trusted);
            Assert.Equal(5, loaded.Content.Serial);

            store.Save(loaded.Content);
            foreach (string file in Directory.GetFiles(Path.Combine(folder, "secure"))) AssertUnreadable(file);
            Assert.Equal(StoreState.Ok, Open().Load().State);
            Assert.Equal(6, Open().Load().Content.Serial);
        }

        [Fact]
        public void A_store_of_version_1_1_that_was_edited_is_refused()
        {
            SecureStore first = Open();
            first.Load();
            first.Save(new StoreContent());
            WriteOldStore(first, Armed().Policy, breakSeal: true);

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Tampered, loaded.State);
            Assert.Contains("seal is broken", loaded.Problem);
        }

        [Fact]
        public void The_store_notices_when_someone_else_touches_the_file()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            Assert.True(store.IsUntouched(out string _));

            File.AppendAllText(store.StorePath, " ");
            Assert.False(store.IsUntouched(out string changed));
            Assert.Contains("changed outside LOTUS", changed);

            File.Delete(store.StorePath);
            Assert.False(store.IsUntouched(out string deleted));
            Assert.Contains("deleted", deleted);
        }

        [Fact]
        public void The_list_of_blocked_devices_is_kept_in_the_same_way()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            var ledger = new Ledger { ReadOnlyOwned = true };
            ledger.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            store.SaveLedger(ledger);

            SecureStore again = Open();
            again.Load();
            Ledger back = again.LoadLedger(out string error);
            Assert.Null(error);
            Assert.True(back.ReadOnlyOwned);
            Assert.True(back.Contains(Devices.StickId));
        }

        [Fact]
        public void A_changed_list_of_blocked_devices_is_refused_and_reported()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            store.SaveLedger(ledger);
            byte[] bytes = File.ReadAllBytes(store.LedgerPath);
            bytes[40] ^= 0x01;
            File.WriteAllBytes(store.LedgerPath, bytes);

            Ledger back = store.LoadLedger(out string error);
            Assert.Equal(0, back.Count);
            Assert.Contains("seal is broken", error);
        }

        [Fact]
        public void A_list_of_blocked_devices_cannot_stand_in_for_the_policy()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            store.SaveLedger(new Ledger());
            File.Copy(store.LedgerPath, store.StorePath, true);

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Tampered, loaded.State);
            Assert.Contains("another kind", loaded.Problem);
        }

        [Fact]
        public void A_reset_throws_the_policy_away_and_keeps_the_list_of_blocked_devices()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            store.SaveLedger(ledger);
            File.WriteAllText(store.StorePath, "wrecked");

            SecureStore damaged = Open();
            Assert.Equal(StoreState.Tampered, damaged.Load().State);
            damaged.ResetPolicy();

            SecureStore fresh = Open();
            // The key is still there, so a missing store would count as tampering.
            // After a reset the service saves a new, disarmed policy straight away.
            fresh.Load();
            fresh.Save(new StoreContent());
            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Ok, loaded.State);
            Assert.False(loaded.Content.Policy.Armed);
            Assert.True(fresh.LoadLedger(out string _).Contains(Devices.StickId));
        }

        [Fact]
        public void Erasing_leaves_nothing_behind()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            store.Save(Armed());
            store.SaveLedger(new Ledger());
            store.SaveLedger(new Ledger());
            store.Erase();
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "secure")));
        }
    }

    /// <summary>
    /// Runs the real service loop against a pretend USB bus and talks to it over
    /// the real channel: a named pipe on Windows, a Unix socket on Linux.
    /// </summary>
    public class GuardWorkerTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-guard-" + Guid.NewGuid().ToString("N"));
#if NETFRAMEWORK
        private readonly string pipeName = "LotusGuard.Test." + Guid.NewGuid().ToString("N");
#else
        // The name of a socket is a path, and a path to a socket may only be about a hundred characters long.
        private readonly string pipeName =
            Path.Combine(Path.GetTempPath(), "lotus-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");
#endif
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly SimulatedReadOnlyPolicy readOnly = new SimulatedReadOnlyPolicy();
        private readonly SimulatedInstallGate installGate = new SimulatedInstallGate();
        private GuardWorker worker;
        private readonly List<string> stops = new List<string>();
        private readonly EventIdTests.Recorder systemLog = new EventIdTests.Recorder();
        /// <summary>How far the service's clock runs ahead of the real one.</summary>
        private TimeSpan ahead = TimeSpan.Zero;

        public GuardWorkerTests()
        {
            LotusPaths.OverrideDataDir(folder);
            bus.Gate = installGate;
        }

        public void Dispose()
        {
            worker?.Dispose();
            LotusPaths.OverrideDataDir(null);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private GuardClient Start(bool setupAskedForReset = false)
        {
            worker?.Dispose();
            worker = new GuardWorker(new GuardOptions
            {
#if !NETFRAMEWORK
                Platform = new Lotus.Linux.LinuxPlatform(Path.Combine(folder, "no-devices")),
#endif
                Backend = bus,
                ReadOnly = readOnly,
                InstallGate = installGate,
                PipeName = pipeName,
                AdministratorsOnly = false,
                HardenFolders = false,
                WatchDevices = false,
                TakeResetRequest = () => setupAskedForReset,
                SystemLog = systemLog,
                Clock = () => DateTime.UtcNow + ahead
            });
            worker.StopRequested += purpose => { lock (stops) stops.Add(purpose); };
            worker.Start();
            Assert.True(worker.WaitUntilReady(10000), "the service loop did not come up");
            return new GuardClient(pipeName, false);
        }

        private static string Json(Policy policy)
        {
            return policy.ToJson();
        }

        private static Policy StorageBlocked()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block };
        }

        private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        private List<LotusEvent> Events()
        {
            return new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(1000);
        }

        [Fact]
        public void A_new_installation_is_disarmed()
        {
            GuardReply status = Start().Status();
            Assert.True(status.Ok);
            Assert.False(status.Armed);
            Assert.False(status.Lockdown);
            Assert.True(File.Exists(LotusPaths.StoreFile));
        }

        [Fact]
        public void A_policy_is_applied_without_any_password()
        {
            GuardClient client = Start();
            GuardReply reply = client.Apply(Json(StorageBlocked()));
            Assert.True(reply.Ok, reply.Message);
            Assert.True(reply.Armed);

            Assert.True(client.Apply(Json(new Policy())).Ok);
            Assert.False(client.Status().Armed);
        }

        [Fact]
        public void A_request_from_version_1_1_with_password_fields_still_works()
        {
            GuardClient client = Start();
            GuardReply reply = client.Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["password"] = "whatever",
                ["newPassword"] = "whatever else",
                ["policy"] = Lotus.Core.Json.Parse(Json(StorageBlocked()))
            });
            Assert.True(reply.Ok, reply.Message);
            Assert.False(reply.Data.ContainsKey("hasPassword"));
        }

        [Fact]
        public void Trust_that_was_given_for_a_time_ends_by_itself_and_the_device_is_blocked()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            TrustEntry entry = TrustEntry.ForDevice(Devices.Stick());
            entry.UntilUtc = DateTime.UtcNow.AddHours(1);
            policy.AddTrust(entry);
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Assert.True(client.Apply(Json(policy)).Ok);
            Thread.Sleep(400);
            Assert.False(bus.Scan().Single().IsOff);
            Assert.Equal(2, client.Read().ReadPolicy().Trusted.Count);

            ahead = TimeSpan.FromHours(2);
            worker.Kick();

            Assert.True(Eventually(() => bus.Scan().Single().IsOff));
            // The entry is off the list, the one without an end is not.
            Policy now = client.Read().ReadPolicy();
            Assert.Equal(Devices.HeadsetId, now.Trusted.Single().Id);
            LotusEvent ended = Events().Single(e => e.Type == EventTypes.TrustEnded);
            Assert.Equal(Devices.StickId, ended.DeviceId);
            Assert.Contains("has ended", ended.Message);
            Assert.Contains(Events(), e => e.Type == EventTypes.Blocked && e.DeviceId == Devices.StickId);
        }

        [Fact]
        public void Trust_that_ended_stays_ended_after_the_service_starts_again()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            TrustEntry entry = TrustEntry.ForDevice(Devices.Stick());
            entry.UntilUtc = DateTime.UtcNow.AddHours(1);
            policy.AddTrust(entry);
            Assert.True(client.Apply(Json(policy)).Ok);
            ahead = TimeSpan.FromHours(2);
            worker.Kick();
            Assert.True(Eventually(() => client.Read().ReadPolicy().Trusted.Count == 0));

            ahead = TimeSpan.Zero;
            client = Start();
            Assert.Empty(client.Read().ReadPolicy().Trusted);
            Assert.Single(Events(), e => e.Type == EventTypes.TrustEnded);
        }

        [Fact]
        public void A_locked_down_service_leaves_the_list_alone()
        {
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            TrustEntry entry = TrustEntry.ForDevice(Devices.Stick());
            entry.UntilUtc = DateTime.UtcNow.AddHours(1);
            policy.AddTrust(entry);
            Assert.True(client.Apply(Json(policy)).Ok);
            worker.Dispose();
            worker = null;
            File.WriteAllText(LotusPaths.StoreFile, "{ \"armed\": false }");
            File.Delete(LotusPaths.StoreFile + ".bak");

            ahead = TimeSpan.FromHours(2);
            client = Start();
            Assert.True(client.Status().Lockdown);
            Thread.Sleep(500);
            Assert.DoesNotContain(Events(), e => e.Type == EventTypes.TrustEnded);
        }

        [Fact]
        public void Every_line_of_the_activity_log_reaches_the_systems_log_with_its_number()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));
            Assert.True(Eventually(() => systemLog.Snapshot().Any(e => e.Type == EventTypes.Blocked)));

            List<LotusEvent> mirrored = systemLog.Snapshot();
            Assert.Equal(Events().Count, mirrored.Count);
            Assert.Contains(mirrored, e => EventIds.For(e.Type) == EventIds.GuardStarted);
            Assert.Contains(mirrored, e => EventIds.For(e.Type) == EventIds.PolicyChanged);
            LotusEvent blocked = mirrored.Single(e => e.Type == EventTypes.Blocked);
            Assert.Equal(Devices.StickId, blocked.DeviceId);
            Assert.Equal(EventIds.Blocked, EventIds.For(blocked.Type));
        }

        [Fact]
        public void An_armed_policy_blocks_through_the_real_loop()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Keyboard());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);

            Assert.True(Eventually(() => bus.Scan().Single(d => d.InstanceId == Devices.StickId).IsOff));
            Assert.False(bus.Scan().Single(d => d.InstanceId == Devices.KeyboardId).IsOff);
        }

        [Fact]
        public void The_service_hands_out_the_policy_and_the_list_of_blocked_devices()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Assert.True(client.Apply(Json(policy)).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));

            GuardReply state = client.Read();
            Assert.True(state.Ok);
            Policy read = state.ReadPolicy();
            Assert.True(read.Armed);
            Assert.Equal(StorageMode.Block, read.Storage);
            Assert.Single(read.Trusted);
            Assert.True(Eventually(() => client.Read().ReadLedger().Contains(Devices.StickId)));
        }

        [Fact]
        public void The_policy_survives_a_restart()
        {
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Assert.True(client.Apply(Json(policy)).Ok);

            client = Start();
            Assert.True(client.Status().Armed);
            Assert.Single(client.Read().ReadPolicy().Trusted);
        }

        [Fact]
        public void An_edit_to_the_store_is_undone_and_written_down()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));

            byte[] written = File.ReadAllBytes(LotusPaths.StoreFile);
            var edited = (byte[])written.Clone();
            edited[edited.Length / 2] ^= 0x01;
            File.WriteAllBytes(LotusPaths.StoreFile, edited);
            worker.Kick();

            Assert.True(Eventually(() => Events().Any(e => e.Type == EventTypes.Tamper)));
            Assert.True(Eventually(() =>
            {
                // LOTUS writes a fresh file with a new serial number, so it is not the old bytes again.
                var store = new SecureStore(LotusPaths.SecureDir);
                StoreLoad loaded = store.Load();
                return loaded.State == StoreState.Ok && loaded.Content.Policy.Armed;
            }));
            Assert.True(client.Status().Armed);
            Assert.True(bus.Scan().Single().IsOff);
        }

        [Fact]
        public void A_readable_policy_dropped_into_the_folder_changes_nothing()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);

            File.WriteAllText(LotusPaths.StoreFile, new Policy().ToJson());
            worker.Kick();

            Assert.True(Eventually(() => Events().Any(e => e.Type == EventTypes.Tamper)));
            Assert.True(Eventually(() => SealedBox.LooksSealed(File.ReadAllBytes(LotusPaths.StoreFile))));
            Assert.True(client.Status().Armed);
        }

        [Fact]
        public void A_deleted_store_is_put_back_while_the_service_runs()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
            File.Delete(LotusPaths.StoreFile);
            worker.Kick();

            Assert.True(Eventually(() => File.Exists(LotusPaths.StoreFile)));
            Assert.True(client.Status().Armed);
        }

        [Fact]
        public void A_store_that_is_wrecked_while_the_service_is_down_locks_everything()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Headset());
            bus.Plug(Devices.Keyboard());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(new Policy())).Ok);
            worker.Dispose();
            worker = null;

            File.WriteAllText(LotusPaths.StoreFile, "{ \"armed\": false }");
            File.Delete(LotusPaths.StoreFile + ".bak");

            client = Start();
            GuardReply status = client.Status();
            Assert.True(status.Lockdown);
            Assert.True(status.Armed);
            Assert.True(Eventually(() => bus.Scan().Single(d => d.InstanceId == Devices.StickId).IsOff));
            Assert.True(bus.Scan().Single(d => d.InstanceId == Devices.HeadsetId).IsOff);
            Assert.False(bus.Scan().Single(d => d.InstanceId == Devices.KeyboardId).IsOff); // keyboards stay usable
            Assert.Equal(GuardProtocol.ErrLockdown, client.Apply(Json(new Policy())).Code);
            Assert.Contains(Events(), e => e.Type == EventTypes.Tamper && e.Message.Contains("locked down"));
        }

        [Fact]
        public void Running_setup_again_ends_a_lockdown_and_frees_the_devices()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Headset());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(new Policy())).Ok);
            worker.Dispose();
            worker = null;
            File.WriteAllText(LotusPaths.StoreFile, "wrecked");
            File.Delete(LotusPaths.StoreFile + ".bak");

            client = Start();
            Assert.True(client.Status().Lockdown);
            Assert.True(Eventually(() => bus.Scan().All(d => d.IsOff)));

            // A restart alone does not help.
            client = Start();
            Assert.True(client.Status().Lockdown);

            client = Start(setupAskedForReset: true);
            GuardReply status = client.Status();
            Assert.False(status.Lockdown);
            Assert.False(status.Armed);
            Assert.True(Eventually(() => bus.Scan().All(d => !d.IsOff)));
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
        }

        [Fact]
        public void A_reset_request_does_nothing_to_a_healthy_store()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);

            client = Start(setupAskedForReset: true);
            Assert.True(client.Status().Armed);
        }

        [Fact]
        public void The_policy_file_of_version_1_0_is_moved_into_the_store()
        {
            Directory.CreateDirectory(folder);
            var old = new Policy { Armed = true, Storage = StorageMode.Block };
            File.WriteAllText(LotusPaths.LegacyPolicyFile, old.ToJson());

            GuardClient client = Start();
            Assert.True(client.Status().Armed);
            Assert.False(File.Exists(LotusPaths.LegacyPolicyFile));
            Assert.True(SealedBox.LooksSealed(File.ReadAllBytes(LotusPaths.StoreFile)));
        }

        [Fact]
        public void The_readable_list_of_blocked_devices_from_version_1_1_is_moved_into_the_store()
        {
            bus.Plug(Devices.Stick());
            bus.Block(Devices.StickId, out BlockMethod _);
            Directory.CreateDirectory(LotusPaths.SecureDir);
            File.WriteAllText(LotusPaths.LegacyPolicyFile, StorageBlocked().ToJson());
            var old = new Ledger();
            old.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            string oldPath = Path.Combine(LotusPaths.SecureDir, "ledger.json");
            File.WriteAllText(oldPath, old.ToJson());

            GuardClient client = Start();
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(LotusPaths.LedgerFile));
            Assert.True(client.Read().ReadLedger().Contains(Devices.StickId));
            Assert.True(bus.Scan().Single().IsOff);
            Assert.DoesNotContain(Events(), e => e.Type == EventTypes.Blocked); // it was blocked already
        }

        [Fact]
        public void A_device_that_arrives_is_judged_at_once()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
            Assert.True(Eventually(() => Events().Any(e => e.Type == EventTypes.PolicyChanged &&
                                                           e.Message.StartsWith("Policy changed"))));
            Thread.Sleep(150); // let the pass that follows the change finish

            bus.Plug(Devices.Stick());
            DateTime plugged = DateTime.UtcNow;
            worker.NotifyArrival(Devices.StickId);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff, 2000));
            Assert.True((DateTime.UtcNow - plugged).TotalMilliseconds < 500, "the block took too long");
        }

        [Fact]
        public void Setup_can_stop_the_service_for_an_update()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);

            Assert.Equal(GuardProtocol.ErrBadRequest, client.Stop("because").Code);
            lock (stops) Assert.Empty(stops);

            Assert.True(client.Stop(GuardProtocol.PurposeUpdate).Ok);
            Assert.True(Eventually(() => { lock (stops) return stops.Contains(GuardProtocol.PurposeUpdate); }));
            Assert.True(File.Exists(LotusPaths.StoreFile)); // an update keeps the policy
        }

        [Fact]
        public void Removing_LOTUS_releases_every_device_and_erases_the_store()
        {
            bus.Plug(Devices.Stick());
            bus.Remember(Devices.Phone());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked())).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));
            Assert.True(Eventually(() => bus.HasDisabledMark(Devices.PhoneId)));

            Assert.True(client.Stop(GuardProtocol.PurposeUninstall).Ok);

            Assert.False(bus.Scan().Single().IsOff);
            Assert.False(bus.HasDisabledMark(Devices.PhoneId));
            Assert.Empty(Directory.GetFiles(LotusPaths.SecureDir));
            Assert.True(Eventually(() => { lock (stops) return stops.Contains(GuardProtocol.PurposeUninstall); }));
        }

        [Fact]
        public void Nonsense_on_the_pipe_is_refused_politely()
        {
            GuardClient client = Start();
            GuardReply unknown = client.Send(new Dictionary<string, object> { ["op"] = "format_c" });
            Assert.Equal(GuardProtocol.ErrBadRequest, unknown.Code);

            GuardReply gone = client.Send(new Dictionary<string, object> { ["op"] = "set_password" });
            Assert.Equal(GuardProtocol.ErrBadRequest, gone.Code);

            GuardReply noPolicy = client.Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpApply });
            Assert.Equal(GuardProtocol.ErrBadRequest, noPolicy.Code);

            GuardReply badPolicy = client.Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["policy"] = new Dictionary<string, object> { ["storage"] = "everything" }
            });
            Assert.Equal(GuardProtocol.ErrBadRequest, badPolicy.Code);
            Assert.True(client.Status().Ok); // and the service is still there
        }

#if NETFRAMEWORK
        [Fact]
        public void A_second_program_cannot_take_over_the_pipe_name()
        {
            Start();
            using (var intruder = new GuardPipeServer(pipeName, r => new Dictionary<string, object>(), null, false))
            {
                var problems = new List<string>();
                using (var second = new GuardPipeServer(pipeName, r => new Dictionary<string, object>(),
                           problems.Add, false))
                {
                    second.Start();
                    Assert.True(Eventually(() => { lock (problems) return problems.Count > 0; }));
                }
            }
        }
#endif

        [Fact]
        public void The_status_file_says_whether_LOTUS_is_armed_and_nothing_about_the_policy()
        {
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Assert.True(client.Apply(Json(policy)).Ok);
            Assert.True(Eventually(() => File.Exists(LotusPaths.StatusFile) &&
                                         File.ReadAllText(LotusPaths.StatusFile).Contains("\"armed\": true")));
            string status = File.ReadAllText(LotusPaths.StatusFile);
            Assert.DoesNotContain("storage", status);
            Assert.DoesNotContain("trusted", status);
            Assert.DoesNotContain("VID_", status);
        }
    }
}
