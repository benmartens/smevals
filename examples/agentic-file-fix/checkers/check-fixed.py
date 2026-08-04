#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path


expected = "COPILOT_SMEVALS_FIXED"
path = Path(os.environ["SMEVALS_RUN_DIR"]) / "workspace" / "status.txt"
actual = path.read_text(encoding="utf-8").strip() if path.is_file() else None
ok = actual == expected
print(
    json.dumps(
        {
            "score": 1.0 if ok else 0.0,
            "metrics": {"status_correct": ok},
            "notes": "status.txt is correct" if ok else f"status.txt was {actual!r}",
        }
    )
)
sys.exit(0 if ok else 1)
