"""Observe module controls in one receipt-owned isolated E-game, without desktop input."""
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
parser.add_argument('--out', required=True)
parser.add_argument('--seconds', type=int, default=120)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2] / 'artifacts'
launch = Path(args.launch).resolve(strict=True)
output = Path(args.out).resolve()
if not 5 <= args.seconds <= 300 or not launch.is_relative_to(root) or not output.is_relative_to(root):
    raise ValueError('Bounded owned artifact paths required')
receipt = json.loads(launch.read_text(encoding='utf-8-sig'))
expected = Path(r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe')
if receipt.get('mode') != 'in-process-offline' or not receipt.get('zeroNetworkCapabilities') or receipt['imageSha256'] != 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988':
    raise ValueError('Pinned isolated E-game receipt required')
if hashlib.sha256(expected.read_bytes()).hexdigest() != receipt['imageSha256']:
    raise ValueError('Game image changed')
lease = acquire_native_lease(launch)
source = r'''
const image=Process.getModuleByName('EliteDangerous64.exe');
if(image.path.toLowerCase()!==String.raw`E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`.toLowerCase())throw new Error('Wrong game process');
const modules=new Map();
function rva(p){return p.compare(image.base)>=0&&p.compare(image.base.add(image.size))<0?p.sub(image.base).toUInt32():null;}
function text(p){try{const length=p.readU64().toNumber();if(length<1||length>160)return null;const s=p.add(0x14).readUtf8String(length);return /^[A-Za-z0-9_]+$/.test(s)?s:null;}catch(_){return null;}}
function component(life,typeRva){
 const object=life.add(0xC8).readPointer();if(object.isNull())return null;
 const query=object.add(8);if(!query.readPointer().readPointer().equals(image.base.add(0x8C6EF0)))return null;
 const root=object.sub(0x30),slots=root.add(0x10).readPointer().add(0x30).readPointer(),values=root.add(0x18).readPointer();
 const type=image.base.add(typeRva).readU32();if(type===0||type>100000)return null;
 const index=slots.add(type*16).readU64().toNumber();return index<=512?values.add(index*8).readPointer():null;
}
function snapshot(row){
 if(row.life.add(0xB0).readU64().toString()!==row.id)return null;
 const types={};
 for(const type of [0x5F2C224,0x5F2C228,0x5F41D5C,0x5F420F0]){
  const c=component(row.life,type);if(c===null||c.isNull())continue;
  const table=c.readPointer();
  types[type.toString(16)]={pointer:c.toString(),vtableRva:rva(table),methods:[0x20,0x28,0x30,0x38,0x40,0x48,0x50,0x58,0x98,0xA0,0xE0,0x108].map(offset=>({offset,rva:rva(table.add(offset).readPointer())})),bytes:Array.from(new Uint8Array(c.readByteArray(0x100)))};
 }
 let powerPriority=null,powerPriorityPointer=null;
 if(row.ship!==undefined){const manager=component(row.ship,0x5F2C234);if(manager!==null&&!manager.isNull()){
  const power=manager.add(0xE0).readPointer();if(power.readPointer().equals(image.base.add(0x550AC48))){
   const count=power.add(0x140).readU64().toNumber(),entries=power.add(0x148).readPointer();if(count<=128)for(let i=0;i<count;i++){
    const entry=entries.add(i*24);if(entry.readPointer().equals(row.life)){const priority=entry.add(16).readPointer();if(priority.readPointer().equals(image.base.add(0x55983B0))){powerPriority=priority.add(0xB8).readU32();powerPriorityPointer=priority.toString();}}}
  }
 }}
 let engineering=null,ammunition=null,maximumHealth=null;
 const identity=component(row.life,0x5F2C224);if(identity!==null&&!identity.isNull()){
  const recipe=identity.add(0x168).readPointer();
  if(!recipe.isNull()&&recipe.readPointer().equals(image.base.add(0x515B860))){
   const count=recipe.add(0x30).readU64().toNumber(),name=recipe.add(0x50).readPointer();
   if(count>0&&count<=64&&!name.isNull()){
    const values=recipe.add(0x38).readPointer(),modifiers=[];
    for(let i=0;i<count;i++){const item=values.add(i*16);modifiers.push({name:text(item.readPointer()),value:item.add(8).readFloat(),type:item.add(12).readU32()});}
    engineering={recipeId:recipe.add(0x40).readU64().toString(),engineerId:recipe.add(0x48).readU64().toString(),recipeName:text(name),level:recipe.add(0x58).readU32(),progress:recipe.add(0x5C).readFloat(),modifiers};
   }
  }
 }
 const clip=component(row.life,0x5F41D40),hopper=component(row.life,0x5F5096C);
 if(clip!==null&&!clip.isNull()&&hopper!==null&&!hopper.isNull())ammunition={clip:(clip.add(0x438).readU32()^clip.add(0x43C).readU32())>>>0,hopper:hopper.add(0xF0).readU64().xor(hopper.add(0xF8).readU64()).toString()};
 const health=component(row.life,0x5F41D5C);if(health!==null&&!health.isNull()){
  const bits=(health.add(0x410).readU32()^health.add(0x414).readU32())>>>0,buffer=new ArrayBuffer(4);new DataView(buffer).setUint32(0,bits,true);maximumHealth=new DataView(buffer).getFloat32(0,true);
 }
 return {kind:'native-live-module',slot:row.slot,module:row.name,id:row.id,life:row.life.toString(),state:row.life.add(0xC0).readU32(),powerPriority,powerPriorityPointer,engineering,ammunition,maximumHealth,types};
}
Interceptor.attach(image.base.add(0x24F0B80),{
 onEnter(args){try{
  const state=args[2],slot=text(state.readPointer()),name=text(state.add(8).readPointer());
  if(slot===null||name===null)return;
  const life=args[1].readPointer();if(life.isNull())return;
  const row={slot,name,life,id:life.add(0xB0).readU64().toString(),ship:args[0].add(0xB0).readPointer()};modules.set(slot,row);this.row=row;
 }catch(error){send({kind:'module-observation-error',message:String(error)});}},
 onLeave(){if(this.row!==undefined){try{const row=snapshot(this.row);if(row!==null)send(row);}catch(error){send({kind:'module-observation-error',message:String(error)});}}}
});
let next=0;
Interceptor.attach(image.base.add(0x2154C40),{onLeave(){
 const now=Date.now();if(now<next)return;next=now+5000;
 for(const row of modules.values()){try{const result=snapshot(row);if(result!==null)send(result);}catch(_){}}
}});
send({kind:'refit-observer-installed'});
'''
session = script = None
gate = threading.Lock()
failed = False
try:
    with output.open('x', encoding='utf-8') as file:
        def message(value, data):
            global failed
            record = value.get('payload') if value.get('type') == 'send' else {'kind':'observer-error','message':value.get('description')}
            if record.get('kind') in ('observer-error','module-observation-error'):
                failed = True
            with gate:
                if not file.closed:
                    file.write(json.dumps(record)+'\n')
                    file.flush()
        session = frida.attach(receipt['ProcessId'])
        script = session.create_script(source)
        script.on('message', message)
        script.load()
        deadline = time.monotonic()+args.seconds
        while time.monotonic() < deadline:
            time.sleep(min(.5, deadline-time.monotonic()))
        script.unload()
        script.off('message',message)
        script = None
finally:
    if script is not None:
        script.unload()
    if session is not None:
        session.detach()
    lease.close()
if failed:
    raise SystemExit('Module observation reported an error; inspect the log.')
