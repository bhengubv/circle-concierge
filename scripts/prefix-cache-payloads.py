"""The three payloads the prefix-cache comment says to re-run before believing it.

    1. a 3,075-char USER prompt   -> completed with cache off
    2. a 16-char SYSTEM turn      -> faulted at 121 chars fed, cache on
    3. a 6,882-char payload       -> faulted as system, completed as user

Run against Concierge.Model.Host, one payload per host process, so a fault in one
cannot be mistaken for a fault in the next.
"""
import json
import os
import subprocess
import sys
import time

HOST = sys.argv[1]
CACHE = sys.argv[2] if len(sys.argv) > 2 else "0"


def run(name, turns, budget=180):
    env = dict(os.environ, CONCIERGE_PREFIX_CACHE=CACHE)
    p = subprocess.Popen(
        [HOST],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", env=env, bufsize=1)

    began = time.time()
    p.stdin.write(json.dumps({"op": "hello"}) + "\n")
    p.stdin.flush()

    ready = None
    while time.time() - began < budget:
        line = p.stdout.readline()
        if not line:
            break
        try:
            said = json.loads(line)
        except Exception:
            continue
        if "ready" in said:
            ready = said
            break

    if ready is None or not ready.get("ready"):
        p.kill()
        return name, "no model", 0, (ready or {}).get("status")

    p.stdin.write(json.dumps({"op": "stream", "id": 1, "turns": turns}) + "\n")
    p.stdin.flush()

    fed = 0
    done = False
    error = None
    while time.time() - began < budget:
        line = p.stdout.readline()
        if not line:
            break
        try:
            said = json.loads(line)
        except Exception:
            continue
        if said.get("chunk"):
            fed += len(said["chunk"])
        if said.get("error"):
            error = said["error"]
        if said.get("done"):
            done = True
            break

    alive = p.poll() is None
    if not done and not alive:
        outcome = "FAULTED (pipe closed)"
    elif error:
        outcome = f"error: {error}"
    elif done:
        outcome = "completed"
    else:
        outcome = "timed out"

    p.kill()
    return name, outcome, fed, None


LONG_USER = "Describe a quiet harbour at dawn. " * 94      # ~3,075 chars
HUGE = "Concierge is a local-first assistant. " * 182      # ~6,882 chars

print(f"prefix cache = {CACHE}\n")

for name, turns in [
    ("1. 3,075-char USER prompt", [{"role": "user", "content": LONG_USER[:3075]}]),
    ("2. 16-char SYSTEM turn", [{"role": "system", "content": "You are helpful"},
                                {"role": "user", "content": "Say hello."}]),
    ("3. 6,882 chars as SYSTEM", [{"role": "system", "content": HUGE[:6882]},
                                  {"role": "user", "content": "Say hello."}]),
    ("4. 6,882 chars as USER", [{"role": "user", "content": HUGE[:6882]}]),
]:
    n, outcome, fed, why = run(name, turns)
    print(f"{n:<28} {outcome:<24} fed={fed}" + (f"  [{why}]" if why else ""))
