# -*- coding: utf-8 -*-
"""Screenshots a deployed WebGL page after it has actually finished loading.

Chrome's --screenshot fires on the load event, which for a Unity page is the moment the
loader script arrives -- long before 59 MB of Brotli has been inflated in JavaScript and the
wasm has started. Every capture taken that way shows the Unity splash and a progress bar, and
says nothing about whether the game runs.

This drives the browser over the DevTools protocol instead: open the page, poll the page's own
DOM for the loader's progress bar disappearing, and only then capture. It also drains the
console so a failure has an error attached rather than being an indefinite splash screen.
"""
import base64, io, json, subprocess, sys, time, urllib.request
import websocket

URL = sys.argv[1] if len(sys.argv) > 1 else "https://ojh650765.github.io/PProejct/"
OUT = sys.argv[2] if len(sys.argv) > 2 else "Temp/deployed_loaded.png"
BUDGET = int(sys.argv[3]) if len(sys.argv) > 3 else 480

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
PORT = 9222

# Any Chrome still holding the debugging port is a previous run that crashed before it could
# clean up. Left alone, the new Chrome silently fails to bind the port, targets() answers the
# OLD browser, and the run attaches to a dead tab -- which presents as the very first CDP call
# timing out after the full budget, ten minutes from anything informative.
subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | "
                "Where-Object { $_.CommandLine -like '*remote-debugging-port*' } | "
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"],
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

# Per-recv socket timeout is short so a stalled page is noticed; this is how long a single
# CDP call may take in total before the run gives up on it.
CALL_BUDGET = 600

_id = [0]
def send(method, **params):
    """One CDP call, tolerant of the page stalling the protocol thread.

    A Unity page under swiftshader routinely blocks the renderer for tens of seconds at a
    time -- inflating 70 MB of asset data, compiling shader variants, the first frame. CDP
    replies queue behind that, and a single recv timeout used to kill the whole run with a
    stack trace two hundred frames from anything meaningful, after four minutes of loading.
    A timed-out recv is not a dead connection here; it is a busy one, so it is retried until
    the call's own budget is spent.
    """
    _id[0] += 1
    ws.send(json.dumps({"id": _id[0], "method": method, "params": params}))
    deadline = time.time() + CALL_BUDGET
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
                text = str(a.get("value", ""))
                if text: console.append(text)
        if msg.get("method") == "Runtime.exceptionThrown":
            d = msg["params"]["exceptionDetails"]
            console.append("EXCEPTION: " + d.get("text", "") + " " +
                           str(d.get("exception", {}).get("description", ""))[:300])
    raise TimeoutError(f"{method} got no reply within {CALL_BUDGET}s")

console = []
send("Runtime.enable"); send("Page.enable")
send("Page.navigate", url=URL)

PROBE = """(() => {
  const bar = document.querySelector('#unity-progress-bar-full');
  const banner = document.querySelector('#unity-warning');
  const canvas = document.querySelector('#unity-canvas');
  return JSON.stringify({
    width: bar ? bar.style.width : null,
    hidden: !!(document.querySelector('#unity-loading-bar') &&
               document.querySelector('#unity-loading-bar').style.display === 'none'),
    canvas: !!canvas,
    warn: banner ? banner.textContent.trim().slice(0,200) : ''
  });
})()"""

start = time.time()
state = {}
while time.time() - start < BUDGET:
    try:
        r = send("Runtime.evaluate", expression=PROBE, returnByValue=True)
        state = json.loads(r["result"]["value"])
    except Exception:
        pass
    if state.get("hidden"):
        print(f"loader finished after {time.time()-start:.0f}s")
        break
    time.sleep(3)
else:
    print(f"loader still running after {BUDGET}s; last progress={state.get('width')}")

time.sleep(4)   # a moment for the first frames to render

# Advance past the opening so the capture shows the world.
#
# The dialogue box draws its portrait through a UI Image and the world draws its people
# through the alpha-clipped billboard shader -- two completely different paths, and the fault
# being chased is in the second. A screenshot of the opening says nothing about it.
import os as _os

def click(x, y):
    """A real click at canvas coordinates.

    The opening asks a question with two buttons, and space cannot answer it -- so a capture
    driven by keys alone stops on that screen forever and never reaches the world, which is
    where the sprites this is chasing are drawn.
    """
    # Move, press, wait, release. The wait is the part that matters: Unity's EventSystem
    # reads the pointer once per frame, so a press and a release dispatched back to back are
    # both collapsed into the same poll and the button sees only "up" — it highlights under
    # the cursor and never fires. Holding the press across several frames makes it a click
    # the game can actually observe.
    send("Input.dispatchMouseEvent", type="mouseMoved", x=x, y=y, buttons=0)
    time.sleep(0.25)
    send("Input.dispatchMouseEvent", type="mousePressed", x=x, y=y, button="left",
         clickCount=1, buttons=1)
    time.sleep(0.30)
    send("Input.dispatchMouseEvent", type="mouseReleased", x=x, y=y, button="left",
         clickCount=1, buttons=0)
    time.sleep(0.8)

ADVANCE = int(_os.environ.get("ADVANCE_PRESSES", "0"))
CLICKS = _os.environ.get("CLICK_POINTS", "")
for _ in range(ADVANCE):
    for kind, t in (("keyDown", "rawKeyDown"), ("keyUp", "keyUp")):
        send("Input.dispatchKeyEvent", type=t, windowsVirtualKeyCode=32,
             nativeVirtualKeyCode=32, key=" ", code="Space", text=" ")
    time.sleep(1.1)
if ADVANCE:
    time.sleep(2)

for point in [p for p in CLICKS.split(";") if p.strip()]:
    px, py = (int(v) for v in point.split(","))
    click(px, py)
    for _ in range(int(_os.environ.get("AFTER_CLICK_PRESSES", "6"))):
        for t in ("rawKeyDown", "keyUp"):
            send("Input.dispatchKeyEvent", type=t, windowsVirtualKeyCode=32,
                 nativeVirtualKeyCode=32, key=" ", code="Space", text=" ")
        time.sleep(1.0)
    time.sleep(2)

# Enter answers the name prompt, which space cannot -- a run that only presses space stops
# there and never reaches the world, which is where the sprites being chased are drawn.
for _ in range(int(_os.environ.get("ENTER_PRESSES", "0"))):
    for t in ("rawKeyDown", "keyUp"):
        send("Input.dispatchKeyEvent", type=t, windowsVirtualKeyCode=13,
             nativeVirtualKeyCode=13, key="Enter", code="Enter", text=chr(13))
    time.sleep(0.6)
    for _ in range(int(_os.environ.get("TAIL_PRESSES", "10"))):
        for t in ("rawKeyDown", "keyUp"):
            send("Input.dispatchKeyEvent", type=t, windowsVirtualKeyCode=32,
                 nativeVirtualKeyCode=32, key=" ", code="Space", text=" ")
        time.sleep(0.5)
    time.sleep(3)

def capture():
    """The screenshot, with the surface capture behind a shorter leash.

    Page.captureScreenshot's default asks the renderer for a *fresh* surface frame, and a Unity
    page under swiftshader can hold the renderer for minutes at a time compiling the world's
    shader variants -- so the request simply queues behind that and the run dies ten minutes
    later having written nothing, after everything it was set up to photograph has already
    happened. fromSurface=False takes the frame the browser has already composited instead: a
    fraction of a second old, and available while the renderer is still busy.
    """
    global CALL_BUDGET
    was = CALL_BUDGET
    try:
        CALL_BUDGET = 90
        return send("Page.captureScreenshot", format="png")
    except TimeoutError:
        print("surface capture stalled; taking the last composited frame instead")
        CALL_BUDGET = 120
        return send("Page.captureScreenshot", format="png", fromSurface=False)
    finally:
        CALL_BUDGET = was

shot = capture()
open(OUT, "wb").write(base64.b64decode(shot["data"]))
print("wrote", OUT)

if state.get("warn"):
    print("PAGE WARNING:", state["warn"])
io.open("Temp/web_console.txt", "w", encoding="utf-8").write(chr(10).join(console))
print("console lines:", len(console), "-> Temp/web_console.txt")
for line in console[-8:]:
    print("  console:", line[:160])

ws.close(); proc.kill()
