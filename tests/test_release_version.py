"""An older queued publication must not replace the current download."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from check_publish_version import should_publish


class ReleaseVersionTests(unittest.TestCase):
    def test_queued_versions_cannot_regress_the_listing(self):
        listing = [{"InternalName": "NyaaTriggers", "AssemblyVersion": "0.3.0.0"}]
        for candidate in ["0.2.0.999", "0.3.0", "0.3.0.0"]:
            self.assertFalse(should_publish(listing, candidate))
        for candidate in ["0.3.0.1", "0.4.0", "1.0.0.0"]:
            self.assertTrue(should_publish(listing, candidate))

    def test_invalid_or_ambiguous_listing_fails_closed(self):
        entry = {"InternalName": "NyaaTriggers", "AssemblyVersion": "0.3.0.0"}
        for listing in [[], [entry, entry]]:
            with self.assertRaises(ValueError):
                should_publish(listing, "0.4.0.0")
        for candidate in ["0.4", "v0.4.0", "0.4.0-rc1", "$(touch injected)"]:
            with self.assertRaises(ValueError):
                should_publish([entry], candidate)


if __name__ == "__main__":
    unittest.main()
