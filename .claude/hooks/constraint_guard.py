#!/usr/bin/env python3
"""Nexora constraint guard for Claude Code.

pre  (PreToolUse, Bash):  deny shell commands that break hard constraints
                          (WinRM/remoting, AD writes); ask before touching
                          live operator PCs or running PsExec.
post (PostToolUse, Edit|Write|MultiEdit): scan written code for constraint
                          violations and block, so Claude must fix them.

Stdlib only. Fails open on malformed input (prints {}), never crashes the session.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import PurePath

PILOT_HOST = "wks-111"

# ---------- PreToolUse: shell commands ----------
DENY_CMD: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"\b(Enter|New)-PSSession\b", re.I), "C4: PowerShell remoting is forbidden (no WinRM)."),
    (re.compile(r"\bInvoke-Command\b[^|;]*-ComputerName\b", re.I), "C4: Invoke-Command -ComputerName uses WinRM."),
    (re.compile(r"\bEnable-PSRemoting\b|\bwinrm(\.cmd)?\s|\bWSMan:", re.I), "C4: WinRM configuration is forbidden."),
    (re.compile(r"\b(Set|New|Remove|Add|Move|Rename|Enable|Disable|Unlock|Clear|Reset)-AD\w+", re.I),
     "C3: AD write cmdlets are forbidden."),
    (re.compile(r"\b(setspn|dsmod|dsadd|dsrm|dsmove|netdom)\b", re.I), "C3: AD-modifying tools are forbidden."),
]

ASK_CMD: list[tuple[re.Pattern[str], str]] = [
    (re.compile(rf"\\\\(?!{PILOT_HOST}\b)wks\w+", re.I), "Checkpoint: touches a live operator PC share."),
    (re.compile(r"\bpsexec(64)?(\.exe)?\b", re.I), "Checkpoint: PsExec runs code on a remote PC."),
    (re.compile(r"nexora\.ps1\b.*-Action\s+(Install|Uninstall)", re.I), "Checkpoint: elevated install/uninstall."),
]

# ---------- PostToolUse: written code ----------
SKIP_DIRS = {"docs", ".claude", "node_modules", "bin", "obj"}
SKIP_SUFFIXES = {".md", ".txt"}

# (pattern, message, file-suffix filter or None, "block" | "warn")
# "block" = hard constraint, Claude must rewrite. "warn" = needs security-reviewer judgement.
CODE_RULES: list[tuple[re.Pattern[str], str, set[str] | None, str]] = [
    (re.compile(r"\bUseHttpsRedirection\b|\bUseHsts\b|\bAddHttpsRedirection\b"),
     "C1: HTTPS redirect/HSTS is forbidden (HTTP only).", None, "block"),
    (re.compile(r"\bWSManConnectionInfo\b|\b(Enter|New)-PSSession\b|Invoke-Command[^\n]*-ComputerName", re.I),
     "C4: WinRM/PS remoting is forbidden; use WMI/CIM, SYSTEM scheduled task or PsExec via the helper.",
     None, "block"),
    (re.compile(r"[\"']?CredentialOps[\"']?\s*[:=]\s*true\b", re.I),
     "C7: CredentialOps must stay false until HTTPS.", None, "block"),
    (re.compile(r"#Requires\s+-Version\s+[67]|ForEach-Object\s+-Parallel|\s\?\?\s|\s\?\.\w", re.I),
     "C8: Script must be PowerShell 5.1-compatible (no PS7-only syntax).", {".ps1", ".psm1"}, "block"),
    (re.compile(r"^\s*(Import-Module|#Requires\s+-Modules)\b", re.I | re.M),
     "C8: Endpoint scripts must not depend on external modules.", {".ps1", ".psm1"}, "block"),
    # AD writes: forbidden for Nexora's own identity/infrastructure. An operator-initiated action
    # running under the operator's token (via the helper) may be legitimate -> reviewer decides.
    (re.compile(r"\.CommitChanges\s*\(|UserPrincipal[^\n]*\.Save\s*\(|\b(Set|New|Remove|Add|Unlock)-AD\w+", re.I),
     "C3/C5: AD write detected. Allowed ONLY as an operator-initiated helper action under the operator's "
     "own token, audited; never from the server identity. Flag for security-reviewer.", None, "warn"),
]


def emit(obj: dict) -> None:
    print(json.dumps(obj))


def pre(payload: dict) -> None:
    cmd = payload.get("tool_input", {}).get("command", "") or ""
    for rx, why in DENY_CMD:
        if rx.search(cmd):
            emit({"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                         "permissionDecision": "deny",
                                         "permissionDecisionReason": f"Nexora {why}"}})
            return
    for rx, why in ASK_CMD:
        if rx.search(cmd):
            emit({"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                         "permissionDecision": "ask",
                                         "permissionDecisionReason": f"Nexora {why}"}})
            return
    emit({})


def written_text(tool_input: dict) -> str:
    if "content" in tool_input:
        return tool_input.get("content") or ""
    if "new_string" in tool_input:
        return tool_input.get("new_string") or ""
    return "\n".join(e.get("new_string", "") for e in tool_input.get("edits", []) or [])


def post(payload: dict) -> None:
    tool_input = payload.get("tool_input", {}) or {}
    path = PurePath(str(tool_input.get("file_path", "")).replace("\\", "/"))
    if path.suffix.lower() in SKIP_SUFFIXES or SKIP_DIRS.intersection(p.lower() for p in path.parts):
        emit({})
        return

    text = written_text(tool_input)
    suffix = path.suffix.lower()
    blocks, warns = [], []
    for rx, why, only, level in CODE_RULES:
        if (only is None or suffix in only) and rx.search(text):
            (blocks if level == "block" else warns).append(why)

    if blocks:
        emit({"decision": "block",
              "reason": f"Nexora constraint violation in {path.name}: " + " | ".join(blocks + warns)
                        + " Revert or rewrite this change to comply."})
    elif warns:
        emit({"hookSpecificOutput": {"hookEventName": "PostToolUse",
                                     "additionalContext": f"Nexora warning in {path.name}: "
                                                          + " | ".join(warns)}})
    else:
        emit({})


def main() -> None:
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        emit({})
        return
    {"pre": pre, "post": post}.get(mode, lambda _p: emit({}))(payload)


if __name__ == "__main__":
    main()
