# LOTUS on Linux

LOTUS for Linux is a daemon and a command, `lotus`. The rules are the ones the Windows version has, decided by the same code: removable storage can be allowed, read-only or blocked, every other USB device can be allowed or limited to a trusted list, and the policy sits in the same encrypted store.

There is no window. You set the policy with `sudo lotus ...` in a terminal.

## What it needs

- A 64-bit Intel or AMD computer.
- A Linux that uses systemd.
- No runtime to install. The program is one file that carries its own.

LOTUS for Linux is tested on Ubuntu 24.04 with kernel 6.8, in a virtual machine with USB devices emulated by QEMU. It has not been run against physical USB hardware. See [How it is tested](#how-it-is-tested).

## Install

Download `lotus-<version>-linux-x64.tar.gz` from the [releases page](https://github.com/MAXIVA11/LOTUS/releases). The first Linux build is 1.3.0-rc.1, which is listed there as a pre-release. Then:

```sh
tar -xzf lotus-*-linux-x64.tar.gz
cd lotus-*-linux-x64
sudo ./install.sh
```

The installer copies the program to `/usr/local/bin/lotus`, adds the service `lotus.service` and starts it. Nothing is blocked afterwards. LOTUS stays disarmed until you choose a policy and arm it.

Running `install.sh` from a newer release updates LOTUS and keeps your policy.

## Your first policy

See what is connected:

```sh
sudo lotus devices
```

Choose the rules. For example, block USB storage and let only trusted devices work:

```sh
sudo lotus storage block
sudo lotus usb trusted
```

Trust your own devices by the number or the ID that `lotus devices` shows:

```sh
sudo lotus trust 3
```

Then start enforcing:

```sh
sudo lotus arm
```

A change that would switch off a device that works right now lists those devices and asks first. Nothing happens unless you answer yes. In a script, add `--yes`.

## Commands

| Command | What it does |
| --- | --- |
| `lotus status` | What is in force right now |
| `lotus devices` | The connected USB devices and what the policy says about each |
| `lotus log [lines]` | The activity log |
| `lotus storage allow\|readonly\|block` | USB drives, card readers, cameras and phones |
| `lotus usb allow\|trusted` | Every other USB device: all of them, or only trusted ones |
| `lotus input on\|off` | Keep keyboards and mice working under `trusted`. On by default |
| `lotus strict on\|off` | Hold new devices back before they start. On by default |
| `lotus arm`, `lotus disarm` | Start and stop enforcing |
| `lotus trust <number\|id>` | Trust one device |
| `lotus trust <number\|id> --model` | Trust every unit of that model |
| `lotus untrust <number\|id\|model>` | Take a device or a model off the trusted list |
| `lotus release-all` | Disarm and switch every blocked device back on |
| `lotus version` | The version |

Every command except `version` and `help` needs root, because only root may talk to the daemon.

## How blocking works on Linux

The kernel has a switch for every USB device: the file `authorized` in the device's folder under `/sys/bus/usb/devices`. While it holds 0 the device has no configuration. No driver is bound to it, and none of its functions exist: a stick has no disk, a keyboard types nothing. LOTUS blocks a device by writing 0 there and lets it back in by writing 1.

The kernel reads a device's descriptors before it asks whether the device is authorized, and keeps them readable. So LOTUS knows what a device is, down to every function in every configuration it offers, without letting it run.

Strict mode uses a second switch. Each USB controller has a file `authorized_default`. While it holds 0, the kernel does not switch on anything that is plugged into that controller. It reads the descriptors and waits. With strict mode on and LOTUS armed, LOTUS sets every controller to 0 and switches on the devices the policy allows, one by one. A device the policy blocks never runs. The kernel log then has the line "Device is not authorized for usage" for it and nothing from any driver.

A device the policy allows is held back in the same way at first and switched on as soon as LOTUS has read what it is. In the test machine a new mouse was running 44 to 48 milliseconds after the kernel had held it back.

LOTUS hears about new devices from the kernel itself, over a netlink socket. It takes such a message as a reason to look, and decides from what it then reads under `/sys`.

Read-only mode is a udev rule, `/etc/udev/rules.d/99-lotus-readonly.rules`. It marks every USB disk as read-only in the kernel the moment the disk appears, before the desktop can mount it. The kernel then refuses writes whatever a mount command asks for. When the mode changes, LOTUS switches connected disks off and on once so that they appear again under the new rule.

LOTUS keeps a ledger of the devices it switched off and only ever switches those back on. A device that is off for another reason is left alone.

### The disk the system runs from

A Linux that is installed on a USB disk, or runs live from a stick, depends on that disk. LOTUS works out which USB devices hold the system's own filesystems, also through encrypted and logical volumes, and never switches them off, nor a hub on the way to them. The read-only rule names that disk as an exception, so it stays writable.

If LOTUS cannot work out which disk the system runs from, it leaves every storage device alone and does not switch read-only mode on.

### What counts as storage

The storage rule covers mass storage devices (USB class 08), which is every stick, external disk and card reader, and devices of the still image class (06), which is how cameras and current phones offer their files. Some older phones and players speak MTP over a vendor specific interface instead. LOTUS recognises those by the shape MTP requires, an interface with subclass FF, protocol 00 and one bulk endpoint in each direction plus an interrupt endpoint, and treats them as phones too.

### How a device is named

LOTUS gives every device an ID in the form the Windows version uses: `USB\VID_0781&PID_5567\4C530001230505116353`. The last part is the device's serial number. A device without one is named by its port instead, for example `USB\VID_046D&PID_C31C\P&1-2`, so trusting it holds for that port.

A port the kernel reports as fixed, which is how a laptop's webcam or Bluetooth radio is wired, makes the device count as built in. The "trusted only" rule leaves built-in devices alone.

### Hubs

A hub is a device like any other, and under "trusted only" an untrusted hub is switched off. Nothing that is plugged into a hub that is off exists for the system, so trust the hub first. Whatever is behind a trusted hub is still judged on its own.

`lotus` tells you when a change would take a hub down together with the devices behind it.

## Where the files are

| Path | Purpose |
| --- | --- |
| `/usr/local/bin/lotus` | The program: daemon and command in one |
| `/usr/local/sbin/lotus-uninstall` | The uninstaller |
| `/etc/systemd/system/lotus.service` | The service |
| `/var/lib/lotus/secure/policy.dat` | The policy and the trusted list, encrypted |
| `/var/lib/lotus/secure/ledger.dat` | The devices LOTUS switched off, encrypted |
| `/var/lib/lotus/secure/seal.key` | The key both files are encrypted with |
| `/var/lib/lotus/events.jsonl` | The activity log |
| `/run/lotus/control.sock` | The socket the `lotus` command talks to the daemon on |

`/var/lib/lotus` and `/run/lotus`, and everything in them, belong to root and are closed to everyone else.

## Protection

The store has the same format as on Windows: AES-256 with HMAC-SHA256, in a binary container. Someone who gets hold of `policy.dat` without the key cannot read it or change it unnoticed. If the file is changed, replaced or deleted while the daemon runs, the daemon puts its own copy back and writes a "tamper" line to the log. If the store is wrecked while the daemon is down, LOTUS locks down: it blocks storage and every untrusted device until `install.sh` is run again, which resets it to an empty policy.

Linux has no counterpart to the Windows data protection service, so the key is an ordinary file that only root can read. The encryption keeps the policy from everyone who is not root. Root can read the key.

The control socket is a Unix socket with mode 0600, and the daemon also asks the kernel who is on the other end of each connection and turns away anyone who is not root.

The daemon runs as root with no capabilities at all, a read-only view of the system apart from its own folders and the udev rules folder, no network, and no access to home directories. The service file has the full list.

## Limits

These come on top of the [limits that apply everywhere](../README.md#limits).

- Root can do anything LOTUS can do, and undo it. LOTUS on Linux protects a computer from the devices plugged into it and from users who are not root.
- Stopping the daemon opens nothing: blocked devices stay off and the controllers stay shut. But a restart of the computer resets the kernel's switches. From power-on until the daemon starts, the kernel runs every device that is plugged in. The daemon then switches off what the policy blocks.
- With strict mode off, a blocked device that is plugged in runs until the daemon has switched it off.
- If another program already holds the USB controllers shut, as USBGuard does, LOTUS does not touch them, and strict mode is off. It still switches blocked devices off after they start.
- Read-only mode covers disks. A phone or camera that speaks MTP or PTP has no disk for the kernel to protect, so read-only mode does not make it read-only. Set storage to `block` to keep such devices out.
- There is no notification on the desktop when a device is blocked. `lotus log` shows what happened.
- LOTUS covers USB. It does not manage Thunderbolt, Bluetooth pairing, SATA ports or network shares.

## If you lock yourself out

With `lotus input off`, an untrusted keyboard is blocked like any other device.

- A laptop's own keyboard is usually not a USB device and keeps working.
- Over the network nothing changes: `ssh` in and run `sudo lotus release-all`.
- Otherwise restart the computer and start it in rescue mode: at the boot menu, add `systemd.unit=rescue.target` to the kernel line. The daemon does not run there, and after a restart the kernel has every device switched on. Then run `systemctl start lotus` and `lotus release-all`, or remove LOTUS with `lotus-uninstall`.

## Uninstall

```sh
sudo lotus-uninstall
```

The daemon switches every blocked device back on, opens the controllers, lifts read-only mode and erases the policy. Then the service, the program, the folders and the uninstaller itself are removed. If the daemon cannot be reached, the uninstaller opens the controllers and switches the devices on by itself.

## Build from source

You need Linux and the .NET SDK 8 or newer.

```sh
git clone https://github.com/MAXIVA11/LOTUS.git
cd LOTUS
./build-linux.sh
```

The result is `dist/lotus-<version>-linux-x64.tar.gz`. The script runs the unit tests first.

To try LOTUS without touching the system, run the daemon in developer mode against a device tree of your own making:

```sh
lotus --dev --sysfs ./fake-sys --state ./state --socket ./lotus.sock daemon
lotus --sysfs ./fake-sys --state ./state --socket ./lotus.sock status
```

## How it is tested

`tests/Lotus.Linux.Tests` holds the unit tests. The tests of the shared core run there against the Linux build, and the Linux parts are tested on a made-up device tree: reading devices, the controller switch, the read-only rule, the socket, and the whole daemon from arming to removal.

`tests/linux/vm-test.sh` tests on a real kernel. It boots Ubuntu 24.04 under QEMU, once from an ordinary disk and once from an emulated USB disk, installs LOTUS from the release folder the way a user would, and plugs emulated devices into the machine's USB controller: three sticks, two keyboards, a mouse, a phone, a network adapter, a sound card, a smart card reader, a serial adapter and a hub. After each step it asks the kernel in the machine what happened. It checks, among other things, that:

- a blocked stick loses its disk, and a new stick under strict mode never gets a driver
- a new device the policy allows is switched on
- a phone counts as storage, and a network adapter, a sound card and a card reader are switched off under "trusted only"
- trust follows a stick to another port and does not cover a second stick of the same model
- nothing behind an untrusted hub exists, and a stick behind a trusted hub is still blocked
- read-only disks refuse writes, also a disk that is plugged in later
- the policy survives a killed daemon and a restart of the computer
- a damaged store locks LOTUS down and the installer resets it
- removal leaves no file and no device switched off
- a machine that itself starts from a USB disk keeps that disk: it is not switched off under any rule, it stays writable in read-only mode, and the machine starts again with read-only mode on

CI runs both on every change.

The test does not cover physical hardware. The devices in it are emulated, while the kernel and everything above it are real. Timing on real ports, devices that misbehave electrically and controllers other than xHCI have not been tested.
