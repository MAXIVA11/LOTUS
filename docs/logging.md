# Logging

LOTUS writes what it sees and does to its activity log, which the app and `lotus log` show. From 1.6.0 the service also copies every line to the log of the operating system, where log collectors look and where the system decides how long it is kept.

## Where the lines go

| System | Where | How to read it |
| --- | --- | --- |
| Windows | The Application log, source `LotusGuard` | Event Viewer, or `Get-WinEvent -ProviderName LotusGuard` |
| Linux | The systemd journal, identifier `lotus` | `journalctl -t lotus`, or `journalctl -t lotus LOTUS_EVENT=blocked` |

On Linux every entry carries the fields `LOTUS_EVENT`, `LOTUS_EVENT_ID` and, when the event is about a device, `LOTUS_DEVICE_ID` and `LOTUS_DEVICE_NAME`.

The activity log itself stays what it was: `events.jsonl` in the data folder, 1 MB and one older file.

## Event numbers

The numbers stay the same from version to version. A new kind of event gets a new number, and a number never gets a second meaning.

| Number | Event | Level | Meaning |
| --- | --- | --- | --- |
| 1000 | `guard_started` | Information | The service started |
| 1001 | `guard_stopped` | Information | The service stopped |
| 1100 | `policy_changed` | Information | The policy was loaded or changed |
| 1101 | `trust_ended` | Information | A trust entry reached its end and was taken off the list |
| 2000 | `connected` | Information | A USB device was plugged in |
| 2001 | `removed` | Information | A USB device was unplugged |
| 2100 | `blocked` | Warning | A device was blocked |
| 2101 | `released` | Information | A blocked device was switched back on |
| 2102 | `input_let_in` | Warning | A keyboard or mouse nobody approved was let in, because no other one was working |
| 2200 | `readonly_on` | Information | Read-only mode for storage came on |
| 2201 | `readonly_off` | Information | Read-only mode for storage was lifted |
| 3000 | `warning` | Warning | Something did not work the first time |
| 3001 | `error` | Error | Something failed |
| 4000 | `tamper` | Error | Someone or something interfered with LOTUS itself |
| 4100 | `security` | Warning | Setup asked the service to stop for an update or a removal |
