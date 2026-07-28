#!/usr/bin/env python3
"""Drive the NyaaTriggers companion plugin without the app.

Connects to the plugin's loopback WebSocket and feeds it a fake pull: a
timeline that counts down, and callouts at fixed points. Lets the in-game
drawing be checked before the app knows anything about the plugin.

    python plugin/test_bridge.py [--port 27080] [--speed 1.0]

Needs the `websockets` package (the app itself uses Qt's client instead; this
is a standalone tool, not part of the app).
"""
import argparse
import asyncio
import contextlib
import json
import sys

try:
    from websockets.asyncio.client import connect
except ImportError:
    print("This tool needs the websockets package:  pip install websockets",
          file=sys.stderr)
    raise SystemExit(1)

# (timeline second, label) - the same shape TimelineEngine.upcoming() produces.
SCHEDULE = [
    (8.0, "Wing of Ruin"),
    (16.0, "Akh Morn"),
    (24.0, "Stack"),
    (33.0, "Spread"),
    (41.0, "Tower soak"),
    (52.0, "Enrage"),
]

# (timeline second, text, severity)
CALLOUTS = [
    (6.0, "Wing of Ruin - move out", "info"),
    (14.0, "Akh Morn - stack for towers", "alert"),
    (22.0, "STACK", "alarm"),
    (31.0, "SPREAD", "alarm"),
    (39.0, "Soak your tower", "alert"),
]

TICK_SECONDS = 0.25


async def run(port: int, speed: float) -> None:
    url = f"ws://127.0.0.1:{port}/"
    print(f"connecting to {url}")
    async with connect(url) as ws:
        hello = json.loads(await ws.recv())
        print(f"plugin says: {hello}")
        if hello.get("protocol") != 1:
            print(f"unexpected protocol {hello.get('protocol')!r}, expected 1",
                  file=sys.stderr)

        await ws.send(json.dumps({"c": "timeline", "v": [list(e) for e in SCHEDULE]}))
        print(f"sent {len(SCHEDULE)} timeline entries; running the clock "
              f"at {speed}x (ctrl-c to stop)")

        clock = 0.0
        fired = set()
        end = SCHEDULE[-1][0] + 5.0
        while clock < end:
            await ws.send(json.dumps({"c": "tick", "t": round(clock, 2)}))

            for index, (at, text, severity) in enumerate(CALLOUTS):
                if index not in fired and clock >= at:
                    fired.add(index)
                    await ws.send(json.dumps(
                        {"c": "alert", "text": text, "sev": severity}))
                    print(f"  {clock:6.1f}  [{severity}] {text}")

            await asyncio.sleep(TICK_SECONDS / max(speed, 0.05))
            clock += TICK_SECONDS

        await ws.send(json.dumps({"c": "clear"}))
        print("pull over, cleared")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=27080,
                        help="plugin port (default 27080)")
    parser.add_argument("--speed", type=float, default=1.0,
                        help="clock multiplier, e.g. 4 to run the pull fast")
    args = parser.parse_args()

    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(run(args.port, args.speed))


if __name__ == "__main__":
    main()
