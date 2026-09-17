"""Build in the connected Unity Editor, verify WebGL, and publish to GitHub Pages."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from unity_mcp_client import UnityMCP

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "Build" / "WebGL"
REMOTE = "https://github.com/ojh650765/PProejct.git"


def run(*args, cwd=ROOT, capture=False):
    return subprocess.run(args, cwd=cwd, check=True, text=True, encoding="utf-8",
                          errors="replace", capture_output=capture)


def prepare_checkout():
    if not OUTPUT.exists():
        run("git", "clone", "--depth", "1", "--single-branch", "--branch", "gh-pages", REMOTE, str(OUTPUT))
    top = run("git", "rev-parse", "--show-toplevel", cwd=OUTPUT, capture=True).stdout.strip()
    branch = run("git", "branch", "--show-current", cwd=OUTPUT, capture=True).stdout.strip()
    remote = run("git", "remote", "get-url", "origin", cwd=OUTPUT, capture=True).stdout.strip()
    if Path(top).resolve() != OUTPUT.resolve() or branch != "gh-pages" or remote != REMOTE:
        raise RuntimeError("Build/WebGL must be its own gh-pages checkout with the expected origin.")
    if run("git", "status", "--porcelain", cwd=OUTPUT, capture=True).stdout.strip():
        raise RuntimeError("Build/WebGL has an unpublished build. Use --deploy-existing to verify and publish it first.")
    run("git", "pull", "--ff-only", "origin", "gh-pages", cwd=OUTPUT)


def build():
    mcp = UnityMCP()
    project = mcp.rpc("resources/read", {"uri": "mcpforunity://project/info"})
    info = json.loads(project["contents"][0]["text"])["data"]
    if Path(info["projectRoot"]).resolve() != ROOT:
        raise RuntimeError("Unity MCP is connected to another project. Open this workspace in Unity.")
    state = mcp.rpc("resources/read", {"uri": "mcpforunity://editor/state"})
    state = json.loads(state["contents"][0]["text"])["data"]
    if state["editor"]["play_mode"]["is_playing"]:
        raise RuntimeError("Stop Unity Play mode before deploying.")
    result_file = ROOT / "Temp" / "release_build.json"
    result_file.parent.mkdir(exist_ok=True)
    result_file.write_text('{"state":"queued"}', encoding="utf-8")
    print("Building WebGL in Unity. Progress is also shown in the Editor.", flush=True)
    try:
        result = mcp.call("execute_menu_item", menu_path="Tools/Poké Lab/Build/Release WebGL")
        if result.get("isError"):
            raise RuntimeError(str(result))
    except (TimeoutError, ConnectionError):
        pass
    except Exception as error:
        # A long synchronous build can outlive the HTTP request. Its result file is authoritative.
        if result_file.read_text(encoding="utf-8-sig").find('"running"') < 0:
            raise
        print("Editor is building; waiting for its completion report:", type(error).__name__, flush=True)
    deadline = time.monotonic() + 3600
    while time.monotonic() < deadline:
        try:
            result = json.loads(result_file.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            result = {}
        if result.get("state") == "passed":
            return
        if result.get("state") == "failed":
            raise RuntimeError("Unity build failed: " + result.get("message", ""))
        if result.get("state") == "queued" and time.monotonic() > deadline - 3480:
            raise RuntimeError("Unity did not start the build. Check its console and MCP connection.")
        time.sleep(5)
    raise RuntimeError("Unity build exceeded one hour; no deployment was attempted.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true", help="build and verify without publishing")
    parser.add_argument("--deploy-existing", action="store_true", help="verify and publish an already completed build")
    args = parser.parse_args()
    os.chdir(ROOT)
    lock_path = ROOT / "Temp" / "release.lock"
    lock_path.parent.mkdir(exist_ok=True)
    try:
        lock = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        raise RuntimeError("Another deployment is active (Temp/release.lock).")
    try:
        os.write(lock, str(os.getpid()).encode("ascii"))
        os.close(lock)
        if not args.deploy_existing:
            prepare_checkout()
            build()
        command = [sys.executable, "Tools/deploy_webgl.py"]
        if args.dry_run:
            command.append("--dry-run")
        run(*command)
    finally:
        lock_path.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("DEPLOYMENT STOPPED:", error, file=sys.stderr)
        sys.exit(1)
