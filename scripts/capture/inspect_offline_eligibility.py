"""Observe boolean menu prerequisites in one explicitly owned, isolated E launch."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch',required=True)
p.add_argument('--out',required=True)
args=p.parse_args()
launch=Path(args.launch).resolve(strict=True)
root=Path(__file__).resolve().parents[2]/'artifacts'
if not launch.is_relative_to(root) or launch.name!='launch.json':
    raise ValueError('An owned artifact launch receipt is required')
receipt=json.loads(launch.read_text())
expected=r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
if not receipt['zeroNetworkCapabilities'] or receipt['visible']:
    raise ValueError('An isolated diagnostic launch is required')
if hashlib.sha256(Path(expected).read_bytes()).hexdigest()!=receipt['imageSha256']:
    raise ValueError('Owned image differs')
output=Path(args.out).resolve()
if not output.is_relative_to(root):raise ValueError('Diagnostic output must remain in artifacts')
source=r"""
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==EXPECTED.toLowerCase())throw new Error('wrong owned image');
const installed=new Set(),last=new Map();
function observe(p,label,instance) {
 const rva=p.sub(m.base).toUInt32();
 if(p.compare(m.base)<0||p.compare(m.base.add(m.size))>=0||installed.has(rva))return;
 installed.add(rva);
 Interceptor.attach(p,{
  onEnter(args){
   this.observed=instance===undefined||instance.equals(args[0]);
   if(!this.observed)return;
   if(label==='menu-availability'){
    for(const [offset,slot,name] of [[0xD8,0x140,'profile-availability'],[0x240,0x18,'runtime-availability']]){
     const v=args[0].add(offset).readPointer();
     if(!v.isNull())observe(v.readPointer().add(slot).readPointer(),name,v);
    }
   }
   if(label==='profile-availability'){
    const self=args[0];
    for(const [slot,name] of [[0x138,'profile-initialized'],[0x18,'profile-version-ready'],[0x70,'profile-capability-list']])
     observe(self.readPointer().add(slot).readPointer(),name,self);
    const service=self.add(0x38).readPointer();
    if(!service.isNull())observe(service.readPointer().add(0x48).readPointer(),'profile-ship-access',service);
   }
   if(label==='profile-ship-access'){
    const service=args[0].add(0xC0).readPointer();
    if(!service.isNull())observe(service.readPointer().add(0x18).readPointer(),'local-session-reference',service);
   }
  },
  onLeave(ret){if(!this.observed)return;const value=label==='profile-capability-list'?ret.add(0x10).readU64().toString():ret.toUInt32()&255;if(last.get(label)!==value){last.set(label,value);send({label,rva,result:value});}}
 });
}
observe(m.base.add(0x1D5E490),'menu-availability');
send({ready:true});
""".replace('EXPECTED',json.dumps(expected))
lease=acquire_native_lease(launch)
session=frida.attach(receipt['ProcessId'])
script=session.create_script(source)
with output.open('x',encoding='utf-8') as file:
    def message(value,data):
        if value.get('type')=='send':
            file.write(json.dumps(value['payload'])+'\n');file.flush()
        else:
            file.write(json.dumps({'observerError':value.get('description','error')})+'\n');file.flush()
    script.on('message',message)
    try:script.load();time.sleep(30)
    finally:
        try:script.unload();session.detach()
        except frida.InvalidOperationError:pass
print('Owned offline prerequisite observation complete')
