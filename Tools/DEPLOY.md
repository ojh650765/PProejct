# Deploying the WebGL build

**Nobody pushes gh-pages by hand any more.** A build with a silently-broken
StreamingAssets JSON went live once because the push happened before anyone looked at the
console. The gate below is now the only sanctioned way to publish.

## How to deploy

1. Build the WebGL player from the Unity editor menu (the gate does not build).
2. From the repo root (`C:\PProejct`, the checkout that contains `Build/WebGL`):

```
python Tools/deploy_webgl.py
```

Useful variants:

```
python Tools/deploy_webgl.py --dry-run      # run every check, push nothing
python Tools/deploy_webgl.py --force        # push even if Build/WebGL shows no diff
python Tools/deploy_webgl.py --budget 600   # give a slow machine a longer loader budget
```

## What the gate does, in order

Each step must pass before the next runs; the first failure aborts with that step's
number as the exit code. `0` means deployed and verified (or a clean dry run / nothing
to deploy).

| Step | Check |
|------|-------|
| 1/6  | Preflight: `Build/WebGL` exists, is its own git checkout on `gh-pages`, and has changes to publish (no diff = "nothing to deploy", exit 0, unless `--force`). |
| 2/6  | Data sanity: every `.json` under `Build/WebGL/StreamingAssets/` parses. This is the failure class that already shipped once — JsonUtility answers null on broken JSON and the game silently degrades. |
| 3/6  | Local load: serves `Build/WebGL` with `http.server` on a free port and drives headless Chrome via `Tools/capture_web.py`; the Unity loader must actually finish. Screenshot: `Temp/deploy_gate_local.png`. |
| 4/6  | Console triage: scans `Temp/web_console.txt` for error signatures (`Exception`, `error CS`, `NullReference`, `IndexOutOf`, `Failed to`, `would not parse`, `uncaught`, `ERROR: Shader`). Any hit not on the allowlist fails the gate and is printed with its line number. |
| 5/6  | Deploy: `git add -A && commit -m "Publish the WebGL build" && push` inside `Build/WebGL`. Skipped by `--dry-run`. |
| 6/6  | Live check: waits ~90 s for GitHub Pages, loads https://ojh650765.github.io/PProejct/ the same way, triages the console again. Screenshot: `Temp/deploy_gate_live.png`. A live failure after a local pass is **reported, not rolled back** — the output says so; roll back by reverting the last commit in `Build/WebGL` if needed. |

## Extending the allowlist

`Tools/deploy_webgl_allowlist.txt` — one substring per line, `#` for comments. A console
line containing an entry is treated as known-benign. Unity's shader errors arrive as two
lines (`ERROR: Shader` then the shader name); the triage joins them, so allowlisting the
shader's name is enough.

Add an entry only after you have looked at the line and understood why it is harmless,
and say why in a comment. The allowlist is the record of noise we have decided to live
with — it is not a mute button for errors you are tired of seeing.

## Requirements

- `python` on PATH with the `websocket-client` module (capture_web.py imports `websocket`).
- Chrome at `C:\Program Files\Google\Chrome\Application\chrome.exe` (capture_web.py's path).
- capture_web.py kills any Chrome holding debug port 9222 — don't run two gates at once.

## Tests

```
python -m unittest discover -s Tools/tests -v
```

covers the console triage and JSON sanity logic without needing a build or Chrome.
