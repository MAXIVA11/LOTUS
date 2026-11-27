#!/usr/bin/env bash
# Tests LOTUS on a real Linux kernel with USB devices that QEMU emulates.
#
#   tests/linux/vm-test.sh dist/linux/lotus-<version>-linux-x64 [work folder]
#
# It boots an Ubuntu cloud image in a virtual machine, installs LOTUS from the
# given folder the way a user would, and then plugs sticks, keyboards, a hub
# and a serial adapter into the machine's USB controller and pulls them out
# again. After every step it asks the kernel in the machine what happened.
#
# Needs: qemu-system-x86_64, qemu-img, cloud-localds, ssh, python3, curl,
# mkfs.ext4. With /dev/kvm the run takes a few minutes. Without it QEMU
# emulates the processor and the run takes much longer.
set -u

package=${1:?"Give the folder with the unpacked LOTUS release."}
package=$(cd "$package" && pwd)
here=$(cd "$(dirname "$0")" && pwd)
work=${2:-$(mktemp -d /tmp/lotus-vm.XXXXXX)}
mkdir -p "$work"
cache=${LOTUS_VM_CACHE:-$HOME/.cache/lotus-vm}
image_url=${LOTUS_VM_IMAGE:-https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img}
port=${LOTUS_VM_PORT:-2222}
qmp="$work/qmp.sock"

passed=0
failed=0
failures=()

section() { printf '\n== %s\n' "$*"; }
ok() { passed=$((passed + 1)); printf '  PASS  %s\n' "$1"; }
bad() {
    failed=$((failed + 1))
    failures+=("$1")
    printf '  FAIL  %s\n' "$1"
    [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/          /'
}

ssh_options=(-i "$work/key" -p "$port" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null
    -o LogLevel=ERROR -o ConnectTimeout=5 -o BatchMode=yes)

# Runs a command in the machine.
g() { ssh "${ssh_options[@]}" lotus@127.0.0.1 "$@"; }

# check "what is expected" command...   passes when the command in the machine succeeds.
check() {
    local what=$1 output
    shift
    if output=$(g "$@" 2>&1); then ok "$what"; else bad "$what" "$(printf '%s' "$output" | tail -n 6)"; fi
}

# check_not "what is expected" command...   passes when the command fails.
check_not() {
    local what=$1 output
    shift
    if output=$(g "$@" 2>&1); then bad "$what" "$(printf '%s' "$output" | tail -n 6)"; else ok "$what"; fi
}

plug() { python3 "$here/qmp.py" "$qmp" plug "$@" || bad "QEMU plugged in $1"; }
unplug() { python3 "$here/qmp.py" "$qmp" unplug "$1" || bad "QEMU pulled out $1"; }

lotus_log() { g "sudo lotus log 400" 2>/dev/null; }

stop_machine() {
    if [ -f "$work/qemu.pid" ]; then
        kill "$(cat "$work/qemu.pid")" 2>/dev/null
        rm -f "$work/qemu.pid"
    fi
}
trap stop_machine EXIT

wait_for_ssh() {
    local tries=0
    while [ "$tries" -lt "${1:-180}" ]; do
        g true 2>/dev/null && return 0
        tries=$((tries + 1))
        sleep 2
    done
    return 1
}

# ---------------------------------------------------------------- the machine

section "Starting the test machine"
for tool in qemu-system-x86_64 qemu-img cloud-localds ssh ssh-keygen python3 curl mkfs.ext4; do
    command -v "$tool" >/dev/null 2>&1 || { echo "Missing: $tool"; exit 2; }
done
for file in lotus install.sh uninstall.sh lotus.service; do
    [ -f "$package/$file" ] || { echo "Missing in the release folder: $file"; exit 2; }
done

mkdir -p "$cache"
image="$cache/$(basename "$image_url")"
if [ ! -s "$image" ]; then
    echo "  Downloading $(basename "$image_url")"
    curl -fL --retry 3 -o "$image.part" "$image_url" || { echo "Could not download the image."; exit 2; }
    mv "$image.part" "$image"
fi

rm -f "$work/disk.qcow2" "$work/key" "$work/key.pub" "$qmp"
qemu-img create -q -f qcow2 -F qcow2 -b "$image" "$work/disk.qcow2" 8G
ssh-keygen -q -t ed25519 -N '' -f "$work/key"
cat > "$work/user-data" <<EOF
#cloud-config
users:
  - name: lotus
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    ssh_authorized_keys:
      - $(cat "$work/key.pub")
ssh_pwauth: false
EOF
printf 'instance-id: lotus-test\nlocal-hostname: lotus-test\n' > "$work/meta-data"
cloud-localds "$work/seed.img" "$work/user-data" "$work/meta-data"

mkdir -p "$work/phone"
echo "a file on the pretend phone" > "$work/phone/photo.txt"

# Three small disks to plug in as USB sticks, each with a filesystem on it.
for n in 1 2 3; do
    truncate -s 64M "$work/stick$n.img"
    mkfs.ext4 -q -F "$work/stick$n.img"
done

accel="tcg"
cpu="max"
if [ -e /dev/kvm ]; then
    [ -w /dev/kvm ] || sudo chmod 666 /dev/kvm 2>/dev/null
    if [ -w /dev/kvm ]; then accel="kvm"; cpu="host"; fi
fi
echo "  Processor: $accel"

qemu-system-x86_64 -name lotus-test -machine "q35,accel=$accel" -cpu "$cpu" -smp 2 -m 2048 \
    -display none -serial "file:$work/console.log" \
    -drive "if=virtio,format=qcow2,file=$work/disk.qcow2" \
    -drive "if=virtio,format=raw,file=$work/seed.img" \
    -netdev "user,id=net0,hostfwd=tcp:127.0.0.1:$port-:22" -device virtio-net-pci,netdev=net0 \
    -device qemu-xhci,id=xhci,p2=12,p3=4 \
    -blockdev "driver=file,filename=$work/stick1.img,node-name=file1" -blockdev driver=raw,file=file1,node-name=disk1 \
    -blockdev "driver=file,filename=$work/stick2.img,node-name=file2" -blockdev driver=raw,file=file2,node-name=disk2 \
    -blockdev "driver=file,filename=$work/stick3.img,node-name=file3" -blockdev driver=raw,file=file3,node-name=disk3 \
    -chardev null,id=nowhere -netdev user,id=net1 -audiodev none,id=silence \
    -qmp "unix:$qmp,server=on,wait=off" -pidfile "$work/qemu.pid" -daemonize \
    || { echo "QEMU did not start."; exit 2; }

if ! wait_for_ssh 240; then
    echo "The machine did not come up. The end of its console:"
    tail -n 40 "$work/console.log"
    exit 2
fi
g "cloud-init status --wait >/dev/null 2>&1; uname -r; . /etc/os-release; echo \$PRETTY_NAME"

# Copies the release and the test's helper into the machine.
copy_in() {
    local options=(-q -i "$work/key" -P "$port" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR)
    scp "${options[@]}" -r "$package" lotus@127.0.0.1:release
    scp "${options[@]}" "$here/guest-tool.sh" lotus@127.0.0.1:t
    scp "${options[@]}" "$here/tui-drive.py" lotus@127.0.0.1:tui-drive.py
    g "sudo install -m 0755 t /usr/local/bin/t"
}
copy_in

# The kernel in a cloud image comes without the drivers for USB sticks unless the extra modules are there.
if ! g "sudo modprobe usb-storage 2>/dev/null"; then
    echo "  Installing the kernel's extra modules for usb-storage"
    g "sudo apt-get update -qq >/dev/null 2>&1; sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq linux-modules-extra-\$(uname -r) >/dev/null 2>&1; sudo modprobe usb-storage" \
        || echo "  usb-storage is not available. The checks on disks will fail."
fi

# Serial numbers the test gives its devices. LOTUS writes serial numbers in capitals.
STICK1=LOTUSSTICK1
STICK2=LOTUSSTICK2
STICK3=LOTUSSTICK3
STICK4=LOTUSSTICK4
KBD=LOTUSKBD1
MOUSE=LOTUSMOUSE1
SERIAL=LOTUSSERIAL1
HUB=LOTUSHUB1
KBD2=LOTUSKBD2
PHONE=LOTUSPHONE1
NETWORK=LOTUSNET1
SOUND=LOTUSSOUND1
CARD=LOTUSCARD1

# ---------------------------------------------------------------- installing

section "Installing"
install_output=$(g "cd release && sudo ./install.sh" 2>&1)
printf '%s\n' "$install_output" | sed 's/^/    /'
check "the installer finished and says LOTUS is disarmed" "cd release && true; sudo lotus status | grep -q DISARMED"
check "the daemon is running under systemd" "systemctl is-active --quiet lotus"
check "the daemon starts with the computer" "systemctl is-enabled --quiet lotus"
check "the control socket belongs to root alone" "[ \"\$(sudo stat -c '%a %U' /run/lotus/control.sock)\" = '600 root' ]"
check "the folders LOTUS keeps its files in belong to root alone" "[ \"\$(sudo stat -c '%a %U' /var/lib/lotus)\" = '700 root' ] && [ \"\$(sudo stat -c '%a %U' /var/lib/lotus/secure)\" = '700 root' ]"
check "every file in them is readable by root only" "[ -z \"\$(sudo find /var/lib/lotus -type f ! -perm 600)\" ]"
check "the store is there and is not text" "sudo test -s /var/lib/lotus/secure/policy.dat && ! sudo grep -aqiE 'armed|trusted|storage' /var/lib/lotus/secure/policy.dat"
check "someone who is not root is turned away" "lotus status 2>&1 | grep -q 'Only root'"
check_not "and learns nothing from the attempt" "lotus status 2>/dev/null | grep -q DISARMED"
check "the daemon runs without any capability" "grep -q 'CapEff:[[:space:]]*0000000000000000' /proc/\$(pidof lotus)/status"
check "the daemon listens to the kernel's device announcements" "sudo ss -H -f netlink 2>/dev/null | grep -i uevent | grep -q \"\$(pidof lotus)\""
check "the log says the daemon started" "sudo lotus log 50 | grep -q 'started'"
check "a new installation allows storage and every device" "sudo lotus status | grep -Eq 'STORAGE +ALLOW' && sudo lotus status | grep -Eq 'USB DEVICES +ALLOW'"
check "it keeps keyboards and mice working, has strict mode off and trusts nothing" "sudo lotus status | grep -q 'ALWAYS WORK' && sudo lotus status | grep -q 'SWITCHED OFF AFTER THEY START' && sudo lotus status | grep -Eq 'TRUSTED +0'"
check "the trusted list is empty" "sudo lotus trusted | grep -q 'Nothing is trusted yet'"

section "For monitoring tools and log collectors"
g "sudo lotus status --json" | sed 's/^/    /'
check "the status comes as JSON that says what is in force" "t json 'd.running and not d.armed and not d.lockdown and d.storage == \"allow\" and d.usb == \"allow\" and d.keepInput and not d.strict and d.trusted == 0 and d.blocked == 0'"
check "it carries the version and no tampering" "t json 'len(d.version.split(\".\")) == 3 and d.lastTamper is None and not d.heldAtStart'"
check "someone who is not root gets JSON too, which says the daemon did not answer, and exit code 1" "lotus status --json > out.json; [ \$? = 1 ] && python3 -c 'import json; d = json.load(open(\"out.json\")); assert d[\"running\"] is False and \"armed\" not in d'"
check "the journal has the start of the daemon, with its event number" "t journal guard_started '\"LOTUS_EVENT_ID\":\"1000\"'"
check "the journal knows the line came from the lotus service" "t journal guard_started '\"_SYSTEMD_UNIT\":\"lotus.service\"'"

# ---------------------------------------------------------------- disarmed

section "Disarmed: nothing is touched"
plug s1 usb-storage 1 drive=disk1 serial=$STICK1
plug k1 usb-kbd 2 serial=$KBD
check "the stick shows up and runs" "t present $STICK1 && t wait-auth $STICK1 1"
check "the stick has its disk" "t wait-block $STICK1"
check "the disk can be written to" "b=\$(t block $STICK1) && sudo mount /dev/\$b /mnt && sudo sh -c 'echo hello > /mnt/written-while-disarmed' && sudo umount /mnt"
check "LOTUS lists the stick as storage" "sudo lotus devices | grep -A1 'QEMU USB HARDDRIVE' | grep -q $STICK1 && sudo lotus devices | grep -q 'ALLOWED.*Storage'"
check "LOTUS lists the keyboard as an input device" "sudo lotus devices | grep -q 'ALLOWED.*Input'"
check "no controller is held shut" "[ \"\$(t defaults)\" = '1 ' ]"

# ---------------------------------------------------------------- the interactive interface

section "The interactive interface"
# "lotus ui --frame" presses the given keys and prints the screen once. That
# drives the real interface against the real daemon without a terminal.
ui() { g "sudo lotus ui --frame --plain --size 100x30 --keys '$1'"; }
check "without a terminal, lotus on its own prints the status as before" "sudo lotus </dev/null | grep -q 'STATUS *DISARMED'"
check_not "and the interface says it needs a terminal" "sudo lotus ui </dev/null"
check "the first screen is the control page of a disarmed LOTUS" "sudo lotus ui --frame --plain --size 100x30 | grep -q 'REMOVABLE STORAGE' && sudo lotus ui --frame --plain --size 100x30 | grep -q 'CONNECTED 2'"
check "it fits a terminal of 80 by 24" "sudo lotus ui --frame --plain --size 80x24 | grep -q 'APPLY' && [ \"\$(sudo lotus ui --frame --plain --size 80x24 | wc -l)\" = 24 ]"
check "the devices page shows the stick with its ID" "sudo lotus ui --frame --plain --size 100x30 --keys 2 | grep -q 'ALLOWED *QEMU USB HARDDRIVE' && sudo lotus ui --frame --plain --size 100x30 --keys 2 | grep -q $STICK1"
check "someone who is not root gets the screen that says there is no answer" "lotus ui --frame --plain --size 100x30 | grep -q 'NO ANSWER FROM THE LOTUS DAEMON'"

screen=$(ui "space down right right a")
if printf '%s' "$screen" | grep -q 'APPLY POLICY' && printf '%s' "$screen" | grep -q 'QEMU USB HARDDRIVE'; then ok "choosing block and pressing apply lists the stick that would be switched off"; else bad "choosing block and pressing apply lists the stick that would be switched off" "$screen"; fi
check "until the answer is yes nothing has changed" "sudo lotus status | grep -q DISARMED && t auth $STICK1 | grep -qx 1"
screen=$(ui "space down right right a left enter")
if printf '%s' "$screen" | grep -q 'Applied. LOTUS is armed.'; then ok "confirming in the interface arms LOTUS"; else bad "confirming in the interface arms LOTUS" "$screen"; fi
check "and the kernel has switched the stick off" "t wait-auth $STICK1 0 && t wait-no-block $STICK1 && sudo lotus status | grep -q ARMED"
screen=$(ui "2 down t")
if printf '%s' "$screen" | grep -q 'QEMU USB HARDDRIVE is trusted.'; then ok "pressing t on the stick in the devices page trusts it"; else bad "pressing t on the stick in the devices page trusts it" "$screen"; fi
check "and the stick is back with its disk" "t wait-auth $STICK1 1 && t wait-block $STICK1"
check "the trusted page lists it" "sudo lotus ui --frame --plain --size 100x30 --keys 3 | grep -q 'THIS DEVICE' && sudo lotus ui --frame --plain --size 100x30 --keys 3 | grep -q $STICK1"
screen=$(ui "3 n left text:0403 tab text:Serial%20adapters enter")
if printf '%s' "$screen" | grep -q 'Every device from vendor 0403 is trusted.' && printf '%s' "$screen" | grep -q 'Serial adapters'; then ok "a vendor typed into the form is added with its name"; else bad "a vendor typed into the form is added with its name" "$screen"; fi
check "the command line sees the same list" "sudo lotus trusted | grep -q 'WHOLE VENDOR *Serial adapters'"
screen=$(ui "3 n text:printer enter")
if printf '%s' "$screen" | grep -q 'ADD BY HAND' && printf '%s' "$screen" | grep -q 'A model is a vendor ID and a product ID'; then ok "something that is not an ID is refused in the form with a reason"; else bad "something that is not an ID is refused in the form with a reason" "$screen"; fi
ui "3 down x" >/dev/null
ui "3 x" >/dev/null
check "x on the trusted page removes an entry at once, and asks first when a device would go off" "sudo lotus trusted | grep -q $STICK1 && ! sudo lotus trusted | grep -q VID_0403"
ui "3 x left enter" >/dev/null
check "confirmed, the stick is untrusted and off again" "sudo lotus trusted | grep -q 'Nothing is trusted yet' && t wait-auth $STICK1 0"
ui "space down left left a enter" >/dev/null
check "disarming and allowing storage again in the interface puts everything back" "sudo lotus status | grep -q DISARMED && sudo lotus status | grep -Eq 'STORAGE +ALLOW' && t wait-auth $STICK1 1 && t wait-block $STICK1"
check "the activity page has what happened" "sudo lotus ui --frame --plain --size 100x30 --keys 4 | grep -q 'BLOCKED *QEMU USB HARDDRIVE was blocked'"

# The same program at a real terminal: a pseudo terminal with a person's keys.
drive=$(g "python3 tui-drive.py" 2>&1)
printf '%s\n' "$drive" | sed 's/^/    /'
if printf '%s' "$drive" | grep -q '^FAIL' || ! printf '%s' "$drive" | grep -q '^PASS'; then bad "at a real terminal the interface starts, reacts to keys and hands the terminal back"; else ok "at a real terminal the interface starts, reacts to keys and hands the terminal back"; fi

# ---------------------------------------------------------------- storage blocked

section "Storage blocked"
check_not "a change that switches a device off asks first, and without an answer nothing happens" "sudo lotus storage block </dev/null && sudo lotus arm </dev/null"
check "the stick is still running after the refused change" "t auth $STICK1 | grep -qx 1"
check "with --yes the rule is set and LOTUS is armed" "sudo lotus storage block --yes && sudo lotus arm --yes && sudo lotus status | grep -q ARMED"
check "the stick is switched off" "t wait-auth $STICK1 0"
check "its disk is gone" "t wait-no-block $STICK1"
check "the kernel took its configuration away" "t not-configured $STICK1"
check "the keyboard keeps working" "t auth $KBD | grep -qx 1 && t configured $KBD"
check "strict mode is off until it is chosen, so the controllers are still open" "[ \"\$(t defaults)\" = '1 ' ]"
check "switching strict mode on shuts every controller" "sudo lotus strict on && sleep 1 && [ \"\$(t defaults)\" = '0 ' ] && sudo lotus status | grep -q 'REFUSED BEFORE THEY START'"
check "LOTUS shows the stick as blocked and says why" "sudo lotus devices | grep -q 'BLOCKED.*Storage' && sudo lotus devices | grep -q 'removable storage is blocked'"
check "the log has the block" "sudo lotus log 50 | grep -q 'BLOCKED.*QEMU USB HARDDRIVE was blocked'"
check "the journal has it too, with the device and the number 2100" "t journal blocked $STICK1 && t journal blocked '\"LOTUS_EVENT_ID\":\"2100\"' && t journal blocked '\"LOTUS_DEVICE_NAME\":\"QEMU USB HARDDRIVE\"'"
check "as a warning, so a filter on the level finds it" "sudo journalctl -t lotus -p warning..warning --no-pager | grep -q 'was blocked'"
check "the status as JSON counts one blocked device" "t json 'd.armed and d.storage == \"block\" and d.strict and d.strictState == \"on\" and d.blocked == 1'"

section "Strict mode: a new stick never runs"
g "sudo dmesg -C"
plug s2 usb-storage 3 drive=disk2 serial=$STICK2
check "the kernel sees the new stick" "t present $STICK2"
sleep 4
check "it is not switched on" "t auth $STICK2 | grep -qx 0"
check "the kernel says it held the device back" "sudo dmesg | grep -q 'not authorized'"
check_not "no storage driver ever touched it" "sudo dmesg | grep -Eq 'usb-storage|scsi host|Attached SCSI|sd [0-9]'"
check "it never had a disk" "t no-block $STICK2 && t not-configured $STICK2"
check "LOTUS knows what it is all the same" "sudo lotus devices | grep -B1 $STICK2 | grep -q 'BLOCKED.*Storage'"
check "the newest lines of the log have the block" "sudo lotus log 4 | grep -q 'QEMU USB HARDDRIVE was blocked'"

section "Strict mode: a new device the policy allows is switched on"
g "sudo dmesg -C"
plug m1 usb-mouse 4 serial=$MOUSE
check "the mouse is switched on" "t wait-auth $MOUSE 1 5"
check "and gets its driver" "t configured $MOUSE && sudo dmesg | grep -qi 'input:.*mouse'"
g "sudo dmesg | grep -E 'new (full|high|low|super)-speed USB device|authorized to connect|not authorized' | sed 's/^/    /'"

# ---------------------------------------------------------------- trusted only

section "Only trusted devices"
check "the rule is set, and the keyboard and mouse it would spare do not make it ask" "sudo lotus usb trusted </dev/null"
check "keyboard and mouse still work" "t auth $KBD | grep -qx 1 && t auth $MOUSE | grep -qx 1"
plug c1 usb-serial 5 chardev=nowhere always-plugged=on serial=$SERIAL
check "a serial adapter is seen" "t present $SERIAL"
sleep 3
check "and stays off" "t auth $SERIAL | grep -qx 0 && t not-configured $SERIAL"
check "LOTUS says only trusted devices are allowed" "sudo lotus devices | grep -q 'only trusted USB devices are allowed'"

check "trusting the adapter switches it on" "sudo lotus trust \"\$(t id $SERIAL)\" && t wait-auth $SERIAL 1 && t configured $SERIAL"
check "it is listed as trusted" "sudo lotus devices | grep -B1 $SERIAL | grep -q TRUSTED"
check "taking the trust away asks first" "! sudo lotus untrust \"\$(t id $SERIAL)\" </dev/null && t auth $SERIAL | grep -qx 1"
check "and with --yes switches it off again" "sudo lotus untrust \"\$(t id $SERIAL)\" --yes && t wait-auth $SERIAL 0"

section "Trusting a stick"
check "the first stick is trusted and comes back with its disk" "sudo lotus trust \"\$(t id $STICK1)\" && t wait-auth $STICK1 1 && t wait-block $STICK1"
check "the file written earlier is still on it" "b=\$(t block $STICK1) && sudo mount /dev/\$b /mnt && sudo test -f /mnt/written-while-disarmed; r=\$?; sudo umount /mnt; exit \$r"
check "the second stick, same model, stays off" "t auth $STICK2 | grep -qx 0"
unplug s1
check "pulled out, it is gone" "t absent $STICK1"
plug s1 usb-storage 6 drive=disk1 serial=$STICK1
check "in another port it is still the trusted stick" "t wait-auth $STICK1 1 5 && t wait-block $STICK1"
check "trusting the whole model lets the second stick in" "sudo lotus trust \"\$(t id $STICK2)\" --model && t wait-auth $STICK2 1 && t wait-block $STICK2"
check "untrusting the model switches it off again and leaves the first on" "sudo lotus untrust 'VID_46F4&PID_0001' --yes && t wait-auth $STICK2 0 && t auth $STICK1 | grep -qx 1"

# ---------------------------------------------------------------- trust by hand

section "Trust entries added by hand"
check "a vendor that is typed in lets the serial adapter work" "sudo lotus trust vendor 0403 && t wait-auth $SERIAL 1 && t configured $SERIAL"
check "the list shows the entry as a whole vendor" "sudo lotus trusted | grep -A1 'WHOLE VENDOR' | grep -q VID_0403"
check "LOTUS says why the adapter is allowed" "sudo lotus devices | grep -B1 $SERIAL | grep -q TRUSTED"
check "taking the vendor off the list asks first" "! sudo lotus untrust 0403 </dev/null && t auth $SERIAL | grep -qx 1"
check "with --yes the adapter is switched off again" "sudo lotus untrust 0403 --yes && t wait-auth $SERIAL 0"
check "a model that is typed in, with a name, lets the second stick work" "sudo lotus trust model 46f4:0001 --name 'Test sticks' && t wait-auth $STICK2 1 && t wait-block $STICK2"
check "the list shows the name that was given" "sudo lotus trusted | grep -q 'WHOLE MODEL *Test sticks'"
check "untrusting the model switches the second stick off and leaves the first, which is trusted itself" "sudo lotus untrust 46f4:0001 --yes && t wait-auth $STICK2 0 && t auth $STICK1 | grep -qx 1"
check "an instance path that is typed in trusts that one stick" "sudo lotus trust id \"\$(t id $STICK2 | tr A-Z a-z)\" && t wait-auth $STICK2 1"
check "and untrusting it switches the stick off" "sudo lotus untrust \"\$(t id $STICK2)\" --yes && t wait-auth $STICK2 0"
check "a device that is not plugged in can be trusted in advance" "sudo lotus trust id 'USB\\VID_1234&PID_5678\\NOTHERE' && sudo lotus trusted | grep -q NOTHERE && sudo lotus untrust 'USB\\VID_1234&PID_5678\\NOTHERE'"
check_not "something that is not a vendor ID is refused" "sudo lotus trust vendor printer"
check_not "half a model is refused" "sudo lotus trust model 46f4"
check_not "the path of one function of a device is refused" "sudo lotus trust id 'USB\\VID_1234&PID_5678&MI_00\\X'"

# ---------------------------------------------------------------- trust that ends

section "Trust that ends by itself"
check_not "a time that cannot be read is refused" "sudo lotus trust \"\$(t id $STICK2)\" --for 3x"
check "and nothing was trusted" "t auth $STICK2 | grep -qx 0"
check "the second stick is trusted for five minutes and comes on" "sudo lotus trust \"\$(t id $STICK2)\" --for 5m | grep -q 'is trusted until 20' && t wait-auth $STICK2 1 && t wait-block $STICK2"
check "the list shows when the trust ends" "sudo lotus trusted | grep -A1 'QEMU USB HARDDRIVE' | grep $STICK2 | grep -q 'until 20'"
check "the status as JSON counts one entry with an end" "t json 'd.trusted == 2 and d.trustedWithEnd == 1'"
check "the first stick, trusted without an end, has none" "! sudo lotus trusted | grep $STICK1 | grep -q until"
screen=$(ui "3 down e")
if printf '%s' "$screen" | grep -q 'TRUST UNTIL' && printf '%s' "$screen" | grep -q 'Now: until 20'; then ok "the interface opens the form for the end with the time that is set"; else bad "the interface opens the form for the end with the time that is set" "$screen"; fi
screen=$(ui "3 down e text:1m enter")
if printf '%s' "$screen" | grep -q 'ends on 20' && printf '%s' "$screen" | grep -q 'UNTIL 20'; then ok "typing one minute into it shortens the trust"; else bad "typing one minute into it shortens the trust" "$screen"; fi
check "the stick still works while the minute runs" "t auth $STICK2 | grep -qx 1"
echo "    waiting for the minute to pass"
check "when the minute is over the stick is switched off without anyone doing anything" "t wait-auth $STICK2 0 80 && t wait-no-block $STICK2"
check "the entry is off the list and the other one is still there" "! sudo lotus trusted | grep -q $STICK2 && sudo lotus trusted | grep -q $STICK1 && t json 'd.trusted == 1 and d.trustedWithEnd == 0'"
check "the log says the trust ended" "sudo lotus log 20 | grep -q 'TRUST_ENDED.*has ended'"
check "the journal has it with the number 1101" "t journal trust_ended $STICK2 && t journal trust_ended '\"LOTUS_EVENT_ID\":\"1101\"'"
check "the first stick was not touched" "t auth $STICK1 | grep -qx 1"
check "an end can be given to an entry that is on the list, and taken away again" "sudo lotus trust \"\$(t id $STICK1)\" --until 2031-01-01 | grep -q 'now ends on 2031-01-01 23:59' && sudo lotus trust \"\$(t id $STICK1)\" --for never | grep -q 'has no end now' && ! sudo lotus trusted | grep -q until"

# ---------------------------------------------------------------- other kinds

section "Other kinds of device"
check "storage is blocked and other devices are allowed" "sudo lotus usb allow && sudo lotus status | grep -q 'USB DEVICES *ALLOW'"
plug p1 usb-mtp 9 "rootdir=$work/phone" readonly=on serial=$PHONE
check "a phone is seen" "t present $PHONE"
sleep 3
check "a phone counts as storage and stays off" "t auth $PHONE | grep -qx 0 && sudo lotus devices | grep -B1 $PHONE | grep -q 'BLOCKED.*Phone or camera'"
# Whether the machine's kernel has the driver for the emulated network adapter.
# Without it the adapter is still switched on and off, it just never becomes a network interface.
if g "sudo modprobe cdc_ether 2>/dev/null"; then network_driver=yes; else network_driver=no; fi
links_before=$(g "ls /sys/class/net | wc -l")
plug n1 usb-net 10 netdev=net1 serial=$NETWORK
plug a1 usb-audio 11 audiodev=silence serial=$SOUND
plug r1 usb-ccid 12 serial=$CARD
check "a network adapter, a sound card and a card reader are let in while only storage is blocked" "t wait-auth $NETWORK 1 && t wait-auth $SOUND 1 && t wait-auth $CARD 1"
check "LOTUS names what each of them is" "sudo lotus devices | grep -B1 $NETWORK | grep -q Network && sudo lotus devices | grep -B1 $SOUND | grep -q Audio && sudo lotus devices | grep -B1 $CARD | grep -q 'Smart card'"
if [ "$network_driver" = yes ]; then
    check "the network adapter gave the system a new network interface" "sleep 1; [ \"\$(ls /sys/class/net | wc -l)\" -gt $links_before ]"
else
    echo "  (this kernel has no driver for the emulated network adapter, so there is no network interface to count)"
fi
check "under trusted only all three are switched off" "sudo lotus usb trusted --yes && t wait-auth $NETWORK 0 && t wait-auth $SOUND 0 && t wait-auth $CARD 0"
[ "$network_driver" = yes ] && check "the network interface is gone again" "[ \"\$(ls /sys/class/net | wc -l)\" -eq $links_before ]"
check "keyboard and mouse are still on" "t auth $KBD | grep -qx 1 && t auth $MOUSE | grep -qx 1"
unplug n1
g "sudo dmesg -C"
plug n1 usb-net 10 netdev=net1 serial=$NETWORK
sleep 3
check "plugged in again, the network adapter never starts" "t auth $NETWORK | grep -qx 0 && [ \"\$(ls /sys/class/net | wc -l)\" -eq $links_before ] && ! sudo dmesg | grep -Eq 'cdc_ether|rndis|usbnet'"
g "sudo lotus devices | sed 's/^/    /'"
unplug p1
unplug n1
unplug a1
unplug r1

# ---------------------------------------------------------------- hub

section "A hub"
plug h1 usb-hub 7 serial=$HUB
check "the hub is seen" "t present $HUB"
sleep 3
check "an untrusted hub stays off" "t auth $HUB | grep -qx 0"
plug k2 usb-kbd 7.1 serial=$KBD2
sleep 2
check "what is plugged into a hub that is off does not exist for the system" "t absent $KBD2"
check "trusting the hub switches it on" "sudo lotus trust \"\$(t id $HUB)\" && t wait-auth $HUB 1"
check "the keyboard behind it appears and is let in on its own merits" "t present $KBD2 && t wait-auth $KBD2 1"
plug s3 usb-storage 7.2 drive=disk3 serial=$STICK3
check "a stick behind the trusted hub is seen" "t present $STICK3"
sleep 3
check "and is blocked like any other stick" "t auth $STICK3 | grep -qx 0 && t no-block $STICK3"
check "untrusting the hub warns that the keyboard behind it goes too" "sudo lotus untrust \"\$(t id $HUB)\" </dev/null 2>&1 | grep -q 'plugged into a hub that is switched off'"
unplug s3
unplug k2
unplug h1

# ---------------------------------------------------------------- read-only

section "Read-only storage"
check "the rule is set" "sudo lotus usb allow && sudo lotus storage readonly && sudo lotus status | grep -q READ-ONLY"
check "the udev rule is in place, readable like any other rule" "[ \"\$(stat -c '%a %U' /etc/udev/rules.d/99-lotus-readonly.rules)\" = '644 root' ]"
check "udev finds nothing wrong with the rule" "! udevadm verify --help >/dev/null 2>&1 || udevadm verify /etc/udev/rules.d/99-lotus-readonly.rules"
check "both sticks are on" "t wait-auth $STICK1 1 && t wait-auth $STICK2 1"
check "the kernel marks the first disk read-only" "t wait-ro $STICK1 1"
check "and the second" "t wait-ro $STICK2 1"
check "a direct write to the disk is refused" "b=\$(t block $STICK1) && ! sudo dd if=/dev/zero of=/dev/\$b bs=512 count=1 oflag=direct 2>/dev/null"
check "mounted, the files can be read" "b=\$(t block $STICK1) && sudo mount /dev/\$b /mnt 2>/dev/null; sudo test -f /mnt/written-while-disarmed"
check "and nothing can be written" "! sudo touch /mnt/written-while-read-only 2>/dev/null; r=\$?; sudo umount /mnt; exit \$r"
unplug s2
plug s2 usb-storage 3 drive=disk2 serial=$STICK2
check "a stick plugged in now is read-only from the first moment" "t wait-block $STICK2 && t wait-ro $STICK2 1 3"
check "switching read-only off makes the disks writable again" "sudo lotus storage allow && t wait-ro $STICK1 0 && t wait-ro $STICK2 0"
check "the udev rule is gone" "! test -e /etc/udev/rules.d/99-lotus-readonly.rules"
check "writing works" "b=\$(t block $STICK1) && sudo mount /dev/\$b /mnt && sudo touch /mnt/written-after-read-only && sudo umount /mnt"

# ---------------------------------------------------------------- the daemon dies

section "The daemon is killed"
g "sudo lotus storage block --yes >/dev/null; sudo lotus usb trusted --yes >/dev/null"
check "the rules are back: the second stick and the adapter are off" "t wait-auth $STICK2 0 && t wait-auth $SERIAL 0"
g "sudo kill -KILL \$(pidof lotus)"
check "the controllers stay shut while it is down" "[ \"\$(t defaults)\" = '0 ' ]"
check "systemd brings it back" "sleep 3; systemctl is-active --quiet lotus && sudo lotus status | grep -q ARMED"
check "nothing was let through" "t auth $STICK2 | grep -qx 0 && t auth $SERIAL | grep -qx 0 && t auth $STICK1 | grep -qx 1"

section "The daemon is stopped by hand"
g "sudo systemctl stop lotus"
check "stopping it opens nothing" "[ \"\$(t defaults)\" = '0 ' ] && t auth $STICK2 | grep -qx 0"
plug k2 usb-kbd 8 serial=$KBD2
sleep 2
check "a keyboard plugged in meanwhile waits" "t auth $KBD2 | grep -qx 0"
g "sudo systemctl start lotus"
check "and is let in when the daemon is back" "t wait-auth $KBD2 1 10"
unplug k2

# ---------------------------------------------------------------- restart

section "The computer restarts"
g "sudo systemctl reboot" 2>/dev/null
sleep 8
if wait_for_ssh 120; then ok "the machine is back"; else bad "the machine is back" "$(tail -n 20 "$work/console.log")"; fi
check "LOTUS is armed again without anyone doing anything" "sudo lotus status | grep -q ARMED && sudo lotus status | grep -q 'TRUSTED ONLY'"
check "the blocked stick is off" "t wait-auth $STICK2 0 20"
check "the untrusted adapter is off" "t wait-auth $SERIAL 0 20"
check "the trusted stick is on, with its disk" "t wait-auth $STICK1 1 20 && t wait-block $STICK1"
check "keyboard and mouse work" "t auth $KBD | grep -qx 1 && t auth $MOUSE | grep -qx 1"
check "the controllers are shut again" "[ \"\$(t defaults)\" = '0 ' ]"

# ---------------------------------------------------------------- held back at the start

section "The kernel holds devices back at the start"
# With usbcore.authorized_default=2 the kernel switches on nothing that is
# not built in, from its first moment. The daemon then has to do it. A stick
# LOTUS has never seen is put in while the daemon is down, so that at the next
# start nobody but the kernel has had a word about it.
check "with strict mode switched off the controllers are open" "sudo lotus strict off >/dev/null && t wait-defaults 1"
g "sudo systemctl stop lotus"
plug s4 usb-storage 9 drive=disk3 serial=$STICK4
check "a third stick is plugged in while the daemon is down, and runs" "t present $STICK4 && t wait-auth $STICK4 1"
g "echo 'GRUB_CMDLINE_LINUX_DEFAULT=\"\$GRUB_CMDLINE_LINUX_DEFAULT usbcore.authorized_default=2\"' | sudo tee /etc/default/grub.d/99-lotus-test.cfg >/dev/null; sudo update-grub >/dev/null 2>&1"
g "sudo systemctl reboot" 2>/dev/null
sleep 8
if wait_for_ssh 120; then ok "the machine is back"; else bad "the machine is back" "$(tail -n 20 "$work/console.log")"; fi
check "the kernel runs with the setting" "grep -q 'usbcore.authorized_default=2' /proc/cmdline && [ \"\$(cat /sys/module/usbcore/parameters/authorized_default)\" = 2 ]"
check "the kernel held the keyboard back, and LOTUS switched it on" "t wait-auth $KBD 1 30 && t held-at-start $KBD && t switched-on $KBD && t configured $KBD"
check "the same for the mouse and for the trusted stick, which has its disk" "t wait-auth $MOUSE 1 && t wait-auth $STICK1 1 && t held-at-start $STICK1 && t wait-block $STICK1"
check "the third stick, which the policy blocks, was held back and never switched on" "t present $STICK4 && t auth $STICK4 | grep -qx 0 && t held-at-start $STICK4 && ! t switched-on $STICK4 && t no-block $STICK4"
check "LOTUS lists it as blocked" "sudo lotus devices | grep -B1 $STICK4 | grep -q 'BLOCKED.*Storage'"
check "the stick and the adapter that were blocked before are still off" "t auth $STICK2 | grep -qx 0 && t auth $SERIAL | grep -qx 0 && ! t switched-on $STICK2 && ! t switched-on $SERIAL"
check "strict mode is off, so the controllers are open again for what comes later" "[ \"\$(t defaults)\" = '1 ' ]"
check "the log says the kernel held devices back" "sudo lotus log 60 | grep -q 'usbcore.authorized_default=2'"
check "the status says so, as text and as JSON" "sudo lotus status | grep -q 'AT START *DEVICES WAIT FOR LOTUS' && t json 'd.heldAtStart and d.armed'"
plug k2 usb-kbd 8 serial=$KBD2
check "a keyboard plugged in afterwards works" "t wait-auth $KBD2 1 10 && t configured $KBD2"
unplug k2

check "strict mode is switched on again before the second restart" "sudo lotus strict on >/dev/null && t wait-defaults 0"
g "sudo systemctl reboot" 2>/dev/null
sleep 8
if wait_for_ssh 120; then ok "the machine is back a second time, now with strict mode on"; else bad "the machine is back a second time, now with strict mode on" "$(tail -n 20 "$work/console.log")"; fi
check "keyboard, mouse and the trusted stick are let in" "t wait-auth $KBD 1 30 && t wait-auth $MOUSE 1 && t wait-auth $STICK1 1 && t wait-block $STICK1"
check "the blocked sticks and the adapter never ran" "t auth $STICK2 | grep -qx 0 && t auth $STICK4 | grep -qx 0 && t auth $SERIAL | grep -qx 0 && ! t switched-on $STICK2 && ! t switched-on $STICK4 && ! t switched-on $SERIAL"
check "the controllers are shut, as strict mode has them" "[ \"\$(t defaults)\" = '0 ' ] && sudo lotus status | grep -q 'REFUSED BEFORE THEY START'"
unplug s4
g "sudo lotus untrust \"\$(t id $STICK4)\" --yes >/dev/null 2>&1; true"

# ---------------------------------------------------------------- the store

section "The policy store is damaged"
g "sudo cp -a /var/lib/lotus/secure /root/secure-before"
g "sudo rm /var/lib/lotus/secure/policy.dat"
check "a deleted store is put back at once" "sleep 3; sudo test -s /var/lib/lotus/secure/policy.dat && sudo lotus status | grep -q ARMED"
check "the log calls it tampering" "sudo lotus log 30 | grep -q 'TAMPER'"
g "sudo systemctl stop lotus; sudo sh -c 'for f in /var/lib/lotus/secure/policy.dat*; do printf XXXX | dd of=\$f bs=1 seek=200 conv=notrunc 2>/dev/null; done'; sudo systemctl start lotus; sleep 2"
check "a store that was changed locks LOTUS down" "sudo lotus status | grep -q 'LOCKED DOWN'"
check "locked down, the trusted stick is off too" "t wait-auth $STICK1 0"
check_not "and no command gets out of it" "sudo lotus disarm --yes"
check "keyboard and mouse still work" "t auth $KBD | grep -qx 1 && t auth $MOUSE | grep -qx 1"
reset_output=$(g "cd release && sudo ./install.sh" 2>&1)
printf '%s\n' "$reset_output" | sed 's/^/    /'
if printf '%s' "$reset_output" | grep -q 'was reset' && ! printf '%s' "$reset_output" | grep -q 'unchanged'; then ok "running the installer again resets it and says so"; else bad "running the installer again resets it and says so"; fi
check "LOTUS is disarmed and trusts nothing" "sudo lotus status | grep -q DISARMED && sudo lotus status | grep -Eq 'TRUSTED +0'"
check "every device is on again" "t wait-auth $STICK1 1 && t wait-auth $STICK2 1 && t wait-auth $SERIAL 1 && t all-on"
check "the controllers are open" "[ \"\$(t defaults)\" = '1 ' ]"

# ---------------------------------------------------------------- update

section "Updating"
g "sudo lotus storage block --yes >/dev/null && sudo lotus arm --yes >/dev/null"
check "armed, both sticks are off" "t wait-auth $STICK1 0 && t wait-auth $STICK2 0"
update_output=$(g "cd release && sudo ./install.sh" 2>&1)
if printf '%s' "$update_output" | grep -q 'Your policy is unchanged'; then ok "installing over it keeps the policy and says so"; else bad "installing over it keeps the policy and says so" "$update_output"; fi
check "LOTUS is still armed and the sticks are still off" "sudo lotus status | grep -q ARMED && t auth $STICK1 | grep -qx 0 && t auth $STICK2 | grep -qx 0"

# ---------------------------------------------------------------- rescue and removal

section "The way out"
check "release-all disarms and switches everything on" "sudo lotus release-all --yes && t wait-auth $STICK1 1 && t wait-auth $STICK2 1 && t all-on && [ \"\$(t defaults)\" = '1 ' ]"

section "The log"
check_not "in all of this LOTUS never failed to switch a device" "sudo lotus log 1000 | grep -Eq 'Could not (block|switch)'"
g "sudo lotus log 1000 | grep -E 'WARNING|ERROR' | sed 's/^/    /'"

section "Removing"
g "sudo lotus storage readonly >/dev/null; sudo lotus usb trusted --yes >/dev/null; sudo lotus arm --yes >/dev/null"
check "armed with a blocked adapter and read-only disks" "t wait-auth $SERIAL 0 && t wait-ro $STICK1 1"
remove_output=$(g "sudo lotus-uninstall" 2>&1)
printf '%s\n' "$remove_output" | sed 's/^/    /'
# The machine still starts with the kernel setting from the section above.
if printf '%s' "$remove_output" | grep -q 'usbcore.authorized_default=2' && printf '%s' "$remove_output" | grep -q 'before you restart'; then ok "the uninstaller warns that the kernel setting would leave every device off"; else bad "the uninstaller warns that the kernel setting would leave every device off"; fi
check "every device is on" "t wait-auth $SERIAL 1 && t all-on"
check "the controllers are open" "[ \"\$(t defaults)\" = '1 ' ]"
check "the disks are writable again" "t wait-ro $STICK1 0 && t wait-ro $STICK2 0"
check "nothing of LOTUS is left" "! test -e /usr/local/bin/lotus && ! test -e /usr/local/sbin/lotus-uninstall && ! test -e /etc/systemd/system/lotus.service && ! sudo test -e /var/lib/lotus && ! sudo test -e /run/lotus && ! test -e /etc/udev/rules.d/99-lotus-readonly.rules && ! sudo test -e /var/cache/lotus"
check "systemd no longer knows the service" "! systemctl cat lotus >/dev/null 2>&1 && ! pidof lotus"

section "Removing when the daemon cannot help"
g "cd release && sudo ./install.sh >/dev/null 2>&1; sudo lotus strict on >/dev/null; sudo lotus storage readonly >/dev/null; sudo lotus usb trusted --yes >/dev/null; sudo lotus arm --yes >/dev/null"
check "installed again and armed: the adapter is off and the sticks are read-only" "t wait-auth $SERIAL 0 && t wait-ro $STICK1 1 && t wait-ro $STICK2 1 && [ \"\$(t defaults)\" = '0 ' ]"
g "sudo systemctl stop lotus; sudo cp /usr/local/sbin/lotus-uninstall /root/u.sh; sudo rm /usr/local/bin/lotus"
broken_output=$(g "sudo sh /root/u.sh" 2>&1)
printf '%s\n' "$broken_output" | sed 's/^/    /'
check "the uninstaller switches every device on by itself" "t wait-auth $SERIAL 1 && t all-on"
check "and makes the disks writable again" "t wait-ro $STICK1 0 5 && t wait-ro $STICK2 0 5 && b=\$(t block $STICK1) && sudo mount /dev/\$b /mnt && sudo touch /mnt/written-after-removal && sudo umount /mnt"
check "and opens the controllers" "[ \"\$(t defaults)\" = '1 ' ]"
check "nothing of LOTUS is left" "! test -e /usr/local/bin/lotus && ! test -e /etc/systemd/system/lotus.service && ! sudo test -e /var/lib/lotus"
check "the sticks have their disks back" "t wait-block $STICK1 && t wait-block $STICK2"

# ---------------------------------------------------------------- a system on a USB disk

section "A computer that runs from a USB disk"
# The worst mistake LOTUS could make is to block or write-protect the disk the
# system itself runs from. This machine starts from an emulated USB disk.
stop_machine
sleep 2
ROOT=LOTUSROOT1
rm -f "$work/usbroot.qcow2" "$qmp"
qemu-img create -q -f qcow2 -F qcow2 -b "$image" "$work/usbroot.qcow2" 8G
printf 'instance-id: lotus-test-usb\nlocal-hostname: lotus-usb\n' > "$work/meta-data"
cloud-localds "$work/seed2.img" "$work/user-data" "$work/meta-data"
: > "$work/console.log"
qemu-system-x86_64 -name lotus-test-usb -machine "q35,accel=$accel" -cpu "$cpu" -smp 2 -m 2048 \
    -display none -serial "file:$work/console.log" \
    -drive "if=virtio,format=raw,file=$work/seed2.img" \
    -netdev "user,id=net0,hostfwd=tcp:127.0.0.1:$port-:22" -device virtio-net-pci,netdev=net0 \
    -device qemu-xhci,id=xhci,p2=12,p3=4 \
    -blockdev "driver=file,filename=$work/usbroot.qcow2,node-name=rootfile" -blockdev driver=qcow2,file=rootfile,node-name=rootdisk \
    -device "usb-storage,bus=xhci.0,port=1,drive=rootdisk,serial=$ROOT,bootindex=1" \
    -blockdev "driver=file,filename=$work/stick1.img,node-name=file1" -blockdev driver=raw,file=file1,node-name=disk1 \
    -qmp "unix:$qmp,server=on,wait=off" -pidfile "$work/qemu.pid" -daemonize \
    || bad "QEMU started the second machine"

if wait_for_ssh 300; then
    ok "the machine starts from the USB disk"
    g "cloud-init status --wait >/dev/null 2>&1"
    copy_in
    check "its root filesystem is on the USB disk" "b=\$(t block $ROOT) && findmnt -n -o SOURCE / | grep -q \"^/dev/\$b\""
    check_not "the installer refuses an option it does not know" "cd release && sudo ./install.sh --everything"
    check "and has installed nothing" "! test -e /usr/local/bin/lotus"
    g "cd release && sudo ./install.sh --trust-connected" 2>&1 | sed 's/^/    /'
    check "asked to, the installer put the connected devices on the trusted list" "sudo lotus trusted | grep -q $ROOT"
    check "and left everything else as on a new installation" "sudo lotus status | grep -q DISARMED && sudo lotus status | grep -Eq 'STORAGE +ALLOW' && sudo lotus status | grep -Eq 'USB DEVICES +ALLOW'"
    check "LOTUS says the system runs from that disk" "sudo lotus devices | grep -A1 'QEMU USB HARDDRIVE' | grep -q 'the system runs from it'"
    plug s1 usb-storage 2 drive=disk1 serial=$STICK1
    check "a stick is plugged in next to it" "t present $STICK1 && t wait-block $STICK1"

    lost=$(g "sudo lotus strict on >/dev/null; sudo lotus storage block --yes >/dev/null; sudo lotus usb trusted --yes >/dev/null; sudo lotus arm --yes" 2>&1)
    printf '%s\n' "$lost" | sed 's/^/    /'
    if [ "$(printf '%s\n' "$lost" | grep -c 'QEMU USB HARDDRIVE')" = 1 ]; then
        ok "arming with everything blocked lists one disk to switch off, the stick"
    else
        bad "arming with everything blocked lists one disk to switch off, the stick" "$lost"
    fi
    check "the stick is switched off" "t wait-auth $STICK1 0"
    check "the stick was not connected when LOTUS was installed, so it is not on the trusted list" "! sudo lotus trusted | grep -q $STICK1"
    check "the system's disk stays on and the system keeps running" "sleep 3; t auth $ROOT | grep -qx 1 && sudo touch /var/tmp/written-while-armed"
    check "LOTUS explains why it leaves that disk alone" "sudo lotus devices | grep -A1 'QEMU USB HARDDRIVE' | grep -q 'the system runs from it'"

    check "read-only mode is switched on" "sudo lotus usb allow && sudo lotus storage readonly && sudo lotus status | grep -q READ-ONLY"
    check "the stick is read-only" "t wait-auth $STICK1 1 && t wait-ro $STICK1 1"
    check "the rule names the system's disk as the exception" "grep -q 'ATTRS{serial}==\"$ROOT\"' /etc/udev/rules.d/99-lotus-readonly.rules"
    check "udev finds nothing wrong with the rule" "! udevadm verify --help >/dev/null 2>&1 || udevadm verify /etc/udev/rules.d/99-lotus-readonly.rules"
    check "the system's disk is still writable" "[ \"\$(cat /sys/block/\$(t block $ROOT)/ro)\" = 0 ] && sudo touch /var/tmp/written-while-read-only"
    g "sudo udevadm trigger --action=change --subsystem-match=block; sudo udevadm settle"
    check "it stays writable when udev goes over every disk again" "[ \"\$(cat /sys/block/\$(t block $ROOT)/ro)\" = 0 ] && sudo touch /var/tmp/written-after-trigger && t wait-ro $STICK1 1 3"
    check "none of its partitions is read-only either" "! grep -qx 1 /sys/block/\$(t block $ROOT)/*/ro"

    g "sudo systemctl reboot" 2>/dev/null
    sleep 8
    if wait_for_ssh 240; then ok "with read-only mode on, the machine starts again from its USB disk"; else bad "with read-only mode on, the machine starts again from its USB disk" "$(tail -n 20 "$work/console.log")"; fi
    check "the system's disk is writable after the restart" "[ \"\$(cat /sys/block/\$(t block $ROOT)/ro)\" = 0 ] && sudo touch /var/tmp/written-after-restart && findmnt -n -o OPTIONS / | tr ',' '\n' | grep -qx rw"
    check "the stick is read-only after the restart" "t wait-ro $STICK1 1 20"
    check "LOTUS is armed" "sudo lotus status | grep -q ARMED"

    g "sudo lotus-uninstall" 2>&1 | sed 's/^/    /'
    check "after removal the stick is writable and nothing of LOTUS is left" "t wait-ro $STICK1 0 && ! test -e /usr/local/bin/lotus && ! sudo test -e /var/lib/lotus"
else
    bad "the machine starts from the USB disk" "$(tail -n 30 "$work/console.log")"
fi

# ---------------------------------------------------------------- result

section "Result"
printf '  %d passed, %d failed\n' "$passed" "$failed"
if [ "$failed" -gt 0 ]; then
    printf '  Failed:\n'
    for name in "${failures[@]}"; do printf '    %s\n' "$name"; done
    exit 1
fi
exit 0
