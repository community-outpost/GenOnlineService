#!/usr/bin/env python3
# Refreshes GenOnlineService/data/oui.tsv, the MAC vendor registry of the staff pages, from the public IEEE
# Registration Authority listings: MA-L (24 bit blocks), MA-M (28 bit) and MA-S (36 bit).
# Usage: python3 scripts/update-oui.py [output path]
# The first lines of the output name the sources and the retrieval date; commit the file with that date.

import csv
import datetime
import io
import os
import sys
import urllib.request

SOURCES = [
    ("MA-L", "https://standards-oui.ieee.org/oui/oui.csv"),
    ("MA-M", "https://standards-oui.ieee.org/oui28/mam.csv"),
    ("MA-S", "https://standards-oui.ieee.org/oui36/oui36.csv"),
]

LENGTHS = {"MA-L": 6, "MA-M": 7, "MA-S": 9}


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "genonline-oui-update"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read().decode("utf-8", errors="replace")


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    output = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "GenOnlineService", "data", "oui.tsv")
    vendors = {}
    for registry, url in SOURCES:
        rows = csv.DictReader(io.StringIO(fetch(url)))
        for row in rows:
            prefix = (row.get("Assignment") or "").strip().upper()
            name = " ".join((row.get("Organization Name") or "").split())
            if len(prefix) == LENGTHS[registry] and all(c in "0123456789ABCDEF" for c in prefix) and name:
                vendors[prefix] = name
    if len(vendors) < 10000:
        sys.exit("only %d blocks read, the listings look incomplete; nothing written" % len(vendors))

    today = datetime.date.today().isoformat()
    with open(output, "w", encoding="utf-8", newline="\n") as out:
        out.write("# IEEE Registration Authority public listings MA-L, MA-M and MA-S, retrieved %s\n" % today)
        for registry, url in SOURCES:
            out.write("# %s %s\n" % (registry, url))
        out.write("# <hex prefix><tab><organization name>, refreshed with scripts/update-oui.py\n")
        for prefix in sorted(vendors):
            out.write("%s\t%s\n" % (prefix, vendors[prefix]))
    print("wrote %d blocks to %s" % (len(vendors), output))


if __name__ == "__main__":
    main()
