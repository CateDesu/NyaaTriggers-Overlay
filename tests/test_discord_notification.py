"""Check notification payloads locally without contacting Discord."""
from contextlib import chdir
import importlib.util
import json
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("discord_notify", ROOT / ".github/discord_notify.py")
notify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notify)


class NotificationTests(unittest.TestCase):
    def test_commit_mentions_remain_text(self):
        message = "Fix <@123456789012345678> @everyone <@&123456789012345678>"
        for image in (False, True):
            payload = notify.notification("local/test", "main", "contributor", message,
                                          "https://github.com/local/test/commit/abc", image)
            self.assertIn(message, payload["content"])
            self.assertEqual(payload.get("allowed_mentions"), {"parse": []})

    def test_prepare_uses_working_directory(self):
        env = {
            "REPO": "local/test", "COMMIT_SHA": "abc", "COMMIT_MSG": "Update",
            "REF": "main", "ACTOR": "contributor", "RUNNER_TEMP": "untrusted/missing",
        }
        with TemporaryDirectory() as directory, chdir(directory):
            for image in (b"preview", None):
                with self.subTest(image=image), patch.object(notify, "fetch_preview", return_value=image):
                    notify.prepare(env)
                    payload = json.loads(Path("discord-notification.json").read_text())
                    self.assertEqual("attachments" in payload, image is not None)
                    self.assertEqual(Path("discord-preview.png").exists(), image is not None)
                    if image is not None:
                        self.assertEqual(Path("discord-preview.png").read_bytes(), image)
            self.assertFalse(Path("untrusted").exists())

    def test_prepare_reads_commit_message_from_checkout(self):
        env = {
            "REPO": "local/test", "COMMIT_SHA": "HEAD", "COMMIT_MSG": "",
            "REF": "main", "ACTOR": "contributor",
        }
        message = subprocess.check_output(
            ["git", "-C", str(ROOT), "show", "-s", "--format=%B", "HEAD"], text=True,
        ).strip()
        with TemporaryDirectory() as directory, chdir(directory):
            with patch.object(notify, "fetch_preview", return_value=None):
                notify.prepare(env)
            payload = json.loads(Path("discord-notification.json").read_text())
            expected = notify.notification(
                "local/test", "main", "contributor", message,
                "https://github.com/local/test/commit/HEAD", False,
            )
            self.assertEqual(payload, expected)


if __name__ == "__main__":
    unittest.main()
