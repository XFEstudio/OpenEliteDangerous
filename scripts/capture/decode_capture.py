"""Decode only locally captured envelopes from the pinned game; never makes network requests."""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import time
from urllib.parse import unquote, parse_qsl
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms

MAXIMUM = 16 * 1024 * 1024
SUPPORTED_HASH = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988"

def transform(key: bytes, nonce: str, value: bytes) -> bytes:
    if len(key) != 32 or not re.fullmatch(r"[0-9a-fA-F]{12}", nonce):
        raise ValueError("Unsupported envelope key or nonce size")
    # Exact initial ChaCha state used by the existing independently checked C# codec.
    decryptor = Cipher(algorithms.ChaCha20(key, b"\0" * 4 + nonce.encode("ascii")), mode=None).decryptor()
    return decryptor.update(value) + decryptor.finalize()

def raw_lz4(value: bytes, expected: int) -> bytes:
    if not 0 <= expected <= MAXIMUM or not value:
        raise ValueError("Invalid bounded raw LZ4 size")
    output = bytearray()
    cursor = 0
    last_match = -1
    def length(nibble):
        nonlocal cursor
        count = nibble
        if nibble == 15:
            while True:
                if cursor >= len(value):
                    raise ValueError("Truncated LZ4 extension")
                extra = value[cursor]; cursor += 1; count += extra
                if count > expected:
                    raise ValueError("LZ4 length exceeds output bound")
                if extra != 255:
                    break
        return count
    while cursor < len(value):
        token = value[cursor]; cursor += 1
        literals = length(token >> 4)
        if cursor + literals > len(value) or len(output) + literals > expected:
            raise ValueError("Truncated or excessive LZ4 literals")
        output.extend(value[cursor:cursor + literals]); cursor += literals
        if cursor == len(value):
            if len(output) != expected or (last_match >= 0 and (literals < 5 or expected - last_match < 12)):
                raise ValueError("LZ4 final sequence or decoded size mismatch")
            return bytes(output)
        if cursor + 2 > len(value):
            raise ValueError("Truncated LZ4 offset")
        offset = int.from_bytes(value[cursor:cursor + 2], "little"); cursor += 2
        if not 0 < offset <= len(output):
            raise ValueError("Invalid LZ4 history offset")
        match = length(token & 15) + 4
        if len(output) + match > expected:
            raise ValueError("LZ4 match exceeds output bound")
        last_match = len(output)
        for unused in range(match):
            output.append(output[-offset])
    raise ValueError("Missing LZ4 final literal sequence")

def response(key: bytes, headers: dict, body: bytes) -> bytes:
    if "nonce" not in headers or "uncompressedsize" not in headers:
        return body
    plain = transform(key, headers["nonce"], base64.b64decode(body, validate=True))
    if plain[:4] != b"EDDE":
        raise ValueError("The captured response does not use the supported EDDE marker")
    return raw_lz4(plain[8:], int(headers["uncompressedsize"]))

def self_test(root: Path) -> int:
    fixture = json.loads((root / "Client/Tests/XFEEliteDangerous.Client.Tests/Fixtures/response-envelope-reference.json").read_text(encoding="utf-8-sig"))
    count = 0
    for case in fixture["cases"]:
        if case.get("kind") != "decode":
            continue
        decoded = response(bytes.fromhex(fixture["keyHex"]), {"nonce": case["nonce"], "uncompressedsize": case["uncompressedSize"]}, case["bodyBase64"].encode("ascii"))
        if hashlib.sha256(decoded).hexdigest() != case["uncompressedSha256"]:
            raise AssertionError(case["name"])
        count += 1
    print(f"PASS {count} independent public response vectors; no game process or account data used")
    return 0

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--game")
    parser.add_argument("--capture")
    parser.add_argument("--follow-seconds", type=int, default=0)
    args = parser.parse_args()
    if args.self_test:
        return self_test(Path(__file__).resolve().parents[2])
    if not args.game or not args.capture:
        parser.error("--game and --capture are required")
    game = Path(args.game).resolve(strict=True)
    with game.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != SUPPORTED_HASH:
            parser.error("The selected executable differs from the audited response codec")
        source.seek(0x5131E08); key = source.read(32)
    capture = Path(args.capture).resolve(strict=True)
    output = capture / "decoded"; output.mkdir(exist_ok=True)
    deadline = time.monotonic() + max(0, min(args.follow_seconds, 7200))
    observed = set()
    results = []
    while True:
        for line in (capture / "events.jsonl").read_text(encoding="utf-8").splitlines():
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            if event["type"] not in ("http-response", "http-request-body", "native-data-document") or not event.get("bodyFile"):
                continue
            identity = (event["type"], event["id"])
            if identity in observed:
                continue
            observed.add(identity)
            result = {"id": event["id"], "type": event["type"], "path": event["path"], "method": event.get("method"), "status": event.get("status"), "utc": event.get("utc")}
            try:
                body_path = (capture / event["bodyFile"]).resolve(strict=True)
                if not body_path.is_relative_to(capture / "responses"):
                    raise ValueError("Captured body path leaves its evidence directory")
                body = body_path.read_bytes()
                if len(body) > MAXIMUM or event.get("truncated"):
                    raise ValueError("The captured body is truncated or excessive")
                if event["type"] == "native-data-document":
                    payload = body
                    result["source"] = event.get("source")
                elif event["type"] == "http-response":
                    payload = response(key, event.get("headers", {}), body)
                else:
                    query = base64.b64decode(unquote(event.get("query", "")), validate=True)
                    encrypted_body = base64.b64decode(body, validate=True)
                    payload = transform(key, event["headers"]["nonce"], query + encrypted_body)[len(query):]
                request_headers = event.get("requestHeaders", {}) if event["type"] == "http-response" else event.get("headers", {})
                if event.get("query") and request_headers.get("nonce"):
                    try:
                        encoded_query = base64.b64decode(unquote(event["query"]), validate=True)
                        query_pairs = parse_qsl(transform(key, request_headers["nonce"], encoded_query).decode("utf-8"), keep_blank_values=True)
                        result["queryFields"] = sorted(set(name for name, unused in query_pairs))
                        # Preserve business selectors privately; never store decrypted authentication fields.
                        business = [(name, value) for name, value in query_pairs if not any(term in name.casefold() for term in ("token", "auth", "password", "secret", "machine", "ticket", "session", "cookie", "runid", "email"))]
                        query_name = f"{event['type']}-{event['id']:06d}.query.json"
                        (output / query_name).write_text(json.dumps(business, ensure_ascii=False), encoding="utf-8")
                        result["businessQueryFile"] = query_name
                        result["requestQueryDecoded"] = True
                    except (ValueError, UnicodeError) as query_error:
                        # A separately observed query-format gap must not discard a valid response body.
                        result["requestQueryDecoded"] = False
                        result["requestQueryError"] = type(query_error).__name__ + ": " + str(query_error)
                text = payload.decode("utf-8")
                suffix = ".xml" if text.lstrip().startswith("<") else ".json" if text.lstrip().startswith(("{", "[")) else ".txt"
                filename = f"{event['type']}-{event['id']:06d}{suffix}"
                (output / filename).write_bytes(payload)
                result.update(decoded=True, length=len(payload), file=filename, sha256=hashlib.sha256(payload).hexdigest())
            except Exception as error:
                result.update(decoded=False, error=type(error).__name__ + ": " + str(error))
            results.append(result)
            print(json.dumps(result, ensure_ascii=False), flush=True)
        temporary_index = output / "index.json.new"
        temporary_index.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary_index.replace(output / "index.json")
        if time.monotonic() >= deadline or (capture / "stop-decoding").exists():
            break
        time.sleep(2)
    return 0 if all(item["decoded"] for item in results) else 2

if __name__ == "__main__":
    raise SystemExit(main())
