#!/usr/bin/env python3
"""Sends commands to a QEMU machine over its QMP socket. Used by vm-test.sh to
plug emulated USB devices into the test machine and pull them out again.

  qmp.py SOCKET plug ID DRIVER PORT [property=value ...]
  qmp.py SOCKET unplug ID
"""
import json
import socket
import sys
import time


class Qmp:
    def __init__(self, path):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(15)
        self.sock.connect(path)
        self.file = self.sock.makefile("rw", encoding="utf-8", newline="\n")
        self.read()  # the greeting
        self.command("qmp_capabilities")

    def read(self):
        line = self.file.readline()
        if not line:
            raise RuntimeError("QEMU closed the connection")
        return json.loads(line)

    def command(self, name, **arguments):
        message = {"execute": name}
        if arguments:
            message["arguments"] = arguments
        self.file.write(json.dumps(message) + "\n")
        self.file.flush()
        while True:
            answer = self.read()
            if "error" in answer:
                raise RuntimeError("%s: %s" % (name, answer["error"].get("desc", answer["error"])))
            if "return" in answer:
                return answer["return"]
            # anything else is an event

    def wait_for_event(self, name, device, seconds=15):
        deadline = time.time() + seconds
        while time.time() < deadline:
            try:
                answer = self.read()
            except socket.timeout:
                break
            if answer.get("event") == name and answer.get("data", {}).get("device") == device:
                return True
        return False


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    qmp = Qmp(sys.argv[1])
    action, device = sys.argv[2], sys.argv[3]
    if action == "plug":
        arguments = {"driver": sys.argv[4], "id": device, "bus": "xhci.0", "port": sys.argv[5]}
        for pair in sys.argv[6:]:
            key, _, value = pair.partition("=")
            arguments[key] = value
        qmp.command("device_add", **arguments)
    elif action == "unplug":
        qmp.command("device_del", id=device)
        if not qmp.wait_for_event("DEVICE_DELETED", device):
            sys.exit("QEMU did not confirm that %s is gone" % device)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError) as error:
        sys.exit("qmp: %s" % error)
