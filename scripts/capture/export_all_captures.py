"""Copy the complete stopped capture tree into a self-contained, verified ZIP."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import zipfile


def digest(stream):
    result = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        result.update(chunk)
    return result.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--out', required=True, help='New directory under this project artifacts')
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    artifacts = (project / 'artifacts').resolve(strict=True)
    source = Path(args.source).resolve(strict=True)
    destination = Path(args.out).absolute()
    if (not source.is_relative_to(artifacts) or not source.is_dir()
            or not destination.resolve().is_relative_to(artifacts)
            or destination.resolve().is_relative_to(source)):
        parser.error('Separate source and output artifact directories required')
    # Never follow a link into another folder or silently overwrite an export.
    for base in (source, destination.parent):
        for ancestor in (base, *base.parents):
            if ancestor.exists() and (ancestor.is_symlink() or ancestor.is_junction()):
                raise ValueError('Capture export cannot traverse filesystem links')
    files = []
    for entry in source.rglob('*'):
        if entry.is_symlink() or entry.is_junction():
            raise ValueError('Capture tree contains a filesystem link')
        if entry.is_file():
            files.append(entry)
    files.sort(key=lambda entry: entry.relative_to(source).as_posix())
    if not files:
        raise ValueError('Capture tree is empty')
    destination.mkdir(parents=True, exist_ok=False)
    archive = destination / 'Elite-Dangerous-all-captures.zip'
    pending = destination / 'Elite-Dangerous-all-captures.zip.partial'
    entries = []
    readme = (
        '# Elite Dangerous 完整抓包导出\n\n'
        '本包逐字节保留本次任务现存的全部联网采集数据，没有删减或脱敏原始文件。\n\n'
        f'数据目录：`{source.name}/`。\n\n'
        '- `solo-wire.pcapng`、`wire-live/`：最初进入 SOLO 和持续操作期间的原始网络包。\n'
        '- `process`、`process-r2`、`process-r3`：持续操作期间的进程观察、原始正文和解码结果。\n'
        '- `cold-start/process/decoded/index.json`：重新启动、主菜单和进入 SOLO 的请求响应索引。\n'
        '- `cold-start/wire/`、`cold-start/snapshot-final/`：冷启动网络分段和最终快照。\n'
        '- `cold-start/letters-r3/`：已拆分的 UDP 消息与索引。\n'
        '- `user-phases.jsonl`：用户操作阶段记录。\n'
        '- `docs/capture-20261020.md`：采集范围、操作覆盖和已知缺失说明。\n\n'
        '各次采集和分析快照保持原目录，包括重复包、早期观察器错误及未完整解码的数据。\n'
        '这是一份联网协议证据副本，不表示离线世界或全部游戏功能已经实现。\n\n'
        '`manifest.json` 列出每个文件的原始相对路径、字节数和 SHA-256。'
        '导出器已重新读取 ZIP 中的每一项，逐项比对长度和 SHA-256。\n'
    )
    metadata = {
        'schema': 1, 'createdUtc': datetime.now(timezone.utc).isoformat(),
        'sourceDirectory': source.name, 'captureFileCount': len(files),
        'captureBytes': sum(entry.stat().st_size for entry in files),
        'rawFilesPreserved': True, 'entries': entries,
    }
    with zipfile.ZipFile(pending, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6,
                         allowZip64=True) as output:
        for index, entry in enumerate(files, 1):
            before = entry.stat()
            relative = entry.relative_to(source).as_posix()
            name = source.name + '/' + relative
            hasher = hashlib.sha256()
            with entry.open('rb') as input_file, output.open(name, 'w', force_zip64=True) as target:
                while chunk := input_file.read(1024 * 1024):
                    hasher.update(chunk)
                    target.write(chunk)
            after = entry.stat()
            if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                raise RuntimeError('A capture file changed during export: ' + relative)
            entries.append({'path': name, 'bytes': before.st_size, 'sha256': hasher.hexdigest()})
            if index % 2000 == 0:
                print(f'Archived {index}/{len(files)} capture files', flush=True)
        evidence = project / 'docs/protocol-evidence/capture-20261020.md'
        if evidence.exists():
            data = evidence.read_bytes()
            name = 'docs/capture-20261020.md'
            output.writestr(name, data)
            entries.append({'path': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        encoded = (json.dumps(metadata, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
        output.writestr('manifest.json', encoded)
        output.writestr('README.md', readme.encode('utf-8'))
    with zipfile.ZipFile(pending) as check:
        if len(check.infolist()) != len(entries) + 2:
            raise RuntimeError('Archive entry count differs from the manifest')
        for entry in entries:
            if check.getinfo(entry['path']).file_size != entry['bytes']:
                raise RuntimeError('Archive length mismatch: ' + entry['path'])
            with check.open(entry['path']) as input_file:
                if digest(input_file) != entry['sha256']:
                    raise RuntimeError('Archive hash mismatch: ' + entry['path'])
        if check.read('manifest.json') != encoded or check.read('README.md') != readme.encode('utf-8'):
            raise RuntimeError('Archive metadata differs from the export')
    pending.rename(archive)
    (destination / 'manifest.json').write_bytes(encoded)
    (destination / 'README.md').write_text(readme, encoding='utf-8')
    with archive.open('rb') as input_file:
        archive_hash = digest(input_file)
    (destination / 'Elite-Dangerous-all-captures.zip.sha256').write_text(
        archive_hash + '  ' + archive.name + '\n', encoding='ascii')
    result = {key: value for key, value in metadata.items() if key != 'entries'}
    result.update(archive=str(archive), archiveBytes=archive.stat().st_size,
                  archiveSha256=archive_hash, allEntryHashesVerified=True)
    (destination / 'verification.json').write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
