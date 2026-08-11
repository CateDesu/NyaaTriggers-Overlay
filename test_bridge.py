#!/usr/bin/env python3
"""Drive the NyaaTriggers companion plugin without the app.

Connects to the plugin's loopback WebSocket and feeds it a fake pull: a
timeline that counts down, and callouts at fixed points. Lets the in-game
drawing be checked before the app knows anything about the plugin.

    python test_bridge.py [--port 27080] [--speed 1.0]
    python test_bridge.py --dps          fake encounter for the DPS meter instead

Needs the `websockets` package (the app itself uses Qt's client instead; this
is a standalone tool, not part of the app).

Note on --speed: the plugin interpolates the fight clock in real time between
ticks, so a fast-forwarded clock makes the bars step rather than glide. Above
1x, trust the callout timings, not the animation.
"""
import argparse
import asyncio
import json
import random
import sys

try:
    from websockets.asyncio.client import connect
    from websockets.exceptions import WebSocketException
except ImportError:
    print("This tool needs the websockets package:  pip install websockets",
          file=sys.stderr)
    raise SystemExit(1)

# Wire format this tool speaks. Must match BridgeHost.ProtocolVersion.
PROTOCOL_VERSION = 1

# A plugin that upgrades the socket but never greets is broken; do not wait on
# it forever with nothing on screen to say so.
HELLO_TIMEOUT = 5.0

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

# (name, job, base encdps, base enchps, is the local player) - the same
# shape the app's meter rows produce, already close to sorted; each frame
# jitters and re-sorts them.
PARTY = [
    ("Alphinaud L", "SGE", 10234.5, 9123.4, False),
    ("Beta Tester", "DRG", 9876.0, 0.0, True),
    ("Cid Garlond", "MCH", 9450.0, 0.0, False),
    ("Dulia Chai", "WHM", 9012.0, 8456.0, False),
    ("Estinien W", "DRG", 8780.0, 0.0, False),
    ("Five Heads", "BLM", 8540.0, 0.0, False),
    ("G'raha Tia", "RDM", 8100.0, 0.0, False),
    ("Hythlodaeus", "PLD", 7600.0, 322.0, False),
]

TICK_SECONDS = 0.25

# The app's meter cadence: one dps frame per second while the encounter runs.
DPS_SECONDS = 1.0
DPS_FRAMES = 6


async def handshake(ws) -> None:
    """Read the hello and refuse to drive a plugin we do not understand."""
    try:
        raw = await asyncio.wait_for(ws.recv(), timeout=HELLO_TIMEOUT)
    except asyncio.TimeoutError:
        raise SystemExit(
            f"connected, but no hello within {HELLO_TIMEOUT:g}s. "
            "Something is listening on this port, but it is not the plugin.")

    hello = json.loads(raw)
    print(f"plugin says: {hello}")

    # Gate rather than warn: driving a plugin whose wire format we do not
    # understand produces confusing in-game behaviour, not a clean failure.
    if hello.get("protocol") != PROTOCOL_VERSION:
        raise SystemExit(
            f"plugin speaks protocol {hello.get('protocol')!r}, this tool speaks "
            f"{PROTOCOL_VERSION}. Update whichever is older.")


async def run(port: int, speed: float) -> None:
    url = f"ws://127.0.0.1:{port}/"
    print(f"connecting to {url}")
    async with connect(url) as ws:
        await handshake(ws)

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


async def run_dps(port: int) -> None:
    url = f"ws://127.0.0.1:{port}/"
    print(f"connecting to {url}")
    async with connect(url) as ws:
        await handshake(ws)

        # Seeded so the demo looks the same every run.
        rng = random.Random(42)
        print(f"sending {DPS_FRAMES} dps frames, one per second (ctrl-c to stop)")
        for frame in range(DPS_FRAMES):
            rows = []
            for name, job, base, base_hps, is_self in PARTY:
                dps = round(base * rng.uniform(0.95, 1.05), 1)
                hps = round(base_hps * rng.uniform(0.95, 1.05), 1)
                rows.append([name, job, dps, 0.0, hps, is_self])

            rows.sort(key=lambda row: row[2], reverse=True)
            total = sum(row[2] for row in rows)
            for row in rows:
                row[3] = round(row[2] / total * 100.0, 1)

            await ws.send(json.dumps({
                "c": "dps",
                "show": True,
                "enc": {
                    "t": "Everkeep",
                    "d": f"03:{12 + frame:02d}",
                    "dps": round(total, 1),
                },
                "rows": rows,
            }))
            print(f"  03:{12 + frame:02d}  party {total:,.1f} "
                  f"(top: {rows[0][0]} {rows[0][2]:,.1f})")
            await asyncio.sleep(DPS_SECONDS)

        await ws.send(json.dumps({"c": "dps", "show": False}))
        print("encounter over, meter hidden")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=27080,
                        help="plugin port (default 27080)")
    parser.add_argument("--speed", type=float, default=1.0,
                        help="clock multiplier, e.g. 4 to run the pull fast. "
                             "Above 1x the bars step instead of gliding, because "
                             "the plugin interpolates the clock in real time")
    parser.add_argument("--dps", action="store_true",
                        help="feed the DPS meter a fake encounter instead of "
                             "the timeline demo")
    args = parser.parse_args()

    try:
        asyncio.run(run_dps(args.port) if args.dps else run(args.port, args.speed))
    except KeyboardInterrupt:
        print("\nstopped")
    except ConnectionRefusedError:
        raise SystemExit(
            f"nothing is listening on 127.0.0.1:{args.port}. Start the game with the "
            "plugin enabled, and check the port matches the one in /nyaa.")
    except OSError as exc:
        raise SystemExit(f"could not reach the plugin: {exc}")
    except WebSocketException as exc:
        raise SystemExit(f"the plugin dropped the connection: {exc}")


if __name__ == "__main__":
    main()
