#!/usr/bin/env python3
"""PreToolUse hook: keep test/build output from flooding the context window.

Wraps test and build commands so only the last N lines reach Claude,
while preserving the original exit code (pipefail).
"""
import json
import re
import sys

TAIL_LINES = 120
RUNNERS = re.compile(
    r"^\s*(pytest|python -m pytest|npm (run )?test|npx (vitest|jest)|ctest|"
    r"cmake --build|dotnet (build|test)|go test|cargo (test|build))\b"
)


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        print("{}")
        return

    tool_input = payload.get("tool_input", {})
    cmd = tool_input.get("command", "")

    # Skip if not a runner, or already piped/redirected by Claude.
    if not RUNNERS.match(cmd) or "|" in cmd or "tail -n" in cmd:
        print("{}")
        return

    wrapped = f"set -o pipefail; ( {cmd} ) 2>&1 | tail -n {TAIL_LINES}"
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "updatedInput": {**tool_input, "command": wrapped},
        }
    }))


if __name__ == "__main__":
    main()
