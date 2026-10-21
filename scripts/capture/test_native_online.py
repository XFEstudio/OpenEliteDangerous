"""Verify two owned native Odyssey clients from logs, without desktop input.

Requires an isolated OnlineIntegration fixture and two already running hidden
bootstrapper launches. The test invokes configured native menu actions and
bounded ship input; it never writes transforms or readiness flags.
"""
import argparse
import hashlib
import json
import math
import subprocess
import sys
import time
import uuid
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--launch-a', required=True)
parser.add_argument('--launch-b', required=True)
parser.add_argument('--fixture', required=True)
parser.add_argument('--out', required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
artifacts = (repo / 'artifacts').resolve()
out = Path(args.out).resolve()
fixture = Path(args.fixture).resolve(strict=True)
paths = [Path(args.launch_a).resolve(strict=True), Path(args.launch_b).resolve(strict=True)]
if not out.is_relative_to(artifacts) or not fixture.is_relative_to(artifacts) or any(not path.is_relative_to(artifacts) for path in paths):
    raise ValueError('Only isolated artifact fixtures are supported')
receipts = [json.loads(path.read_text()) for path in paths]
if (receipts[0]['ProcessId'] == receipts[1]['ProcessId'] or receipts[0]['accountId'] == receipts[1]['accountId']
        or receipts[0]['profile'] == receipts[1]['profile'] or receipts[0]['server'] != receipts[1]['server']
        or any(not row.get('online') or row.get('visible') or not row.get('zeroNetworkCapabilities') for row in receipts)):
    raise ValueError('Two independent owned hidden online clients are required')
if Path(json.loads((fixture / 'server.json').read_text())['workspace']).resolve() != (fixture / 'Workspace').resolve():
    raise ValueError('Expected the isolated fixture workspace')
out.mkdir(parents=True, exist_ok=False)
tag = 'online-' + uuid.uuid4().hex[:12]
helper = repo / 'scripts/capture/inspect_offline_controls.py'
report = {'schema': 1, 'passed': False, 'desktopInputUsed': False, 'gameRestarts': 0,
          'clients': [{'label': label, 'pid': row['ProcessId'], 'imageSha256': row['imageSha256']}
                      for label, row in zip(('A', 'B'), receipts)],
          'scope': ['native-live-ship-presence', 'bidirectional-position', 'bidirectional-rotation', 'central-save-reload'],
          'checks': [], 'evidence': []}
children = []


def check(condition, name, **details):
    report['checks'].append({'name': name, 'passed': bool(condition), **details})
    if not condition:
        raise AssertionError(name)


def rows(path):
    if not path.exists():
        return []
    if path.stat().st_size > 32 * 1024 * 1024:
        raise ValueError('Diagnostic log exceeded bound')
    result = []
    for line in path.read_text(encoding='utf-8').splitlines():
        try:
            result.append(json.loads(line))
        except ValueError:
            pass  # Last line can be incomplete while an observer is running.
    return result


def start(index, step, action='observe', seconds=30, entity=None, axis='forward'):
    path = paths[index].parent / ('entity-' + tag + '-' + step + '.jsonl')
    command = [sys.executable, str(helper), '--launch', str(paths[index]), '--out', str(path),
               '--seconds', str(seconds), '--action', action, '--axis', axis]
    if entity is not None:
        command += ['--entity', entity]
    stream = (out / (step + '.console.log')).open('x', encoding='utf-8')
    process = subprocess.Popen(command, cwd=repo, stdout=stream, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    children.append((process, stream))
    report['evidence'].append(str(path))
    deadline = time.monotonic() + 12
    while not any(row.get('kind') == 'observer-ready' for row in rows(path)):
        if process.poll() is not None or time.monotonic() > deadline:
            raise RuntimeError('Observer did not attach: ' + step)
        time.sleep(0.1)
    return process, path


def finish(job):
    process, path = job
    process.wait(timeout=95)
    data = rows(path)
    check(process.returncode == 0 and any(row.get('kind') == 'observer-closed' and not row.get('failed') for row in data),
          'Diagnostic completed: ' + path.name)
    check(not any('observerError' in row or row.get('kind') == 'native-axis-rejected' for row in data),
          'No native observer or input errors: ' + path.name)
    return data


def distance(first, second):
    return math.dist(first, second)


def angle(first, second):
    # Frobenius product = trace(Ra * Rb^T), with native rounding clamped.
    return math.degrees(math.acos(max(-1, min(1, (sum(a*b for a, b in zip(first, second)) - 1) / 2))))


def valid_pose(row):
    if row.get('kind') != 'native-entity-position' or row.get('type') != 'SideWinder' or row.get('state') != 8:
        return False
    position, rotation = row.get('position', []), row.get('rotation', [])
    if len(position) != 3 or len(rotation) != 9 or not all(math.isfinite(value) for value in position + rotation):
        return False
    vectors = [rotation[index:index+3] for index in (0, 3, 6)]
    return (all(abs(sum(v*v for v in vector) - 1) < 0.0001 for vector in vectors)
            and all(abs(sum(a*b for a, b in zip(vectors[first], vectors[second]))) < 0.0001
                    for first, second in ((0, 1), (0, 2), (1, 2))))


def poses(data, entity):
    #A cached freed address is insufficient evidence: require this ID to be
    #visited by the original native ship-physics owner check in this observation.
    if not any(row.get('kind') == 'native-physics-owner' and row.get('id') == entity for row in data):
        return []
    return [row for row in data if row.get('id') == entity and valid_pose(row)]


def state_locations():
    state = json.loads((fixture / 'Workspace/state.json').read_text(encoding='utf-8-sig'))
    return [state['accounts'][receipt['accountId']]['nativeSave']['snapshot']['location'] for receipt in receipts]


def xyz(location):
    return [location['position'][key] for key in ('x', 'y', 'z')]


def identify(data, location):
    latest = {row['id']: row for row in data if valid_pose(row)}
    candidates = sorted((distance(row['position'], xyz(location)), entity) for entity, row in latest.items())
    check(bool(candidates) and candidates[0][0] < 1, 'Ship matches its isolated server save',
          nearestDistanceMeters=candidates[0][0] if candidates else None)
    check(len(candidates) == 1 or candidates[1][0] > 10, 'Ship ownership selection is unambiguous')
    return candidates[0][1]


def identify_local(data):
    #The native3CB93C0 getter uses the un-interpolated cache when this flag is
    #set. Require one such live hull per client, then independently verify its
    #motion against the other client and its authenticated account's final save.
    latest = {row['id']: row for row in data if row.get('kind') == 'native-ship-transform-caches'}
    candidates = [entity for entity, row in latest.items() if row.get('selector') == 1 and poses(data, entity)]
    check(len(candidates) == 1, 'One native un-interpolated player hull is selected', count=len(candidates))
    return candidates[0]


def observe_pair(step, seconds=10):
    jobs = [start(index, step + '-' + label, seconds=seconds) for index, label in enumerate(('A', 'B'))]
    return [finish(job) for job in jobs]


def drive(index, entity, axis):
    label = ('A', 'B')[index]
    print('Native ' + label + ' ' + axis + ': driving selected ship and observing its peer', flush=True)
    receiver = start(1-index, label + '-' + axis + '-receiver', seconds=27)
    actor = start(index, label + '-' + axis + '-actor', 'flight-axis-and-pause', 30, entity, axis)
    local, remote = finish(actor), finish(receiver)
    first, second = poses(local, entity), poses(remote, entity)
    check(len(first) >= 8 and len(second) >= 15, label + ' ' + axis + ': both native worlds sampled')
    holds = [row for row in local if row.get('kind') == 'native-axis-hold']
    check(any(row.get('applied') == 0.25 for row in holds) and all(row.get('entity') == entity for row in holds),
          label + ' ' + axis + ': input reached only the selected ship')
    check(any(row.get('outcome') == 'synthetic-native-axis-and-pause' for row in local),
          label + ' ' + axis + ': native resume, input restoration and pause completed')
    motion = distance(first[0]['position'], first[-1]['position'])
    remote_motion = distance(second[0]['position'], second[-1]['position'])
    rotation = angle(first[0]['rotation'], first[-1]['rotation'])
    remote_rotation = angle(second[0]['rotation'], second[-1]['rotation'])
    error = distance(first[-1]['position'], second[-1]['position'])
    rotation_error = angle(first[-1]['rotation'], second[-1]['rotation'])
    check((motion > 5 and remote_motion > 5) if axis == 'forward' else (rotation > 3 and remote_rotation > 3),
          label + ' ' + axis + ': actual native motion replicated',
          displacementMeters=motion, receiverDisplacementMeters=remote_motion,
          rotationDegrees=rotation, receiverRotationDegrees=remote_rotation)
    check(error < 0.5 and rotation_error < 0.2, label + ' ' + axis + ': peer converged to source pose',
          positionErrorMeters=error, rotationErrorDegrees=rotation_error)
    changed = [row for row in second if distance(row['position'], second[0]['position']) > 1
               or angle(row['rotation'], second[0]['rotation']) > 1]
    check(len(changed) >= 3, label + ' ' + axis + ': multiple updated native replica samples')
    report.setdefault('trajectories', []).append({'client': label, 'entity': entity, 'axis': axis,
        'sourceSamples': len(first), 'receiverSamples': len(second), 'sourceDisplacementMeters': motion,
        'receiverDisplacementMeters': remote_motion, 'sourceRotationDegrees': rotation,
        'receiverRotationDegrees': remote_rotation, 'positionErrorMeters': error,
        'rotationErrorDegrees': rotation_error})
    return first[-1]


def rotation_vector_matrix(vector):
    x, y, z = [vector[key] for key in ('x', 'y', 'z')]
    theta = math.sqrt(x*x+y*y+z*z)
    if theta < 1e-12:
        return [1, 0, 0, 0, 1, 0, 0, 0, 1]
    x, y, z = x/theta, y/theta, z/theta
    c, s, t = math.cos(theta), math.sin(theta), 1-math.cos(theta)
    # Native matrices use row vectors.
    return [t*x*x+c, t*x*y+s*z, t*x*z-s*y,
            t*x*y-s*z, t*y*y+c, t*y*z+s*x,
            t*x*z+s*y, t*y*z-s*x, t*z*z+c]


def verify_native_startup(index):
    path = paths[index].parent / 'logs/native-game-flow.tsv'
    records = [line.split('\t') for line in path.read_text(encoding='utf-8').splitlines()]
    modes = [row for row in records if len(row) >= 4 and row[1] == 'NATIVE_CONTENT_MODE']
    check(bool(modes) and modes[-1][2:4] == ['Odyssey', '1'],
          ('A', 'B')[index] + ': original native content check reports Odyssey')
    check(any(len(row) >= 3 and row[1:3] == ['NATIVE_SAVE_RESULT', '0'] for row in records),
          ('A', 'B')[index] + ': original native full-save parser returned zero')


try:
    print('Native dual-client regression: baseline', flush=True)
    for index in (0, 1):
        verify_native_startup(index)
    baseline = observe_pair('baseline', 12)
    ships = [identify_local(data) for data in baseline]
    check(ships[0] != ships[1], 'The two commanders have distinct native ship entities')
    for index, data in enumerate(baseline):
        check(all(poses(data, ship) for ship in ships), ('A', 'B')[index] + ': both full SideWinder entities are Live')
    report['shipEntities'] = ships
    latest = [None, None]
    for axis in ('forward', 'angular-x'):
        for index, ship in enumerate(ships):
            latest[index] = drive(index, ship, axis)
    print('Native central saves and rejoin with the same game processes', flush=True)
    for index, label in enumerate(('A', 'B')):
        finish(start(index, 'save-' + label, 'save-and-exit', 40))
    saved = state_locations()
    for index, label in enumerate(('A', 'B')):
        position_error = distance(xyz(saved[index]), latest[index]['position'])
        rotation_error = angle(rotation_vector_matrix(saved[index]['rotationVector']), latest[index]['rotation'])
        check(position_error < 0.5 and rotation_error < 0.2, label + ': central save contains the actual native final pose',
              positionErrorMeters=position_error, rotationErrorDegrees=rotation_error)
    for index, label in enumerate(('A', 'B')):
        finish(start(index, 'reload-' + label, 'rejoin-open', 75))
    reloaded = observe_pair('reload-observe', 12)
    new_ships = [identify(data, location) for data, location in zip(reloaded, saved)]
    check(new_ships[0] != new_ships[1], 'Reload preserves independent native ship identities')
    for index, data in enumerate(reloaded):
        for owner, ship in enumerate(new_ships):
            samples = poses(data, ship)
            check(bool(samples), ('A', 'B')[index] + ': both native ships reappeared after save/rejoin')
            error = distance(samples[-1]['position'], xyz(saved[owner]))
            rotation_error = angle(samples[-1]['rotation'], rotation_vector_matrix(saved[owner]['rotationVector']))
            check(error < 0.5 and rotation_error < 0.2, ('A', 'B')[index] + ': reloaded ship pose matches the central save',
                  ship=ship, positionErrorMeters=error, rotationErrorDegrees=rotation_error)
    check([json.loads(path.read_text())['ProcessId'] for path in paths] == [row['ProcessId'] for row in receipts],
          'Original two owned game process IDs retained throughout the test')
    report['reloadedShipEntities'] = new_ships
    report['passed'] = True
except Exception as error:
    report['failure'] = type(error).__name__ + ': ' + str(error)
    print(report['failure'], flush=True)
finally:
    for process, stream in children:
        if process.poll() is None:
            try:
                process.wait(timeout=85)
            except subprocess.TimeoutExpired:
                process.terminate()  # Only this test's diagnostic helper, never the game.
                process.wait(timeout=10)
        stream.close()
    report['completedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    report['helperSha256'] = hashlib.sha256(helper.read_bytes()).hexdigest()
    report['testHarnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    for path in report['evidence']:
        file = Path(path)
        report.setdefault('evidenceHashes', {})[path] = hashlib.sha256(file.read_bytes()).hexdigest()
    (out / 'native-online-validation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'passed': report['passed'], 'checks': len(report['checks']), 'report': str(out / 'native-online-validation.json')}), flush=True)
sys.exit(0 if report['passed'] else 2)
