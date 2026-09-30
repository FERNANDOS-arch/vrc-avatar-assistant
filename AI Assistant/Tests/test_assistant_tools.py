import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("assistant_tools", Path(__file__).parents[1] / "Installer/assistant_tools.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name).resolve()
        for name in ("Assets", "Packages", "ProjectSettings"): (self.root / name).mkdir()
        (self.root / "Packages/manifest.json").write_text('{"dependencies":{"com.example.test":"1.0"}}')
        self.asset = self.root / "Assets/avatar.prefab"
        self.asset.write_text("before")
        (self.root / "Assets/avatar.prefab.meta").write_text("guid: original")

    def tearDown(self): self.tmp.cleanup()

    def test_restore_preserves_meta_and_rescue(self):
        key = tools.checkpoint(self.root, ["Assets/avatar.prefab"])["checkpoint"]
        self.asset.write_text("after"); tools.seal(self.root, key)
        result = tools.restore(self.root, key, True)
        self.assertEqual(self.asset.read_text(), "before")
        self.assertEqual((self.root / "Assets/avatar.prefab.meta").read_text(), "guid: original")
        tools.restore(self.root, result["rescue_checkpoint"], True)
        self.assertEqual(self.asset.read_text(), "after")

    def test_newer_user_edit_blocks_all_restore(self):
        key = tools.checkpoint(self.root, ["Assets/avatar.prefab"])["checkpoint"]
        self.asset.write_text("agent"); tools.seal(self.root, key)
        self.asset.write_text("user")
        with self.assertRaises(ValueError): tools.restore(self.root, key, True)
        self.assertEqual(self.asset.read_text(), "user")

    def test_preview_does_not_write(self):
        key = tools.checkpoint(self.root, ["Assets/avatar.prefab"])["checkpoint"]
        self.asset.write_text("after"); tools.seal(self.root, key)
        self.assertEqual(tools.restore(self.root, key)["status"], "preview")
        self.assertEqual(self.asset.read_text(), "after")

    def test_traversal_and_symlink_rejected(self):
        with self.assertRaises(ValueError): tools.checkpoint(self.root, ["Assets/../Packages/manifest.json"])
        (self.root / "Assets/link").symlink_to(self.asset)
        with self.assertRaises(ValueError): tools.checkpoint(self.root, ["Assets/link"])

    def test_corrupt_backup_rejected_before_writes(self):
        key = tools.checkpoint(self.root, ["Assets/avatar.prefab"])["checkpoint"]
        self.asset.write_text("after"); tools.seal(self.root, key)
        (self.root / "AI Assistant/Backups/Checkpoints" / key / "files/Assets/avatar.prefab").write_text("bad")
        with self.assertRaises(ValueError): tools.restore(self.root, key, True)
        self.assertEqual(self.asset.read_text(), "after")

    def test_unsealed_or_reseal_rejected(self):
        key = tools.checkpoint(self.root, ["Assets/avatar.prefab"])["checkpoint"]
        with self.assertRaises(ValueError): tools.restore(self.root, key, True)
        tools.seal(self.root, key)
        with self.assertRaises(ValueError): tools.seal(self.root, key)

    def test_install_idempotent_and_local_changes_blocked(self):
        self.assertEqual(tools.install(self.root)["status"], "installed")
        self.assertEqual(tools.install(self.root)["status"], "already-installed")
        (self.root / "Packages" / tools.PACKAGE / "Editor/AssistantHistory.cs").write_text("user")
        with self.assertRaises(ValueError): tools.install(self.root)

    def test_existing_foreign_package_not_overwritten(self):
        (self.root / "Packages" / tools.PACKAGE).mkdir()
        with self.assertRaises(ValueError): tools.install(self.root)

    def test_package_dependency_conflict_blocked(self):
        (self.root / "Packages/manifest.json").write_text(json.dumps({"dependencies":{tools.PACKAGE:"1.0"}}))
        with self.assertRaises(ValueError): tools.install(self.root)

    def test_annotations_survive_inventory_refresh(self):
        tools.inventory(self.root)
        p = self.root / "AI Assistant/History/package-inventory.json"
        data = json.loads(p.read_text()); data["packages"][0]["purpose"] = "clothes"
        tools.write_json(p, data); tools.inventory(self.root)
        self.assertEqual(json.loads(p.read_text())["packages"][0]["purpose"], "clothes")

    def test_update_preserves_history_preferences_and_solutions(self):
        source = self.root / "update"; (source / "AvatarAgent").mkdir(parents=True)
        (source / "AGENTS.md").write_text("new rules")
        (source / "AvatarAgent/POLICY.md").write_text("new policy")
        preserved = ["History/events.jsonl", "AvatarAgent/USER_PREFERENCES.md", "AI Memory/Solutions/real.md", "AI Memory/KNOWLEDGE_BASE.md"]
        for name in preserved:
            p = self.root / "AI Assistant" / name; p.parent.mkdir(parents=True, exist_ok=True); p.write_text("keep")
        tools.update_profile(self.root, source)
        for name in preserved: self.assertEqual((self.root / "AI Assistant" / name).read_text(), "keep")
        self.assertEqual((self.root / "AI Assistant/AGENTS.md").read_text(), "new rules")


if __name__ == "__main__": unittest.main()
