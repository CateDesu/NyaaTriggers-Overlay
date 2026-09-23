#!/usr/bin/env python3
"""Build the stable Dalamud listing from the packaged manifest and release metadata."""
import argparse
import json
import sys
import time
from pathlib import Path

# Regenerated listing fields that are excluded from manifest comparisons.
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

    if tag.lstrip("v") != version:
        raise SystemExit(
            f"tag {tag!r} and AssemblyVersion {version!r} disagree. "
            "Bump <Version> in the csproj to match the tag, or retag.")

    asset = f"https://github.com/{repo}/releases/download/{tag}/latest.zip"

    entry = dict(manifest)
    entry["IsHide"] = False
    entry["LastUpdate"] = int(time.time())
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

    out = Path(args.out)
    out.write_text(json.dumps([entry], indent=2, sort_keys=True) + "\n",
                   encoding="utf-8")

    print(f"wrote {out} for {entry['InternalName']} {entry['AssemblyVersion']}",
          file=sys.stderr)
    print(f"  install: {entry['DownloadLinkInstall']}", file=sys.stderr)


if __name__ == "__main__":
    main()
