#!/usr/bin/env python3
"""Drive the NyaaTriggers companion plugin without the app.

Connects to the plugin's loopback WebSocket and feeds it a fake pull: a
timeline that counts down, and callouts at fixed points. Lets the in-game
drawing be checked before the app knows anything about the plugin.

    python test_bridge.py [--port 27080] [--speed 1.0]

Needs the `websockets` package (the app itself uses Qt's client instead; this
is a standalone tool, not part of the app).

Note on --speed: the plugin interpolates the fight clock in real time between
ticks, so a fast-forwarded clock makes the bars step rather than glide. Above
1x, trust the callout timings, not the animation.
"""
import argparse
import asyncio
import json
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

TICK_SECONDS = 0.25


async def run(port: int, speed: float) -> None:
    url = f"ws://127.0.0.1:{port}/"
    print(f"connecting to {url}")
    async with connect(url) as ws:
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
                        help="clock multiplier, e.g. 4 to run the pull fast. "
                             "Above 1x the bars step instead of gliding, because "
                             "the plugin interpolates the clock in real time")
    args = parser.parse_args()

    try:
        asyncio.run(run(args.port, args.speed))
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
