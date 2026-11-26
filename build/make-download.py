"""Puts a built installer on the site: site/download/Replio-Setup.exe + site/download/version.json.

The app reads version.json (https://replio.ru/download/version.json) at start and once a day; a newer "version"
shows the banner «Доступна версия X». The site's «Скачать для Windows» button links to Replio-Setup.exe.

Usage (from D:\\AI\\CuePilot\\Replio):
    python build\\make-download.py build\\installer\\Replio-Setup-1.1.0.exe --notes-file notes.md [--mandatory]
    python build\\make-download.py <setup.exe> --site <other site folder>   (a test copy)
    python build\\make-download.py <setup.exe> --mac-version 1.1.4   (Replio-mac-arm64.dmg and -x64.dmg are already
        in site\\download: the Mac app reads the "mac" block and offers them; without the flag an existing block is kept)
Then upload the site with tools\\vylozhit_sayt.py (Arteom runs it).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
from datetime import date


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("setup")
    parser.add_argument("--site", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "site"))
    parser.add_argument("--version", help="defaults to the number in the file name")
    parser.add_argument("--notes", default="")
    parser.add_argument("--notes-file")
    parser.add_argument("--mandatory", action="store_true")
    parser.add_argument("--mac-version", help="version of the .dmg files in site/download (adds the mac block)")
    args = parser.parse_args()

    version = args.version
    if not version:
        match = re.search(r"(\d+\.\d+\.\d+)", os.path.basename(args.setup))
        if not match:
            sys.exit("No version in the file name; pass --version.")
        version = match.group(1)

    notes = args.notes
    if args.notes_file:
        with open(args.notes_file, encoding="utf-8") as handle:
            notes = handle.read().strip()

    folder = os.path.join(os.path.abspath(args.site), "download")
    os.makedirs(folder, exist_ok=True)
    previous_mac = None
    try:
        with open(os.path.join(folder, "version.json"), encoding="utf-8") as handle:
            previous_mac = json.load(handle).get("mac")
    except (OSError, ValueError):
        pass
    target = os.path.join(folder, "Replio-Setup.exe")
    shutil.copyfile(args.setup, target)

    digest = hashlib.sha256()
    with open(target, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)

    info = {
        "version": version,
        # Relative: read next to version.json, so a local test server works the same way.
        "url": "Replio-Setup.exe",
        "sha256": digest.hexdigest(),
        "size": os.path.getsize(target),
        "notes": notes,
        "mandatory": bool(args.mandatory),
        "published": date.today().isoformat(),
    }
    if args.mac_version:
        for name in ("Replio-mac-arm64.dmg", "Replio-mac-x64.dmg"):
            if not os.path.exists(os.path.join(folder, name)):
                sys.exit(f"{name} is not in {folder}: put the Mac builds there first.")
        info["mac"] = {"version": args.mac_version, "arm64": "Replio-mac-arm64.dmg", "x64": "Replio-mac-x64.dmg", "notes": notes}
    elif previous_mac:
        info["mac"] = previous_mac
    with open(os.path.join(folder, "version.json"), "w", encoding="utf-8") as handle:
        json.dump(info, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"[download] {target} ({info['size']:,} B), version {version}, sha256 {info['sha256'][:12]}…")


if __name__ == "__main__":
    main()
