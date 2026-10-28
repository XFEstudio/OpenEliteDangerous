"""Exercise original module setters on one receipt-owned isolated E-game thread, then restore."""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--launch', required=True)
parser.add_argument('--observations', required=True)
parser.add_argument('--out', required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2] / 'artifacts'
launch = Path(args.launch).resolve(strict=True)
observations = Path(args.observations).resolve(strict=True)
output = Path(args.out).resolve()
if any(not path.is_relative_to(root) for path in (launch, observations, output)):
    raise ValueError('Owned artifact paths required')
receipt = json.loads(launch.read_text(encoding='utf-8-sig'))
expected = Path(r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe')
if receipt.get('mode') != 'in-process-offline' or not receipt.get('zeroNetworkCapabilities') or receipt['imageSha256'] != 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988':
    raise ValueError('Pinned isolated E-game receipt required')
if hashlib.sha256(expected.read_bytes()).hexdigest() != receipt['imageSha256']:
    raise ValueError('Game image changed')
rows = [json.loads(line) for line in observations.read_text(encoding='utf-8').splitlines()]
row = next(row for row in reversed(rows) if row.get('kind') == 'native-live-module' and row['slot'] == 'SmallHardpoint1' and row['state'] == 8)
if row.get('powerPriorityPointer') is None:
    raise ValueError('Fresh observation of the actual saved power-priority interface is required')
source = r'''
const image=Process.getModuleByName('EliteDangerous64.exe');
if(image.path.toLowerCase()!==String.raw`E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`.toLowerCase())throw new Error('Wrong game process');
const row=ROW_JSON;
for(const [rva,hex] of [[0x43B1500,'48895c2408574883ec20488bd90fb6fa488b49e04883c128e8639229fc84c074'],[0x43B3780,'40534883ec2080b9c100000000488bd9754380b9c0000000018b81b800000075'],[0x43B2D70,'48895c2408574883ec2033d2410fb6f833c00f28c1f0480fb19110040000f30f'],[0x4398630,'4533c033c0f04c0fb18110040000488bd048c1ea2033d08954241033c0f04c0f']]){
 const actual=Array.from(new Uint8Array(image.base.add(rva).readByteArray(32))).map(x=>x.toString(16).padStart(2,'0')).join('');
 if(actual!==hex)throw new Error('Pinned native setter differs '+rva.toString(16));
}
const life=ptr(row.life), power=ptr(row.types['5f2c228'].pointer), health=ptr(row.types['5f41d5c'].pointer);
const prioritySettings=ptr(row.powerPriorityPointer);
const enabled=new NativeFunction(image.base.add(0x43B1500),'void',['pointer','uchar']);
const priority=new NativeFunction(image.base.add(0x43B3780),'void',['pointer','uint']);
const integrity=new NativeFunction(image.base.add(0x43B2D70),'void',['pointer','float','uchar']);
const readIntegrity=new NativeFunction(image.base.add(0x4398630),'float',['pointer']);
function check(){
 if(life.add(0xB0).readU64().toString()!==row.id||life.add(0xC0).readU32()!==8||!power.readPointer().equals(image.base.add(0x5597F60))||!health.readPointer().equals(image.base.add(0x5598F88))||!prioritySettings.readPointer().equals(image.base.add(0x55983B0)))throw new Error('Native entity no longer live');
}
function read(){check();return {enabled:power.add(0x108).readU8()!==0,priority:prioritySettings.add(0xB8).readU32(),health:readIntegrity(health)};}
let stage=0, next=0, before=null, hook;
hook=Interceptor.attach(image.base.add(0x2154C40),{onLeave(){
 if(stage===4||Date.now()<next)return;
 try{
  if(stage===0){before=read();send({kind:'before',values:before});enabled(power,0);priority(prioritySettings,3);integrity(health,0.91,0);stage=1;next=Date.now()+1200;return;}
  if(stage===1){const values=read();send({kind:'applied',values,verified:values.enabled===false&&values.priority===3&&Math.abs(values.health-.91)<.00001});stage=2;}
  if(stage===2){enabled(power,before.enabled?1:0);priority(prioritySettings,before.priority);integrity(health,before.health,0);stage=3;next=Date.now()+1200;return;}
  if(stage===3){const values=read();send({kind:'restored',values,verified:values.enabled===before.enabled&&values.priority===before.priority&&Math.abs(values.health-before.health)<.00001});stage=4;hook.detach();}
 }catch(error){send({kind:'test-error',message:String(error),stage});if(before!==null&&stage<3){try{check();enabled(power,before.enabled?1:0);priority(prioritySettings,before.priority);integrity(health,before.health,0);}catch(_){}}stage=4;hook.detach();}
}});
send({kind:'test-ready',slot:row.slot,id:row.id});
'''.replace('ROW_JSON', json.dumps(row))
lease = acquire_native_lease(launch)
session = script = None
done = threading.Event()
records = []
try:
    with output.open('x', encoding='utf-8') as file:
        def message(value, data):
            record = value.get('payload') if value.get('type') == 'send' else {'kind':'test-error','message':value.get('description')}
            records.append(record)
            file.write(json.dumps(record)+'\n')
            file.flush()
            if record['kind'] in ('restored','test-error'):
                done.set()
        session = frida.attach(receipt['ProcessId'])
        script = session.create_script(source)
        script.on('message', message)
        script.load()
        if not done.wait(25):
            raise TimeoutError('Native update did not complete the bounded property test')
finally:
    if script is not None:
        script.unload()
    if session is not None:
        session.detach()
    lease.close()
if not all(any(row['kind'] == stage and row.get('verified') for row in records) for stage in ('applied','restored')):
    raise SystemExit('Native property test failed; inspect the log.')
print('Original native module setters applied, read back and restored on the game thread.')
