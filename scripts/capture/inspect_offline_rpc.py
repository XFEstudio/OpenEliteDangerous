"""Read method indices/code RVAs from native objects in an owned isolated E launch; no payloads or native calls."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch',required=True);p.add_argument('--out',required=True)
p.add_argument('--seconds',type=int,default=110)
args=p.parse_args();root=Path(__file__).resolve().parents[2]/'artifacts'
if not 10<=args.seconds<=240:p.error('--seconds must be in 10..240')
launch=Path(args.launch).resolve(strict=True);output=Path(args.out).resolve()
if not launch.is_relative_to(root) or launch.name!='launch.json' or not output.is_relative_to(root):
    raise ValueError('Owned artifact paths required')
receipt=json.loads(launch.read_text())
expected=r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
owned_runtime=(launch.parent/'XFEEliteDangerous.OfflineRuntime.dll').resolve(strict=True)
if (not receipt['zeroNetworkCapabilities'] or receipt.get('mode')!='in-process-offline'
    or Path(receipt['ownedNativeRuntime']).resolve(strict=True)!=owned_runtime
    or hashlib.sha256(Path(expected).read_bytes()).hexdigest()!=receipt['imageSha256']):
    raise ValueError('Owned isolated image required')
source=r"""
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==EXPECTED.toLowerCase())throw new Error('wrong owned image');
if(Process.getModuleByName('XFEEliteDangerous.OfflineRuntime.dll').path.toLowerCase()!==OWNED_RUNTIME.toLowerCase())throw new Error('wrong owned runtime');
const owners=new Map();let serial=0;
function code(p){return p.compare(m.base.add(0x1000))>=0&&p.compare(m.base.add(0x4DD0000))<0?p.sub(m.base).toUInt32():null;}
function receiver(object){
 const result={};
 try{
  const adjustment=object.add(0x48).readS32();
  if(Math.abs(adjustment)>0x10000)return result;
  const root=object.add(0x38).readPointer().add(adjustment),table=root.readPointer();
  result.thisAdjustment=adjustment;
  if(table.compare(m.base.add(0x4DD0000))<0||table.compare(m.base.add(m.size))>=0)return result;
  result.rootVtableRva=table.sub(m.base).toUInt32();result.services=[];
  for(const field of [0x108,0x138,0x168]){
   try{
    const service=root.add(field).readPointer();if(service.isNull())continue;
    const vtable=service.readPointer();
    if(vtable.compare(m.base.add(0x4DD0000))<0||vtable.compare(m.base.add(m.size))>=0)continue;
    const entries=[];
    for(const slot of [0x18,0x30,0x40,0x48,0x78]){const rva=code(vtable.add(slot).readPointer());if(rva!==null)entries.push({slot,rva});}
    result.services.push({field,vtableRva:vtable.sub(m.base).toUInt32(),entries});
   }catch(_){}
  }
 }catch(_){}
 return result;
}
Interceptor.attach(m.base.add(0x62CC40),{onEnter(args){
 const owner=args[3];const id=owner.toString();
 if(!owners.has(id)&&owners.size<1024)owners.set(id,{owner,serial:++serial,reported:false});
}});
const timer=setInterval(()=>{
 for(const value of owners.values()){
  if(value.reported)continue;
  try{
   const count=value.owner.add(0x1E0).readU64().toNumber();
   if(count!==155)continue;
   const table=value.owner.add(0x1E8).readPointer(),methods=[];
   for(let index=0;index<count;index++){
    const object=table.add(index*8).readPointer();
    if(object.isNull()||object.add(0x28).readU32()!==index)throw new Error('table index mismatch');
    const fields=[];
    for(const offset of [0x30,0x38,0x40,0x48,0x50,0x58,0x60]){
     const target=code(object.add(offset).readPointer());if(target!==null)fields.push({offset,rva:target});
    }
    methods.push({index,delivery:object.add(0x18).readU32(),vtableRva:object.readPointer().sub(m.base).toUInt32(),codeFields:fields,
     receiver:[78,79,128,134].includes(index)?receiver(object):undefined});
   }
   value.reported=true;send({kind:'server-admin-method-table',ownerSerial:value.serial,methods});
  }catch(_){}
 }
},250);
rpc.exports.stop=()=>{clearInterval(timer);send({kind:'observer-summary',objects:owners.size,tables:[...owners.values()].filter(v=>v.reported).length});};
send({ready:true});
""".replace('EXPECTED',json.dumps(expected)).replace('OWNED_RUNTIME',json.dumps(str(owned_runtime)))
lease=acquire_native_lease(launch)
session=frida.attach(receipt['ProcessId']);script=session.create_script(source)
with output.open('x',encoding='utf-8') as file:
    def message(value,data):
        if value.get('type')=='send':file.write(json.dumps(value['payload'])+'\n');file.flush()
        else:file.write(json.dumps({'observerError':value.get('description','error')})+'\n');file.flush()
    script.on('message',message)
    try:
        script.load();deadline=time.monotonic()+args.seconds
        while time.monotonic()<deadline:time.sleep(min(1,deadline-time.monotonic()))
        script.exports_sync.stop()
    finally:
        try:script.unload();session.detach()
        except frida.InvalidOperationError:pass
print('Owned native method-table observation complete')
