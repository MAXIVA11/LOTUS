using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
using Lotus.Linux.Tui;
using Lotus.Tests;
using Xunit;

namespace Lotus.Linux.Tests
{
    /// <summary>A daemon that lives in memory: a policy, a few devices, and a record of what was applied.</summary>
    internal sealed class PretendDaemon : ILotusSession
    {
        public Policy Policy = new Policy();
        public List<UsbDevice> Devices = new List<UsbDevice>();
        public List<LotusEvent> Events = new List<LotusEvent>();
        public List<Policy> Applied = new List<Policy>();
        public bool Lockdown;
        public bool Down;
        public string Refuse;
        public string StrictState = "";

        public Program.State Read()
        {
            if (Down) throw new GuardUnavailableException("The LOTUS daemon is not answering. Is the service running?");
            var state = new Program.State
            {
                Policy = Lockdown ? GuardWorker.LockdownPolicy() : Policy.Clone(),
                Ledger = new Ledger(),
                Lockdown = Lockdown,
                Strict = StrictState
            };
            foreach (UsbDevice device in Devices)
            {
                UsbDevice copy = device.Clone();
                Decision decision = PolicyEngine.Evaluate(state.Policy, copy, TimeSpan.MaxValue);
                if (decision.IsBlock)
                {
                    // What the daemon does: a blocked device is off and on the ledger.
                    state.Ledger.Add(LedgerEntry.From(copy, decision.Reason, DateTime.UtcNow));
                    copy.Started = false;
                    copy.Problem = UsbDevice.ProblemDisabled;
                }
                state.Devices.Add(DeviceView.Resolve(state.Policy, copy, state.Ledger, TimeSpan.MaxValue));
            }
            return state;
        }

        public GuardReply Apply(Policy next)
        {
            if (Down) throw new GuardUnavailableException("The LOTUS daemon is not answering. Is the service running?");
            if (Refuse != null) return GuardReply.Fail(GuardProtocol.ErrFailed, Refuse);
            if (Lockdown) return GuardReply.Fail(GuardProtocol.ErrLockdown, "LOTUS is locked down.");
            Applied.Add(next.Clone());
            Policy = next.Clone();
            return new GuardReply { Ok = true };
        }

        public List<LotusEvent> Log(int count)
        {
            return Events.Take(count).ToList();
        }
    }

    public class TuiTests
    {
        private readonly PretendDaemon daemon = new PretendDaemon();
        private TuiApp app;

        public TuiTests()
        {
            UsbDevice stick = Devices.Stick();
            stick.Location = "1-2";
            UsbDevice keyboard = Devices.Keyboard();
            keyboard.Location = "1-1";
            UsbDevice headset = Devices.Headset();
            headset.Location = "1-3";
            daemon.Devices.AddRange(new[] { stick, keyboard, headset });
            daemon.Events.Add(new LotusEvent { Type = EventTypes.GuardStarted, Message = "LOTUS Guard started." });
        }

        private TuiApp Start(bool unicode = true)
        {
            app = new TuiApp(daemon, "9.9.9", unicode);
            app.Refresh();
            return app;
        }

        private string Screen(int width = 100, int height = 30)
        {
            var canvas = new Canvas(width, height);
            app.Render(canvas);
            return canvas.ToPlain();
        }

        /// <summary>Presses keys given as words, the way "lotus ui --frame --keys" takes them.</summary>
        private void Press(string keys)
        {
            foreach (ConsoleKeyInfo key in TuiRunner.ParseKeys(keys))
            {
                app.Key(key);
                if (app.Deferred != null) app.RunDeferred();
            }
        }

        // ---------- the first screen ----------

        [Fact]
        public void The_first_screen_is_the_control_page_of_a_disarmed_LOTUS()
        {
            Start();
            string screen = Screen();
            Assert.Contains("LOTUS", screen);
            Assert.Contains("v9.9.9", screen);
            Assert.Contains("01 CONTROL", screen);
            Assert.Contains("05 ABOUT", screen);
            Assert.Contains("█▀▄ █ █▀ ▄▀█ █▀█ █▀▄▀█ █▀▀ █▀▄", screen); // DISARMED in big letters
            Assert.Contains("Nothing is blocked.", screen);
            Assert.Contains("REMOVABLE STORAGE", screen);
            Assert.Contains("PNP DEVICES", screen);
            Assert.Contains("KEYBOARDS AND MICE", screen);
            Assert.Contains("ALWAYS", screen);
            Assert.Contains("APPROVE", screen);
            Assert.Contains("[ ] Refuse new devices before they start", screen);
            Assert.Contains("CONNECTED 3", screen);
            Assert.Contains("BLOCKED 0", screen);
            Assert.Contains("TRUSTED 0", screen);
            Assert.Contains("LOTUS Guard started.", screen);
            Assert.Equal(TuiPage.Control, app.Page);
        }

        [Fact]
        public void It_fits_a_terminal_of_80_by_24_and_says_so_when_the_terminal_is_smaller()
        {
            Start();
            string small = Screen(80, 24);
            Assert.Contains("APPLY", small);
            Assert.Contains("CONNECTED 3", small);
            Assert.All(small.Split('\n'), line => Assert.True(line.Length <= 80));

            string tiny = Screen(60, 20);
            Assert.Contains("Make this window larger.", tiny);
            Assert.DoesNotContain("APPLY", tiny);
        }

        [Fact]
        public void Without_fancy_characters_everything_is_plain_ASCII()
        {
            Start(false);
            foreach (string keys in new[] { "", "space down right a", "2", "3 n", "4", "5" })
            {
                Start(false);
                Press(keys);
                string screen = Screen();
                Assert.All(screen, c => Assert.True(c < 127, "not ASCII: " + c + " after \"" + keys + "\""));
            }
            Start(false);
            Assert.Contains("D I S A R M E D", Screen());
        }

        [Fact]
        public void Every_page_and_every_dialog_draws_at_every_size_without_falling_over()
        {
            foreach (string keys in new[] { "", "space down right right down right", "space down right right a", "2 down",
                         "3", "3 n", "3 n text:zz enter", "4 down", "5 down down" })
            {
                for (int width = 20; width <= 200; width += 13)
                {
                    for (int height = 5; height <= 60; height += 7)
                    {
                        Start();
                        Press(keys);
                        var canvas = new Canvas(width, height);
                        app.Render(canvas);
                        Assert.Equal(height, canvas.ToPlain().Split('\n').Length - 1);
                        Assert.NotEmpty(canvas.ToAnsi());
                    }
                }
            }
        }

        // ---------- getting around ----------

        [Fact]
        public void The_number_keys_and_tab_change_the_page()
        {
            Start();
            Press("2");
            Assert.Equal(TuiPage.Devices, app.Page);
            Assert.Contains("What is plugged in", Screen());
            Press("3");
            Assert.Contains("Nothing is trusted yet.", Screen());
            Press("4");
            Assert.Contains("What LOTUS did, newest first.", Screen());
            Press("5");
            Assert.Contains("without warranty", Screen());
            Press("tab");
            Assert.Equal(TuiPage.Control, app.Page);
            Press("tab tab");
            Assert.Equal(TuiPage.Trusted, app.Page);
        }

        [Fact]
        public void Q_and_control_C_end_it()
        {
            Start();
            Press("q");
            Assert.True(app.Quit);

            Start();
            app.Key(new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));
            Assert.True(app.Quit);
        }

        // ---------- the control page ----------

        [Fact]
        public void Settings_are_changed_with_the_arrows_and_nothing_is_sent_until_apply()
        {
            Start();
            Press("space down right right down right");
            string screen = Screen();
            Assert.Contains("You have changes that are not applied yet.", screen);
            Assert.Contains("ON", screen);
            Assert.Contains("in. Devices on the trusted list keep", screen);
            Assert.Contains("Only devices on the trusted list work.", screen);
            Assert.Empty(daemon.Applied);
            // The status still shows what is in force.
            Assert.Contains("Nothing is blocked.", screen);
        }

        [Fact]
        public void Escape_drops_changes_that_were_not_applied_and_only_then_ends_it()
        {
            Start();
            Press("space down right right esc");
            Assert.False(app.Quit);
            Assert.Contains("Your changes were dropped.", Screen());
            Assert.Empty(daemon.Applied);
            Press("a");
            Assert.Contains("Nothing has changed", Screen());
            Press("esc");
            Assert.True(app.Quit);
        }

        [Fact]
        public void The_two_boxes_can_only_be_reached_when_they_mean_something()
        {
            Start();
            // With everything allowed the focus skips the box for strict mode and goes to Apply.
            Press("down down down down enter");
            Assert.Contains("Nothing has changed", Screen());

            Start();
            // Storage: block. Devices: trusted only. Keyboards and mice: one step left of "always" is "trusted".
            Press("down right right down right down left down space");
            Press("a");
            string screen = Screen();
            Assert.Contains("APPLY POLICY", screen);
            Press("esc");
            screen = Screen();
            Assert.Contains("[■] Refuse new devices before they start", screen);
            Assert.Contains("Only devices on the trusted list work.", screen);
            // LOTUS is not switched on here, so nothing goes off and the answer that has the focus is Apply.
            Press("a enter");
            Policy sent = Assert.Single(daemon.Applied);
            Assert.Equal(InputRule.Trusted, sent.Input);
            Assert.True(sent.Strict);
        }

        [Fact]
        public void New_keyboards_and_mice_can_be_made_to_wait_and_the_ones_connected_are_offered_for_trust()
        {
            Start();
            // Master switch on. Three steps down to keyboards and mice, one to the right: approve.
            Press("space down down down");
            Assert.Contains("Any keyboard or mouse works, also one that", Screen());
            Press("right");
            Assert.Contains("A new keyboard or mouse waits until you", Screen());
            // Strict mode can be chosen now, because there is something to hold back.
            Press("down space");
            Assert.Contains("[■] Refuse new devices before they start", Screen());

            Press("a");
            string screen = Screen();
            Assert.Contains("New keyboards and mice wait for approval.", screen);
            Assert.Contains("Connected USB devices that are not trusted yet:", screen);
            Assert.Contains("USB Keyboard", screen);
            Assert.Contains("HyperX Cloud III Wireless", screen);
            Assert.Contains("Trust these 2 devices, so they keep working", screen);
            Assert.DoesNotContain("SanDisk", screen); // storage is allowed and a stick is not an input device

            Press("left enter");
            Policy sent = Assert.Single(daemon.Applied);
            Assert.Equal(InputRule.Approve, sent.Input);
            Assert.Equal(PnpMode.Allow, sent.Pnp);
            Assert.True(sent.Strict);
            Assert.Equal(2, sent.Trusted.Count);
            Assert.Contains("New keyboards and mice wait for approval.", Screen());
        }

        [Fact]
        public void A_keyboard_that_waits_is_marked_pending_and_trusting_it_lets_it_in()
        {
            daemon.Policy = new Policy { Armed = true, Input = InputRule.Approve };
            Start();
            Press("2");
            string screen = Screen();
            Assert.Contains("PENDING", screen);
            Assert.Contains("ALLOWED", screen); // the stick, which is chosen first

            // The keyboard is the second device, and the line at the bottom explains the one that is chosen.
            Press("down");
            Assert.Contains("USB Keyboard", Screen());
            Assert.Contains("Waiting for approval", Screen());
            Press("t");
            Assert.Contains(daemon.Applied.Last().Trusted, e => e.Id == Devices.KeyboardId && !e.LetIn);
        }

        [Fact]
        public void A_device_that_was_only_let_in_says_so_and_t_approves_it()
        {
            daemon.Policy = new Policy { Armed = true, Input = InputRule.Approve };
            TrustEntry letIn = TrustEntry.ForDevice(Devices.Keyboard());
            letIn.LetIn = true;
            daemon.Policy.AddTrust(letIn);
            Start();
            Press("2 down");
            string screen = Screen();
            Assert.Contains("LET IN", screen);
            Press("3");
            Assert.Contains("NOT APPROVED", Screen());

            Press("2 t");
            TrustEntry approved = daemon.Applied.Last().Trusted.Single();
            Assert.Equal(Devices.KeyboardId, approved.Id);
            Assert.False(approved.LetIn);
            Press("3");
            Assert.DoesNotContain("NOT APPROVED", Screen());
        }

        [Fact]
        public void Apply_lists_what_would_be_switched_off_and_the_safe_answer_has_the_focus()
        {
            Start();
            Press("space down right right down right a");
            string screen = Screen();
            Assert.Contains("APPLY POLICY", screen);
            Assert.Contains("LOTUS will be armed.  Removable storage: blocked.  Other USB devices:", screen);
            Assert.Contains("trusted only.", screen);
            Assert.Contains("Removable storage that is switched off right away:", screen);
            Assert.Contains("SanDisk Cruzer Blade USB Device", screen);
            Assert.Contains("Connected USB devices that are not trusted yet:", screen);
            Assert.Contains("HyperX Cloud III Wireless", screen);
            Assert.Contains("[■] Trust this device, so it keeps working", screen);
            Assert.DoesNotContain("▪ USB Keyboard", screen); // keyboards keep working

            // Enter on the answer that has the focus changes nothing.
            Press("enter");
            Assert.Empty(daemon.Applied);
            Assert.DoesNotContain("APPLY POLICY", Screen());
        }

        [Fact]
        public void Confirming_sends_the_policy_and_trusts_the_devices_that_were_offered()
        {
            Start();
            Press("space down right right down right a left enter");
            Policy sent = Assert.Single(daemon.Applied);
            Assert.True(sent.Armed);
            Assert.Equal(StorageMode.Block, sent.Storage);
            Assert.Equal(PnpMode.Block, sent.Pnp);
            Assert.True(sent.AllowInput);
            Assert.False(sent.Strict);
            TrustEntry headset = Assert.Single(sent.Trusted);
            Assert.Equal(Devices.HeadsetId, headset.Id);

            string screen = Screen();
            Assert.Contains("Applied. LOTUS is armed.", screen);
            Assert.Contains("▄▀█ █▀█ █▀▄▀█ █▀▀ █▀▄", screen); // ARMED
            Assert.Contains("Storage blocked. Only trusted devices work, plus keyboards and mice.", screen);
            Assert.Contains("BLOCKED 1", screen);
            Assert.Contains("TRUSTED 1", screen);
            Assert.DoesNotContain("not applied yet", screen);
        }

        [Fact]
        public void With_the_box_unticked_nothing_is_trusted()
        {
            Start();
            Press("space down right right down right a left left space right enter");
            Policy sent = Assert.Single(daemon.Applied);
            Assert.True(sent.Armed);
            Assert.Empty(sent.Trusted);
        }

        [Fact]
        public void Y_confirms_and_N_cancels()
        {
            Start();
            Press("space down right right a n");
            Assert.Empty(daemon.Applied);
            Press("a y");
            Assert.Single(daemon.Applied);
        }

        [Fact]
        public void Disarming_says_what_it_means()
        {
            daemon.Policy = new Policy { Armed = true, Storage = StorageMode.Block };
            Start();
            Press("space a");
            string screen = Screen();
            Assert.Contains("LOTUS will be disarmed.", screen);
            Assert.Contains("Switched back on:", screen);
            Assert.Contains("SanDisk Cruzer Blade USB Device", screen);
            Press("enter");
            Assert.False(Assert.Single(daemon.Applied).Armed);
            Assert.Contains("Applied. LOTUS is disarmed.", Screen());
        }

        [Fact]
        public void Switching_keyboards_off_is_warned_about()
        {
            Start();
            // Devices: trusted only. Keyboards and mice: one step left of "always" is "trusted".
            Press("space down down right down left a");
            Assert.Contains("Keyboards and mice are not exempt.", Screen().Replace("\n", " "));
            Assert.Contains("USB Keyboard", Screen());
        }

        [Fact]
        public void A_hub_takes_what_is_plugged_into_it_down_with_it_and_the_dialog_says_so()
        {
            daemon.Devices.Clear();
            daemon.Devices.Add(new UsbDevice
            {
                InstanceId = @"USB\VID_05E3&PID_0608\P&1-3", Name = "USB2.0 Hub", Vid = "05E3", Pid = "0608",
                Functions = new List<string> { "09" }, Kinds = DeviceKinds.Hub, IsHub = true, Started = true, Location = "1-3"
            });
            UsbDevice mouse = Devices.Mouse();
            mouse.Location = "1-3.2";
            daemon.Devices.Add(mouse);
            Start();
            Press("space down down right a");
            string screen = Screen();
            Assert.Contains("USB2.0 Hub", screen);
            Assert.Contains("Plugged into a hub that is switched off, so they go with it:", screen);
            Assert.Contains("Wireless Receiver", screen);
        }

        [Fact]
        public void A_refusal_from_the_daemon_is_shown_and_nothing_is_taken_as_applied()
        {
            daemon.Refuse = "The policy could not be saved. See the activity log.";
            Start();
            Press("space a enter");
            string screen = Screen();
            Assert.Contains("THE POLICY WAS NOT SAVED", screen);
            Assert.Contains("The policy could not be saved.", screen);
            Press("enter");
            Assert.Contains("You have changes that are not applied yet.", Screen());
        }

        // ---------- devices ----------

        [Fact]
        public void The_devices_page_lists_each_device_with_what_the_policy_says()
        {
            daemon.Policy = new Policy { Armed = true, Storage = StorageMode.Block };
            Start();
            Press("2");
            string screen = Screen();
            Assert.Contains("BLOCKED   SanDisk Cruzer Blade USB Device", screen);
            Assert.Contains("ALLOWED   USB Keyboard", screen);
            Assert.Contains(Devices.StickId, screen);
            Assert.Contains("Storage", screen);
            // The line at the bottom explains the device that is chosen.
            Assert.Contains("Blocked because removable storage is blocked and this device is not trusted.", screen);
            Press("down");
            Assert.DoesNotContain("Blocked because", Screen());
        }

        [Fact]
        public void T_trusts_the_chosen_device_and_M_its_model()
        {
            daemon.Policy = new Policy { Armed = true, Storage = StorageMode.Block };
            Start();
            Press("2 t");
            TrustEntry entry = Assert.Single(Assert.Single(daemon.Applied).Trusted);
            Assert.Equal(TrustKind.Device, entry.Kind);
            Assert.Equal(Devices.StickId, entry.Id);
            Assert.Equal(new[] { "08" }, entry.Functions);
            string screen = Screen();
            Assert.Contains("SanDisk Cruzer Blade USB Device is trusted.", screen);
            Assert.Contains("TRUSTED   SanDisk Cruzer Blade USB Device", screen);

            Press("t");
            Assert.Contains("That is on the trusted list already.", Screen());
            Assert.Single(daemon.Applied);

            Press("down m");
            Assert.Equal(2, daemon.Applied.Count);
            Assert.Contains(daemon.Applied[1].Trusted, e => e.Kind == TrustKind.Model && e.Id == "VID_1A2C&PID_2D23");
        }

        [Fact]
        public void U_takes_the_trust_away_and_asks_first_when_that_switches_the_device_off()
        {
            daemon.Policy = new Policy { Armed = true, Storage = StorageMode.Block };
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Start();
            Press("2 u");
            string screen = Screen();
            Assert.Contains("REMOVE TRUST", screen);
            Assert.Contains("Switched off right away:", screen);
            Assert.Contains("SanDisk Cruzer Blade USB Device", screen);
            Press("enter"); // the focus is on Cancel
            Assert.Empty(daemon.Applied);
            Press("u left enter");
            Assert.Empty(Assert.Single(daemon.Applied).Trusted);

            // Nothing to ask when LOTUS is disarmed.
            daemon.Policy = new Policy();
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            daemon.Applied.Clear();
            Start();
            Press("2 u");
            Assert.DoesNotContain("REMOVE TRUST", Screen());
            Assert.Empty(Assert.Single(daemon.Applied).Trusted);

            Press("u");
            Assert.Contains("is not on the trusted list.", Screen());
        }

        // ---------- trusted ----------

        [Fact]
        public void A_vendor_a_model_and_a_device_can_be_typed_in()
        {
            Start();
            Press("3 n left text:046d tab text:Office%20gear enter");
            TrustEntry vendor = Assert.Single(Assert.Single(daemon.Applied).Trusted);
            Assert.Equal(TrustKind.Vendor, vendor.Kind);
            Assert.Equal("VID_046D", vendor.Id);
            Assert.Equal("Office gear", vendor.Name);
            string screen = Screen();
            Assert.Contains("Every device from vendor 046D is trusted.", screen);
            Assert.Contains("Office gear", screen);
            Assert.Contains("WHOLE VENDOR", screen);
            Assert.Contains("NOT CONNECTED", screen);

            Press("n text:0781:5567 enter");
            Assert.Contains(daemon.Policy.Trusted, e => e.Kind == TrustKind.Model && e.Id == "VID_0781&PID_5567");
            Assert.Contains("WHOLE MODEL", Screen());

            Press(@"n right text:usb\vid_1234&pid_5678\abc enter");
            Assert.Contains(daemon.Policy.Trusted, e => e.Kind == TrustKind.Device && e.Id == @"USB\VID_1234&PID_5678\ABC");
            Assert.Equal(3, daemon.Policy.Trusted.Count);
        }

        [Fact]
        public void A_device_that_is_plugged_in_gets_its_functions_recorded_when_its_path_is_typed()
        {
            Start();
            Press("3 n right text:" + Devices.StickId.ToLowerInvariant() + " enter");
            TrustEntry entry = Assert.Single(daemon.Policy.Trusted);
            Assert.Equal(new[] { "08" }, entry.Functions);
            Assert.Contains("CONNECTED", Screen());
        }

        [Fact]
        public void What_is_not_an_ID_keeps_the_form_open_and_says_why()
        {
            Start();
            Press("3 n text:printer enter");
            string screen = Screen();
            Assert.Contains("ADD BY HAND", screen);
            Assert.Contains("A model is a vendor ID and a product ID", screen.Replace("\n", " "));
            Assert.Empty(daemon.Applied);

            // Fix it without starting over.
            Press("bksp bksp bksp bksp bksp bksp bksp text:046d:c52b enter");
            Assert.DoesNotContain("ADD BY HAND", Screen());
            Assert.Single(daemon.Applied);

            Press("n text:046D%20C52B enter");
            Assert.Contains("That is on the trusted list already.", Screen());
            Press("esc");
            Assert.Single(daemon.Applied);
        }

        [Fact]
        public void X_removes_the_chosen_entry()
        {
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            daemon.Policy.AddTrust(TrustEntry.ForModel(Devices.Keyboard()));
            Start();
            Press("3 down x");
            TrustEntry left = Assert.Single(Assert.Single(daemon.Applied).Trusted);
            Assert.Equal(Devices.StickId, left.Id);
            Assert.Contains("Removed from the trusted list.", Screen());
        }

        // ---------- activity and about ----------

        [Fact]
        public void E_gives_the_chosen_entry_an_end_and_the_list_shows_it()
        {
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Start();
            Press("3 e");
            string form = Screen();
            Assert.Contains("TRUST UNTIL", form);
            Assert.Contains("Now: no end", form);
            Assert.Contains("12h, 7d or 2w", form);

            DateTime before = DateTime.UtcNow;
            Press("text:7d enter");

            TrustEntry entry = daemon.Applied.Single().Trusted.Single();
            Assert.NotNull(entry.UntilUtc);
            Assert.InRange(entry.UntilUtc.Value, before.AddDays(7).AddSeconds(-2), DateTime.UtcNow.AddDays(7).AddSeconds(2));
            string list = Screen();
            Assert.DoesNotContain("TRUST UNTIL", list);
            Assert.Contains("UNTIL " + entry.UntilText(), list);
            Assert.Contains("ends on " + entry.UntilText(), list);
        }

        [Fact]
        public void An_empty_field_takes_the_end_away_again()
        {
            TrustEntry limited = TrustEntry.ForDevice(Devices.Stick());
            limited.UntilUtc = DateTime.UtcNow.AddDays(3);
            daemon.Policy.AddTrust(limited);
            Start();
            Press("3");
            Assert.Contains("UNTIL " + limited.UntilText(), Screen());

            Press("e");
            Assert.Contains("Now: until " + limited.UntilText(), Screen());
            Press("enter");

            Assert.Null(daemon.Applied.Single().Trusted.Single().UntilUtc);
            Assert.DoesNotContain("UNTIL ", Screen());
            Assert.Contains("has no end", Screen());
        }

        [Fact]
        public void A_time_that_cannot_be_read_keeps_the_form_open_and_changes_nothing()
        {
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Start();
            Press("3 e text:tomorrow enter");
            Assert.Contains("TRUST UNTIL", Screen());
            Assert.Contains("Give a length of time", Screen());
            Assert.Empty(daemon.Applied);

            Press("esc");
            Assert.DoesNotContain("TRUST UNTIL", Screen());
            Assert.Empty(daemon.Applied);
        }

        [Fact]
        public void The_end_of_one_entry_leaves_the_others_as_they_are()
        {
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Headset()));
            Start();
            Press("3 down e text:12h enter");

            Policy applied = daemon.Applied.Single();
            Assert.Null(applied.Trusted[0].UntilUtc);
            Assert.NotNull(applied.Trusted[1].UntilUtc);
        }

        [Fact]
        public void The_trusted_page_names_the_key_for_the_end_date()
        {
            daemon.Policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Start();
            Press("3");
            Assert.Contains("e end date", Screen());
            // With nothing on the list there is nothing to limit.
            daemon.Policy.Trusted.Clear();
            app.Refresh();
            Press("e");
            Assert.DoesNotContain("TRUST UNTIL", Screen());
        }

        [Fact]
        public void The_activity_page_shows_the_log_newest_first_and_scrolls()
        {
            daemon.Events.Clear();
            for (int i = 0; i < 60; i++)
            {
                daemon.Events.Add(new LotusEvent { Type = EventTypes.Blocked, Message = "event number " + i + "." });
            }
            Start();
            Press("4");
            string screen = Screen();
            Assert.Contains("event number 0.", screen);
            Assert.DoesNotContain("event number 40.", screen);
            Assert.Contains("1 of 60", screen);
            Press("pgdn pgdn pgdn");
            Assert.Contains("event number 40.", Screen());
        }

        [Fact]
        public void A_name_from_a_device_cannot_carry_escape_sequences_onto_the_screen()
        {
            UsbDevice evil = Devices.Stick(@"USB\VID_6666&PID_0001\X");
            evil.Name = "Evil\u001b[2J\u001b[31mdrive\u0007\r\n";
            evil.Location = "2-9";
            daemon.Devices.Add(evil);
            daemon.Events.Add(new LotusEvent { Type = EventTypes.Blocked, Message = "Evil\u001b]0;owned\u0007 was blocked." });
            Start();
            foreach (string keys in new[] { "", "2", "4" })
            {
                Press(keys);
                var canvas = new Canvas(100, 30);
                app.Render(canvas);
                Assert.DoesNotContain('\u0007', canvas.ToAnsi());
                Assert.DoesNotContain("\u001b[2J", canvas.ToAnsi(), StringComparison.Ordinal);
                Assert.DoesNotContain("\u001b]", canvas.ToAnsi(), StringComparison.Ordinal);
                Assert.DoesNotContain('\r', canvas.ToPlain());
            }
        }

        // ---------- when things are not in order ----------

        [Fact]
        public void A_locked_down_LOTUS_says_so_and_takes_no_changes()
        {
            daemon.Lockdown = true;
            Start();
            string screen = Screen();
            Assert.Contains("█   █▀█ █▀▀ █▄▀ █▀▀ █▀▄", screen); // LOCKED
            Assert.Contains("Run the LOTUS installer again to reset it.", screen);
            Press("space a");
            Assert.Contains("LOCKED DOWN", Screen());
            Press("enter 2 t");
            Assert.Contains("takes no changes while it is locked down", Screen().Replace("\n", " ").Replace("│", "").Replace("  ", " "));
            Assert.Empty(daemon.Applied);
        }

        [Fact]
        public void When_the_daemon_does_not_answer_the_screen_says_so_and_comes_back_by_itself()
        {
            daemon.Down = true;
            Start();
            string screen = Screen();
            Assert.Contains("NO ANSWER FROM THE LOTUS DAEMON", screen);
            Assert.Contains("Is the service running?", screen);
            Press("2 t space");
            Assert.Empty(daemon.Applied);

            daemon.Down = false;
            app.Refresh();
            Assert.Contains("What is plugged in", Screen());
        }

        [Fact]
        public void The_daemon_going_away_in_the_middle_of_an_apply_is_reported()
        {
            Start();
            Press("space a");
            daemon.Down = true;
            Press("enter");
            Assert.Contains("THE POLICY WAS NOT SAVED", Screen());
        }

        [Fact]
        public void A_refresh_keeps_what_the_person_is_in_the_middle_of_changing()
        {
            Start();
            Press("space down right right");
            app.Refresh();
            app.Refresh();
            Assert.Contains("You have changes that are not applied yet.", Screen());
            Press("a left enter");
            Assert.Equal(StorageMode.Block, Assert.Single(daemon.Applied).Storage);
        }

        [Fact]
        public void Without_changes_of_its_own_the_screen_follows_what_someone_else_changed()
        {
            Start();
            daemon.Policy = new Policy { Armed = true, Storage = StorageMode.ReadOnly };
            app.Refresh();
            string screen = Screen();
            Assert.Contains("Storage read-only. Other USB devices allowed.", screen);
            Assert.DoesNotContain("not applied yet", screen);
        }

        // ---------- the pieces ----------

        [Fact]
        public void Keys_can_be_written_as_words()
        {
            List<ConsoleKeyInfo> keys = TuiRunner.ParseKeys("2 down text:a%20b enter esc");
            Assert.Equal(new[] { ConsoleKey.D2, ConsoleKey.DownArrow, ConsoleKey.A, ConsoleKey.Spacebar, ConsoleKey.B, ConsoleKey.Enter, ConsoleKey.Escape },
                keys.Select(k => k.Key).ToArray());
            Assert.Equal("a b", new string(keys.Skip(2).Take(3).Select(k => k.KeyChar).ToArray()));
            Assert.Empty(TuiRunner.ParseKeys(null));
        }

        [Fact]
        public void Text_is_wrapped_at_spaces_and_cut_with_a_mark()
        {
            Assert.Equal(new[] { "one two", "three" }, Canvas.Wrap("one two three", 8).ToArray());
            Assert.Equal(new[] { "abcdefgh", "ij" }, Canvas.Wrap("abcdefghij", 8).ToArray());
            Assert.Equal(new[] { "a", "", "b" }, Canvas.Wrap("a\n\nb", 10).ToArray());
            Assert.Empty(Canvas.Wrap("anything", 0));
            Assert.Equal("abc…", Canvas.Shorten("abcdefgh", 4));
            Assert.Equal("ab..", Canvas.Shorten("abcdefgh", 4, false));
            Assert.Equal("abc", Canvas.Shorten("abc", 4));
        }

        [Fact]
        public void The_canvas_cuts_what_does_not_fit_and_writes_colours_as_escape_sequences()
        {
            var canvas = new Canvas(10, 2);
            canvas.Text(7, 0, "abcdef", Palette.Cyan, -1, true);
            canvas.Text(-2, 1, "xyz", Palette.Pink);
            canvas.Text(0, 5, "nowhere", Palette.Pink);
            Assert.Equal("       abc\nz\n", canvas.ToPlain());
            Assert.Equal(Palette.Cyan, canvas.ForegroundAt(7, 0));
            Assert.Equal(Palette.Ink, canvas.BackgroundAt(7, 0));
            string ansi = canvas.ToAnsi();
            Assert.Contains("\u001b[1;1H", ansi);
            Assert.Contains("\u001b[2;1H", ansi);
            Assert.Contains("38;2;0;240;255", ansi);
            Assert.EndsWith("\u001b[0m", ansi);
        }
    }
}
