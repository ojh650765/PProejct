# -*- coding: utf-8 -*-
"""The deploy gate: no WebGL build reaches gh-pages without passing verification.

A build with a silently-broken Resources JSON went live once; console errors were triaged
only after pushing. This script exists so that cannot happen again. It does NOT build --
it verifies an existing Build/WebGL output and gates the push:

    [1/6] preflight     - Build/WebGL exists, is the gh-pages repo, has changes to publish
    [2/6] data sanity   - every .json under Build/WebGL/StreamingAssets parses
    [3/6] local serve   - http.server + Tools/capture_web.py must see the loader finish
    [4/6] console triage- Temp/web_console.txt scanned for error signatures, minus allowlist
    [5/6] deploy        - commit + push inside Build/WebGL (skipped by --dry-run)
    [6/6] live check    - wait, capture the live site, triage again (reported, not rolled back)

Usage (from the repo root, where Build/WebGL lives):

    python Tools/deploy_webgl.py               # full gate + deploy
    python Tools/deploy_webgl.py --dry-run     # steps 1-4 only, report what would deploy
    python Tools/deploy_webgl.py --force       # deploy even with no diff in Build/WebGL

Exit codes: 0 = deployed and verified (or clean dry-run / nothing to deploy);
1..6 = the step of that number failed.

The allowlist (Tools/deploy_webgl_allowlist.txt) is data, not code: one substring per
line, '#' for comments. Extend it there -- never by editing this script.
"""
import argparse
import io
import json
import os
import re
import socket
import subprocess
import sys
import time

TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(TOOLS_DIR)
BUILD_DIR = os.path.join(REPO_ROOT, "Build", "WebGL")
CAPTURE = os.path.join(TOOLS_DIR, "capture_web.py")
ALLOWLIST_PATH = os.path.join(TOOLS_DIR, "deploy_webgl_allowlist.txt")
CONSOLE_DUMP = os.path.join(REPO_ROOT, "Temp", "web_console.txt")
LOCAL_SHOT = os.path.join(REPO_ROOT, "Temp", "deploy_gate_local.png")
LIVE_SHOT = os.path.join(REPO_ROOT, "Temp", "deploy_gate_live.png")
LIVE_URL = "https://ojh650765.github.io/PProejct/"
COMMIT_MESSAGE = "Publish the WebGL build"

# Error signatures. Everything here fails the gate unless the line is allowlisted.
# 'Exception|EXCEPTION' also catches capture_web.py's "EXCEPTION: ..." lines, which is how
# a page-level uncaught exception arrives in the dump. "ERROR: Shader" is the two-line
# Unity shape -- the shader's name is on the NEXT line, which is why triage looks ahead.
SIGNATURES = [
    re.compile(r"Exception|EXCEPTION"),
    re.compile(r"error CS"),
    re.compile(r"NullReference"),
    re.compile(r"IndexOutOf"),
    re.compile(r"Failed to"),
    re.compile(r"would not parse"),
    re.compile(r"uncaught", re.IGNORECASE),
    re.compile(r"ERROR: Shader"),
    # Once dismissed as WebGL noise, this is the symptom of audio imported with an
    # unsupported load type (Streaming) or of code reading clip.length before the clip
    # loads. Both are fixed; seeing it again is a regression, so it fails the gate.
    re.compile(r"Trying to get length of sound"),
]


# ---------------------------------------------------------------------------
# Pure, separately testable pieces
# ---------------------------------------------------------------------------

def load_allowlist(path):
    """Read the allowlist file: one substring per line, blank lines and '#' comments skipped."""
    entries = []
    if not os.path.isfile(path):
        return entries
    with io.open(path, "r", encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            entries.append(line)
    return entries


def _next_nonempty(lines, i):
    """The next non-blank line after index i, or '' -- for the two-line ERROR: Shader shape."""
    for j in range(i + 1, len(lines)):
        if lines[j].strip():
            return lines[j]
    return ""


def triage_console(lines, allowlist):
    """Scan console lines for error signatures.

    Returns (findings, allowlisted_count) where findings is a list of
    (line_number, line_text) for every signature hit NOT covered by the allowlist.
    Line numbers are 1-based, matching the file on disk.

    Allowlist matching is substring, against the line itself -- and, for Unity's two-line
    "ERROR: Shader" shape, also against the following non-blank line, because the shader's
    name (the thing the allowlist names) arrives on the line after the ERROR.
    """
    findings = []
    allowlisted = 0
    for i, line in enumerate(lines):
        if not any(sig.search(line) for sig in SIGNATURES):
            continue
        haystack = line
        if "ERROR: Shader" in line:
            haystack = line + "\n" + _next_nonempty(lines, i)
        if any(entry in haystack for entry in allowlist):
            allowlisted += 1
        else:
            findings.append((i + 1, line.rstrip()))
    return findings, allowlisted


def check_streaming_assets_json(build_dir):
    """Parse every .json under Build/WebGL/StreamingAssets (recursive).

    Returns (checked_count, errors) where errors is a list of (relative_path, message).
    A missing StreamingAssets folder counts as zero files, not an error -- not every
    build ships one.
    """
    root = os.path.join(build_dir, "StreamingAssets")
    checked = 0
    errors = []
    if not os.path.isdir(root):
        return checked, errors
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in sorted(filenames):
            if not name.lower().endswith(".json"):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, build_dir)
            checked += 1
            try:
                with io.open(full, "r", encoding="utf-8-sig") as f:
                    json.load(f)
            except (ValueError, OSError) as e:
                errors.append((rel, str(e)))
    return checked, errors


# ---------------------------------------------------------------------------
# Orchestration helpers (subprocess-facing; exercised by the real run)
# ---------------------------------------------------------------------------

def _git(build_dir, *args):
    return subprocess.run(["git", "-C", build_dir] + list(args),
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


def find_free_port():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def run_capture(url, out_png, budget):
    """Run capture_web.py against a URL. Returns (loader_finished, combined_output).

    capture_web.py exits 0 even when the loader never finishes (it still screenshots),
    so success is its explicit "loader finished" line, not the exit code. cwd is the
    repo root because capture_web writes Temp/web_console.txt relative to cwd.
    """
    os.makedirs(os.path.join(REPO_ROOT, "Temp"), exist_ok=True)
    try:
        proc = subprocess.run(
            [sys.executable, CAPTURE, url, out_png, str(budget)],
            cwd=REPO_ROOT, capture_output=True, text=True,
            encoding="utf-8", errors="replace",
            timeout=budget + 900,  # capture_web's own per-call budget can add minutes
        )
    except subprocess.TimeoutExpired:
        return False, "capture_web.py exceeded its overall time budget and was killed"
    out = (proc.stdout or "") + (proc.stderr or "")
    finished = proc.returncode == 0 and "loader finished" in out
    return finished, out


def triage_dump_file(path, allowlist):
    """Triage a console dump file on disk. Returns (findings, allowlisted, total_lines)."""
    with io.open(path, "r", encoding="utf-8") as f:
        lines = f.read().splitlines()
    findings, allowlisted = triage_console(lines, allowlist)
    return findings, allowlisted, len(lines)


def _report_findings(findings):
    for lineno, text in findings:
        print("      line %d: %s" % (lineno, text[:200]))


# ---------------------------------------------------------------------------
# The gate itself
# ---------------------------------------------------------------------------

def _wait_until_written(build_dir, quiet_seconds=20, budget=900):
    """Blocks until the player's files have stopped changing size.

    Unity's build menu item returns -- and the request runner writes its "ok" -- before the
    player is finished being written to disk. Deploying in that window publishes a truncated
    WebGL.data.unityweb, and the gate then fails at step 3 with a 404 on a file that is right
    there, which reads as a mystery rather than a race. It has cost three runs.

    So the shape of the build is what is waited on, not a fixed sleep: every file under Build/
    has to hold the same size for a stretch, and the four the loader needs have to exist.
    """
    build = os.path.join(build_dir, "Build")
    if not os.path.isdir(build):
        return

    needed = ("WebGL.loader.js", "WebGL.data.unityweb",
              "WebGL.framework.js.unityweb", "WebGL.wasm.unityweb")

    previous, steady_since = None, None
    deadline = time.time() + budget
    while time.time() < deadline:
        try:
            sizes = {n: os.path.getsize(os.path.join(build, n))
                     for n in os.listdir(build)}
        except OSError:
            sizes = {}

        complete = all(sizes.get(n, 0) > 0 for n in needed)
        if complete and sizes == previous:
            if steady_since is None:
                steady_since = time.time()
            elif time.time() - steady_since >= quiet_seconds:
                return
        else:
            steady_since = None
        previous = sizes
        time.sleep(4)

    print("      (still being written after %ds; deploying anyway)" % budget)


def step_preflight(build_dir, force):
    """Step 1. Returns 'deploy', 'nothing', or raises GateFailure."""
    if not os.path.isdir(build_dir):
        raise GateFailure(
            "Build/WebGL not found at %s.\n"
            "      Build the player first (Unity editor menu), and run this script from a\n"
            "      checkout that actually contains the build output." % build_dir)
    r = _git(build_dir, "rev-parse", "--is-inside-work-tree")
    if r.returncode != 0 or r.stdout.strip() != "true":
        raise GateFailure("Build/WebGL is not a git repo (expected its own gh-pages checkout).")
    r = _git(build_dir, "rev-parse", "--show-toplevel")
    top = os.path.normcase(os.path.normpath(r.stdout.strip()))
    if top != os.path.normcase(os.path.normpath(build_dir)):
        raise GateFailure(
            "Build/WebGL is inside the repo at %s, not its own checkout.\n"
            "      Deploying from here would commit into the wrong repository." % r.stdout.strip())
    r = _git(build_dir, "rev-parse", "--abbrev-ref", "HEAD")
    branch = r.stdout.strip()
    if branch != "gh-pages":
        raise GateFailure("Build/WebGL is on branch '%s', expected 'gh-pages'." % branch)
    _wait_until_written(build_dir)

    r = _git(build_dir, "status", "--porcelain")
    if r.stdout.strip():
        changed = len(r.stdout.strip().splitlines())
        print("[1/6] preflight ok - gh-pages checkout, %d path(s) changed" % changed)
        return "deploy"
    if force:
        print("[1/6] preflight ok - no changes, but --force was given")
        return "deploy"
    print("[1/6] preflight: Build/WebGL has no changes - nothing to deploy (use --force to push anyway)")
    return "nothing"


def step_data_sanity(build_dir):
    checked, errors = check_streaming_assets_json(build_dir)
    if errors:
        print("[2/6] data sanity FAILED - %d of %d JSON file(s) do not parse:" % (len(errors), checked))
        for rel, msg in errors:
            print("      %s: %s" % (rel, msg))
        raise GateFailure("broken JSON must not ship (this exact failure went live once).")
    print("[2/6] data sanity ok - %d JSON file(s) under StreamingAssets all parse" % checked)


def step_local_load(build_dir, budget):
    port = find_free_port()
    url = "http://localhost:%d/" % port
    server = subprocess.Popen(
        [sys.executable, "-m", "http.server", str(port), "--bind", "127.0.0.1"],
        cwd=build_dir, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        deadline = time.time() + 10
        up = False
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    up = True
                    break
            except OSError:
                time.sleep(0.3)
        if not up:
            raise GateFailure("local http.server never came up on port %d" % port)
        print("[3/6] serving Build/WebGL at %s - loading in headless Chrome (budget %ds)..." % (url, budget))
        finished, out = run_capture(url, LOCAL_SHOT, budget)
        for line in out.splitlines()[-6:]:
            print("      %s" % line[:200])
        if not finished:
            raise GateFailure("the loader never finished locally - see %s and the output above." % LOCAL_SHOT)
        print("[3/6] local load ok - loader finished, screenshot at %s" % LOCAL_SHOT)
    finally:
        server.kill()
        try:
            server.wait(timeout=10)
        except Exception:
            pass


def step_console_triage(allowlist, label, step_no="4/6"):
    if not os.path.isfile(CONSOLE_DUMP):
        raise GateFailure("no console dump at %s - capture_web.py did not produce one." % CONSOLE_DUMP)
    findings, allowlisted, total = triage_dump_file(CONSOLE_DUMP, allowlist)
    if findings:
        print("[%s] %s console FAILED - %d finding(s) in %d lines (%d allowlisted):"
              % (step_no, label, len(findings), total, allowlisted))
        _report_findings(findings)
        raise GateFailure(
            "console errors above are not on the allowlist. Fix them, or -- only if truly\n"
            "      benign -- add a substring to Tools/deploy_webgl_allowlist.txt.")
    print("[%s] %s console ok - 0 findings in %d lines (%d allowlisted)"
          % (step_no, label, total, allowlisted))


def step_deploy(build_dir, dry_run):
    r = _git(build_dir, "status", "--porcelain")
    changed = len(r.stdout.strip().splitlines()) if r.stdout.strip() else 0
    if dry_run:
        print("[5/6] dry run - would commit %d changed path(s) as '%s' and push gh-pages. Skipping."
              % (changed, COMMIT_MESSAGE))
        return False
    r = _git(build_dir, "add", "-A")
    if r.returncode != 0:
        raise GateFailure("git add failed: %s" % r.stderr.strip())
    # Published as a single fresh commit, always. Sixteen deploys of ~80 MB binary
    # history had grown the gh-pages checkout past a gigabyte -- the served tree is
    # 84 MB. Nothing downstream reads this branch's history; the site is its tip.
    tree = _git(build_dir, "write-tree")
    if tree.returncode != 0:
        raise GateFailure("git write-tree failed: %s" % tree.stderr.strip())
    commit = _git(build_dir, "commit-tree", tree.stdout.strip(), "-m", COMMIT_MESSAGE)
    if commit.returncode != 0:
        raise GateFailure("git commit-tree failed: %s" % commit.stderr.strip())
    r = _git(build_dir, "reset", "--soft", commit.stdout.strip())
    if r.returncode != 0:
        raise GateFailure("git reset failed: %s" % r.stderr.strip())
    r = _git(build_dir, "push", "--force")
    if r.returncode != 0:
        raise GateFailure("git push failed: %s" % r.stderr.strip())
    sha = _git(build_dir, "rev-parse", "--short", "HEAD").stdout.strip()
    print("[5/6] deployed - gh-pages is at %s (single-commit publish)" % sha)
    return True


def step_live_check(allowlist, budget, wait_seconds):
    print("[6/6] waiting %ds for GitHub Pages to serve the new build..." % wait_seconds)
    time.sleep(wait_seconds)
    finished, out = run_capture(LIVE_URL, LIVE_SHOT, budget)
    for line in out.splitlines()[-6:]:
        print("      %s" % line[:200])
    ok = True
    if not finished:
        print("[6/6] live check FAILED - the loader never finished on %s" % LIVE_URL)
        ok = False
    else:
        try:
            step_console_triage(allowlist, "live", step_no="6/6")
        except GateFailure as e:
            print("      %s" % e)
            ok = False
    if not ok:
        print("      NOTE: the deploy already happened and has NOT been rolled back.")
        print("      Investigate now; roll back by reverting the last commit in Build/WebGL if needed.")
    else:
        print("[6/6] live check ok - screenshot at %s" % LIVE_SHOT)
    return ok


class GateFailure(Exception):
    pass


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Verify Build/WebGL and, only if everything passes, publish it to gh-pages.")
    parser.add_argument("--dry-run", action="store_true",
                        help="run steps 1-4 and report what would deploy, without pushing")
    parser.add_argument("--force", action="store_true",
                        help="proceed even if Build/WebGL has no changes to publish")
    parser.add_argument("--budget", type=int, default=480,
                        help="seconds to allow the Unity loader per capture (default 480)")
    parser.add_argument("--live-wait", type=int, default=90,
                        help="seconds to wait for GitHub Pages before the live check (default 90)")
    args = parser.parse_args(argv)

    # Windows consoles default to a legacy codepage; the dump is UTF-8.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    allowlist = load_allowlist(ALLOWLIST_PATH)

    step = 1
    try:
        verdict = step_preflight(BUILD_DIR, args.force)
        if verdict == "nothing":
            return 0
        step = 2
        step_data_sanity(BUILD_DIR)
        step = 3
        step_local_load(BUILD_DIR, args.budget)
        step = 4
        step_console_triage(allowlist, "local")
        step = 5
        deployed = step_deploy(BUILD_DIR, args.dry_run)
        if not deployed:
            print("dry run complete - the build passed every check and is ready to deploy.")
            return 0
        step = 6
        if not step_live_check(allowlist, args.budget, args.live_wait):
            return 6
        print("deploy complete and verified: %s" % LIVE_URL)
        return 0
    except GateFailure as e:
        print("[%d/6] FAILED - %s" % (step, e))
        if step < 5:
            print("nothing was deployed.")
        return step


if __name__ == "__main__":
    sys.exit(main())
