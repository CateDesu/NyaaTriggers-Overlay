"""Publish resumable releases and prune only versions older than the listing."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

from check_publish_version import version_parts


def gh(*args, missing_ok=False):
    result = subprocess.run(["gh", *args], text=True, capture_output=True)
    if result.returncode:
        if missing_ok and "HTTP 404" in result.stderr:
            return None
        raise RuntimeError(result.stderr.strip() or "GitHub request failed")
    return result.stdout


def api(path, missing_ok=False):
    raw = gh("api", path, missing_ok=missing_ok)
    return None if raw is None else json.loads(raw)


def publish(repo, tag, commit, run, package):
    version_parts(tag.removeprefix("v"))
    marker = f"<!-- overlay release run {run} commit {commit} -->"
    endpoint = f"repos/{repo}/releases/tags/{tag}"
    release = api(endpoint, missing_ok=True)
    if release is None:
        tag_ref = api(f"repos/{repo}/git/ref/tags/{tag}", missing_ok=True)
        if tag_ref is not None and api(f"repos/{repo}/commits/{tag}")["sha"] != commit:
            raise ValueError("Existing tag points to a different commit")
        with tempfile.TemporaryDirectory(prefix="overlay-release-") as folder:
            notes = Path(folder) / "notes.md"
            notes.write_text(marker + "\n", encoding="utf-8")
            gh("release", "create", tag, "--repo", repo, "--title", tag,
               "--target", commit, "--generate-notes", "--notes-file", str(notes))
        release = api(endpoint)

    if (release["tag_name"] != tag or release["draft"] or release["prerelease"]
            or marker not in (release.get("body") or "")
            or api(f"repos/{repo}/commits/{tag}")["sha"] != commit):
        raise ValueError("Existing release does not belong to this publication")

    assets = [asset for asset in release["assets"] if asset["name"] == package.name]
    if len(assets) == 1 and assets[0]["state"] == "starter":
        gh("release", "delete-asset", tag, package.name, "--repo", repo, "--yes")
        assets = []
    if not assets:
        gh("release", "upload", tag, str(package), "--repo", repo)
        release = api(endpoint)
        assets = [asset for asset in release["assets"] if asset["name"] == package.name]
    if len(assets) != 1 or assets[0]["state"] != "uploaded" or assets[0]["size"] <= 0:
        raise ValueError("Release package is not fully uploaded")
    with tempfile.TemporaryDirectory(prefix="overlay-package-") as folder:
        gh("release", "download", tag, "--repo", repo, "--pattern", package.name, "--dir", folder)
        with zipfile.ZipFile(Path(folder) / package.name) as archive:
            manifest = json.loads(archive.read("NyaaTriggers.json"))
            if (manifest.get("AssemblyVersion") != tag.removeprefix("v")
                    or archive.getinfo("NyaaTriggers.dll").file_size == 0):
                raise ValueError("Published package does not match the release version")
    print(f"Release {tag} is ready for listing publication")


def prune(repo, dry):
    content = api(f"repos/{repo}/contents/pluginmaster.json?ref=main")
    listing = json.loads(base64.b64decode(content["content"]))
    entries = [row for row in listing if row.get("InternalName") == "NyaaTriggers"]
    if len(entries) != 1:
        raise ValueError("Expected one NyaaTriggers entry in the published listing")
    current = version_parts(entries[0]["AssemblyVersion"])
    pages = json.loads(gh("api", f"repos/{repo}/releases?per_page=100", "--paginate", "--slurp"))
    releases = [release for page in pages for release in page]
    published = [release for release in releases if release["tag_name"] == "v" + entries[0]["AssemblyVersion"]]
    if (len(published) != 1 or published[0]["draft"] or published[0]["prerelease"]
            or not any(asset["name"] == "latest.zip" and asset["state"] == "uploaded" and asset["size"] > 0
                       for asset in published[0]["assets"])):
        raise ValueError("The published release package must exist before cleanup")
    for release in releases:
        tag = release["tag_name"]
        if release["draft"] or release["prerelease"] or not tag.startswith("v"):
            continue
        try:
            older = version_parts(tag[1:]) < current
        except ValueError:
            continue
        if older:
            print(f"{'Would delete' if dry else 'Deleting'} {tag}")
            if not dry:
                gh("release", "delete", tag, "--repo", repo, "--cleanup-tag", "--yes")


if __name__ == "__main__":
    repo = os.environ["GITHUB_REPOSITORY"]
    if sys.argv[1:] == ["publish"]:
        publish(repo, os.environ["TAG"], os.environ["GITHUB_SHA"],
                os.environ["GITHUB_RUN_ID"], Path(os.environ["PACKAGE"]))
    elif sys.argv[1:] == ["prune"]:
        prune(repo, os.environ.get("DRY", "false") == "true")
    else:
        sys.exit("Expected publish or prune")
