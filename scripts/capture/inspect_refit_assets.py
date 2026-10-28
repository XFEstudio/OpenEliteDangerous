"""Read-only asset, component and exception diagnostics in one receipt-owned private game."""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch', required=True)
p.add_argument('--out', required=True)
p.add_argument('--seconds', type=int, default=120)
a = p.parse_args()
root = Path(__file__).resolve().parents[2] / 'artifacts'
launch, output = Path(a.launch).resolve(strict=True), Path(a.out).resolve()
if not launch.is_relative_to(root) or not output.is_relative_to(root) or not 5 <= a.seconds <= 600:
    raise ValueError('Bounded owned artifact paths required')
r = json.loads(launch.read_text(encoding='utf-8-sig'))
expected = Path(r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe')
if r.get('mode') != 'in-process-offline' or not r.get('zeroNetworkCapabilities') or hashlib.sha256(expected.read_bytes()).hexdigest() != r.get('imageSha256'):
    raise ValueError('Pinned isolated E-game required')
source = r'''
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==String.raw`E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`.toLowerCase())throw Error('Wrong owned game');
const rows=new Map();let next=0;
function rva(p){return p.compare(m.base)>=0&&p.compare(m.base.add(m.size))<0?p.sub(m.base).toUInt32():null;}
function text(p){try{const n=p.readU64().toNumber();return n>0&&n<160?p.add(0x14).readUtf8String(n):null;}catch(_){return null;}}
function components(life){
 const q=life.add(0xC8).readPointer(),root=q.sub(0x30),values=root.add(0x18).readPointer(),result=[];
 for(let i=0;i<128;i++){try{const c=values.add(i*8).readPointer();if(c.isNull())continue;const vt=c.readPointer(),v=rva(vt);if(v===null||rva(vt.readPointer())===null)continue;
  result.push({index:i,pointer:c.toString(),vtableRva:v,methods:Array.from({length:12},(_,j)=>rva(vt.add(j*8).readPointer())),bytes:Array.from(new Uint8Array(c.readByteArray(0x180)))});
 }catch(_){}}
 return result;
}
Process.setExceptionHandler(e=>{send({kind:'native-exception',type:e.type,address:e.address.toString(),addressRva:rva(e.address),memory:e.memory,context:e.context});return false;});
Interceptor.attach(m.base.add(0x24F0B80),{onEnter(args){try{
 const state=args[2],life=args[1].readPointer(),slot=text(state.readPointer()),name=text(state.add(8).readPointer());if(slot===null||name===null)return;
 const row={life,slot,name,ship:args[0].add(0xB0).readPointer()};rows.set(slot,row);
 send({kind:'refit-asset-owner',owner:args[0].toString(),library:args[0].add(0x48).readPointer().toString(),modifierContext:args[0].add(0x78).readPointer().toString(),slot,name,state:Array.from(new Uint8Array(state.readByteArray(0xE8)))});
 }catch(e){send({kind:'asset-observe-error',message:String(e)});}}});
Interceptor.attach(m.base.add(0x2154C40),{onLeave(){if(Date.now()<next)return;next=Date.now()+5000;for(const r of rows.values()){try{send({kind:'refit-components',slot:r.slot,name:r.name,life:r.life.toString(),ship:r.ship.toString(),components:components(r.life)});}catch(_){}}}});
send({kind:'asset-observer-ready',threads:Process.enumerateThreads().slice(0,16).map(t=>({id:t.id,state:t.state}))});
'''
lease = acquire_native_lease(launch)
session = script = None
gate = threading.Lock()
try:
    with output.open('x', encoding='utf-8') as f:
        def message(v, data):
            row = v.get('payload') if v.get('type') == 'send' else {'kind': 'observer-error', 'message': v.get('description')}
            with gate:
                if not f.closed:
                    f.write(json.dumps(row)+'\n'); f.flush()
        session = frida.attach(r['ProcessId'])
        script = session.create_script(source)
        script.on('message', message)
        script.load()
        end = time.monotonic()+a.seconds
        while time.monotonic() < end and not session.is_detached:
            time.sleep(.25)
finally:
    if script is not None and not session.is_detached:
        script.unload()
    if session is not None and not session.is_detached:
        session.detach()
    lease.close()
