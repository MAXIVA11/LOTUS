#!/bin/bash
# Runs inside the test machine as "t". Answers questions about a USB device,
# which is named by the serial number the test gave it.
root=/sys/bus/usb/devices

name() {
    local dir base
    for dir in "$root"/*; do
        base=$(basename "$dir")
        case "$base" in usb*|*:*) continue ;; esac
        if [ -f "$dir/serial" ] && [ "$(cat "$dir/serial" 2>/dev/null)" = "$1" ]; then
            echo "$base"
            return 0
        fi
    done
    return 1
}

block() {
    local n
    n=$(name "$1") || return 1
    ls -d "$root/$n/$n":*/host*/target*/*/block/* 2>/dev/null | head -n 1 | xargs -r basename | grep .
}

until_true() {
    local seconds=$1
    shift
    local end=$((SECONDS + seconds))
    while [ "$SECONDS" -lt "$end" ]; do
        "$@" >/dev/null 2>&1 && return 0
        sleep 0.1
    done
    "$@" >/dev/null 2>&1
}

auth_is() {
    local n
    n=$(name "$1") || return 1
    [ "$(cat "$root/$n/authorized" 2>/dev/null)" = "$2" ]
}

configured() {
    local n
    n=$(name "$1") || return 1
    ls -d "$root/$n/$n":* >/dev/null 2>&1
}

not_configured() {
    local n
    n=$(name "$1") || return 1
    ! ls -d "$root/$n/$n":* >/dev/null 2>&1
}

no_block() {
    ! block "$1" >/dev/null
}

read_only_is() {
    local b
    b=$(block "$1") || return 1
    [ "$(cat "/sys/block/$b/ro")" = "$2" ]
}

defaults_are() {
    [ "$(cat "$root"/usb*/authorized_default | sort -u | tr '\n' ' ')" = "$1 " ]
}

case "${1:-}" in
    name) name "$2" ;;
    present) until_true "${3:-8}" name "$2" ;;
    absent) ! name "$2" >/dev/null ;;
    auth) n=$(name "$2") || exit 2; cat "$root/$n/authorized" ;;
    wait-auth) until_true "${4:-8}" auth_is "$2" "$3" ;;
    block) block "$2" ;;
    wait-block) until_true "${3:-10}" block "$2" ;;
    no-block) ! block "$2" >/dev/null ;;
    wait-no-block) until_true "${3:-10}" no_block "$2" ;;
    configured) until_true "${3:-8}" configured "$2" ;;
    not-configured) not_configured "$2" ;;
    wait-ro) until_true "${4:-15}" read_only_is "$2" "$3" ;;
    # The ID LOTUS gives the device: model, then the serial number in capitals.
    id)
        n=$(name "$2") || exit 2
        printf 'USB\\VID_%s&PID_%s\\%s\n' "$(tr a-f A-F < "$root/$n/idVendor")" "$(tr a-f A-F < "$root/$n/idProduct")" "$2"
        ;;
    defaults) cat "$root"/usb*/authorized_default | sort -u | tr '\n' ' ' ;;
    # t wait-defaults 1: every controller has this setting, now or within a few seconds.
    wait-defaults) until_true "${3:-8}" defaults_are "$2" ;;
    # t json 'd.armed and d.trusted == 1': something that must hold for what "lotus status --json" prints.
    json)
        sudo lotus status --json | python3 -c 'import json, sys, types
d = types.SimpleNamespace(**json.load(sys.stdin))
sys.exit(0 if eval(sys.argv[1]) else 1)' "$2"
        ;;
    # t journal blocked [text]: the journal holds an entry for this kind of event, with the text in it.
    journal)
        sudo journalctl -t lotus -o json --no-pager "LOTUS_EVENT=$2" | grep -F -- "${3:-LOTUS_EVENT}" | grep -q .
        ;;
    # Takes the machine's own keyboard and mouse away, the ones on the PS/2 ports
    # that every PC has, so that the USB ones are all there is.
    no-ps2)
        for driver in atkbd psmouse; do
            for device in /sys/bus/serio/drivers/$driver/serio*; do
                [ -e "$device" ] && echo -n "$(basename "$device")" | sudo tee "/sys/bus/serio/drivers/$driver/unbind" >/dev/null
            done
        done
        sleep 1
        ! grep -Eq 'AT Translated|PS/2' /proc/bus/input/devices
        ;;
    # What the kernel wrote about a device since the computer started.
    held-at-start) n=$(name "$2") || exit 2; sudo dmesg | grep -q "usb $n: Device is not authorized for usage" ;;
    switched-on) n=$(name "$2") || exit 2; sudo dmesg | grep -q "usb $n: authorized to connect" ;;
    all-on)
        for file in "$root"/*/authorized; do
            [ "$(cat "$file")" = 1 ] || { echo "$file is off"; exit 1; }
        done
        ;;
    *) echo "unknown question: ${1:-}" >&2; exit 64 ;;
esac
