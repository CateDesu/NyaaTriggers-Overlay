#!/usr/bin/env python3
"""Build pluginmaster.json, the file Dalamud reads to offer this plugin.

Dalamud's custom-repository format is a JSON array of plugin entries. Almost
every field is already in the manifest DalamudPackager writes at build time, so
this reads that and adds only what the manifest cannot know: where the release
lives and when it was cut.

    python tools/make_pluginmaster.py \
        --manifest NyaaTriggers.Plugin/bin/Release/NyaaTriggers.json \
        --repo CateDesu/NyaaTriggers-Overlay \
        --tag v0.1.0.0 \
        --out pluginmaster.json

Deliberately stable-channel only. A testing channel needs TestingAssemblyVersion
and DownloadLinkTesting to sit alongside the stable keys in the *same* entry,
which means merging with whatever is already published rather than rewriting it.
Not worth the machinery until there is something to test.
"""
import argparse
import json
import sys
import time
from pathlib import Path

# Written by us, not by DalamudPackager, and not carried over from the manifest.
GENERATED_KEYS = (
    "DownloadLinkInstall",
    "DownloadLinkUpdate",
    "DownloadLinkTesting",
    "LastUpdate",
    "IsHide",
)


def build_entry(manifest: dict, repo: str, tag: str, changelog: str) -> dict:
    version = manifest.get("AssemblyVersion")
    if not version:
        raise SystemExit("manifest has no AssemblyVersion; was the plugin built?")

    # The tag is the release, and the release is where the zip lives. They must
    # agree or the entry advertises a version whose download is a different one.
    if tag.lstrip("v") != version:
        raise SystemExit(
            f"tag {tag!r} and AssemblyVersion {version!r} disagree. "
            "Bump <Version> in the csproj to match the tag, or retag.")

    asset = f"https://github.com/{repo}/releases/download/{tag}/latest.zip"

    entry = dict(manifest)
    entry["IsHide"] = False
    entry["LastUpdate"] = int(time.time())
    # Install and update are the same artifact; Dalamud asks for them separately
    # so a repo *can* serve a different file for a fresh install.
    entry["DownloadLinkInstall"] = asset
    entry["DownloadLinkUpdate"] = asset
    entry["DownloadLinkTesting"] = asset
    if changelog:
        entry["Changelog"] = changelog
    return entry


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True,
                        help="the built NyaaTriggers.json")
    parser.add_argument("--repo", required=True, help="owner/name")
    parser.add_argument("--tag", required=True, help="release tag, e.g. v0.1.0.0")
    parser.add_argument("--changelog", default="", help="optional release notes")
    parser.add_argument("--out", default="pluginmaster.json")
    args = parser.parse_args()

    manifest_path = Path(args.manifest)
    if not manifest_path.is_file():
        raise SystemExit(f"no manifest at {manifest_path}")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entry = build_entry(manifest, args.repo, args.tag, args.changelog)

    # One plugin per repo, so the array always has exactly one entry. Written
    # sorted and indented so a version bump is a readable diff.
    out = Path(args.out)
    out.write_text(json.dumps([entry], indent=2, sort_keys=True) + "\n",
                   encoding="utf-8")

    print(f"wrote {out} for {entry['InternalName']} {entry['AssemblyVersion']}",
          file=sys.stderr)
    print(f"  install: {entry['DownloadLinkInstall']}", file=sys.stderr)


if __name__ == "__main__":
    main()
