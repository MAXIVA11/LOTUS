# Security

This page says what LOTUS protects against, how it protects itself, and where the protection ends.

## Who LOTUS is built to stop

| Attacker | What LOTUS does about it |
| --- | --- |
| Someone who plugs in a device | Blocks storage and untrusted devices according to the policy |
| A device that lies about itself | Reads the device's own descriptors, records what a trusted device does, and treats its name as hostile input |
| A Windows user without administrator rights | Cannot read the policy, cannot change anything LOTUS reads, cannot reach the control pipe |
| An administrator who tries the obvious things | Cannot edit the policy file, stop the service or remove LOTUS without the LOTUS password |
| An administrator who is determined to remove LOTUS | Can, with effort. See [Where the protection ends](#where-the-protection-ends) |

## The policy store

The policy is kept in `C:\ProgramData\LOTUS\secure\policy.dat`. It is JSON with a keyed checksum (HMAC-SHA256) in front. The key is 32 random bytes in `secure\seal.key`, wrapped with Windows data protection and readable by the service account only.

- Only the service writes the store. Administrators can read it. Other users cannot open the folder at all, because the trusted list tells an attacker which device to imitate.
- If the file is edited or deleted while the service runs, the service notices, puts its own copy back and writes a "tampering" line to the activity log. The policy in force does not change.
- If the store cannot be verified when the service starts, the last good copy is used. If that fails too, LOTUS locks down: removable storage and untrusted USB devices are blocked, keyboards and mice keep working, and the only way out is to run LOTUS-Setup again, which resets LOTUS to disarmed.

## The password

You choose a LOTUS password the first time you arm LOTUS. From then on the service asks for it before it:

- changes the policy or the trusted list
- changes the password
- stops for an update
- releases everything and stops for removal

`LotusGuard --release-all` asks for it too.

The password is stored as PBKDF2-HMAC-SHA256 with a random salt and 210,000 rounds. After five wrong attempts the service pauses password entry for 30 seconds, and the pause doubles with each further attempt up to 15 minutes. Wrong attempts are written to the activity log.

A policy carried over from LOTUS 1.0 has no password. It stays in force as it was, and LOTUS asks you to choose a password the next time you apply an armed policy.

LOTUS cannot recover a lost password. See [If the password is lost](#if-the-password-is-lost).

## The service

- It does not accept a stop request from Windows. It stops when setup asks over the control pipe with the password, and when Windows shuts down.
- Its permissions let administrators see it and start it, and nothing else: no stop, no reconfiguring, no delete. Setup lifts that again for an update or a removal after the password has been checked.
- If its process is killed, Windows starts it again after a second. Devices that were blocked stay blocked in the meantime, because the block is stored with the device.
- A blocked device that is enabled by hand in Device Manager is switched off again.

## The control pipe

- Only elevated administrators on this PC can connect. Connections over the network are refused.
- The service creates the pipe with the first-instance flag, so it fails loudly if another program took the name first.
- The app and setup ask Windows for the process ID of the LotusGuard service and compare it with the process behind the pipe before they send a password.

## Setup

- A data folder that already exists when LOTUS is installed is only kept if it is owned by the system or the Administrators group and ordinary users cannot write to it. Otherwise it is deleted, without following links inside it, and created again.
- Setup restricts its library search to the Windows system folder before it loads anything, so a library planted next to it in a Downloads folder is not picked up.
- The setup log goes into the install folder, which only administrators can write to.

## What an independent review found in 1.0

Before 1.1 the code was reviewed and attacked with the three attackers above in mind. These were the findings and what was done:

| Finding | Severity | Fixed in 1.1 by |
| --- | --- | --- |
| A user who created `C:\ProgramData\LOTUS` before installation stayed its owner and could rewrite the policy afterwards | High | Setup checks owner and permissions and replaces a folder it cannot trust. The service puts the owner and the permissions back every time it starts |
| Setup loaded ten system libraries from its own folder while running with administrator rights | Medium | Library search restricted to the system folder. Confirmed: the 1.0 installer loads planted copies, the 1.1 installer loads none |
| A blocked drive stayed usable for seconds when a program had it open | Medium | The device is judged the moment it appears, a refused switch-off is forced by resetting the port, and failures are retried four times a second. Measured in 1.1: a device Windows had never seen ran for 5 to 7 milliseconds, and a device blocked before did not start at all |
| Any user could read the policy and the trusted list | Low | The policy store moved to a folder only administrators can read |
| The policy was a plain file any administrator could edit | By design in 1.0 | Sealed store, password, service as the only writer |
| A device name could carry control characters or text-reordering characters into the log and the window | Low | Names are cleaned before use |
| "Built in" relied on one Windows property | Low | The port also has to be marked as not removable |
| The app started Explorer by bare name while elevated | Low | Full path |

Examined and found sound: the JSON reader (depth limit, large inputs, bad numbers and escapes), quoting of the service path and registry commands, zip extraction in setup, the process-by-name check before setup closes running copies, and the enforcement loop's handling of errors.

## Where the protection ends

- Trust is based on what a device reports. A device built to copy the vendor ID, product ID, serial number and interfaces of a trusted device is treated as that device. Trusting a whole model accepts any device that claims that model.
- With "Keep keyboards and mice working" on, which is the default, any device that is only a keyboard or mouse works. Turn it off to stop devices that type on their own.
- A device this PC has never seen runs for a few milliseconds before LOTUS has it switched off, and Windows reads its descriptors and loads its driver in that time. A device built to attack the Windows USB drivers themselves is not stopped by LOTUS.
- Trust entries carried over from LOTUS 1.0 do not record what the device does, because 1.0 did not store that. Remove such a device from the trusted list and trust it again to pin its functions.
- LOTUS runs in user mode. An administrator can become the system account, and the system account can do anything the service can. The password, the sealed store and the service permissions stop casual and accidental changes and make deliberate ones take effort. They do not make LOTUS removal-proof against someone who controls the PC.
- Starting Windows in Safe Mode, or from another disk, bypasses the service entirely.
- LOTUS covers USB ports. It does not manage Bluetooth pairing, Thunderbolt docks, SATA ports or network shares.

## If the password is lost

There is no back door. An administrator can remove LOTUS by hand:

1. Start Windows in Safe Mode. LOTUS Guard does not run there.
2. Delete the service: in an administrator command prompt, run `sc sdset LotusGuard D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)` and then `sc delete LotusGuard`.
3. Delete `C:\Program Files\LOTUS` and `C:\ProgramData\LOTUS`. Windows asks for permission to take over the second folder.
4. Restart. In Device Manager, enable every USB device that shows a down arrow.
5. Install LOTUS again if you want to keep using it.

## Reporting a problem

Open an issue at https://github.com/MAXIVA11/LOTUS/issues.
