"""Report capture completeness without printing authentication or player payloads."""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
from pathlib import Path


def summarize(capture: Path) -> dict:
    events = []
    malformed = 0
    for line in (capture / "events.jsonl").read_text(encoding="utf-8").splitlines():
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            malformed += 1
    index_path = capture / "decoded/index.json"
    decoded = json.loads(index_path.read_text(encoding="utf-8")) if index_path.is_file() else []
    responses = [item for item in events if item.get("type") == "http-response"]
    started = {item["id"] for item in events if item.get("type") == "http-start"}
    completed = {item["id"] for item in responses}
    operations = {}
    for item in responses:
        key = item.get("method", "?") + " " + item.get("path", "?")
        operation = operations.setdefault(key, {"responses": 0, "statuses": Counter(), "bytes": 0})
        operation["responses"] += 1
        operation["statuses"][str(item.get("status", 0))] += 1
        operation["bytes"] += item.get("length", 0)
    errors = [{key: item.get(key) for key in ("type", "id", "path", "stage", "description", "error") if key in item}
              for item in events if item.get("type") in ("observer-error", "capture-error", "capture-limit")]
    decode_errors = [{key: item.get(key) for key in ("id", "type", "path", "error") if key in item}
                     for item in decoded if item.get("error")]
    incomplete = [{"id": item.get("id"), "path": item.get("path"), "truncated": item.get("truncated", False),
                   "curlResult": item.get("curlResult")}
                  for item in responses if item.get("truncated") or item.get("curlResult", 0) != 0]
    missing_bodies = [item.get("id") for item in events if item.get("bodyFile") and not (capture / item["bodyFile"]).is_file()]
    result = {
        "atUtc": datetime.now(timezone.utc).isoformat(),
        "officialOnlineEvidenceOnly": True,
        "offlineSoloValidated": False,
        "events": dict(Counter(item.get("type", "unknown") for item in events)),
        "responses": len(responses),
        "operationCount": len(operations),
        "responseBytes": sum(item.get("length", 0) for item in responses),
        "decodedBodies": sum(bool(item.get("decoded")) for item in decoded),
        "pendingRequestIds": sorted(started - completed),
        "firstResponseUtc": min((item.get("utc", "") for item in responses), default=None),
        "lastResponseUtc": max((item.get("utc", "") for item in responses), default=None),
        "nativeMilestones": [{"name": item.get("name"), "utc": item.get("utc")} for item in events if item.get("type") == "native-milestone"],
        "cachedDocuments": [{"path": item.get("path"), "length": item.get("length"), "utc": item.get("utc")} for item in events if item.get("type") == "native-data-document"],
        "observerErrors": errors,
        "decodeErrors": decode_errors,
        "incompleteResponses": incomplete,
        "missingBodyFiles": missing_bodies,
        "partialOrMalformedEventLines": malformed,
        "operations": {key: operations[key] for key in sorted(operations)},
    }
    temporary = capture / "session-summary.json.new"
    temporary.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(capture / "session-summary.json")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture")
    args = parser.parse_args()
    result = summarize(Path(args.capture).resolve(strict=True))
    print(json.dumps({key: value for key, value in result.items() if key != "operations"}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
