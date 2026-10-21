"""Observe an explicitly selected Elite process; evidence stays in a new private local directory."""
from __future__ import annotations
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import threading
import time
import frida

SUPPORTED_HASH = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988"

def process_path(pid: int) -> Path | None:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.QueryFullProcessImageNameW.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_uint32)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = ctypes.c_uint32(len(buffer))
        return Path(buffer.value) if kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)) else None
    finally:
        kernel.CloseHandle(handle)

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--seconds", type=int, default=1800)
    args = parser.parse_args()
    game = Path(args.game).resolve(strict=True)
    with game.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != SUPPORTED_HASH:
        parser.error("This passive native observer requires the exact audited game hash; no attachment was attempted.")
    output = Path(args.out).resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / "responses").mkdir()
    (output / "capture-helper.pid").write_text(str(os.getpid()), encoding="ascii")
    (output / "profile.json").write_text(json.dumps({"executable": str(game), "sha256": digest, "mode": "passive-process-observation", "startedAtUnix": time.time(), "changesNetwork": False}, indent=2), encoding="utf-8")
    device = frida.get_local_device()
    deadline = time.monotonic() + max(10, min(args.seconds, 7200))
    print("WAITING for exact game path; evidence=" + str(output), flush=True)
    pid = None
    while time.monotonic() < deadline:
        matches = [item.pid for item in device.enumerate_processes() if item.name.casefold() == game.name.casefold() and process_path(item.pid) == game]
        if len(matches) > 1:
            raise RuntimeError("More than one exact game process exists; refusing an ambiguous attachment.")
        if matches:
            pid = matches[0]
            break
        time.sleep(1)
    if pid is None:
        return 2
    # Recheck the process identity immediately before attachment; never inspect a launcher or another copy.
    if process_path(pid) != game:
        raise RuntimeError("The selected process identity changed.")
    stopped = threading.Event()
    lock = threading.Lock()
    total = 0
    errors = {}
    with (output / "events.jsonl").open("w", encoding="utf-8", buffering=1) as events:
        def on_message(message, data):
            nonlocal total
            if message["type"] != "send":
                value = {"type": "observer-error", "description": message.get("description", message["type"])}
            else:
                value = message["payload"]
            value["pid"] = pid
            with lock:
                if data:
                    total += len(data)
                    if total > 512 * 1024 * 1024:
                        events.write(json.dumps({"type": "capture-limit", "pid": pid}) + "\n")
                        stopped.set()
                        return
                    prefix = "cache" if value["type"] == "native-data-document" else "request" if value["type"] == "http-request-body" else "response"
                    filename = f"{prefix}-{value['id']:06d}.bin"
                    (output / "responses" / filename).write_bytes(data)
                    value["bodyFile"] = "responses/" + filename
                events.write(json.dumps(value, ensure_ascii=False) + "\n")
            if value["type"] in ("capture-error", "observer-error"):
                description = value.get("description", value.get("error", "unknown"))
                errors[description] = errors.get(description, 0) + 1
                if errors[description] <= 3:
                    print(json.dumps(value, ensure_ascii=False), flush=True)
            elif value["type"] in ("ready", "game-flow", "native-milestone"):
                print(json.dumps(value, ensure_ascii=False), flush=True)
            elif value["type"] == "http-response":
                print(f"HTTP {value.get('method')} {value.get('path')} -> {value.get('status')} ({value.get('length')} bytes)", flush=True)
        session = device.attach(pid)
        session.on("detached", lambda *unused: stopped.set())
        script = session.create_script(Path(__file__).with_name("elite-passive.js").read_text(encoding="utf-8"))
        script.on("message", on_message)
        try:
            script.load()
            while time.monotonic() < deadline and not stopped.wait(1):
                if (output / "stop-capture").exists():
                    break
        except KeyboardInterrupt:
            pass
        finally:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass
    print("CAPTURE STOPPED; game was left running; evidence=" + str(output), flush=True)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
