#!/usr/bin/env python3
"""Local installation, guarded file checkpoints, package inventory and profile updates."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import uuid
from datetime import datetime, timezone

PACKAGE = "com.vrc-avatar-assistant.editor"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def project(path):
    root = Path(path).resolve()
    if not all((root / p).is_dir() for p in ("Assets", "Packages", "ProjectSettings")):
        raise ValueError("Not a Unity project root")
    return root


def safe(root, relative, allowed):
    p = Path(relative)
    if p.is_absolute() or ".." in p.parts or not p.parts or p.parts[0] not in allowed:
        raise ValueError("Unsafe or out-of-scope path: " + str(relative))
    result = root / p
    if result.resolve() != result.absolute():
        raise ValueError("Symlinks are not accepted: " + str(relative))
    if not result.resolve().is_relative_to(root):
        raise ValueError("Path escapes project")
    return result


def install(root, source=None):
    source = Path(source or Path(__file__).parent / "package~").resolve()
    metadata = json.loads((source / "package.json").read_text())
    if metadata["name"] != PACKAGE:
        raise ValueError("Unexpected package identity")
    target = root / "Packages" / PACKAGE
    if target.is_symlink():
        raise ValueError("Package target is a symlink")
    files = {p.relative_to(source).as_posix(): digest(p) for p in source.rglob("*") if p.is_file()}
    if any(p.is_symlink() for p in source.rglob("*")):
        raise ValueError("Package source contains symlink")
    marker = target / ".assistant-owned.json"
    if target.exists():
        if not marker.is_file():
            raise ValueError("Existing package is not owned by this installer")
        old = json.loads(marker.read_text())["files"]
        for name, expected in old.items():
            if digest(safe(target, name, {Path(name).parts[0]})) != expected:
                raise ValueError("Locally modified package: " + name)
        unknown = [p for p in target.rglob("*") if p.is_file() and p.relative_to(target).as_posix() not in old
                   and p.name != ".assistant-owned.json" and p.suffix != ".meta"]
        if unknown:
            raise ValueError("Untracked package files would be lost")
        if old == files:
            return {"status": "already-installed", "version": metadata["version"]}
    manifest = json.loads((root / "Packages/manifest.json").read_text())
    if PACKAGE in manifest.get("dependencies", {}):
        raise ValueError("A manifest dependency already owns this package; resolve it explicitly")
    backup = None
    if target.exists():
        backup = root / "AI Assistant/Backups" / ("package-" + uuid.uuid4().hex)
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(target, backup)
    stage = Path(tempfile.mkdtemp(prefix=".vaa-stage-", dir=root / "Packages"))
    try:
        shutil.copytree(source, stage, dirs_exist_ok=True)
        # Preserve Unity-generated GUID metadata when updating the same package.
        if target.exists():
            for meta in target.rglob("*.meta"):
                rel = meta.relative_to(target)
                counterpart = stage / str(rel)[:-5]
                if counterpart.exists():
                    (stage / rel).parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(meta, stage / rel)
        write_json(stage / ".assistant-owned.json", {"version": metadata["version"], "files": files})
        if target.exists():
            shutil.rmtree(target)
        try:
            os.replace(stage, target)
        except Exception:
            if backup and not target.exists(): shutil.copytree(backup, target)
            raise
    finally:
        if stage.exists(): shutil.rmtree(stage)
    return {"status": "installed", "version": metadata["version"], "backup": str(backup) if backup else None,
            "unity_status": "NOT_TESTED: wait for Editor compilation and health check"}


def checkpoint(root, names):
    expanded = set()
    for name in names:
        p = safe(root, name, {"Assets", "Packages", "ProjectSettings"})
        if p.is_dir(): raise ValueError("Supply exact files, not directories")
        expanded.add(Path(name).as_posix())
        if Path(name).parts[0] == "Assets" and not name.endswith(".meta"):
            expanded.add(Path(name).as_posix() + ".meta")
    key = uuid.uuid4().hex
    base = root / "AI Assistant/Backups/Checkpoints" / key
    entries = []
    for name in sorted(expanded):
        p = safe(root, name, {"Assets", "Packages", "ProjectSettings"})
        before = digest(p)
        if p.exists() and not p.is_file(): raise ValueError("Not a regular file")
        if before:
            dest = base / "files" / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, dest)
            if digest(dest) != before: raise ValueError("Backup verification failed")
        entries.append({"path": name, "before": before, "after": None})
    write_json(base / "checkpoint.json", {"id": key, "utc": datetime.now(timezone.utc).isoformat(),
               "root": str(root), "sealed": False, "entries": entries})
    return {"checkpoint": key, "files": len(entries), "note": "Only saved file bytes; unsaved Unity state is not included"}


def load_checkpoint(root, key):
    if not key or any(c not in "0123456789abcdef" for c in key): raise ValueError("Invalid checkpoint id")
    base = root / "AI Assistant/Backups/Checkpoints" / key
    data = json.loads((base / "checkpoint.json").read_text())
    if data["root"] != str(root): raise ValueError("Checkpoint belongs to another project")
    return base, data


def seal(root, key):
    base, data = load_checkpoint(root, key)
    if data["sealed"]: raise ValueError("Checkpoint is already sealed; do not redefine the post-edit state")
    for entry in data["entries"]:
        entry["after"] = digest(safe(root, entry["path"], {"Assets", "Packages", "ProjectSettings"}))
    data["sealed"] = True
    write_json(base / "checkpoint.json", data)
    return {"checkpoint": key, "status": "sealed"}


def restore(root, key, apply=False):
    base, data = load_checkpoint(root, key)
    if not data["sealed"]: raise ValueError("Seal after the edit before restoring")
    for e in data["entries"]:
        p = safe(root, e["path"], {"Assets", "Packages", "ProjectSettings"})
        if digest(p) != e["after"]: raise ValueError("Newer changes conflict: " + e["path"])
        if e["before"] and digest(base / "files" / e["path"]) != e["before"]:
            raise ValueError("Backup is corrupt: " + e["path"])
    if not apply: return {"status": "preview", "checkpoint": key, "paths": [e["path"] for e in data["entries"]]}
    # Preflight all entries first; keep a rescue copy of the current saved state.
    rescue = checkpoint(root, [e["path"] for e in data["entries"]])
    for e in data["entries"]:
        p = safe(root, e["path"], {"Assets", "Packages", "ProjectSettings"})
        if e["before"]:
            p.parent.mkdir(parents=True, exist_ok=True)
            temp = p.with_name(p.name + ".vaa-restore")
            shutil.copy2(base / "files" / e["path"], temp)
            os.replace(temp, p)
        elif p.exists(): p.unlink()
    seal(root, rescue["checkpoint"])
    return {"status": "restored", "rescue_checkpoint": rescue["checkpoint"],
            "note": "Reopen Editor and revalidate; multi-file restore is not transactional"}


def inventory(root):
    manifest = json.loads((root / "Packages/manifest.json").read_text())
    lock_path = root / "Packages/packages-lock.json"
    locked = json.loads(lock_path.read_text()) if lock_path.exists() else {}
    records = []
    for name, version in manifest.get("dependencies", {}).items():
        lock = locked.get("dependencies", {}).get(name, {})
        records.append({"name": name, "requested": version, "resolved": lock.get("version"),
                        "dependencies": lock.get("dependencies", {}), "purpose": "UNCLASSIFIED",
                        "avatar_usage": "NOT_TESTED"})
    for embedded in (root / "Packages").glob("*/package.json"):
        m = json.loads(embedded.read_text())
        records.append({"name": m["name"], "resolved": m["version"], "source": "embedded",
                        "dependencies": m.get("dependencies", {}), "purpose": "UNCLASSIFIED", "avatar_usage": "NOT_TESTED"})
    path = root / "AI Assistant/History/package-inventory.json"
    # Preserve agent/user annotations keyed by package name.
    existing = {r["name"]: r for r in json.loads(path.read_text()).get("packages", [])} if path.exists() else {}
    for r in records:
        for field in ("purpose", "avatar_usage", "required_by", "removal_risk"):
            if field in existing.get(r["name"], {}): r[field] = existing[r["name"]][field]
    write_json(path, {"packages": records, "manifest_sha256": digest(root / "Packages/manifest.json")})
    return {"status": "inventoried", "packages": len(records), "path": str(path)}


def update_profile(root, source):
    source = Path(source).resolve()
    owned = ["AGENTS.md", "FIRST_RUN.txt", "START_HERE_RU.md", "Agent_Instructions_RU.txt", "ACCEPTANCE_TESTS_RU.md",
             "SOURCES.md", "codex-config.example.toml", "optional-memories.example.toml", "MODULE_GUIDE_RU.md"]
    owned += [p.relative_to(source).as_posix() for p in (source / "Installer").rglob("*") if p.is_file() and "__pycache__" not in p.parts]
    owned += [p.relative_to(source).as_posix() for p in (source / "AI Memory/Algorithms").rglob("*.md")]
    owned += [p.relative_to(source).as_posix() for p in (source / "AI Memory/Templates").rglob("*.md")]
    owned += ["AvatarAgent/POLICY.md", "AvatarAgent/STORAGE.md"]
    target = root / "AI Assistant"
    staged = []
    for name in owned:
        src = safe(source, name, {Path(name).parts[0]})
        if not src.is_file(): continue
        dst = safe(target, name, {Path(name).parts[0]})
        if src.resolve() == dst.resolve(): raise ValueError("Use an independently extracted update")
        staged.append((name, src, dst))
    backup = target / "Backups" / ("profile-" + uuid.uuid4().hex)
    for name, src, dst in staged:
        if dst.exists():
            saved = backup / name; saved.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(dst, saved)
            if digest(saved) != digest(dst): raise ValueError("Update backup failed")
    for name, src, dst in staged:
        dst.parent.mkdir(parents=True, exist_ok=True)
        tmp = dst.with_name(dst.name + ".vaa-update"); shutil.copy2(src, tmp); os.replace(tmp, dst)
    return {"status": "updated", "backup": str(backup), "files": len(staged),
            "preserved": ["History", "AvatarAgent project journals", "AI Memory knowledge and solutions", "Reports"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("install")
    sub.add_parser("inventory")
    p = sub.add_parser("checkpoint"); p.add_argument("paths", nargs="+")
    p = sub.add_parser("seal"); p.add_argument("id")
    p = sub.add_parser("restore"); p.add_argument("id"); p.add_argument("--apply", action="store_true")
    p = sub.add_parser("update"); p.add_argument("--source", required=True)
    args = parser.parse_args(); root = project(args.project)
    try:
        result = install(root) if args.command == "install" else inventory(root) if args.command == "inventory" else \
            checkpoint(root, args.paths) if args.command == "checkpoint" else seal(root, args.id) if args.command == "seal" else \
            restore(root, args.id, args.apply) if args.command == "restore" else update_profile(root, args.source)
        print(json.dumps(result, ensure_ascii=False))
    except (OSError, ValueError, KeyError) as exc:
        parser.exit(1, "Blocked: " + str(exc) + "\n")


if __name__ == "__main__": main()
