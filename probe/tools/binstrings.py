"""Extract printable ASCII and UTF-16LE strings from a binary and filter by regex.

usage: binstrings.py <file> <regex> [--min N] [--max-len N] [--limit N] [--count-only]
"""
import mmap
import re
import sys


def main():
    args = sys.argv[1:]
    path, pattern = args[0], args[1]
    opts = {"--min": 4, "--max-len": 200, "--limit": 100000}
    count_only = "--count-only" in args
    for key in list(opts):
        if key in args:
            opts[key] = int(args[args.index(key) + 1])
    rx = re.compile(pattern)
    n = opts["--min"]
    ascii_rx = re.compile(rb"[\x20-\x7e]{%d,}" % n)
    wide_rx = re.compile(rb"(?:[\x20-\x7e]\x00){%d,}" % n)
    seen = set()
    out = []
    with open(path, "rb") as f:
        data = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        for m in ascii_rx.finditer(data):
            s = m.group().decode("ascii")
            if rx.search(s) and s not in seen:
                seen.add(s)
                out.append(s)
        for m in wide_rx.finditer(data):
            s = m.group().decode("utf-16le")
            if rx.search(s) and s not in seen:
                seen.add(s)
                out.append("[w] " + s)
    if count_only:
        print(len(out))
        return
    for s in out[: opts["--limit"]]:
        print(s[: opts["--max-len"]])
    if len(out) > opts["--limit"]:
        print("... (%d more)" % (len(out) - opts["--limit"]))


if __name__ == "__main__":
    main()
