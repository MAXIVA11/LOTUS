using System;
using System.Collections.Generic;
using System.IO;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    public class JsonTests
    {
        [Fact]
        public void Parses_nested_values()
        {
            var obj = Json.AsObject(Json.Parse(
                "{ \"a\": 1, \"b\": [true, false, null], \"c\": { \"d\": \"x\" }, \"e\": -2.5e1 }"));
            Assert.Equal(1.0, obj["a"]);
            var list = (List<object>)obj["b"];
            Assert.Equal(new object[] { true, false, null }, list.ToArray());
            Assert.Equal("x", Json.AsObject(obj["c"])["d"]);
            Assert.Equal(-25.0, obj["e"]);
        }

        [Fact]
        public void Round_trips_awkward_strings()
        {
            string original = "back\\slash \"quote\" tab\t newline\n unicode é control \u0001";
            string json = Json.Write(new Dictionary<string, object> { ["s"] = original });
            Assert.Equal(original, Json.AsObject(Json.Parse(json))["s"]);
        }

        [Fact]
        public void Instance_ids_survive_a_round_trip()
        {
            string json = Json.Write(new Dictionary<string, object> { ["id"] = Devices.KeyboardId }, false);
            Assert.Equal("{\"id\":\"USB\\\\VID_1A2C&PID_2D23\\\\5&CC3F949&0&6\"}", json);
            Assert.Equal(Devices.KeyboardId, Json.AsObject(Json.Parse(json))["id"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1,]")]
        [InlineData("{\"a\":1} extra")]
        [InlineData("\"unterminated")]
        [InlineData("tru")]
        public void Rejects_broken_text(string text)
        {
            Assert.Throws<FormatException>(() => Json.Parse(text));
            Assert.False(Json.TryParse(text, out object _));
        }

        [Fact]
        public void Accepts_a_byte_order_mark()
        {
            Assert.NotNull(Json.AsObject(Json.Parse("﻿{}")));
        }

        [Fact]
        public void Rejects_absurd_nesting()
        {
            Assert.Throws<FormatException>(() => Json.Parse(new string('[', 500) + new string(']', 500)));
        }

        [Fact]
        public void Time_round_trips_as_utc()
        {
            var time = new DateTime(2026, 11, 26, 9, 30, 15, DateTimeKind.Utc);
            Assert.Equal("2026-11-26T09:30:15Z", Json.FormatTime(time));
            Assert.Equal(time, Json.ParseTime("2026-11-26T09:30:15Z", DateTime.MinValue));
            Assert.Equal(DateTime.MinValue, Json.ParseTime("not a time", DateTime.MinValue));
        }
    }

    public class PolicyTests
    {
        [Fact]
        public void A_new_policy_blocks_nothing()
        {
            var policy = new Policy();
            Assert.False(policy.Armed);
            Assert.Equal(StorageMode.Allow, policy.Storage);
            Assert.Equal(PnpMode.Allow, policy.Pnp);
            Assert.True(policy.AllowInput);
            Assert.Empty(policy.Trusted);
        }

        [Fact]
        public void Round_trips_through_json()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.ReadOnly, Pnp = PnpMode.Block, AllowInput = false };
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            policy.AddTrust(TrustEntry.ForModel(Devices.Keyboard()));

            Policy copy = Policy.FromJson(policy.ToJson());

            Assert.True(copy.SettingsEqual(policy));
            Assert.Equal(2, copy.Trusted.Count);
            Assert.Equal(TrustKind.Device, copy.Trusted[0].Kind);
            Assert.Equal(Devices.StickId, copy.Trusted[0].Id);
            Assert.Equal(TrustKind.Model, copy.Trusted[1].Kind);
            Assert.Equal("VID_1A2C&PID_2D23", copy.Trusted[1].Id);
            Assert.Equal("USB Keyboard", copy.Trusted[1].Name);
        }

        [Theory]
        [InlineData("{\"storage\":\"sometimes\"}")]
        [InlineData("{\"pnp\":\"maybe\"}")]
        [InlineData("{\"armed\":\"yes\"}")]
        [InlineData("{\"version\":99}")]
        [InlineData("{\"trusted\":[{\"kind\":\"planet\",\"id\":\"X\"}]}")]
        [InlineData("[]")]
        public void Refuses_settings_it_does_not_understand(string json)
        {
            Assert.Throws<FormatException>(() => Policy.FromJson(json));
        }

        [Fact]
        public void Ignores_unknown_fields_and_fills_in_defaults()
        {
            Policy policy = Policy.FromJson("{\"armed\":true,\"futureOption\":123}");
            Assert.True(policy.Armed);
            Assert.Equal(StorageMode.Allow, policy.Storage);
            Assert.True(policy.AllowInput);
        }

        [Fact]
        public void Trust_matching_ignores_case_and_avoids_duplicates()
        {
            var policy = new Policy();
            Assert.True(policy.AddTrust(new TrustEntry { Kind = TrustKind.Device, Id = Devices.StickId.ToLowerInvariant() }));
            Assert.False(policy.AddTrust(new TrustEntry { Kind = TrustKind.Device, Id = Devices.StickId }));
            Assert.Single(policy.Trusted);
            Assert.True(policy.IsTrusted(Devices.Stick()));
            Assert.False(policy.IsTrusted(Devices.Keyboard()));

            Assert.True(policy.RemoveTrust(TrustKind.Device, Devices.StickId.ToLowerInvariant()));
            Assert.False(policy.IsTrusted(Devices.Stick()));
        }

        [Fact]
        public void Model_trust_covers_every_unit_of_that_model()
        {
            var policy = new Policy();
            policy.AddTrust(TrustEntry.ForModel(Devices.Stick()));
            Assert.True(policy.IsTrusted(Devices.Stick(@"USB\VID_0781&PID_5567\OTHERSERIAL")));
            Assert.False(policy.IsTrusted(Devices.Phone()));
        }

        [Fact]
        public void Device_trust_covers_one_unit_only()
        {
            var policy = new Policy();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Assert.False(policy.IsTrusted(Devices.Stick(@"USB\VID_0781&PID_5567\OTHERSERIAL")));
        }

        [Fact]
        public void Clone_is_independent()
        {
            var policy = new Policy { Armed = true };
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Policy copy = policy.Clone();
            copy.Armed = false;
            copy.Trusted.Clear();
            Assert.True(policy.Armed);
            Assert.Single(policy.Trusted);
        }
    }

    public class PolicyStoreTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "lotus-tests-" + Guid.NewGuid().ToString("N"));
        private string PolicyPath => Path.Combine(dir, "policy.json");

        public PolicyStoreTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public void A_missing_file_gives_the_disarmed_default_without_an_error()
        {
            PolicyLoadResult result = PolicyStore.Load(PolicyPath);
            Assert.Equal(PolicySource.Default, result.Source);
            Assert.Null(result.Error);
            Assert.False(result.Policy.Armed);
        }

        [Fact]
        public void Save_then_load_returns_the_same_policy()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            PolicyStore.Save(PolicyPath, policy);
            PolicyLoadResult result = PolicyStore.Load(PolicyPath);
            Assert.Equal(PolicySource.File, result.Source);
            Assert.True(result.Policy.SettingsEqual(policy));
            Assert.False(File.Exists(PolicyPath + ".tmp"));
        }

        [Fact]
        public void A_damaged_file_falls_back_to_the_last_good_copy()
        {
            PolicyStore.Save(PolicyPath, new Policy { Armed = true, Storage = StorageMode.Block });
            PolicyStore.Save(PolicyPath, new Policy { Armed = true, Storage = StorageMode.ReadOnly });
            File.WriteAllText(PolicyPath, "{ this is not json");

            PolicyLoadResult result = PolicyStore.Load(PolicyPath);

            Assert.Equal(PolicySource.Backup, result.Source);
            Assert.NotNull(result.Error);
            Assert.True(result.Policy.Armed);
            Assert.Equal(StorageMode.Block, result.Policy.Storage);
        }

        [Fact]
        public void A_damaged_file_with_no_backup_reports_the_error()
        {
            File.WriteAllText(PolicyPath, "garbage");
            PolicyLoadResult result = PolicyStore.Load(PolicyPath);
            Assert.Equal(PolicySource.Default, result.Source);
            Assert.NotNull(result.Error);
            Assert.False(result.Policy.Armed);
        }
    }

    public class LedgerTests
    {
        [Fact]
        public void Round_trips_through_json()
        {
            var ledger = new Ledger { ReadOnlyOwned = true };
            LedgerEntry entry = LedgerEntry.From(Devices.Headset(), Reason.PnpBlocked, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            entry.Method = BlockMethod.Removed;
            ledger.Add(entry);

            Ledger copy = Ledger.FromJson(ledger.ToJson());

            Assert.True(copy.ReadOnlyOwned);
            LedgerEntry loaded = copy.Get(Devices.HeadsetId.ToLowerInvariant());
            Assert.NotNull(loaded);
            Assert.Equal("HyperX Cloud III Wireless", loaded.Name);
            Assert.Equal(DeviceKinds.Audio | DeviceKinds.Input, loaded.Kinds);
            Assert.Equal(new[] { "01", "03" }, loaded.Functions.ToArray());
            Assert.Equal(Reason.PnpBlocked, loaded.Reason);
            Assert.Equal(BlockMethod.Removed, loaded.Method);
            Assert.Equal(entry.SinceUtc, loaded.SinceUtc);
        }

        [Fact]
        public void An_entry_restores_what_a_switched_off_device_forgot()
        {
            LedgerEntry entry = LedgerEntry.From(Devices.Headset(), Reason.PnpBlocked, DateTime.UtcNow);
            var off = new UsbDevice
            {
                InstanceId = Devices.HeadsetId, Name = "USB Composite Device", IsComposite = true,
                FunctionsKnown = false, Kinds = DeviceKinds.None, Problem = UsbDevice.ProblemDisabled
            };

            entry.Restore(off);

            Assert.Equal("HyperX Cloud III Wireless", off.Name);
            Assert.True(off.FunctionsKnown);
            Assert.Equal(DeviceKinds.Audio | DeviceKinds.Input, off.Kinds);
        }

        [Fact]
        public void A_missing_ledger_file_is_an_empty_ledger()
        {
            Ledger ledger = Ledger.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"), out string error);
            Assert.Null(error);
            Assert.Equal(0, ledger.Count);
        }
    }

    public class ActivityLogTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "lotus-tests-" + Guid.NewGuid().ToString("N"));

        public ActivityLogTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public void Reads_back_newest_first()
        {
            var log = new ActivityLog(Path.Combine(dir, "events.jsonl"), Path.Combine(dir, "events.old.jsonl"));
            log.Write(EventTypes.Connected, "first");
            log.Write(EventTypes.Blocked, "second", Devices.Stick());
            log.Write(EventTypes.Released, "third");

            List<LotusEvent> events = log.ReadRecent(2);

            Assert.Equal(2, events.Count);
            Assert.Equal("third", events[0].Message);
            Assert.Equal("second", events[1].Message);
            Assert.Equal(Devices.StickId, events[1].DeviceId);
            Assert.Equal(EventTypes.Blocked, events[1].Type);
        }

        [Fact]
        public void Tail_only_returns_events_written_after_it_was_opened()
        {
            string path = Path.Combine(dir, "events.jsonl");
            var log = new ActivityLog(path, Path.Combine(dir, "events.old.jsonl"));
            log.Write(EventTypes.Connected, "old");
            var tail = new ActivityTail(path);
            Assert.Empty(tail.ReadNew());

            log.Write(EventTypes.Blocked, "new one");
            log.Write(EventTypes.Blocked, "new two");

            List<LotusEvent> events = tail.ReadNew();
            Assert.Equal(new[] { "new one", "new two" }, events.ConvertAll(e => e.Message).ToArray());
            Assert.Empty(tail.ReadNew());
        }

        [Fact]
        public void Tail_starts_over_when_the_log_is_replaced()
        {
            string path = Path.Combine(dir, "events.jsonl");
            var log = new ActivityLog(path, Path.Combine(dir, "events.old.jsonl"));
            log.Write(EventTypes.Connected, "a fairly long first message that makes the file big");
            var tail = new ActivityTail(path);
            File.Delete(path);
            log.Write(EventTypes.Blocked, "fresh");
            List<LotusEvent> events = tail.ReadNew();
            Assert.Single(events);
            Assert.Equal("fresh", events[0].Message);
        }

        [Fact]
        public void Skips_lines_that_are_not_json()
        {
            string path = Path.Combine(dir, "events.jsonl");
            File.WriteAllText(path, "garbage\n{\"t\":\"2026-01-01T00:00:00Z\",\"type\":\"blocked\",\"msg\":\"ok\"}\n\n");
            var log = new ActivityLog(path, Path.Combine(dir, "events.old.jsonl"));
            List<LotusEvent> events = log.ReadRecent(10);
            Assert.Single(events);
            Assert.Equal("ok", events[0].Message);
        }
    }
}
