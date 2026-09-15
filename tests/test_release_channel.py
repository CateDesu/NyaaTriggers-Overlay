"""Exercise publication and cleanup with a recording GitHub CLI substitute."""
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

MOCK = r'''#!/usr/bin/env python3
import base64, json, os, sys, zipfile
from pathlib import Path
path = Path(os.environ["TEST_RELEASE_STATE"])
s = json.loads(path.read_text())
a = sys.argv[1:]
s["calls"].append(a)
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
        out = {"sha": s.get("commit", "commit-one")}
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
        body=Path(a[a.index("--notes-file") + 1]).read_text(), assets=[]))
elif a[:2] in (["release", "upload"], ["release", "delete-asset"]):
    release = next(r for r in s["releases"] if r["tag_name"] == a[2])
    if a[1] == "delete-asset":
        release["assets"] = []
    elif s.pop("fail_upload", False):
        release["assets"] = [dict(name="latest.zip", state="starter", size=0)]
        print("upload interrupted", file=sys.stderr)
        code = 1
    else:
        release["assets"] = [dict(name="latest.zip", state="uploaded", size=500)]
elif a[:2] == ["release", "download"]:
    folder = Path(a[a.index("--dir") + 1])
    with zipfile.ZipFile(folder / "latest.zip", "w") as archive:
        archive.writestr("NyaaTriggers.json", json.dumps({"AssemblyVersion": s.get("asset_version", "0.2.0.23")}))
        archive.writestr("NyaaTriggers.dll", b"recording test assembly")
elif a[:2] == ["release", "delete"]:
    s["deleted"].append(a[2])
else:
    raise AssertionError(a)
path.write_text(json.dumps(s))
if out is not None:
    print(json.dumps(out))
sys.exit(code)
'''


def release(tag, **changes):
    result = dict(tag_name=tag, draft=False, prerelease=False,
                  body="<!-- overlay release run run-one commit commit-one -->",
                  assets=[dict(name="latest.zip", state="uploaded", size=500)])
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
        self.state = dict(releases=[], calls=[], deleted=[], listing=[
            dict(InternalName="NyaaTriggers", AssemblyVersion="0.2.0.22")])
        self.env = dict(os.environ, PATH=str(self.folder) + os.pathsep + os.environ["PATH"],
                        TEST_RELEASE_STATE=str(self.path), GITHUB_REPOSITORY="local/test",
                        TAG="v0.2.0.23", GITHUB_SHA="commit-one", GITHUB_RUN_ID="run-one",
                        PACKAGE=str(self.folder / "latest.zip"), DRY="false")

    def run_channel(self, command, success=True):
        self.path.write_text(json.dumps(self.state))
        result = subprocess.run(["python3", str(ROOT / "tools/release_channel.py"), command],
                                env=self.env, capture_output=True, text=True)
        self.state = json.loads(self.path.read_text())
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result

    def mutations(self):
        return [a for a in self.state["calls"] if a[:1] == ["release"] and a[1] != "download"]

    def test_listing_failure_can_resume_same_release(self):
        self.run_channel("publish")
        listing = self.folder / "pluginmaster.json"
        listing.write_text(json.dumps(self.state["listing"]))
        guard = subprocess.run(["python3", str(ROOT / "tools/check_publish_version.py"),
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
        self.run_channel("publish", success=False)
        self.state["calls"] = []
        self.run_channel("publish")
        self.assertEqual([a[1] for a in self.mutations()], ["delete-asset", "upload"])

    def test_existing_wrong_tag_does_not_create_a_release(self):
        self.state["tag_exists"] = True
        self.state["commit"] = "wrong-commit"
        self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_missing_published_package_preserves_fallback_release(self):
        self.state["releases"] = [release("v0.2.0.21")]
        self.run_channel("prune", success=False)
        self.assertEqual(self.state["deleted"], [])

    def test_unfinished_published_package_preserves_fallback_release(self):
        self.state["releases"] = [release("v0.2.0.21"), release("v0.2.0.22", assets=[])]
        self.run_channel("prune", success=False)
        self.assertEqual(self.state["deleted"], [])

    def test_other_run_is_refused(self):
        self.state["releases"] = [release("v0.2.0.23", body="some other run")]
        self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_wrong_commit_is_refused(self):
        self.state["releases"] = [release("v0.2.0.23")]
        self.state["commit"] = "other-commit"
        self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_draft_and_prerelease_are_refused(self):
        for flag in ["draft", "prerelease"]:
            self.state["releases"] = [release("v0.2.0.23", **{flag: True})]
            self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_bad_uploaded_asset_is_not_overwritten(self):
        self.state["releases"] = [release("v0.2.0.23", assets=[dict(name="latest.zip", state="uploaded", size=0)])]
        self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_package_version_must_match(self):
        self.state["releases"] = [release("v0.2.0.23")]
        self.state["asset_version"] = "0.2.0.22"
        self.run_channel("publish", success=False)
        self.assertEqual(self.mutations(), [])

    def test_network_failure_does_not_create_or_delete(self):
        self.state["network_error"] = True
        self.run_channel("publish", success=False)
        self.run_channel("prune", success=False)
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
        self.run_channel("prune", success=False)
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
