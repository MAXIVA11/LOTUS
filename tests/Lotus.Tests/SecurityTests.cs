using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lotus.Core;
using Xunit;

// LotusPaths points every test at one data folder at a time, so the tests take turns.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lotus.Tests
{
    public class PasswordTests
    {
        private static string Hex(byte[] bytes)
        {
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        [Theory]
        [InlineData(1, "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b")]
        [InlineData(2, "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43")]
        [InlineData(4096, "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a")]
        public void Pbkdf2_matches_the_published_test_vectors(int iterations, string expected)
        {
            byte[] key = Pbkdf2.Sha256(Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("salt"),
                iterations, 32);
            Assert.Equal(expected, Hex(key));
        }

        [Fact]
        public void Pbkdf2_fills_more_than_one_block()
        {
            byte[] key = Pbkdf2.Sha256(Encoding.ASCII.GetBytes("passwordPASSWORDpassword"),
                Encoding.ASCII.GetBytes("saltSALTsaltSALTsaltSALTsaltSALTsalt"), 4096, 40);
            Assert.Equal("348c89dbcbd32b2f32d814b8116e84cf2b17347ebc1800181c4e2a1fb8dd53e1c635518c7dac47e9", Hex(key));
        }

        [Fact]
        public void A_record_accepts_its_own_password_and_nothing_else()
        {
            PasswordRecord record = PasswordRecord.Create("correct horse", 500);
            Assert.True(record.Verify("correct horse"));
            Assert.False(record.Verify("correct horsf"));
            Assert.False(record.Verify(""));
            Assert.False(record.Verify(null));
        }

        [Fact]
        public void Two_records_for_one_password_differ()
        {
            PasswordRecord a = PasswordRecord.Create("correct horse", 500);
            PasswordRecord b = PasswordRecord.Create("correct horse", 500);
            Assert.NotEqual(Hex(a.Hash), Hex(b.Hash));
        }

        [Fact]
        public void Short_passwords_are_refused()
        {
            Assert.NotNull(PasswordRecord.Problem("1234567"));
            Assert.Null(PasswordRecord.Problem("12345678"));
            Assert.Throws<ArgumentException>(() => PasswordRecord.Create("short"));
        }

        [Fact]
        public void A_record_survives_json_and_rejects_damage()
        {
            PasswordRecord record = PasswordRecord.Create("correct horse", 500);
            PasswordRecord back = PasswordRecord.FromJsonObject(record.ToJsonObject());
            Assert.True(back.Verify("correct horse"));

            Dictionary<string, object> broken = record.ToJsonObject();
            broken["salt"] = "not base64 !!";
            Assert.Null(PasswordRecord.FromJsonObject(broken));

            // A planted record must not be able to make the service hash for hours.
            Dictionary<string, object> slow = record.ToJsonObject();
            slow["iterations"] = 2000000000.0;
            Assert.Null(PasswordRecord.FromJsonObject(slow));
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
                Policy = new Policy { Armed = true, Storage = StorageMode.Block },
                Password = PasswordRecord.Create("correct horse", 500)
            };
            content.Policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            return content;
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
            Assert.True(loaded.Content.Password.Verify("correct horse"));
            Assert.Equal(1, loaded.Content.Serial);
        }

        [Fact]
        public void An_edit_in_a_text_editor_breaks_the_seal()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            string text = File.ReadAllText(store.StorePath);
            Assert.Contains("\"armed\": true", text);
            File.WriteAllText(store.StorePath, text.Replace("\"armed\": true", "\"armed\": false"));

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Tampered, loaded.State);
            Assert.Contains("seal is broken", loaded.Problem);
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
        public void The_policy_file_of_the_earlier_version_is_taken_over()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "policy.json"),
                new Policy { Armed = true, Pnp = PnpMode.Block }.ToJson());

            StoreLoad loaded = Open().Load();
            Assert.Equal(StoreState.Legacy, loaded.State);
            Assert.True(loaded.Content.Policy.Armed);
            Assert.Null(loaded.Content.Password);
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
        public void The_policy_can_be_read_for_display_without_the_key()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());

            StoreContent content = SecureStore.ReadUnverified(store.StorePath);
            Assert.True(content.Policy.Armed);
            Assert.NotNull(content.Password);
            Assert.Null(SecureStore.ReadUnverified(Path.Combine(folder, "nothing.dat")));
        }

        [Fact]
        public void Erasing_leaves_nothing_behind()
        {
            SecureStore store = Open();
            store.Load();
            store.Save(Armed());
            store.Save(Armed());
            store.Erase();
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "secure")));
        }
    }

    /// <summary>Runs the real service loop against a pretend USB bus and talks to it over a real pipe.</summary>
    public class GuardWorkerTests : IDisposable
    {
        private const string Password = "correct horse battery";

        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-guard-" + Guid.NewGuid().ToString("N"));
        private readonly string pipeName = "LotusGuard.Test." + Guid.NewGuid().ToString("N");
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly SimulatedReadOnlyPolicy readOnly = new SimulatedReadOnlyPolicy();
        private GuardWorker worker;
        private readonly List<string> stops = new List<string>();

        public GuardWorkerTests()
        {
            LotusPaths.OverrideDataDir(folder);
        }

        public void Dispose()
        {
            worker?.Dispose();
            LotusPaths.OverrideDataDir(null);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private GuardClient Start()
        {
            worker?.Dispose();
            worker = new GuardWorker(new GuardOptions
            {
                Backend = bus,
                ReadOnly = readOnly,
                PipeName = pipeName,
                AdministratorsOnly = false,
                HardenFolders = false,
                WatchDevices = false,
                PasswordIterations = 300
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
        public void A_new_installation_is_disarmed_and_has_no_password()
        {
            GuardReply status = Start().Status();
            Assert.True(status.Ok);
            Assert.False(status.Armed);
            Assert.False(status.HasPassword);
            Assert.False(status.Lockdown);
            Assert.True(File.Exists(LotusPaths.StoreFile));
        }

        [Fact]
        public void Settings_can_be_saved_without_a_password_while_disarmed()
        {
            GuardClient client = Start();
            GuardReply reply = client.Apply(Json(new Policy { Storage = StorageMode.Block }), "");
            Assert.True(reply.Ok, reply.Message);
            Assert.False(client.Status().HasPassword);
        }

        [Fact]
        public void Arming_for_the_first_time_needs_a_password()
        {
            GuardClient client = Start();
            Assert.Equal(GuardProtocol.ErrNewPasswordNeeded, client.Apply(Json(StorageBlocked()), "").Code);
            Assert.Equal(GuardProtocol.ErrWeakPassword, client.Apply(Json(StorageBlocked()), "", "short").Code);
            Assert.False(client.Status().Armed);

            GuardReply reply = client.Apply(Json(StorageBlocked()), "", Password);
            Assert.True(reply.Ok, reply.Message);
            Assert.True(reply.Armed);
            Assert.True(reply.HasPassword);
        }

        [Fact]
        public void An_armed_policy_blocks_through_the_real_loop()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Keyboard());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);

            Assert.True(Eventually(() => bus.Scan().Single(d => d.InstanceId == Devices.StickId).IsOff));
            Assert.False(bus.Scan().Single(d => d.InstanceId == Devices.KeyboardId).IsOff);
        }

        [Fact]
        public void Every_later_change_needs_the_password()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);

            Assert.Equal(GuardProtocol.ErrPasswordNeeded, client.Apply(Json(new Policy()), "").Code);
            Assert.Equal(GuardProtocol.ErrWrongPassword, client.Apply(Json(new Policy()), "wrong password").Code);
            Assert.True(client.Status().Armed);
            Assert.Contains(Events(), e => e.Type == EventTypes.Security && e.Message.Contains("wrong LOTUS password"));

            Assert.True(client.Apply(Json(new Policy()), Password).Ok);
            Assert.False(client.Status().Armed);
        }

        [Fact]
        public void Disarming_does_not_remove_the_password()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            Assert.True(client.Apply(Json(new Policy()), Password).Ok);
            Assert.True(client.Status().HasPassword);
            Assert.Equal(GuardProtocol.ErrPasswordNeeded, client.Apply(Json(StorageBlocked()), "").Code);
        }

        [Fact]
        public void Guessing_is_slowed_down()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(GuardProtocol.ErrWrongPassword, client.Verify("guess number " + i).Code);
            }

            GuardReply locked = client.Verify(Password);
            Assert.Equal(GuardProtocol.ErrLockedOut, locked.Code);
            Assert.InRange(locked.RetryAfterSeconds, 1, 30);
        }

        [Fact]
        public void The_password_can_be_changed_with_the_old_one()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);

            Assert.Equal(GuardProtocol.ErrWrongPassword, client.SetPassword("not the password", "another long one").Code);
            Assert.Equal(GuardProtocol.ErrWeakPassword, client.SetPassword(Password, "tiny").Code);
            Assert.True(client.SetPassword(Password, "another long one").Ok);
            Assert.Equal(GuardProtocol.ErrWrongPassword, client.Verify(Password).Code);
            Assert.True(client.Verify("another long one").Ok);
        }

        [Fact]
        public void The_policy_survives_a_restart()
        {
            GuardClient client = Start();
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Assert.True(client.Apply(Json(policy), "", Password).Ok);

            client = Start();
            GuardReply status = client.Status();
            Assert.True(status.Armed);
            Assert.True(status.HasPassword);
            Assert.Single(SecureStore.ReadUnverified(LotusPaths.StoreFile).Policy.Trusted);
        }

        [Fact]
        public void An_edit_to_the_store_is_undone_and_written_down()
        {
            bus.Plug(Devices.Stick());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));

            string text = File.ReadAllText(LotusPaths.StoreFile);
            File.WriteAllText(LotusPaths.StoreFile, text.Replace("\"armed\": true", "\"armed\": false"));
            worker.Kick();

            Assert.True(Eventually(() => Events().Any(e => e.Type == EventTypes.Tamper)));
            Assert.True(Eventually(() => File.ReadAllText(LotusPaths.StoreFile).Contains("\"armed\": true")));
            Assert.True(client.Status().Armed);
            Assert.True(bus.Scan().Single().IsOff);
        }

        [Fact]
        public void A_deleted_store_is_put_back_while_the_service_runs()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            File.Delete(LotusPaths.StoreFile);
            worker.Kick();

            Assert.True(Eventually(() => File.Exists(LotusPaths.StoreFile)));
            Assert.True(client.Status().Armed);
            Assert.True(client.Verify(Password).Ok);
        }

        [Fact]
        public void A_store_that_is_wrecked_while_the_service_is_down_locks_everything()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Headset());
            bus.Plug(Devices.Keyboard());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(new Policy()), "").Ok);
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
            Assert.Equal(GuardProtocol.ErrLockdown, client.Apply(Json(new Policy()), "").Code);
            Assert.Contains(Events(), e => e.Type == EventTypes.Tamper && e.Message.Contains("locked down"));
        }

        [Fact]
        public void The_policy_file_of_the_earlier_version_is_moved_into_the_store()
        {
            Directory.CreateDirectory(folder);
            var old = new Policy { Armed = true, Storage = StorageMode.Block };
            File.WriteAllText(LotusPaths.LegacyPolicyFile, old.ToJson());

            GuardClient client = Start();
            Assert.True(client.Status().Armed);
            Assert.False(File.Exists(LotusPaths.LegacyPolicyFile));
            // It has no password yet, so the next change has to set one.
            Assert.Equal(GuardProtocol.ErrNewPasswordNeeded, client.Apply(Json(old), "").Code);
        }

        [Fact]
        public void A_device_that_arrives_is_judged_at_once()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
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
        public void Setup_can_stop_the_service_only_with_the_password()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);

            Assert.Equal(GuardProtocol.ErrWrongPassword, client.Stop("nope nope nope", GuardProtocol.PurposeUpdate).Code);
            Assert.Equal(GuardProtocol.ErrBadRequest, client.Stop(Password, "because").Code);
            lock (stops) Assert.Empty(stops);

            Assert.True(client.Stop(Password, GuardProtocol.PurposeUpdate).Ok);
            Assert.True(Eventually(() => { lock (stops) return stops.Contains(GuardProtocol.PurposeUpdate); }));
            Assert.True(File.Exists(LotusPaths.StoreFile)); // an update keeps the policy
        }

        [Fact]
        public void Removing_LOTUS_releases_every_device_and_erases_the_store()
        {
            bus.Plug(Devices.Stick());
            bus.Remember(Devices.Phone());
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            Assert.True(Eventually(() => bus.Scan().Single().IsOff));
            Assert.True(Eventually(() => bus.HasDisabledMark(Devices.PhoneId)));

            Assert.True(client.Stop(Password, GuardProtocol.PurposeUninstall).Ok);

            Assert.False(bus.Scan().Single().IsOff);
            Assert.False(bus.HasDisabledMark(Devices.PhoneId));
            Assert.False(File.Exists(LotusPaths.StoreFile));
            Assert.False(File.Exists(LotusPaths.LedgerFile));
            Assert.True(Eventually(() => { lock (stops) return stops.Contains(GuardProtocol.PurposeUninstall); }));
        }

        [Fact]
        public void Nonsense_on_the_pipe_is_refused_politely()
        {
            GuardClient client = Start();
            GuardReply unknown = client.Send(new Dictionary<string, object> { ["op"] = "format_c" });
            Assert.Equal(GuardProtocol.ErrBadRequest, unknown.Code);

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

        [Fact]
        public void The_status_file_tells_the_notifier_whether_LOTUS_is_armed()
        {
            GuardClient client = Start();
            Assert.True(client.Apply(Json(StorageBlocked()), "", Password).Ok);
            Assert.True(Eventually(() => File.Exists(LotusPaths.StatusFile) &&
                                         File.ReadAllText(LotusPaths.StatusFile).Contains("\"armed\": true")));
            Assert.DoesNotContain("salt", File.ReadAllText(LotusPaths.StatusFile));
        }
    }
}
