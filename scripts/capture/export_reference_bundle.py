"""Export captured global definitions to a new local bundle; never export identity, sessions or player state."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

GAME_HASH = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988"
OPERATIONS = {
    "/2.0/elite/resources/dictionary", "/2.0/elite/resources/finance",
    "/2.0/elite/resources/loadingscreentips", "/2.0/elite/resources/microresources",
    "/2.0/elite/resources/permits",
    "/2.0/elite/resources/commodities",
    "/2.0/elite/resources/ussregion/list", "/2.0/elite/synthesis/recipes",
    "/2.0/elite/powerplay2/powers/list", "/2.0/elite/colonisation/resources/optionsfulllist",
    "/2.0/elite/colonisation/resources/marketlink/weighting",
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    capture = Path(args.capture).resolve(strict=True)
    profile = json.loads((capture / "profile.json").read_text(encoding="utf-8"))
    if profile["sha256"] != GAME_HASH:
        raise ValueError("The capture differs from the supported build")
    decoded = capture / "decoded"
    index = json.loads((decoded / "index.json").read_text(encoding="utf-8"))
    events = [json.loads(line) for line in (capture / "events.jsonl").read_text(encoding="utf-8").splitlines()]
    complete = {item["id"] for item in events if item.get("type") == "http-response" and item.get("curlResult") == 0 and not item.get("truncated")}
    selected = {}
    for item in index:
        if not item.get("decoded") or item.get("path") not in OPERATIONS:
            continue
        if item.get("type") == "http-response" and (item.get("status") != 200 or item["id"] not in complete):
            continue
        if item.get("type") not in ("http-response", "native-data-document"):
            continue
        source = (decoded / item["file"]).resolve(strict=True)
        if source.parent != decoded or source.suffix not in (".json", ".xml"):
            raise ValueError("Invalid reference document path or format")
        content = source.read_bytes()
        if not 0 < len(content) <= 16 * 1024 * 1024 or hashlib.sha256(content).hexdigest() != item["sha256"]:
            raise ValueError("Reference document integrity mismatch")
        if source.suffix == ".json":
            value = json.loads(content)
            if not isinstance(value, dict):
                raise ValueError("Reference JSON must be an object")
            if item["path"] == "/2.0/elite/powerplay2/powers/list" and not value.get("powers"):
                raise ValueError("An empty Powerplay catalogue cannot initialize the native spatial index")
            if item["path"] == "/2.0/elite/resources/permits":
                permits = value.get("permits")
                if set(value) != {"permits"} or not isinstance(permits,dict) or len(permits)>4096:
                    raise ValueError("Permit definitions must be an ID-keyed map")
                for identity, definition in permits.items():
                    if (not identity.isdecimal() or not 0<=int(identity)<=65535 or set(definition)!={"id","description","systems"}
                        or definition["id"]!=int(identity) or not isinstance(definition["description"],str)
                        or not isinstance(definition["systems"],list)
                        or any(not isinstance(address,int) or address<=0 for address in definition["systems"])):
                        raise ValueError("Invalid global permit definition")
        else:
            ET.fromstring(content)
        selected[item["path"]] = (item, source.suffix, content)
    if not selected:
        raise ValueError("The capture contains no complete supported reference documents")
    output = Path(args.out).resolve()
    output.mkdir(parents=True, exist_ok=False)
    documents = []
    for number, (operation, (item, suffix, content)) in enumerate(sorted(selected.items()), 1):
        filename = f"reference-{number:03d}{suffix}"
        (output / filename).write_bytes(content)
        documents.append({"operation": operation, "file": filename, "sha256": hashlib.sha256(content).hexdigest(),
                          "length": len(content), "sourceCapture": str(capture), "sourceEventId": item["id"], "sourceUtc": item.get("utc")})
    manifest = {"schema": 1, "build": "2026.09.03.332841", "gameSha256": GAME_HASH,
                "exportedAtUtc": datetime.now(timezone.utc).isoformat(), "documents": documents}
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"referenceDocuments": len(documents), "bytes": sum(item["length"] for item in documents), "playerStateExported": False, "sessionCredentialsExported": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
