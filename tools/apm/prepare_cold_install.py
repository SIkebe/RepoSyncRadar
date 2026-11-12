"""Back up verified, lock-owned skill files before a fresh CI install."""

import argparse
import hashlib
import shutil
from pathlib import Path, PurePosixPath

import yaml


def prepare_cold_install(project: Path, backup: Path) -> int:
    project = project.resolve(strict=True)
    backup = backup.resolve()
    if backup == project or project in backup.parents:
        raise ValueError("The backup must be outside the project.")

    lock = yaml.safe_load((project / "apm.lock.yaml").read_text(encoding="utf-8"))
    files = {}
    for dependency in lock["dependencies"]:
        for name, digest in dependency.get("deployed_file_hashes", {}).items():
            if name in files and files[name] != digest:
                raise ValueError(f"Conflicting locked hashes for {name}.")
            files[name] = digest
    if not files:
        raise ValueError("No hashed APM skill files were found in the lockfile.")

    moves = []
    for name, digest in sorted(files.items()):
        relative = PurePosixPath(name)
        if (
            relative.parts[:2] != (".agents", "skills")
            or len(relative.parts) < 4
            or ".." in relative.parts
            or "\\" in name
            or ":" in name
        ):
            raise ValueError(f"Not an allowed APM skill path: {name}.")
        source = project.joinpath(*relative.parts)
        if source.resolve(strict=True) != source or not source.is_file():
            raise ValueError(f"Not a regular, non-symlink skill file: {name}.")
        if f"sha256:{hashlib.sha256(source.read_bytes()).hexdigest()}" != digest:
            raise ValueError(f"The skill file differs from its locked hash: {name}.")
        moves.append((source, relative))

    backup.mkdir()
    for source, relative in moves:
        destination = backup.joinpath(*relative.parts)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(source, destination)
    return len(moves)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backup", required=True, type=Path)
    args = parser.parse_args()
    count = prepare_cold_install(Path.cwd(), args.backup)
    print(f"Backed up {count} verified APM skill files to {args.backup}.")
