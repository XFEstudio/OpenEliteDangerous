"""Inspect captured field names and types, keeping all scalar values private."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def shape(value, depth=0):
    if isinstance(value, dict):
        if depth >= 2:
            return {"type": "object", "members": len(value)}
        return {"type": "object", "members": len(value), "fields": {key: shape(item, depth + 1) for key, item in list(value.items())[:10]}}
    if isinstance(value, list):
        return {"type": "array", "length": len(value), "firstItem": shape(value[0], depth + 1) if value else None}
    return type(value).__name__


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture")
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args()
    decoded = Path(args.capture).resolve(strict=True) / "decoded"
    index = json.loads((decoded / "index.json").read_text(encoding="utf-8"))
    selected = {item["path"]: item for item in index if item.get("decoded") and item.get("type") != "http-request-body" and any(item["path"].endswith(path) for path in args.paths)}
    result = []
    for path, item in sorted(selected.items()):
        source = (decoded / item["file"]).resolve(strict=True)
        if source.parent != decoded:
            raise ValueError("Document leaves the captured evidence directory")
        text = source.read_text(encoding="utf-8")
        if source.suffix == ".xml":
            root = ET.fromstring(text)
            document_shape = {"root": root.tag, "children": sorted({child.tag for child in root}), "firstChildChildren": sorted({child.tag for child in root[0]}) if len(root) else []}
        else:
            document_shape = shape(json.loads(text))
        result.append({"path": path, "id": item["id"], "length": item["length"], "shape": document_shape})
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
