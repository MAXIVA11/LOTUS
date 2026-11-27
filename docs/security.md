# Security

This page says what LOTUS protects against, how it protects itself, and where the protection ends.

It describes the Windows version. [On Linux](#on-linux) lists what is different there.

## Who LOTUS is built to stop

| Attacker | What LOTUS does about it |
| --- | --- |
| Someone who plugs in a device | Blocks storage and untrusted devices according to the policy. With strict mode on, a blocked device this PC has never seen does not start at all |
| A device that lies about itself | Reads the device's own descriptors, records what a trusted device does, and treats its name as hostile input |
| A Windows user without administrator rights | Cannot read the policy, cannot change anything LOTUS reads, cannot reach the control pipe |
| Someone who finds the LOTUS files | Sees encrypted binary files. The policy and the trusted list cannot be read from them or changed in them |
| An administrator | Can change the policy in the app. Can also remove LOTUS, with some effort. See [Where the protection ends](#where-the-protection-ends) |

LOTUS has no password of its own. Whoever can run the app as an administrator is the person LOTUS takes its policy from.

## The policy store

The policy and the trusted list are kept in `C:\ProgramData\LOTUS\secure\policy.dat`. The list of devices LOTUS switched off is in `ledger.dat` next to it. Both are binary files in the same container:

| Part | Size | Content |
| --- | --- | --- |
| Mark | 6 bytes | `LOTUS` and a zero byte |
| Version and kind | 2 bytes | Format 2, and whether this is a policy or a ledger |
| Serial number | 8 bytes | Goes up with every save |
| Initialisation vector | 16 bytes | Random, new for every save |
| Length | 4 bytes | Length of the encrypted part |
| Encrypted part | varies | The record, AES-256 in CBC mode |
| Authentication tag | 32 bytes | HMAC-SHA256 over everything before it |

- The record inside is a compact binary form, padded to a multiple of 1024 bytes so the file size says little about how long the trusted list is. It is not JSON and not text.
- The file is authenticated before anything in it is decrypted. Changing a single byte makes LOTUS refuse it.
- Encryption and authentication use separate keys, and policy and ledger use separate keys again. All of them are derived from one master key with HMAC-SHA256, so a ledger cannot be passed off as a policy.
- The master key is 32 random bytes in `secure\seal.key`. It is wrapped with the Windows data protection service for this machine, so the file is useless on another PC, and only the service account may read it. Administrators cannot.

What follows from that:

- Nobody but the service can read the policy. The app and `LotusGuard --scan` ask the service for it over the control pipe.
- If a store file is edited, replaced or deleted while the service runs, the service notices, puts its own copy back and writes a "tampering" line to the activity log. The policy in force does not change.
- If the store cannot be verified when the service starts, the last good copy is used. If that fails too, LOTUS locks down: removable storage and untrusted USB devices are blocked, keyboards and mice keep working, and policy changes are refused. Running LOTUS-Setup again ends the lockdown: LOTUS starts disarmed, as on a new PC.

Two files in `C:\ProgramData\LOTUS` are readable by every user, because the notifier next to the clock runs without special rights and needs them: `status.json` says whether LOTUS is armed, and `events.jsonl` is the activity log with the names of devices that were blocked. Neither contains the policy settings or the trusted list.

The service writes the same lines to the Application log of Windows, under the source `LotusGuard`. Windows lets signed-in users read that log, so it shows what `events.jsonl` shows and nothing more. [logging.md](logging.md) has the event numbers. When a trust entry reaches its end, the line that says so names that entry, which by then is off the list.

## Strict mode

With "Refuse new devices before they start" on, LOTUS uses the device installation restrictions that are built into Windows, the ones Group Policy offers under "Device Installation Restrictions". Windows checks them every time it is about to install a device, before any driver is loaded.

- LOTUS writes a deny list of USB class IDs. With "Trusted only" it covers every USB device and every USB function. With only storage blocked it covers mass storage, cameras and phones.
- It turns on the layered order of evaluation, in which a device instance on the allow list beats a class on the deny list, and puts every device the policy allows on that allow list.
- A blocked device this PC has never seen is refused by Windows itself. Windows logs "configuration blocked by policy" and the device never starts. LOTUS then switches it off in the usual way as well, so it stays off if strict mode is turned off later.
- A device the policy allows but that is not on the list yet, for example a new keyboard under "Keep keyboards and mice working", is refused first. LOTUS reads its descriptors and adds it to the allow list at once, and Windows installs it a moment later. In the hardware test that took 60 to 170 milliseconds. A device with several functions is let in one function at a time.
- Devices that are already installed are not affected. The restrictions only apply when Windows installs a device.

The restrictions live under `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions` and carry a value named `ManagedByLOTUS`. LOTUS only ever changes or removes restrictions with that mark. If the PC already has device installation restrictions from an administrator or from Group Policy, LOTUS leaves them alone, says so in the activity log, and falls back to switching blocked devices off straight after they start.

Strict mode needs Windows 10 21H2 or newer. Disarming, the rescue command and uninstalling remove the restrictions and have Windows install the devices it had refused.

## The service

- It does not accept a stop request from Windows. It stops when setup asks over the control pipe, and when Windows shuts down.
- Its permissions let administrators see it and start it, and nothing else: no stop, no reconfiguring, no delete. Setup lifts that again for an update or a removal.
- If its process is killed, Windows starts it again after a second. Devices that were blocked stay blocked in the meantime, because the block is stored with the device, and the installation restrictions stay in force.
- A blocked device that is enabled by hand in Device Manager is switched off again.

## The control pipe

- Only elevated administrators on this PC can connect. Connections over the network are refused.
- The service creates the pipe with the first-instance flag, so it fails loudly if another program took the name first.
- The app and setup ask Windows for the process ID of the LotusGuard service and compare it with the process behind the pipe before they send anything.

## Setup

- A data folder that already exists when LOTUS is installed is only kept if it is owned by the system or the Administrators group and ordinary users cannot write to it. Otherwise it is deleted, without following links inside it, and created again.
- Setup restricts its library search to the Windows system folder before it loads anything, so a library planted next to it in a Downloads folder is not picked up.
- The setup log goes into the install folder, which only administrators can write to.

## On Linux

The policy store on Linux has the same format and the same checks: a changed, replaced or deleted store is put back while the daemon runs, and a store that is wrecked while the daemon is down locks LOTUS down until the installer is run again.

Three things are different.

- The key. Linux has no counterpart to the Windows data protection service, so `seal.key` is a file that only root can read, in a folder only root can enter. The encryption keeps the policy from everyone who is not root. Root can read the key.
- The daemon. On Windows the service resists being stopped. On Linux root can stop the daemon with `systemctl stop lotus`. Stopping it opens nothing: blocked devices stay off and the USB controllers stay shut. A daemon that is killed is started again by systemd after a second.
- The start of the computer. The kernel forgets its USB switches when the computer restarts. Until the daemon is running again, the kernel runs every device that is plugged in. Started with `usbcore.authorized_default=2`, the kernel holds back every device that is not built in until the daemon has looked at it, which closes that gap. [linux.md](linux.md#before-the-daemon-starts) has the steps and the risk that comes with them.

The control channel is a Unix socket, `/run/lotus/control.sock`, that only root can open. The daemon also asks the kernel who is on the other end of every connection and turns away anyone who is not root. It runs as root without any capability, sees the system read-only apart from its own folders and the udev rules folder, and has no network.

[linux.md](linux.md) has the rest.

## What the security review of 1.0 found

Before 1.1 the code was reviewed and attacked with the first three attackers above in mind. These were the findings and what was done:

| Finding | Severity | Fixed by |
| --- | --- | --- |
| A user who created `C:\ProgramData\LOTUS` before installation stayed its owner and could rewrite the policy afterwards | High | Setup checks owner and permissions and replaces a folder it cannot trust. The service puts the owner and the permissions back every time it starts |
| Setup loaded ten system libraries from its own folder while running with administrator rights | Medium | Library search restricted to the system folder. Confirmed: the 1.0 installer loads planted copies, later installers load none |
| A blocked drive stayed usable for seconds when a program had it open | Medium | The device is judged the moment it appears, a refused switch-off is forced by resetting the port, and failures are retried four times a second. With strict mode, since 1.2, a never-seen device does not start at all |
| Any user could read the policy and the trusted list | Low | The store moved to a folder only the service can use, and since 1.2 it is encrypted |
| The policy was a plain file any administrator could edit | By design in 1.0 | The service is the only reader and writer of an encrypted, authenticated store |
| A device name could carry control characters or text-reordering characters into the log and the window | Low | Names are cleaned before use |
| "Built in" relied on one Windows property | Low | The port also has to be marked as not removable |
| The app started Explorer by bare name while elevated | Low | Full path |

Examined and found sound: the JSON reader used on the control pipe (depth limit, large inputs, bad numbers and escapes), quoting of the service path and registry commands, zip extraction in setup, the process-by-name check before setup closes running copies, and the enforcement loop's handling of errors.

## Where the protection ends

- LOTUS has no password. Anyone who can run the app as an administrator can disarm it or trust a device. LOTUS protects the policy from other users, from other programs and from people who find its files, and it takes its orders from administrators.
- Trust is based on what a device reports. A device built to copy the vendor ID, product ID, serial number and interfaces of a trusted device is treated as that device. Trusting a whole model accepts any device that claims that model, and trusting a whole vendor accepts any device that claims that vendor. An entry typed in by hand does not record what the device does, so it is not withdrawn when the device shows up with another function.
- With "Keep keyboards and mice working" on, which is the default, any device that is only a keyboard or mouse works. Turn it off to stop devices that type on their own.
- With strict mode off, which is how LOTUS starts, or where it is not available, a blocked device this PC has never seen runs for a few milliseconds before LOTUS has it switched off. Windows reads its descriptors and loads its driver in that time.
- Even with strict mode on, Windows reads the descriptors of every device that is plugged in, because that is how it learns what the device is. A device built to attack the Windows USB drivers through its descriptors is not stopped by LOTUS.
- Trust entries carried over from LOTUS 1.0 do not record what the device does, because 1.0 did not store that. Remove such a device from the trusted list and trust it again to pin its functions.
- LOTUS runs in user mode. An administrator can become the system account, and the system account can read the store key and do anything the service can. The encrypted store and the service permissions stop casual and accidental changes and keep the policy private. They do not make LOTUS removal-proof against someone who controls the PC.
- Starting Windows in Safe Mode, or from another disk, bypasses the service.
- LOTUS covers USB ports. It does not manage Bluetooth pairing, Thunderbolt docks, SATA ports or network shares.

## Removing LOTUS by hand

The uninstaller does all of this. If it cannot be used, an administrator can do it by hand:

1. Start Windows in Safe Mode. LOTUS Guard does not run there.
2. In an administrator command prompt, run `"C:\Program Files\LOTUS\LotusGuard.exe" --release-all`. It disarms LOTUS, switches every blocked device back on and removes the installation restrictions.
3. Delete the service: run `sc sdset LotusGuard D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)` and then `sc delete LotusGuard`.
4. Delete `C:\Program Files\LOTUS` and `C:\ProgramData\LOTUS`. Windows asks for permission to take over the second folder.
5. If step 2 could not be run, delete the registry key `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions` when it holds a value named `ManagedByLOTUS`, restart, and enable every USB device that shows a down arrow in Device Manager.

## Reporting a problem

A weakness that someone could use against a PC that runs LOTUS goes to the private report form: https://github.com/MAXIVA11/LOTUS/security/advisories/new. Only the maintainer sees it. [SECURITY.md](../SECURITY.md) says what a report should hold.

Everything else is an issue at https://github.com/MAXIVA11/LOTUS/issues.
