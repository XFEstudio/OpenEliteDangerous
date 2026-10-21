"""Wait for the selected game's next PID and capture its cold start without launching or stopping it."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import frida
from capture_solo import process_path, SUPPORTED_HASH
import hashlib

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--after-pid", type=int, required=True)
    parser.add_argument("--dumpcap", required=True)
    parser.add_argument("--interfaces", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    game = Path(args.game).resolve(strict=True)
    dumpcap = Path(args.dumpcap).resolve(strict=True)
    powershell = shutil.which("pwsh")
    if not powershell:
        raise RuntimeError("PowerShell 7 is required for the bounded wire capture helper")
    with game.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != SUPPORTED_HASH:
            raise RuntimeError("The selected game version differs from the audited observer")
    root = Path(args.out).resolve(); root.mkdir(parents=True, exist_ok=False)
    (root / "watcher.pid").write_text(str(os.getpid()), encoding="ascii")
    (root / "watcher.json").write_text(json.dumps({"game":str(game), "afterPid":args.after_pid, "sha256":SUPPORTED_HASH, "mode":"passive-cold-start", "startedAtUnix":time.time()}), encoding="utf-8")
    stop = root / "stop-capture"
    device = frida.get_local_device()
    print("READY: waiting for the next exact game process; current game is left untouched", flush=True)
    next_pid = None
    while not stop.exists():
        matches = [item.pid for item in device.enumerate_processes() if item.pid != args.after_pid and item.name.casefold() == game.name.casefold() and process_path(item.pid) == game]
        if len(matches) > 1:
            raise RuntimeError("Ambiguous game process; refusing to attach")
        if matches:
            next_pid = matches[0]; break
        time.sleep(0.2)
    if next_pid is None:
        return 0
    print("FOUND next game PID=" + str(next_pid), flush=True)
    children = []
    streams = []
    scripts = Path(__file__).resolve().parent
    def start(command, log):
        stream = (root / log).open("wb"); streams.append(stream)
        child = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        children.append(child)
        return child
    try:
        process_capture = start([sys.executable, str(scripts/"capture_solo.py"), "--game",str(game),"--out",str(root/"process"),"--seconds","7200"], "process-console.log")
        wire_capture = start([powershell,"-NoProfile","-File",str(scripts/"capture_wire.ps1"),"-GameProcessId",str(next_pid),"-GamePath",str(game),"-DumpcapPath",str(dumpcap),"-OutputRoot",str(root/"wire"),"-InterfaceCsv",args.interfaces], "wire-console.log")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline and not (root/"process/events.jsonl").exists():
            if process_capture.poll() is not None:
                raise RuntimeError("Cold-start observer failed; inspect the private process console log")
            time.sleep(0.2)
        decoder = start([sys.executable,str(scripts/"decode_capture.py"),"--game",str(game),"--capture",str(root/"process"),"--follow-seconds","7200"], "decode-console.log")
        (root/"active.json").write_text(json.dumps({"gamePid":next_pid,"captureHelperPid":process_capture.pid,"wireHelperPid":wire_capture.pid,"decoderPid":decoder.pid,"gameStoppedByCapture":False}),encoding="utf-8")
        print("ACTIVE: cold-start process and wire capture for game PID=" + str(next_pid), flush=True)
        wire_ended_reported = False
        while not stop.exists() and process_capture.poll() is None:
            if wire_capture.poll() is not None and not wire_ended_reported:
                print("WIRE HELPER ENDED; process capture continues",flush=True)
                wire_ended_reported = True
            time.sleep(1)
    finally:
        for signal in (root/"process/stop-capture",root/"process/stop-decoding",root/"wire/stop-capture"):
            if signal.parent.exists():
                signal.write_text("End owned capture helpers; leave the game untouched",encoding="utf-8")
        for child in children:
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.terminate(); child.wait(timeout=10)
        for stream in streams:
            stream.close()
        (root/"completed.json").write_text(json.dumps({"gamePid":next_pid,"gameStoppedByCapture":False,"endedAtUnix":time.time()}),encoding="utf-8")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
