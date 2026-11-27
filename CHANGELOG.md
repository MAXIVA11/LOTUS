# Changelog

What changed in each version of LOTUS, newest first. The [releases page](https://github.com/MAXIVA11/LOTUS/releases) has the downloads, their checksums and how each release was tested.

Version numbers follow [semantic versioning](https://semver.org): the last number is for fixes, the middle one for additions that keep everything working, the first for a change that needs something from you when you update.

## 1.6.0, not released yet

### Added

- Trust that ends by itself. A trust entry can carry an end, given as a length of time (`12h`, `7d`, `2w`) or a date. When the time is up the service takes the entry off the list, blocks the device if the policy says so, and writes it to the log as "trust ended". On Windows the field is on the Trusted page. On Linux it is `--for` and `--until` on `lotus trust`, and the key `e` in the interface.
- The Windows event log and the Linux journal get a copy of every line of the activity log. Each kind of event has a number that stays the same from version to version, so log collectors can filter on it. [docs/logging.md](docs/logging.md) has the table.
- `lotus status --json` on Linux prints the status as one JSON object for monitoring tools. `LotusGuard --scan` on Windows now includes the version and the time of the last tampering event.
- On Linux, a kernel started with `usbcore.authorized_default=2` holds back every device that is not built in until the daemon has looked at it. That closes the seconds before the daemon runs. LOTUS now takes such a system over correctly. [docs/linux.md](docs/linux.md) says how to set it and what to watch out for.
- `SECURITY.md`, with a private way to report a weakness.
- A fuzzer for the parsers in `tests/Lotus.Fuzz`. The build runs it on every change.
- The build attests the files it produces, so a download can be checked against the commit it was built from.

### Changed

- The end of a trust entry is understood from 1.6.0 on. An older version reading the same policy would take the entry for trust without an end, so do not install an older version over this one while such entries exist.

### Fixed

- A number too large to hold, such as `1e999`, was accepted in JSON on Linux and refused on Windows. It is refused on both. The fuzzer found this.
- A device name could come out of cleaning one character longer than the limit of 96. The fuzzer found this too.
- `lotus --socket` with a path too long for a socket ended with a crash report. It now says what is wrong.
- Linux: a device that was being unplugged could be taken for an unknown device for a moment, because the kernel removes its descriptors before its folder. Under "trusted only" that put a "could not block" warning in the log. The test in the virtual machine found this.

## 1.5.0, 2026-11-27

### Added

- An interactive interface for Linux. `sudo lotus` on its own at a terminal opens the five pages the Windows app has, driven with the keyboard.

### Changed

- At a terminal the plain commands use the colours of the interface.

## 1.4.0, 2026-11-27

### Added

- Trust by hand: a vendor ID, a vendor and product ID, or an instance path, for a device that need not be plugged in.
- A whole vendor as a kind of trust entry.
- Setup asks whether to trust the USB devices that are connected.

### Changed

- "Refuse new devices before they start" is off after installation. A new installation starts disarmed, with everything allowed and keyboards and mice kept working.

### Fixed

- The banner in the README was cut off in viewers that freeze a picture early.

## 1.3.0, 2026-11-27

### Added

- LOTUS for Linux: a daemon and the `lotus` command, with the rules, the decision engine and the encrypted store of the Windows version.

### Fixed

- Windows: drives are writable again without replugging after read-only mode is lifted by removal or by releasing everything.
- Windows: the setup window stood still when it was opened while the service was busy.

## 1.2.0, 2026-11-27

### Added

- Strict mode: a device the policy blocks, and that the PC has never seen, does not start at all.
- The policy, the trusted list and the list of blocked devices are encrypted and authenticated. Only the service can read them.

### Removed

- The LOTUS password. Changing the policy, updating and removing need administrator rights and nothing else.

### Changed

- `LotusGuard --release-all` disarms LOTUS as well, and works without the service.

## 1.1.0, 2026-11-27

### Added

- The policy moved into a store only the service can write. A changed or deleted store is put back and logged as tampering.
- The service cannot be stopped, reconfigured or deleted with the Windows tools.
- A trusted device is remembered with what it does. A device that comes back with a new function is no longer trusted.

### Fixed

- Four weaknesses a security review found in 1.0.0. [docs/security.md](docs/security.md) lists them.
- A blocked drive could stay usable for seconds while a program had it open.

## 1.0.0, 2026-11-26

The first release: removable storage set to allow, read-only or block, other USB devices set to allow or trusted only, a trusted list, an activity log and a notifier, for Windows 10 and 11.
