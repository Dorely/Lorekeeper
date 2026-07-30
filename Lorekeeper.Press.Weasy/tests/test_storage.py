from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import tempfile
import unittest

from lorekeeper_press_weasy.storage import publish_job


class StorageTests(unittest.TestCase):
    def test_concurrent_same_job_has_one_complete_winner(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def publish() -> Path:
                return publish_job(root, "same-job", {"interior.pdf": b"complete"})

            with ThreadPoolExecutor(max_workers=2) as executor:
                outcomes = []
                for future in (executor.submit(publish), executor.submit(publish)):
                    try:
                        outcomes.append(("completed", future.result()))
                    except ValueError:
                        outcomes.append(("rejected", None))

            self.assertEqual(1, sum(status == "completed" for status, _ in outcomes))
            self.assertEqual(1, sum(status == "rejected" for status, _ in outcomes))
            self.assertEqual(b"complete", (root / "same-job" / "interior.pdf").read_bytes())
            self.assertEqual([], list(root.glob(".*.lock")))
            self.assertEqual([], [path for path in root.iterdir() if path.name.startswith(".same-job-")])

    def test_existing_job_is_never_replaced(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_job(root, "immutable", {"artifact": b"first"})

            with self.assertRaisesRegex(ValueError, "already exists"):
                publish_job(root, "immutable", {"artifact": b"second"})

            self.assertEqual(b"first", (root / "immutable" / "artifact").read_bytes())


if __name__ == "__main__":
    unittest.main()
