"""Read-only callback/state observation in one receipt-verified zero-capability E process."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch',required=True);p.add_argument('--out',required=True)
p.add_argument('--seconds',type=int,default=20)
args=p.parse_args();root=Path(__file__).resolve().parents[2]/'artifacts'
if not 5<=args.seconds<=60:p.error('A bounded 5..60 second observation is required')
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
if(m.path.toLowerCase()!==EXPECTED.toLowerCase())throw new Error('Wrong owned image');
if(Process.getModuleByName('XFEEliteDangerous.OfflineRuntime.dll').path.toLowerCase()!==OWNED_RUNTIME.toLowerCase())throw new Error('Wrong owned runtime');
const targets=[0x1DA1D90,0x1D47080,0x1D46680,0x1DA10F0,0x1DA0CB0,0x1D3C4F0,0x1D3C2A0,
 0x1D21390,0x1D6D9D0,0x1D3BB60,0x1D3B990,0x1D76830,0x1D76650,0x1DA0350,0x1D9ED20,0x1D9ECD0,0x1D6DBD0,
 0x1BE08C0,0x1C4B620,0x1C4BA40,0x1C4AFE0];
const counts=new Map(),states=new Set();
function rva(p){return p.compare(m.base)>=0&&p.compare(m.base.add(m.size))<0?p.sub(m.base).toUInt32():null;}
function name(record){
 try{const text=record.readPointer().readUtf8String(64).split('\0')[0];return /^[A-Za-z_][A-Za-z_0-9]{0,62}$/.test(text)?text:null;}catch(_){return null;}
}
function text(p){try{if(p.isNull())return '';const length=p.readU64().toNumber();return length>=0&&length<=512?p.add(0x14).readUtf8String(length):'<bounded>'; }catch(_){return '<unreadable>';}}
function readiness(owner,index){
 try{
  const entry=owner.add(0x218+index*0x70),objects=[];
  for(const field of [8,0x10]){
   const life=entry.add(field).readPointer();if(life.isNull()){objects.push({field,present:false});continue;}
   const object=life.add(0xC8).readPointer(),value={field,present:true,lifecycle:life.add(0xC0).readS32()};
   if(!object.isNull()){
    value.objectVtableRva=rva(object.readPointer());
    try{value.name=text(object.add(0x20).readPointer().add(0x10).readPointer());}catch(_){}
    value.activationDelay=text(object.sub(0x30).add(0x4D0).readPointer());
   }
   objects.push(value);
  }
  const result={kind:'location-readiness',slot:index,objects},identity=JSON.stringify(result);
  if(!states.has(identity)){states.add(identity);send(result);}
 }catch(error){send({kind:'readiness-observer-unavailable',error:String(error)});}
}
function inspect(owner,target){
 try{
  const vtable=rva(owner.readPointer());if(vtable===null)return;
  for(let offset=0;offset<0x700;offset+=8){
   try{
    const candidate=owner.add(offset).readPointer();
    if(!candidate.readPointer().equals(m.base.add(0x5138DC0)))continue;
    const from=name(candidate.add(0x100).readPointer()),to=name(candidate.add(0x108).readPointer());
    if(from===null||to===null)continue;
    const entry={targetRva:target,ownerVtableRva:vtable,csmField:offset,from,to};const identity=JSON.stringify(entry);
    if(!states.has(identity)){states.add(identity);send({kind:'nested-state',...entry});}
   }catch(_){}
  }
 }catch(_){}
}
for(const target of targets){
 const observation={calls:0,results:{},lastInspect:0};counts.set(target,observation);
 Interceptor.attach(m.base.add(target),{
  onEnter(args){observation.calls++;const now=Date.now();if(now>observation.lastInspect+1000){observation.lastInspect=now;if(target===0x1C4B620)readiness(args[0],args[1].toUInt32()&255);else inspect(args[0],target);}},
  onLeave(result){const value=result.toInt32();if(value>=-2&&value<=8)observation.results[value]=(observation.results[value]||0)+1;}
 });
}
rpc.exports.stop=()=>send({kind:'callback-summary',callbacks:[...counts].filter(([_,value])=>value.calls).map(([targetRva,value])=>({targetRva,...value,lastInspect:undefined}))});
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
print('Owned location callback observation complete')
