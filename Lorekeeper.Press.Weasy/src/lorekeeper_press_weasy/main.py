"""JSON stdin/stdout process entry point."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .protocol import Diagnostic, response
from .render import render_request


MAX_REQUEST_BYTES = 12 * 1024 * 1024


class ProtocolArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise ValueError(message)


def main() -> int:
    parser = ProtocolArgumentParser(add_help=False)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--cmyk-profile", type=Path)
    try:
        arguments = parser.parse_args()
    except ValueError as exception:
        result = response(
            "",
            "failed",
            [Diagnostic("error", "PRESS_STARTUP_ARGUMENTS_INVALID", str(exception))],
        )
        json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
        sys.stdout.write("\n")
        return 2

    data = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
    if len(data) > MAX_REQUEST_BYTES:
        result = response(
            "",
            "failed",
            [Diagnostic("error", "PRESS_REQUEST_TOO_LARGE", "The request exceeds 12 MiB.")],
        )
    else:
        try:
            value = json.loads(data)
        except (UnicodeDecodeError, json.JSONDecodeError) as exception:
            result = response(
                "",
                "failed",
                [Diagnostic("error", "PRESS_REQUEST_JSON_INVALID", str(exception))],
            )
        else:
            result = render_request(value, arguments.output_root, arguments.cmyk_profile)
    json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write("\n")
    return 0 if result["status"] == "completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
