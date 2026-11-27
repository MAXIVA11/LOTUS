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
    -chardev null,id=nowhere \
    -qmp "unix:$qmp,server=on,wait=off" -pidfile "$work/qemu.pid" -daemonize \
    || { echo "QEMU did not start."; exit 2; }

if ! wait_for_ssh 240; then
    echo "The machine did not come up. The end of its console:"
    tail -n 40 "$work/console.log"
    exit 2
fi
g "cloud-init status --wait >/dev/null 2>&1; uname -r; . /etc/os-release; echo \$PRETTY_NAME"

scp -q "${ssh_options[@]/-p/-P}" -r "$package" lotus@127.0.0.1:release 2>/dev/null \
    || scp -q -i "$work/key" -P "$port" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR \
        -r "$package" lotus@127.0.0.1:release
scp -q -i "$work/key" -P "$port" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR \
    "$here/guest-tool.sh" lotus@127.0.0.1:t
g "sudo install -m 0755 t /usr/local/bin/t"

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
KBD=LOTUSKBD1
MOUSE=LOTUSMOUSE1
SERIAL=LOTUSSERIAL1
HUB=LOTUSHUB1
KBD2=LOTUSKBD2

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

# ---------------------------------------------------------------- storage blocked

section "Storage blocked"
check_not "a change that switches a device off asks first, and without an answer nothing happens" "sudo lotus storage block </dev/null && sudo lotus arm </dev/null"
check "the stick is still running after the refused change" "t auth $STICK1 | grep -qx 1"
check "with --yes the rule is set and LOTUS is armed" "sudo lotus storage block --yes && sudo lotus arm --yes && sudo lotus status | grep -q ARMED"
check "the stick is switched off" "t wait-auth $STICK1 0"
check "its disk is gone" "t wait-no-block $STICK1"
check "the kernel took its configuration away" "t not-configured $STICK1"
check "the keyboard keeps working" "t auth $KBD | grep -qx 1 && t configured $KBD"
check "every controller now holds new devices back" "[ \"\$(t defaults)\" = '0 ' ]"
check "LOTUS shows the stick as blocked and says why" "sudo lotus devices | grep -q 'BLOCKED.*Storage' && sudo lotus devices | grep -q 'removable storage is blocked'"
check "the log has the block" "sudo lotus log 50 | grep -q 'BLOCKED.*QEMU USB HARDDRIVE was blocked'"

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
check "the log has the block" "sudo lotus log 50 | grep -c 'was blocked' | grep -qx 2"

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
plug c1 usb-serial 5 chardev=nowhere serial=$SERIAL
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
check "the udev rule is in place" "test -f /etc/udev/rules.d/99-lotus-readonly.rules"
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
g "sudo systemctl kill -s KILL lotus"
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
if printf '%s' "$reset_output" | grep -q 'was reset'; then ok "running the installer again resets it and says so"; else bad "running the installer again resets it and says so"; fi
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

section "Removing"
g "sudo lotus storage readonly >/dev/null; sudo lotus usb trusted --yes >/dev/null; sudo lotus arm --yes >/dev/null"
check "armed with a blocked adapter and read-only disks" "t wait-auth $SERIAL 0 && t wait-ro $STICK1 1"
remove_output=$(g "sudo lotus-uninstall" 2>&1)
printf '%s\n' "$remove_output" | sed 's/^/    /'
check "every device is on" "t wait-auth $SERIAL 1 && t all-on"
check "the controllers are open" "[ \"\$(t defaults)\" = '1 ' ]"
check "the disks are writable again" "t wait-ro $STICK1 0 && t wait-ro $STICK2 0"
check "nothing of LOTUS is left" "! test -e /usr/local/bin/lotus && ! test -e /usr/local/sbin/lotus-uninstall && ! test -e /etc/systemd/system/lotus.service && ! sudo test -e /var/lib/lotus && ! sudo test -e /run/lotus && ! test -e /etc/udev/rules.d/99-lotus-readonly.rules && ! sudo test -e /var/cache/lotus"
check "systemd no longer knows the service" "! systemctl cat lotus >/dev/null 2>&1 && ! pidof lotus"

section "Removing when the daemon cannot help"
g "cd release && sudo ./install.sh >/dev/null 2>&1; sudo lotus storage block --yes >/dev/null; sudo lotus arm --yes >/dev/null"
check "installed again and armed, the sticks are off" "t wait-auth $STICK1 0 && t wait-auth $STICK2 0 && [ \"\$(t defaults)\" = '0 ' ]"
g "sudo systemctl stop lotus; sudo cp /usr/local/sbin/lotus-uninstall /root/u.sh; sudo rm /usr/local/bin/lotus"
broken_output=$(g "sudo sh /root/u.sh" 2>&1)
printf '%s\n' "$broken_output" | sed 's/^/    /'
check "the uninstaller switches every device on by itself" "t wait-auth $STICK1 1 && t wait-auth $STICK2 1 && t all-on"
check "and opens the controllers" "[ \"\$(t defaults)\" = '1 ' ]"
check "nothing of LOTUS is left" "! test -e /usr/local/bin/lotus && ! test -e /etc/systemd/system/lotus.service && ! sudo test -e /var/lib/lotus"
check "the sticks have their disks back" "t wait-block $STICK1 && t wait-block $STICK2"

# ---------------------------------------------------------------- result

section "Result"
printf '  %d passed, %d failed\n' "$passed" "$failed"
if [ "$failed" -gt 0 ]; then
    printf '  Failed:\n'
    for name in "${failures[@]}"; do printf '    %s\n' "$name"; done
    exit 1
fi
exit 0
