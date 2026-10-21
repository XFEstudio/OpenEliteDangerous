"""Read captured UDP CSV with captured directory keys; never opens sockets or prints keys."""
from __future__ import annotations
import argparse
import csv
import hashlib
import json
from pathlib import Path
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms
from cryptography.hazmat.decrepit.ciphers.modes import OFB

def decode(packet: bytes, key: bytes) -> tuple[dict, bytes]:
    if not 10 <= len(packet) <= 65507 or len(key) != 32:
        raise ValueError("Unsupported datagram size or key")
    header = int.from_bytes(packet[:8], "little")
    counter = header >> 49
    decryptor = Cipher(algorithms.AES(key), OFB(counter.to_bytes(4, "little") * 4)).decryptor()
    body = decryptor.update(packet[8:]) + decryptor.finalize()
    checksum = int.from_bytes(body[:2], "little")
    crc = 0x1337
    for value in packet[:8] + b"\0\0" + body[2:]:
        crc ^= value
        for unused in range(8):
            crc = (crc >> 1) ^ (0xEDB88320 if crc & 1 else 0)
    if checksum != crc & 0xFFFF:
        raise ValueError("Native mode-3 checksum mismatch")
    return {"senderRunId": header & 0xFFFFFFFFFFFF, "counter": counter, "compressedLetters": bool(header & (1 << 48))}, body[2:]

def keys(value):
    if isinstance(value, dict):
        for name, item in value.items():
            if name == "encryptionKey" and isinstance(item, str) and len(item) == 64:
                yield bytes.fromhex(item)
            else:
                yield from keys(item)
    elif isinstance(value, list):
        for item in value:
            yield from keys(item)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--csv")
    parser.add_argument("--directory-response", action="append", default=[])
    parser.add_argument("--directory-capture", action="append", default=[])
    parser.add_argument("--out")
    args = parser.parse_args()
    if args.self_test:
        fixture = Path(__file__).resolve().parents[2] / "Client/Tests/XFEEliteDangerous.Client.Tests/Fixtures/edserver-mode3-reference.json"
        vectors = json.loads(fixture.read_text(encoding="utf-8-sig"))["vectors"]
        for vector in vectors:
            header, letters = decode(bytes.fromhex(vector["packetHex"]), bytes.fromhex(vector["keyHex"]))
            assert letters.hex() == vector["letterHex"] and header["counter"] == vector["counter"] and header["senderRunId"] == vector["senderRunId"]
        print(f"PASS {len(vectors)} independent public native mode-3 vectors")
        return 0
    if not args.csv or not (args.directory_response or args.directory_capture) or not args.out:
        parser.error("--csv, a directory response or capture, and --out are required")
    directory_responses = [Path(item).resolve(strict=True) for item in args.directory_response]
    for capture in args.directory_capture:
        decoded = Path(capture).resolve(strict=True) / "decoded"
        for item in json.loads((decoded / "index.json").read_text(encoding="utf-8-sig")):
            if item.get("decoded") and item.get("type") == "http-response" and item.get("path") == "/2.0/elite/server/list":
                response = (decoded / item["file"]).resolve(strict=True)
                if response.parent != decoded:
                    raise ValueError("Directory response leaves the evidence directory")
                directory_responses.append(response)
    candidates = list(dict.fromkeys(key for response in directory_responses
                                   for key in keys(json.loads(response.read_text(encoding="utf-8-sig")))))
    output = Path(args.out).resolve(); output.mkdir(parents=True, exist_ok=False)
    index = []
    accepted = 0
    with Path(args.csv).open(encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            if not row.get("udp.payload"):
                continue
            packet = bytes.fromhex(row["udp.payload"].replace(":", ""))
            result = {name: value for name, value in row.items() if name != "udp.payload"}
            result["bytes"] = len(packet)
            result["sha256"] = hashlib.sha256(packet).hexdigest()
            result["decoded"] = False
            for candidate in candidates:
                try:
                    header, letters = decode(packet, candidate)
                except ValueError:
                    continue
                filename = "letters-" + str(row["frame.number"]) + ".bin"
                (output / filename).write_bytes(letters)
                result.update(header, decoded=True, lettersFile=filename, letterBytes=len(letters))
                accepted += 1
                break
            index.append(result)
    summary = {"udpPackets": len(index), "matchingDirectoryKeyAndChecksum": accepted, "unresolvedPackets": len(index)-accepted,
               "directoryKeyCount": len(candidates), "worldProtocolImplemented": False, "letterCompressionAndBusinessSemanticsVerified": False}
    (output / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
