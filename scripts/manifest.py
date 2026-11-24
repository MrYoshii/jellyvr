#!/usr/bin/env python3
"""Generate meta.json (inside the plugin zip) and manifest.json (Jellyfin plugin repository)."""
import argparse
import json
import os
import sys
from datetime import datetime, timezone

GUID = "9a2a70e4-4266-4a9e-b694-ea6036068502"
NAME = "DeoVR"
DESCRIPTION = "Watch Jellyfin in DeoVR: 2D and 3D (SBS/TB) movies on a cinema screen, VR180/360 videos."
OVERVIEW = "Serves the Jellyfin library as a DeoVR JSON feed with login, per-user feed links and signed direct-play streams."
CATEGORY = "General"
MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "manifest.json")


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def meta(a):
    json.dump({
        "category": CATEGORY,
        "changelog": "",
        "description": DESCRIPTION,
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "owner": a.owner,
        "targetAbi": a.abi,
        "timestamp": now(),
        "version": a.version,
        "status": "Active",
        "autoUpdate": True,
        "assemblies": [],
    }, sys.stdout, indent=2)


def update(a):
    data = []
    if os.path.exists(MANIFEST):
        with open(MANIFEST, encoding="utf-8") as f:
            data = json.load(f) or []

    plugin = next((p for p in data if p.get("guid") == GUID), None)
    if plugin is None:
        plugin = {"guid": GUID, "versions": []}
        data.append(plugin)

    plugin.update({
        "name": NAME,
        "description": DESCRIPTION,
        "overview": OVERVIEW,
        "owner": a.owner,
        "category": CATEGORY,
        "imageUrl": "",
    })

    versions = [v for v in plugin.get("versions", []) if v.get("version") != a.version]
    versions.insert(0, {
        "version": a.version,
        "changelog": a.changelog,
        "targetAbi": a.abi,
        "sourceUrl": a.url,
        "checksum": a.md5,
        "timestamp": now(),
    })
    plugin["versions"] = versions

    with open(MANIFEST, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)

    m = sub.add_parser("meta")
    m.add_argument("--version", required=True)
    m.add_argument("--abi", required=True)
    m.add_argument("--owner", default=os.environ.get("GITHUB_REPOSITORY_OWNER", ""))

    u = sub.add_parser("update")
    for k in ("version", "abi", "url", "md5"):
        u.add_argument("--" + k, required=True)
    u.add_argument("--changelog", default="")
    u.add_argument("--owner", default=os.environ.get("GITHUB_REPOSITORY_OWNER", ""))

    a = ap.parse_args()
    meta(a) if a.cmd == "meta" else update(a)


if __name__ == "__main__":
    main()
