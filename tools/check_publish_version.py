"""Refuse an older queued run before it changes the release channel."""
import json
import sys


def version_parts(value: str) -> tuple[int, ...]:
    if not isinstance(value, str):
        raise TypeError("Release version must be a string")
    fields = value.split(".", 4)
    if len(fields) not in (3, 4) or not all(field.isdecimal() for field in fields):
        raise ValueError(f"Invalid release version: {value!r}")
    parts = tuple(map(int, fields))
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
