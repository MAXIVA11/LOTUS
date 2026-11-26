# How LOTUS works

## The parts

| Program | Runs as | Job |
| --- | --- | --- |
| `LotusGuard.exe` | Windows service, SYSTEM | Watches USB ports and enforces the policy |
| `LOTUS.exe` | Administrator | The window where you edit the policy and trust devices |
| `LOTUS.exe --tray` | The signed-in user | Shows a notification when a device is blocked |
| `Lotus.Core.dll` | Loaded by all of the above | The policy engine and the Windows device calls |

They talk through three files in `C:\ProgramData\LOTUS`. Administrators and SYSTEM can write there. Everyone else can only read.

| File | Written by | Purpose |
| --- | --- | --- |
| `policy.json` | The app | The policy |
| `ledger.json` | The service | The devices LOTUS itself switched off |
| `events.jsonl` | The service | The activity log, one JSON object per line |

There is no network code anywhere in LOTUS.

## What counts as one device

LOTUS decides per physical device: the thing on one USB port. A headset with an audio interface and a button interface is one device. A hub is one device, and whatever is plugged into the hub is judged separately. The same goes for a Bluetooth adapter: the phones and keyboards paired with it sit on another bus and are not part of the adapter.

Windows gives every device an instance ID such as `USB\VID_0781&PID_5567\4C530001230505116353`. The last part is the serial number the device reports. A device without a serial number gets a generated ID that depends on the port, for example `USB\VID_1A2C&PID_2D23\5&CC3F949&0&6`. That is why LOTUS offers two kinds of trust:

- "This device" stores the full instance ID. It follows a device with a serial number to any port. For a device without one, it only holds in the port where you trusted it.
- "Whole model" stores `VID_xxxx&PID_xxxx` and covers every unit of that model in any port.

## Working out what a device does

The USB bus driver publishes each interface's class code as a compatible ID, for example `USB\Class_08` for mass storage or `USB\Class_03` for a keyboard or mouse. LOTUS reads those IDs, plus the setup class of the drivers Windows loaded, and sorts every device into kinds:

| Kind | Comes from |
| --- | --- |
| Storage | USB class 08, or a disk or optical drive below the device |
| Phone or camera | USB class 06, the MTP marker, or a portable device driver |
| Input | USB class 03 |
| Audio, camera, network, Bluetooth, printer, smart card, hub | Their USB class codes |
| Other | Vendor specific classes and anything unrecognised |

The storage setting applies to the first two kinds. The PnP setting applies to everything else a device does. A flash drive that also presents a keyboard is both, and either rule can block it.

A composite device does not list its interfaces until a moment after it is plugged in. LOTUS waits up to three seconds for them. A device that still has not identified itself is treated as "Other".

## The decision

For each device, in this order:

1. LOTUS is disarmed: allow.
2. Windows boots from a disk behind this device: allow.
3. The device is on the trusted list: allow.
4. It has storage and storage is set to Block: block.
5. PnP is set to Trusted only and the device does something besides storage:
   - Windows reports it as built into the computer (a laptop webcam, for example): allow.
   - "Keep keyboards and mice working" is on and everything else it does is input: allow.
   - Otherwise: block.
6. Allow.

The same function runs in the service, in the app (to show what would happen before you apply a change) and in the unit tests.

## Switching a device off

The service calls `CM_Disable_DevNode` with the persist flag. That is the call behind "Disable device" in Device Manager. Windows stops the device's drivers and stores a disabled mark with the device, so it comes back disabled after replugging and after a restart, whether or not the service is running at that time.

Windows refuses to disable a device while a keyboard driver is running on it. For those, the service first calls `CM_Query_And_Remove_SubTree`, which unloads the drivers, and then disables the device. Both steps were confirmed on a real USB keyboard.

Before it touches a device, the service writes the device to its ledger. If the service is stopped in the middle of an operation, the next run still knows the device is its responsibility.

## Switching a device back on

When the policy stops blocking a device on the ledger, because you trusted it, changed a setting or disarmed LOTUS, the service enables it again and removes it from the ledger. If the device is not plugged in at that moment, the service clears the disabled mark Windows keeps for it, so it works the next time it is connected.

LOTUS never enables a device that is not on its ledger. Something you disabled by hand in Device Manager stays disabled.

## Read-only mode

Read-only mode writes `Deny_Write = 1` under `HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices` for removable disks, CD and DVD drives, floppy drives and portable devices. This is the registry form of the Group Policy setting "Removable Storage Access", and Windows enforces it inside the storage stack.

Windows applies the setting when a drive is connected, so the service switches connected drives off and on again when the mode changes. A drive that is in use at that moment picks the change up the next time it is plugged in.

The service checks the registry values every two seconds and puts them back if they are removed. It only removes values it set itself.

## Timing

Windows notifies the service when a device is enumerated, before its drivers have finished loading. The service also runs a full pass every two seconds, so a missed notification costs at most two seconds.

A device Windows has never seen needs its driver installed first. During that short window the device can already be active. From the second connection on, Windows starts it in the disabled state and there is no window at all.

## Safety rules

- The disk Windows boots from is never blocked. If the service cannot work out which disk that is, it leaves all storage alone until it can.
- A failed block or release is retried on every pass and reported once in the activity log.
- If `policy.json` cannot be read, the service keeps the last policy it loaded. On a fresh start with an unreadable file it uses the backup copy, and without one it stays disarmed.
- Uninstalling switches every device on the ledger back on and removes the read-only setting before any file is deleted.

## Command line

```
LotusGuard --scan          List USB devices and what the current policy says about each.
LotusGuard --console       Enforce the policy from a console window until Enter is pressed.
LotusGuard --release-all   Switch every blocked device back on and lift read-only mode.

LOTUS --demo               Open the app with simulated devices.
LOTUS --tray               Run the notifier.

LOTUS-Setup /quiet                 Install without a window.
LOTUS-Setup /uninstall /quiet      Remove without a window.
```

Quiet setup writes its progress to `%TEMP%\LOTUS-Setup.log` and returns 0 on success, 1 on failure and 2 when it finished with warnings.
