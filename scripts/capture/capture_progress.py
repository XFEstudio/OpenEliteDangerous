"""Summarize local capture coverage without printing payloads, credentials, or player identities."""
from __future__ import annotations
import argparse
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root")
    args = parser.parse_args()
    root = Path(args.root).resolve(strict=True)
    operations = {}
    event_types = Counter()
    errors = Counter()
    native_milestones = []
    body_count = decoded_count = 0
    for capture in sorted(path.parent for path in root.rglob("events.jsonl")):
        events = capture / "events.jsonl"
        if not events.is_file():
            continue
        for line in events.read_text(encoding="utf-8").splitlines():
            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                continue
            event_types[item.get("type", "unknown")] += 1
            if item.get("type") in ("observer-error", "capture-error"):
                errors[capture.relative_to(root).as_posix() + ": " + item.get("stage", item.get("description", "unknown"))] += 1
            if item.get("type") == "native-milestone":
                native_milestones.append({"name": item.get("name"), "utc": item.get("utc")})
            if item.get("type") != "http-response":
                continue
            key = item.get("method", "?") + " " + item.get("path", "?")
            operation = operations.setdefault(key, {"method": item.get("method"), "path": item.get("path"), "responses": 0, "statuses": Counter(), "firstUtc": item.get("utc"), "lastUtc": item.get("utc")})
            operation["responses"] += 1
            operation["statuses"][str(item.get("status", 0))] += 1
            if item.get("utc"):
                operation["firstUtc"] = min(operation["firstUtc"], item["utc"])
                operation["lastUtc"] = max(operation["lastUtc"], item["utc"])
            if item.get("bodyFile"):
                body_count += 1
        index_path = capture / "decoded/index.json"
        if index_path.is_file():
            decoded = json.loads(index_path.read_text(encoding="utf-8"))
            decoded_count += sum(item.get("decoded", False) for item in decoded)
    summary = {"atUtc": datetime.now(timezone.utc).isoformat(), "officialOnlineEvidenceOnly": True, "offlineSoloValidated": False,
               "observedOperations": len(operations), "httpResponseBodies": body_count, "decodedResponseAndRequestBodies": decoded_count,
               "eventTypes": dict(event_types), "errorsByCapture": dict(errors), "nativeMilestones": native_milestones,
               "operations": [operations[key] for key in sorted(operations)]}
    temporary = root / "capture-progress.json.new"
    temporary.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(root / "capture-progress.json")
    print(json.dumps({key: value for key, value in summary.items() if key != "operations"}, ensure_ascii=False))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
