#!/bin/sh
# Installs or updates LOTUS on Linux. Run it from the unpacked folder: sudo ./install.sh
set -eu

say() { printf '  %s\n' "$*"; }
fail() { printf '  XX  %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "Run this as root: sudo ./install.sh"
here=$(cd "$(dirname "$0")" && pwd)
for file in lotus lotus.service uninstall.sh; do
    [ -f "$here/$file" ] || fail "$file is missing next to install.sh. Unpack the whole archive first."
done
command -v systemctl >/dev/null 2>&1 || fail "LOTUS needs systemd, and this system does not have it."
[ -d /run/systemd/system ] || fail "systemd is not running here, so the LOTUS daemon could not be started."
[ -d /sys/bus/usb/devices ] || fail "This system shows no USB controllers under /sys/bus/usb, so there is nothing for LOTUS to control."
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) fail "This build of LOTUS is for 64-bit Intel and AMD computers. This one is $(uname -m)." ;;
esac

updating=no
[ -x /usr/local/bin/lotus ] && updating=yes

# Read-only mode writes one rule here. The service may write nowhere else outside its own folders.
mkdir -p /etc/udev/rules.d

# The new program goes in under another name first, so a daemon that is
# running never sees half a file.
install -m 0755 "$here/lotus" /usr/local/bin/lotus.new
mv -f /usr/local/bin/lotus.new /usr/local/bin/lotus
install -m 0755 "$here/uninstall.sh" /usr/local/sbin/lotus-uninstall
install -m 0644 "$here/lotus.service" /etc/systemd/system/lotus.service

systemctl daemon-reload
systemctl enable lotus.service >/dev/null 2>&1
# The policy stays in force across the restart: stopping the daemon opens nothing.
systemctl restart lotus.service

answers() {
    tries=0
    while [ "$tries" -lt 60 ]; do
        /usr/local/bin/lotus version >/dev/null 2>&1 && /usr/local/bin/lotus status >/dev/null 2>&1 && return 0
        tries=$((tries + 1))
        sleep 0.5
    done
    return 1
}

answers || fail "The LOTUS daemon did not come up. Look at: journalctl -u lotus"

# A policy store that was damaged or removed locks LOTUS down. Running the
# installer again is the way out: it leaves a note that only root can leave,
# and the daemon starts again with an empty policy, disarmed.
if /usr/local/bin/lotus locked >/dev/null 2>&1; then
    : > /var/lib/lotus/reset.request
    chmod 0600 /var/lib/lotus/reset.request
    systemctl restart lotus.service
    answers || fail "The LOTUS daemon did not come up after the reset. Look at: journalctl -u lotus"
    say "The policy store was damaged, so LOTUS was reset. It is disarmed and trusts no device."
    updating=reset
fi

if [ "$updating" = reset ]; then
    say "LOTUS $(/usr/local/bin/lotus version | sed 's/^LOTUS //') is installed."
elif [ "$updating" = yes ]; then
    say "LOTUS $(/usr/local/bin/lotus version | sed 's/^LOTUS //') is installed. Your policy is unchanged."
else
    say "LOTUS $(/usr/local/bin/lotus version | sed 's/^LOTUS //') is installed. It is disarmed, so nothing is blocked yet."
    say "See what is connected:   sudo lotus devices"
    say "Choose the rules:        sudo lotus storage block"
    say "Then start enforcing:    sudo lotus arm"
fi
say "Remove it again with:    sudo lotus-uninstall"
