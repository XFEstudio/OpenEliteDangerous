"""Observe bounded native JSON diagnostics for public world readers in an owned E launch."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch',required=True);p.add_argument('--out',required=True)
p.add_argument('--seconds',type=int,default=45)
a=p.parse_args();root=Path(__file__).resolve().parents[2]/'artifacts'
if not 5<=a.seconds<=60:p.error('Bounded observation required')
launch=Path(a.launch).resolve(strict=True);out=Path(a.out).resolve()
if launch.name!='launch.json' or not launch.is_relative_to(root) or not out.is_relative_to(root):raise ValueError('Owned artifact paths required')
receipt=json.loads(launch.read_text());expected=r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
runtime=(launch.parent/'XFEEliteDangerous.OfflineRuntime.dll').resolve(strict=True)
if (not receipt['zeroNetworkCapabilities'] or receipt.get('mode')!='in-process-offline'
    or Path(receipt['ownedNativeRuntime']).resolve(strict=True)!=runtime
    or hashlib.sha256(Path(expected).read_bytes()).hexdigest()!=receipt['imageSha256']):raise ValueError('Owned isolated image required')
source=r"""
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==EXPECTED.toLowerCase())throw new Error('Wrong owned image');
if(Process.getModuleByName('XFEEliteDangerous.OfflineRuntime.dll').path.toLowerCase()!==RUNTIME.toLowerCase())throw new Error('Wrong owned runtime');
const seen=new Set();let observed=0;
Interceptor.attach(m.base.add(0x4C0A7C0),{onEnter(args){
 const caller=this.returnAddress.sub(m.base).toUInt32();
 if(!((caller>=0x346ED60&&caller<0x346EFA0)||(caller>=0x1417230&&caller<0x1418570)))return;
 observed++;
 try{
  const error=args[0].add(0x18).readPointer();let message='';
  if(!error.isNull()){
   const length=error.readU64().toNumber();if(length>0&&length<=4096)message=error.add(0x14).readUtf8String(length);
  }
  message=message.replace(/[A-Fa-f0-9]{32,}/g,'[redacted-hex]').replace(/[A-Za-z0-9+/=]{64,}/g,'[redacted-encoded]').slice(0,1024);
  const record={kind:'public-world-json-status',callerRva:caller,error:message},key=JSON.stringify(record);
  if(!seen.has(key)){seen.add(key);send(record);}
 }catch(error){send({kind:'observer-unavailable',callerRva:caller});}
}});
rpc.exports.stop=()=>send({kind:'status-summary',observed,unique:seen.size});send({ready:true});
""".replace('EXPECTED',json.dumps(expected)).replace('RUNTIME',json.dumps(str(runtime)))
lease=acquire_native_lease(launch)
session=frida.attach(receipt['ProcessId']);script=session.create_script(source)
with out.open('x',encoding='utf-8') as file:
    def message(value,data):
        record=value['payload'] if value.get('type')=='send' else {'observerError':value.get('description','error')}
        file.write(json.dumps(record)+'\n');file.flush()
    script.on('message',message)
    try:
        script.load();deadline=time.monotonic()+a.seconds
        while time.monotonic()<deadline:time.sleep(min(1,deadline-time.monotonic()))
        script.exports_sync.stop()
    finally:
        try:script.unload();session.detach()
        except frida.InvalidOperationError:pass
print('Owned public-world JSON observation complete')
