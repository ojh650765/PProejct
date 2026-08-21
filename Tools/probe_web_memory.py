# -*- coding: utf-8 -*-
"""Reads the live WebGL build's wasm heap while it runs.

The build aborts with OOM, and the only number that says how close it is running is the
wasm memory's own byteLength -- which lives on the Unity instance's Module and is otherwise
sealed inside the loader's closure. The page template publishes it as `window.plHeapBytes`
for exactly this; see Assets/WebGLTemplates/PokeLab/index.html.

Drives Chrome over the DevTools protocol the same way Tools/capture_web.py does: open the
page, wait for the loader to finish, then sample the heap on a timer so growth is visible
rather than a single reading.

    python Tools/probe_web_memory.py [url] [seconds]
"""
import json, subprocess, sys, time, urllib.request
import websocket

URL = sys.argv[1] if len(sys.argv) > 1 else "https://ojh650765.github.io/PProejct/"
WATCH = int(sys.argv[2]) if len(sys.argv) > 2 else 120
BUDGET = 480

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
PORT = 9223   # not capture_web.py's port, so the two can run without evicting each other

subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | "
                "Where-Object { $_.CommandLine -like '*remote-debugging-port=%d*' } | "
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
                % PORT],
               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(1.5)

proc = subprocess.Popen([
    CHROME, "--headless=new", "--no-sandbox", "--disable-gpu-sandbox",
    "--enable-unsafe-swiftshader", "--use-gl=angle", "--use-angle=swiftshader",
    "--window-size=1280,720", f"--remote-debugging-port={PORT}",
    "--remote-allow-origins=*", "about:blank",
], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def targets():
    with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json", timeout=5) as r:
        return json.load(r)


ws = None
for _ in range(40):
    try:
        page = [t for t in targets() if t["type"] == "page"][0]
        ws = websocket.create_connection(page["webSocketDebuggerUrl"], timeout=20)
        break
    except Exception:
        time.sleep(0.5)
if ws is None:
    proc.kill(); sys.exit("could not attach to Chrome")

_id = [0]
console = []


def send(method, **params):
    _id[0] += 1
    ws.send(json.dumps({"id": _id[0], "method": method, "params": params}))
    deadline = time.time() + 600
    while time.time() < deadline:
        try:
            raw = ws.recv()
        except websocket.WebSocketTimeoutException:
            continue
        msg = json.loads(raw)
        if msg.get("id") == _id[0]:
            return msg.get("result", {})
        if msg.get("method") == "Runtime.consoleAPICalled":
            for a in msg["params"].get("args", []):
                t = str(a.get("value", ""))
                if t:
                    console.append(t)
    raise TimeoutError(method)


def evaluate(expr):
    r = send("Runtime.evaluate", expression=expr, returnByValue=True)
    return r.get("result", {}).get("value")


send("Runtime.enable")
send("Page.enable")
send("Page.navigate", url=URL)

print(f"loading {URL} ...")
start = time.time()
while time.time() - start < BUDGET:
    done = evaluate(
        "(() => { const b = document.querySelector('#unity-loading-bar');"
        "return !!(b && b.style.display === 'none'); })()")
    if done:
        print(f"loader finished after {time.time() - start:.0f}s")
        break
    time.sleep(3)
else:
    sys.exit("loader never finished")

print("\n  t(s)   wasm heap    JS heap   note")
base = None
t0 = time.time()
while time.time() - t0 < WATCH:
    heap = evaluate("(typeof plHeapBytes === 'function') ? plHeapBytes() : -1")
    js = evaluate("(performance.memory && performance.memory.usedJSHeapSize) || -1")
    if heap is None:
        heap = -1
    if heap == -1:
        note = "no plHeapBytes -- this build predates the hook"
    else:
        if base is None:
            base = heap
        note = "+%.0f MB since first sample" % ((heap - base) / 1048576)
    print("  %5.0f  %8s  %9s   %s"
          % (time.time() - t0,
             "%.0f MB" % (heap / 1048576) if heap > 0 else "?",
             "%.0f MB" % (js / 1048576) if js and js > 0 else "?",
             note))
    time.sleep(10)

oom = [c for c in console if "OOM" in c or "abort" in c.lower()]
if oom:
    print("\nconsole mentioned an abort:")
    for c in oom[:5]:
        print("   ", c[:200])

ws.close()
proc.kill()
