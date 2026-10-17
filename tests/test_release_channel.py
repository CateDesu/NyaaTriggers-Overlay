"""Exercise publication and cleanup with a recording GitHub CLI substitute."""
import base64
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]

MOCK = r'''#!/usr/bin/env python3
import base64, json, os, sys
from pathlib import Path
path = Path(os.environ["TEST_RELEASE_STATE"])
s = json.loads(path.read_text())
a = sys.argv[1:]
s["calls"].append(a)
path.write_text(json.dumps(s))
if a[0] == "api":
    assert a[1].startswith("repos/local/test/")
else:
    assert a[a.index("--repo") + 1] == "local/test"
code = 0
out = None
if s.get("network_error"):
    print("HTTP 503", file=sys.stderr)
    code = 1
elif a[0] == "api":
    endpoint = a[1]
    if "/releases/tags/" in endpoint:
        tag = endpoint.rsplit("/", 1)[1]
        out = next((r for r in s["releases"] if r["tag_name"] == tag), None)
        if out is None:
            print("HTTP 404", file=sys.stderr)
            code = 1
    elif "/git/ref/tags/" in endpoint:
        if s.get("tag_exists"):
            out = {"object": {"type": "commit", "sha": s.get("commit", "commit-one")}}
        else:
            print("HTTP 404", file=sys.stderr)
            code = 1
    elif "/commits/" in endpoint:
        tag = endpoint.rsplit("/", 1)[1]
        release = next((r for r in s["releases"] if r["tag_name"] == tag), None)
        out = {"sha": release["commit"] if release else s.get("commit", "commit-one")}
    elif "/contents/" in endpoint:
        out = {"content": base64.b64encode(json.dumps(s["listing"]).encode()).decode()}
        s["releases"].extend(s.pop("after_listing", []))
    elif endpoint.endswith("/releases?per_page=100"):
        assert a[2:] == ["--paginate", "--slurp"]
        out = [s["releases"][:1], s["releases"][1:]]
    else:
        raise AssertionError(a)
elif a[:2] == ["release", "create"]:
    assert not any(r["tag_name"] == a[2] for r in s["releases"])
    s["releases"].append(dict(tag_name=a[2], draft=False, prerelease=False,
        body=Path(a[a.index("--notes-file") + 1]).read_text(), assets=[],
        commit=s.get("commit", "commit-one") if s.get("tag_exists") else a[a.index("--target") + 1]))
elif a[:2] in (["release", "upload"], ["release", "delete-asset"]):
    release = next(r for r in s["releases"] if r["tag_name"] == a[2])
    if a[1] == "upload":
        package = Path(a[3])
        payload = package.read_bytes()
        assert not any(asset["name"] == package.name for asset in release["assets"])
    if a[1] == "delete-asset":
        assert any(asset["name"] == a[3] for asset in release["assets"])
        release["assets"] = [asset for asset in release["assets"] if asset["name"] != a[3]]
        release.pop("package", None)
    elif s.pop("fail_upload", False):
        release["assets"].append(dict(name=package.name, state="starter", size=0))
        print("upload interrupted", file=sys.stderr)
        code = 1
    else:
        release["assets"].append(dict(name=package.name, state="uploaded", size=len(payload)))
        release["package"] = base64.b64encode(payload).decode()
elif a[:2] == ["release", "download"]:
    release = next(r for r in s["releases"] if r["tag_name"] == a[2])
    name = a[a.index("--pattern") + 1]
    assert any(asset["name"] == name and asset["state"] == "uploaded" for asset in release["assets"])
    folder = Path(a[a.index("--dir") + 1])
    (folder / name).write_bytes(base64.b64decode(release["package"]))
elif a[:2] == ["release", "delete"]:
    assert any(r["tag_name"] == a[2] for r in s["releases"])
    s["deleted"].append(a[2])
    s["releases"] = [r for r in s["releases"] if r["tag_name"] != a[2]]
else:
    raise AssertionError(a)
path.write_text(json.dumps(s))
if out is not None:
    print(json.dumps(out))
sys.exit(code)
'''


def package_bytes(version):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        archive.writestr("NyaaTriggers.json", json.dumps({"AssemblyVersion": version}))
        archive.writestr("NyaaTriggers.dll", b"recording test assembly")
    return stream.getvalue()


def release(tag, asset_version=None, **changes):
    payload = package_bytes(asset_version or tag.removeprefix("v"))
    result = dict(tag_name=tag, draft=False, prerelease=False,
                  body="<!-- overlay release run run-one commit commit-one -->",
                  commit="commit-one", package=base64.b64encode(payload).decode(),
                  assets=[dict(name="latest.zip", state="uploaded", size=len(payload))])
    result.update(changes)
    return result


class ReleaseChannelTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="overlay-release-tests-")
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        mock = self.folder / "gh"
        mock.write_text(MOCK)
        mock.chmod(0o755)
        self.path = self.folder / "state.json"
        (self.folder / "latest.zip").write_bytes(package_bytes("0.2.0.23"))
        self.state = dict(releases=[], calls=[], deleted=[], listing=[
            dict(InternalName="NyaaTriggers", AssemblyVersion="0.2.0.22")])
        self.env = dict(os.environ, PATH=str(self.folder) + os.pathsep + os.environ["PATH"],
                        TEST_RELEASE_STATE=str(self.path), GITHUB_REPOSITORY="local/test",
                        TAG="v0.2.0.23", GITHUB_SHA="commit-one", GITHUB_RUN_ID="run-one",
                        PACKAGE=str(self.folder / "latest.zip"), DRY="false")

    def run_channel(self, command, error=None):
        self.path.write_text(json.dumps(self.state))
        result = subprocess.run([sys.executable, str(ROOT / "tools/release_channel.py"), command],
                                env=self.env, capture_output=True, text=True, timeout=15)
        self.state = json.loads(self.path.read_text())
        self.assertEqual(result.returncode == 0, error is None, result.stdout + result.stderr)
        if error is not None:
            self.assertIn(error, result.stderr)
        return result

    def mutations(self):
        return [a for a in self.state["calls"] if a[:1] == ["release"] and a[1] != "download"]

    def test_listing_failure_can_resume_same_release(self):
        self.run_channel("publish")
        listing = self.folder / "pluginmaster.json"
        listing.write_text(json.dumps(self.state["listing"]))
        guard = subprocess.run([sys.executable, str(ROOT / "tools/check_publish_version.py"),
                                str(listing), "0.2.0.23"], capture_output=True, text=True, check=True)
        self.assertIn("publish=true", guard.stdout)
        self.state["calls"] = []
        self.run_channel("publish")
        self.assertEqual(self.mutations(), [])

    def test_missing_upload_is_completed(self):
        self.state["releases"] = [release("v0.2.0.23", assets=[])]
        self.run_channel("publish")
        self.assertEqual([a[1] for a in self.mutations()], ["upload"])

    def test_interrupted_upload_is_retried(self):
        self.state["fail_upload"] = True
        self.run_channel("publish", error="upload interrupted")
        self.state["calls"] = []
        self.run_channel("publish")
        self.assertEqual([a[1] for a in self.mutations()], ["delete-asset", "upload"])

    def test_existing_wrong_tag_does_not_create_a_release(self):
        self.state["tag_exists"] = True
        self.state["commit"] = "wrong-commit"
        self.run_channel("publish", error="Existing tag points to a different commit")
        self.assertEqual(self.mutations(), [])

    def test_missing_published_package_preserves_fallback_release(self):
        self.state["releases"] = [release("v0.2.0.21")]
        self.run_channel("prune", error="The published release package must exist before cleanup")
        self.assertEqual(self.state["deleted"], [])

    def test_unfinished_published_package_preserves_fallback_release(self):
        self.state["releases"] = [release("v0.2.0.21"), release("v0.2.0.22", assets=[])]
        self.run_channel("prune", error="The published release package must exist before cleanup")
        self.assertEqual(self.state["deleted"], [])

    def test_other_run_is_refused(self):
        self.state["releases"] = [release("v0.2.0.23", body="some other run")]
        self.run_channel("publish", error="Existing release does not belong to this publication")
        self.assertEqual(self.mutations(), [])

    def test_wrong_commit_is_refused(self):
        self.state["releases"] = [release("v0.2.0.23", commit="other-commit")]
        self.run_channel("publish", error="Existing release does not belong to this publication")
        self.assertEqual(self.mutations(), [])

    def test_draft_and_prerelease_are_refused(self):
        for flag in ["draft", "prerelease"]:
            self.state["releases"] = [release("v0.2.0.23", **{flag: True})]
            self.run_channel("publish", error="Existing release does not belong to this publication")
        self.assertEqual(self.mutations(), [])

    def test_bad_uploaded_asset_is_not_overwritten(self):
        self.state["releases"] = [release("v0.2.0.23", assets=[dict(name="latest.zip", state="uploaded", size=0)])]
        self.run_channel("publish", error="Release package is not fully uploaded")
        self.assertEqual(self.mutations(), [])

    def test_package_version_must_match(self):
        self.state["releases"] = [release("v0.2.0.23", asset_version="0.2.0.22")]
        self.run_channel("publish", error="Published package does not match the release version")
        self.assertEqual(self.mutations(), [])

    def test_network_failure_does_not_create_or_delete(self):
        self.state["network_error"] = True
        self.run_channel("publish", error="HTTP 503")
        self.run_channel("prune", error="HTTP 503")
        self.assertEqual(self.mutations(), [])

    def test_pruning_preserves_a_release_inserted_after_listing_read(self):
        self.state["releases"] = [release("v0.2.0.21"), release("v0.2.0.22")]
        self.state["after_listing"] = [release("v0.2.0.23")]
        self.run_channel("prune")
        self.assertEqual(self.state["deleted"], ["v0.2.0.21"])

    def test_cleanup_after_listing_publish_removes_superseded_release(self):
        self.state["listing"][0]["AssemblyVersion"] = "0.2.0.23"
        self.state["releases"] = [release("v0.2.0.22"), release("v0.2.0.23")]
        self.run_channel("prune")
        self.assertEqual(self.state["deleted"], ["v0.2.0.22"])

    def test_dry_run_does_not_delete(self):
        self.state["releases"] = [release("v0.2.0.21"), release("v0.2.0.22")]
        self.env["DRY"] = "true"
        result = self.run_channel("prune")
        self.assertIn("Would delete", result.stdout)
        self.assertEqual(self.state["deleted"], [])

    def test_unrecognized_tags_and_unpublished_channels_are_preserved(self):
        self.state["releases"] = [release("milestone"), release("v0.1.0", draft=True),
                                  release("v0.1.1", prerelease=True), release("vbroken"), release("v0.2.0.22")]
        self.run_channel("prune")
        self.assertEqual(self.state["deleted"], [])

    def test_ambiguous_listing_stops_cleanup(self):
        self.state["listing"] *= 2
        self.state["releases"] = [release("v0.2.0.21")]
        self.run_channel("prune", error="Expected one NyaaTriggers entry in the published listing")
        self.assertEqual(self.state["deleted"], [])

    def test_workflows_share_serialization_and_test_the_helpers(self):
        publish = (ROOT / ".github/workflows/release.yml").read_text()
        prune = (ROOT / ".github/workflows/prune-old-releases.yml").read_text()
        for workflow in [publish, prune]:
            self.assertIn("concurrency:\n  group: release-publication\n  cancel-in-progress: false", workflow)
            self.assertIn("run: python3 tools/release_channel.py prune", workflow)
            self.assertIn("uses: actions/checkout@v4", workflow)
        self.assertIn("run: python3 tools/release_channel.py publish", publish)
        self.assertIn("python3 tests/test_release_channel.py", publish)
        self.assertIn("dotnet run --project tests/OverlayRegressionTests", publish)


if __name__ == "__main__":
    unittest.main(verbosity=2)
