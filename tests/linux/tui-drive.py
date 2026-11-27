#!/usr/bin/env python3
"""Runs "lotus" on a pseudo terminal inside the test machine, the way a person
at a terminal would: it starts the interactive interface, presses a few keys
and checks what was drawn and that the terminal was handed back in order.

Prints one line per check and exits with 1 when one of them fails.
"""
import fcntl
import os
import pty
import re
import select
import struct
import sys
import termios
import time


def main():
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["LANG"] = "C.UTF-8"
        # Another command line can be given, for a run against a developer daemon.
        command = sys.argv[1:] or ["sudo", "--preserve-env=TERM,LANG", "lotus"]
        os.execvp(command[0], command)

    # 30 rows by 100 columns, before the program asks.
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 30, 100, 0, 0))
    seen = bytearray()

    def pump(seconds):
        end = time.time() + seconds
        while time.time() < end:
            ready, _, _ = select.select([fd], [], [], 0.1)
            if fd not in ready:
                continue
            try:
                data = os.read(fd, 65536)
            except OSError:
                return
            if not data:
                return
            seen.extend(data)

    def plain(raw):
        text = raw.decode("utf-8", "replace")
        return re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", text)

    pump(4)
    first = plain(bytes(seen))
    os.write(fd, b"2")
    pump(2)
    second = plain(bytes(seen))[len(first):]
    os.write(fd, b"5")
    pump(1.5)
    third = plain(bytes(seen))[len(first) + len(second):]
    os.write(fd, b"q")

    status = None
    for _ in range(80):
        pump(0.1)
        done, code = os.waitpid(pid, os.WNOHANG)
        if done:
            status = code
            break
        # Once the program has let go of the terminal, reading returns at once. Give it time to end.
        time.sleep(0.1)
    if status is None:
        os.kill(pid, 9)

    raw = bytes(seen).decode("utf-8", "replace")
    checks = [
        ("it switches to its own screen and hides the cursor", "\x1b[?1049h" in raw and "\x1b[?25l" in raw),
        ("the first screen is the control page", "CONTROL" in first and "REMOVABLE STORAGE" in first and "MASTER SWITCH" in first),
        ("it draws in colour", "\x1b[0;1;38;2;" in raw or "\x1b[0;38;2;" in raw),
        ("it counts the connected devices", re.search(r"CONNECTED \d", first) is not None),
        ("the key 2 opens the devices page", "What is plugged in" in second),
        ("the key 5 opens the about page", "without warranty" in third or "WHAT IT DOES NOT DO" in third),
        ("q ends it with exit code 0", status is not None and os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0),
        ("the terminal gets its own screen and its cursor back", raw.rstrip().endswith("\x1b[?1049l") and "\x1b[?25h" in raw[-40:]),
    ]
    if status is None:
        print("      the program was still running 8 seconds after q")
    elif not (os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0):
        print("      wait status %d: %s" % (status, "exit code %d" % os.WEXITSTATUS(status) if os.WIFEXITED(status)
                                              else "ended by signal %d" % os.WTERMSIG(status)))
    failed = 0
    for what, passed in checks:
        print(("PASS  " if passed else "FAIL  ") + what)
        failed += 0 if passed else 1
    if failed:
        print("--- the first screen as text ---")
        print(first[-2500:])
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
