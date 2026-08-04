#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path


expected = "COPILOT_SMEVALS_OK"
path = Path(os.environ["SMEVALS_RUN_DIR"]) / "output.txt"
actual = path.read_text(encoding="utf-8").strip()
ok = actual == expected
print(
    json.dumps(
        {
            "score": 1.0 if ok else 0.0,
            "metrics": {"exact_response": ok},
            "notes": "response is exact" if ok else f"response was {actual!r}",
        }
    )
)
sys.exit(0 if ok else 1)
