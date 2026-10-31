"""Check notification payloads locally without contacting Discord."""
import importlib.util
from pathlib import Path
import unittest

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


if __name__ == "__main__":
    unittest.main()
