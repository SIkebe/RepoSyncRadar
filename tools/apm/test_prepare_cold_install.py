import hashlib
import tempfile
import unittest
from pathlib import Path

import yaml

from prepare_cold_install import prepare_cold_install


class PrepareColdInstallTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.project = self.root / "project"
        self.project.mkdir()
        self.backup = self.root / "backup"
        self.contents = {
            ".agents/skills/demo/SKILL.md": b"Original skill.\n",
            ".agents/skills/demo/references/guide.md": b"Original guide.\n",
        }
        for name, content in self.contents.items():
            source = self.project / name
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(content)
        self.hashes = {
            name: f"sha256:{hashlib.sha256(content).hexdigest()}"
            for name, content in self.contents.items()
        }
        self.write_lock()

    def write_lock(self):
        (self.project / "apm.lock.yaml").write_text(
            yaml.safe_dump({"dependencies": [{"deployed_file_hashes": self.hashes}]}),
            encoding="utf-8",
        )

    def test_moves_only_verified_managed_files_and_preserves_lock(self):
        unmanaged = self.project / ".agents/skills/demo/local.md"
        unmanaged.write_bytes(b"Keep local content.\n")
        lock_before = (self.project / "apm.lock.yaml").read_bytes()

        prepare_cold_install(self.project, self.backup)

        for name, content in self.contents.items():
            self.assertFalse((self.project / name).exists())
            self.assertEqual(content, (self.backup / name).read_bytes())
        self.assertEqual(b"Keep local content.\n", unmanaged.read_bytes())
        self.assertEqual(lock_before, (self.project / "apm.lock.yaml").read_bytes())

    def test_hash_mismatch_refuses_entire_plan_before_moving_files(self):
        edited = self.project / ".agents/skills/demo/references/guide.md"
        edited.write_bytes(b"Local edit.\n")

        with self.assertRaisesRegex(ValueError, "differs from its locked hash"):
            prepare_cold_install(self.project, self.backup)

        self.assertFalse(self.backup.exists())
        self.assertEqual(b"Local edit.\n", edited.read_bytes())
        self.assertEqual(
            self.contents[".agents/skills/demo/SKILL.md"],
            (self.project / ".agents/skills/demo/SKILL.md").read_bytes(),
        )

    def test_paths_outside_skill_files_are_refused_before_any_move(self):
        for name in (
            "../outside.md",
            ".agents/skills/demo/../../outside.md",
            ".agents/skills/demo",
            r".agents\skills\demo\SKILL.md",
            ".agents/skills/C:/outside.md",
        ):
            with self.subTest(name=name):
                self.hashes[name] = "sha256:unused"
                self.write_lock()
                with self.assertRaisesRegex(ValueError, "Not an allowed APM skill path"):
                    prepare_cold_install(self.project, self.backup)
                self.assertFalse(self.backup.exists())
                for source in self.contents:
                    self.assertTrue((self.project / source).is_file())
                del self.hashes[name]

    def test_existing_backup_is_never_overwritten(self):
        self.backup.mkdir()
        previous = self.backup / "previous.md"
        previous.write_bytes(b"Preserve this backup.\n")

        with self.assertRaises(FileExistsError):
            prepare_cold_install(self.project, self.backup)

        self.assertEqual(b"Preserve this backup.\n", previous.read_bytes())
        for name, content in self.contents.items():
            self.assertEqual(content, (self.project / name).read_bytes())


if __name__ == "__main__":
    unittest.main()
