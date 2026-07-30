"""PyInstaller-compatible process launcher with pre-native font validation."""

import json
import os
import sys
from pathlib import Path

from lorekeeper_press_weasy.protocol import Diagnostic, response
from lorekeeper_press_weasy.startup import validate_font_environment


if __name__ == "__main__":
    font_errors = validate_font_environment(Path(sys.executable), os.environ)
    if font_errors:
        result = response(
            "",
            "failed",
            [
                Diagnostic("error", "PRESS_FONT_ENVIRONMENT_INVALID", message)
                for message in font_errors
            ],
        )
        print(json.dumps(result, separators=(",", ":")))
        raise SystemExit(2)

    from lorekeeper_press_weasy.main import main

    raise SystemExit(main())
