"""固定した Skia の依存ソースを取得する。DNG と piex は取得しない。"""

import json
from pathlib import Path
import re
import subprocess
import sys

configuration = json.loads(Path(__file__).with_name("build.json").read_text(encoding="utf-8"))
root = Path(sys.argv[1]).resolve()
dependency_pattern = re.compile(
    r'^\s*"([^"]+)"\s*:\s*"(https://[^"]+)@([0-9a-f]{40})",?\s*$',
    re.MULTILINE,
)

def git(directory, *arguments, capture=False):
    return subprocess.run(
        ["git", "-C", str(directory), *arguments],
        check=True,
        text=True,
        stdout=subprocess.PIPE if capture else None,
    ).stdout

def fetch_dependency(entry):
    name, repository, commit = entry
    destination = (root / name).resolve()
    if not destination.is_relative_to(root):
        raise ValueError(f"Dependency path is outside the source directory: {name}: {destination}")
    destination.mkdir(parents=True, exist_ok=True)
    if not (destination / ".git").exists():
        subprocess.run(["git", "init", "--quiet", str(destination)], check=True)
        git(destination, "config", "core.longpaths", "true")
        git(destination, "remote", "add", "origin", repository)
    if git(destination, "status", "--porcelain", "--untracked-files=no", capture=True).strip():
        raise ValueError(f"Dependency has local changes: {name}")
    current = subprocess.run(
        ["git", "-C", str(destination), "rev-parse", "--verify", "HEAD"],
        capture_output=True, text=True,
    )
    if current.returncode != 0 or current.stdout.strip() != commit:
        git(destination, "fetch", "--quiet", "--depth", "1", repository, commit)
        git(destination, "checkout", "--quiet", "--detach", commit)
    print(f"{name}: {commit}", flush=True)

dependencies = [
    entry for entry in dependency_pattern.findall((root / "DEPS").read_text(encoding="utf-8"))
    if entry[0] not in configuration["excludedDependencies"]
]
if not dependencies:
    raise ValueError("No pinned dependencies were found in DEPS.")
for dependency in dependencies:
    fetch_dependency(dependency)
subprocess.run([sys.executable, str(root / "bin/fetch-gn")], cwd=root, check=True)
