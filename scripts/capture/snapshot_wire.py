"""Copy completed, PID-correlated capture segments into a reproducible analysis snapshot."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wire", required=True)
    parser.add_argument("--tools", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    wire = Path(args.wire).resolve(strict=True)
    tool_root = Path(args.tools).resolve(strict=True)
    mergecap = (tool_root / "mergecap.exe").resolve(strict=True)
    tshark = (tool_root / "tshark.exe").resolve(strict=True)
    ownership = [json.loads(line) for line in (wire / "ownership.jsonl").read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    selected = []
    for item in ownership:
        source = (wire / item["file"]).resolve(strict=True)
        if source.parent != wire:
            raise ValueError("Capture segment leaves the evidence directory")
        if source.with_suffix(".log").is_file():
            selected.append((source, item))
    if not selected:
        raise ValueError("No completed segments are available yet")
    output = Path(args.out).resolve()
    output.mkdir(parents=True, exist_ok=False)
    merged = output / "wire.pcapng"
    subprocess.run([str(mergecap), "-w", str(merged), *[str(item[0]) for item in selected]], check=True, capture_output=True)
    fields = ["frame.number", "frame.time_epoch", "frame.interface_id", "ip.src", "ip.dst", "ipv6.src", "ipv6.dst", "udp.srcport", "udp.dstport", "udp.payload"]
    command = [str(tshark), "-n", "-r", str(merged), "-Y", "udp", "-T", "fields", "-E", "header=y", "-E", "separator=,", "-E", "quote=d"]
    for field in fields:
        command.extend(["-e", field])
    with (output / "udp.csv").open("wb") as csv_output:
        subprocess.run(command, check=True, stdout=csv_output, stderr=subprocess.PIPE)
    provenance = {
        "atUtc": datetime.now(timezone.utc).isoformat(),
        "scope": "Completed PID-correlated socket-port captures; overlapping segments and multiple interfaces may duplicate packets.",
        "fullProcessEgressProven": False,
        "sourceWireDirectory": str(wire),
        "segments": [{"file": source.name, "length": source.stat().st_size,
                      "sha256": hashlib.sha256(source.read_bytes()).hexdigest(), "ownership": item}
                     for source, item in selected],
        "mergedLength": merged.stat().st_size,
        "mergedSha256": hashlib.sha256(merged.read_bytes()).hexdigest(),
    }
    (output / "provenance.json").write_text(json.dumps(provenance, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"completedSegments": len(selected), "snapshotBytes": provenance["mergedLength"], "fullProcessEgressProven": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
