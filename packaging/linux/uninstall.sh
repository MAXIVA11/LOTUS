#!/bin/sh
# Removes LOTUS from Linux and puts every USB device back the way the kernel
# would have it. Installed as /usr/local/sbin/lotus-uninstall.
set -u

say() { printf '  %s\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { printf '  XX  %s\n' "Run this as root: sudo lotus-uninstall" >&2; exit 1; }

released=no
rule=/etc/udev/rules.d/99-lotus-readonly.rules
read_only=no
[ -e "$rule" ] && read_only=yes
if [ -x /usr/local/bin/lotus ] && command -v systemctl >/dev/null 2>&1; then
    # The daemon knows which devices it switched off, so it is the one to
    # switch them back on. If it is down, bring it up for this.
    systemctl is-active --quiet lotus.service || systemctl start lotus.service >/dev/null 2>&1
    tries=0
    while [ "$tries" -lt 20 ]; do
        /usr/local/bin/lotus status >/dev/null 2>&1 && break
        tries=$((tries + 1))
        sleep 0.5
    done
    if /usr/local/bin/lotus remove-data; then
        released=yes
    fi
fi

if command -v systemctl >/dev/null 2>&1; then
    systemctl disable --now lotus.service >/dev/null 2>&1
fi

if [ "$released" = no ]; then
    # The daemon could not do it, so do it by hand: open every controller and
    # switch on every USB device that is off. That may include a device some
    # other program switched off.
    say "The LOTUS daemon did not answer. Switching every USB device back on directly."
    for file in /sys/bus/usb/devices/usb*/authorized_default; do
        [ -e "$file" ] && echo 1 > "$file" 2>/dev/null
    done
    # Shortest name first, so a hub is on before what is plugged into it.
    for pass in 1 2 3 4 5 6; do
        for file in /sys/bus/usb/devices/*/authorized; do
            [ -e "$file" ] || continue
            [ "$(cat "$file" 2>/dev/null)" = 0 ] && echo 1 > "$file" 2>/dev/null
        done
        sleep 0.3
    done
fi

rm -f "$rule"
command -v udevadm >/dev/null 2>&1 && udevadm control --reload >/dev/null 2>&1

if [ "$released" = no ] && [ "$read_only" = yes ]; then
    # Read-only mode was on. The rule is gone, but a disk that is connected
    # keeps the mark it got. Take it off every USB disk and its partitions.
    for disk in /sys/block/*; do
        [ -e "$disk/ro" ] || continue
        case "$(readlink -f "$disk")" in
            */usb[0-9]*/*) ;;
            *) continue ;;
        esac
        for part in "$disk" "$disk"/*; do
            [ "$(cat "$part/ro" 2>/dev/null)" = 1 ] && blockdev --setrw "/dev/$(basename "$part")" 2>/dev/null
        done
    done
fi

rm -f /etc/systemd/system/lotus.service
command -v systemctl >/dev/null 2>&1 && systemctl daemon-reload >/dev/null 2>&1
rm -rf /var/lib/lotus /run/lotus /var/cache/lotus
rm -f /usr/local/bin/lotus /usr/local/bin/lotus.new

say "LOTUS is removed. Nothing is blocked."
if [ "$(cat /sys/module/usbcore/parameters/authorized_default 2>/dev/null)" = 2 ]; then
    # The kernel was told to hold devices back until LOTUS has looked at them.
    # With LOTUS gone, the next start would leave them all switched off.
    printf '\n  !!  %s\n' "This system starts with usbcore.authorized_default=2 on the kernel command line." >&2
    printf '      %s\n' "From the next start on, nothing switches on a USB device that is not built in," >&2
    printf '      %s\n' "a keyboard or mouse included. Take that setting off the kernel command line" >&2
    printf '      %s\n\n' "before you restart. docs/linux.md says how." >&2
fi
# This script is the last thing to go.
rm -f /usr/local/sbin/lotus-uninstall
exit 0
