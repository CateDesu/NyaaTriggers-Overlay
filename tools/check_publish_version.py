"""Refuse an older queued run before it changes the release channel."""
import json
import re
import sys


def version_parts(value: str) -> tuple[int, ...]:
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", value):
        raise ValueError(f"Invalid release version: {value!r}")
    parts = tuple(map(int, value.split(".")))
    return parts + (0,) * (4 - len(parts))


def should_publish(listing: list[dict], candidate: str) -> bool:
    current = [row["AssemblyVersion"] for row in listing if row.get("InternalName") == "NyaaTriggers"]
    if len(current) != 1:
        raise ValueError("Expected one NyaaTriggers entry in the current listing")
    return version_parts(candidate) > version_parts(current[0])


if __name__ == "__main__":
    with open(sys.argv[1], encoding="utf-8") as stream:
        publish = should_publish(json.load(stream), sys.argv[2])
    print(f"publish={str(publish).lower()}")
