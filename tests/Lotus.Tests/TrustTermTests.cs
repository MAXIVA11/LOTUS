using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    /// <summary>Trust that ends: how the end is typed, stored and counted.</summary>
    public class TrustTermTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

        private static DateTime? Term(string text)
        {
            Assert.True(TrustEntry.TryParseTerm(text, Now, out DateTime? until, out string error), error);
            Assert.Null(error);
            return until;
        }

        private static string Problem(string text)
        {
            Assert.False(TrustEntry.TryParseTerm(text, Now, out DateTime? until, out string error));
            Assert.Null(until);
            Assert.False(string.IsNullOrWhiteSpace(error));
            return error;
        }

        [Theory]
        [InlineData("90m", 90)]
        [InlineData("12h", 12 * 60)]
        [InlineData("7d", 7 * 24 * 60)]
        [InlineData("2w", 14 * 24 * 60)]
        [InlineData(" 7D ", 7 * 24 * 60)]
        [InlineData("1 h", 60)]
        public void A_length_of_time_counts_from_now(string text, int minutes)
        {
            Assert.Equal(Now.AddMinutes(minutes), Term(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("never")]
        [InlineData("NONE")]
        public void Nothing_or_never_means_no_end(string text)
        {
            Assert.Null(Term(text));
        }

        [Fact]
        public void A_date_means_the_end_of_that_day_by_the_computers_clock()
        {
            DateTime? until = Term("2026-12-04");
            Assert.NotNull(until);
            DateTime local = until.Value.ToLocalTime();
            Assert.Equal(new DateTime(2026, 12, 4, 23, 59, 59), local);
        }

        [Theory]
        [InlineData("2026-12-04 17:00")]
        [InlineData("2026-12-04T17:00")]
        [InlineData("2026-12-04 17:00 ")]
        public void A_date_with_a_time_means_that_minute(string text)
        {
            Assert.Equal(new DateTime(2026, 12, 4, 17, 0, 0), Term(text).Value.ToLocalTime());
        }

        [Theory]
        [InlineData("0d")]
        [InlineData("-3d")]
        [InlineData("7")]
        [InlineData("d")]
        [InlineData("7 days")]
        [InlineData("soon")]
        [InlineData("04.12.2026")]
        [InlineData("2026-13-40")]
        [InlineData("999999d")]
        [InlineData("99999w")]
        [InlineData("7d; rm -rf /")]
        public void Anything_else_is_refused_with_a_reason(string text)
        {
            Problem(text);
        }

        [Fact]
        public void A_time_that_has_passed_is_refused()
        {
            Assert.Contains("passed", Problem("2020-01-01"));
            Assert.Contains("passed", Problem("2026-11-27 00:00"));
        }

        [Fact]
        public void Trust_cannot_be_limited_to_more_than_ten_years()
        {
            Assert.Contains("ten years", Problem("2099-01-01"));
            Assert.Contains("ten years", Problem("9000d"));
        }

        [Fact]
        public void A_very_long_text_is_refused_before_it_is_looked_at()
        {
            Problem(new string('9', 500) + "d");
        }

        [Fact]
        public void An_entry_counts_until_its_end_and_not_a_second_longer()
        {
            var entry = TrustEntry.ForDevice(Devices.Stick());
            Assert.False(entry.IsOver(Now));
            entry.UntilUtc = Now.AddHours(1);
            Assert.False(entry.IsOver(Now));
            Assert.False(entry.IsOver(Now.AddHours(1).AddSeconds(-1)));
            Assert.True(entry.IsOver(Now.AddHours(1)));
            Assert.True(entry.IsOver(Now.AddDays(400)));
        }

        [Fact]
        public void An_entry_whose_time_is_up_does_not_make_a_device_trusted()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            TrustEntry entry = TrustEntry.ForDevice(Devices.Stick());
            entry.UntilUtc = DateTime.UtcNow.AddMinutes(-1);
            policy.AddTrust(entry);

            Assert.False(policy.IsTrusted(Devices.Stick()));
            Assert.True(PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.MaxValue).IsBlock);

            entry.UntilUtc = DateTime.UtcNow.AddMinutes(5);
            Assert.True(policy.IsTrusted(Devices.Stick()));
            Assert.False(PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.MaxValue).IsBlock);
        }

        [Fact]
        public void An_entry_that_is_over_does_not_hide_a_second_entry_that_still_counts()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            TrustEntry device = TrustEntry.ForDevice(Devices.Stick());
            device.UntilUtc = DateTime.UtcNow.AddMinutes(-1);
            policy.AddTrust(device);
            policy.AddTrust(TrustEntry.ForModel(Devices.Stick()));

            Assert.Equal(TrustKind.Model, policy.FindTrust(Devices.Stick()).Kind);
        }

        [Fact]
        public void The_end_is_saved_with_the_policy_and_read_back()
        {
            var policy = new Policy();
            TrustEntry limited = TrustEntry.ForDevice(Devices.Stick());
            limited.UntilUtc = new DateTime(2026, 12, 4, 15, 0, 0, DateTimeKind.Utc);
            policy.AddTrust(limited);
            policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));

            string json = policy.ToJson();
            Assert.Contains("\"until\"", json);
            Assert.Equal(1, json.Split(new[] { "\"until\"" }, StringSplitOptions.None).Length - 1);

            Policy read = Policy.FromJson(json);
            Assert.Equal(limited.UntilUtc, read.Trusted[0].UntilUtc);
            Assert.Equal(DateTimeKind.Utc, read.Trusted[0].UntilUtc.Value.Kind);
            Assert.Null(read.Trusted[1].UntilUtc);
            Assert.Equal(limited.UntilUtc, read.Clone().Trusted[0].UntilUtc);
        }

        [Fact]
        public void A_policy_from_an_earlier_version_has_trust_without_an_end()
        {
            string json = "{\"version\":1,\"armed\":true,\"storage\":\"block\",\"pnp\":\"allow\",\"trusted\":[" +
                          "{\"kind\":\"device\",\"id\":\"USB\\\\VID_0781&PID_5567\\\\AA\",\"name\":\"Stick\"}]}";
            Assert.Null(Policy.FromJson(json).Trusted.Single().UntilUtc);
        }

        [Theory]
        [InlineData("\"until\":\"next week\"")]
        [InlineData("\"until\":\"\"")]
        [InlineData("\"until\":42")]
        [InlineData("\"until\":true")]
        public void An_end_that_cannot_be_read_never_turns_into_trust_without_an_end(string until)
        {
            string json = "{\"version\":1,\"armed\":true,\"storage\":\"block\",\"pnp\":\"allow\",\"trusted\":[" +
                          "{\"kind\":\"device\",\"id\":\"USB\\\\VID_0781&PID_5567\\\\AA\",\"name\":\"Stick\"," + until + "}]}";
            Assert.Throws<FormatException>(() => Policy.FromJson(json));
        }

        [Fact]
        public void The_end_is_shown_in_local_time_and_can_be_typed_back_in()
        {
            var entry = TrustEntry.ForDevice(Devices.Stick());
            Assert.Equal("", entry.UntilText());
            entry.UntilUtc = Term("2026-12-04 17:00");
            Assert.Equal("2026-12-04 17:00", entry.UntilText());
            Assert.Equal(entry.UntilUtc, Term(entry.UntilText()));
        }
    }

    /// <summary>The numbers events carry in the system's log. They are a promise to whoever collects that log.</summary>
    public class EventIdTests
    {
        [Theory]
        [InlineData(EventTypes.GuardStarted, 1000, EventLevel.Information)]
        [InlineData(EventTypes.GuardStopped, 1001, EventLevel.Information)]
        [InlineData(EventTypes.PolicyChanged, 1100, EventLevel.Information)]
        [InlineData(EventTypes.TrustEnded, 1101, EventLevel.Information)]
        [InlineData(EventTypes.Connected, 2000, EventLevel.Information)]
        [InlineData(EventTypes.Removed, 2001, EventLevel.Information)]
        [InlineData(EventTypes.Blocked, 2100, EventLevel.Warning)]
        [InlineData(EventTypes.Released, 2101, EventLevel.Information)]
        [InlineData(EventTypes.ReadOnlyOn, 2200, EventLevel.Information)]
        [InlineData(EventTypes.ReadOnlyOff, 2201, EventLevel.Information)]
        [InlineData(EventTypes.Warning, 3000, EventLevel.Warning)]
        [InlineData(EventTypes.Error, 3001, EventLevel.Error)]
        [InlineData(EventTypes.Tamper, 4000, EventLevel.Error)]
        [InlineData(EventTypes.Security, 4100, EventLevel.Warning)]
        [InlineData("something new", 9000, EventLevel.Information)]
        public void Every_kind_of_event_keeps_its_number(string type, int id, EventLevel level)
        {
            Assert.Equal(id, EventIds.For(type));
            Assert.Equal(level, EventIds.LevelFor(type));
        }

        [Fact]
        public void No_two_kinds_of_event_share_a_number()
        {
            var types = typeof(EventTypes).GetFields().Select(f => (string)f.GetValue(null)).ToList();
            Assert.True(types.Count >= 14);
            Assert.Equal(types.Count, types.Select(EventIds.For).Distinct().Count());
            Assert.DoesNotContain(EventIds.Other, types.Select(EventIds.For));
        }

        [Fact]
        public void The_text_for_the_event_log_names_the_device_and_keeps_a_long_message_whole()
        {
            string message = "The policy store could not be trusted. " + new string('x', 300);
            string text = EventIds.Describe(new LotusEvent
            {
                Type = EventTypes.Blocked, Message = message, DeviceName = "SanDisk Cruzer", DeviceId = Devices.StickId
            });
            Assert.Contains(message, text, StringComparison.Ordinal);
            Assert.Contains("Device: SanDisk Cruzer", text, StringComparison.Ordinal);
            Assert.Contains("ID: " + Devices.StickId, text, StringComparison.Ordinal);
            Assert.Contains("Event: blocked", text, StringComparison.Ordinal);
        }

        [Fact]
        public void A_name_from_a_device_cannot_add_lines_of_its_own_to_the_event_log()
        {
            string text = EventIds.Describe(new LotusEvent
            {
                Type = EventTypes.Blocked, Message = "Blocked.", DeviceName = "Stick\r\nEvent: tamper\r\nID: forged"
            });
            Assert.Equal(3, text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length);
        }

        [Fact]
        public void The_activity_log_hands_every_line_to_the_systems_log_and_survives_one_that_fails()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lotus-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                var seen = new Recorder();
                var log = new ActivityLog(System.IO.Path.Combine(folder, "events.jsonl"), System.IO.Path.Combine(folder, "events.old.jsonl"))
                {
                    Mirror = seen
                };
                log.Write(EventTypes.Blocked, "First.");
                seen.Fail = true;
                log.Write(EventTypes.Tamper, "Second.");
                seen.Fail = false;
                log.Write(EventTypes.Released, "Third.");

                Assert.Equal(new[] { "First.", "Third." }, seen.Events.Select(e => e.Message));
                Assert.Equal(3, log.ReadRecent(10).Count);
                Assert.NotNull(log.LastOf(EventTypes.Tamper));
                Assert.Null(log.LastOf(EventTypes.Error));
            }
            finally
            {
                try { System.IO.Directory.Delete(folder, true); } catch (System.IO.IOException) { }
            }
        }

        internal sealed class Recorder : ISystemLog
        {
            public readonly List<LotusEvent> Events = new List<LotusEvent>();
            public bool Fail;

            public void Write(LotusEvent e)
            {
                if (Fail) throw new InvalidOperationException("the system log is full");
                lock (Events) Events.Add(e);
            }

            public List<LotusEvent> Snapshot()
            {
                lock (Events) return new List<LotusEvent>(Events);
            }
        }
    }
}
