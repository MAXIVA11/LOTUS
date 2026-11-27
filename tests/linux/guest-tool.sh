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
    all-on)
        for file in "$root"/*/authorized; do
            [ "$(cat "$file")" = 1 ] || { echo "$file is off"; exit 1; }
        done
        ;;
    *) echo "unknown question: ${1:-}" >&2; exit 64 ;;
esac
