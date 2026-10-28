"""Load a bounded named module through the pinned game's original resource library on its update thread."""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time
import frida
from owned_diagnostic_lock import acquire_native_lease

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch',required=True)
p.add_argument('--observation',required=True)
p.add_argument('--out',required=True)
p.add_argument('--module',choices=['Hpt_PulseLaser_Gimbal_Small','Hpt_MultiCannon_Fixed_Small','Int_Hyperdrive_Size2_Class1'],required=True)
p.add_argument('--install-and-restore',action='store_true',help='Use original installation/removal lifecycle, then restore the original recipe')
p.add_argument('--engineering',action='store_true',help='Apply the captured G5 FSD blueprint through the original native modifier pipeline')
p.add_argument('--ammunition',action='store_true',help='Verify native magazine and reserve setters before restoring the original recipe')
a=p.parse_args()
root=Path(__file__).resolve().parents[2]/'artifacts'
launch,observation,output=Path(a.launch).resolve(strict=True),Path(a.observation).resolve(strict=True),Path(a.out).resolve()
if not all(x.is_relative_to(root) for x in (launch,observation,output)):raise ValueError('Owned artifact paths required')
r=json.loads(launch.read_text(encoding='utf-8-sig'))
game=Path(r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe')
if r.get('mode')!='in-process-offline' or not r.get('zeroNetworkCapabilities') or hashlib.sha256(game.read_bytes()).hexdigest()!=r.get('imageSha256'):raise ValueError('Pinned isolated game required')
owners=[json.loads(s) for s in observation.read_text().splitlines() if json.loads(s).get('kind')=='refit-asset-owner']
owner=next(x for x in reversed(owners) if x.get('slot')=='SmallHardpoint2')
components=[json.loads(s) for s in observation.read_text().splitlines() if json.loads(s).get('kind')=='refit-components' and json.loads(s).get('slot')=='SmallHardpoint2']
ship=components[-1]['ship']
source=r'''
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==String.raw`E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`.toLowerCase())throw Error('Wrong owned game');
const library=ptr(LIBRARY),name=MODULE,installRequested=INSTALL,engineeringRequested=ENGINEERING,ammoRequested=AMMUNITION,slotName=name.startsWith('Int_Hyperdrive_')?'FrameShiftDrive':'SmallHardpoint2';let phase=0,test=null;
const alloc=new NativeFunction(m.base.add(0x53A590),'pointer',['uint64'],{scheduling:'exclusive'});
const ctor=new NativeFunction(m.base.add(0x122EEC0),'pointer',['pointer','pointer'],{scheduling:'exclusive'});
const makeKey=new NativeFunction(m.base.add(0x535DF0),'pointer',['pointer','pointer'],{scheduling:'exclusive'});
const reset=new NativeFunction(m.base.add(0x2139450),'void',['pointer'],{scheduling:'exclusive'});
const releaseHandle=new NativeFunction(m.base.add(0x1B29370),'void',['pointer'],{scheduling:'exclusive'});
const free=new NativeFunction(m.base.add(0x549C90),'void',['pointer','uint64'],{scheduling:'exclusive'});
function rva(p){return p.compare(m.base)>=0&&p.compare(m.base.add(m.size))<0?p.sub(m.base).toUInt32():null;}
function component(life,t){const q=life.add(0xC8).readPointer(),root=q.sub(0x30),slots=root.add(16).readPointer().add(48).readPointer(),v=root.add(24).readPointer(),i=slots.add(m.base.add(t).readU32()*16).readU64().toNumber();return i<=512?v.add(i*8).readPointer():ptr(0);}
function installedLife(){return test.manager.add(0x1D8).readPointer().add(test.index*0x30).readPointer();}
function installRecipe(manager,index,recipe){const output=Memory.alloc(24),weak=Memory.alloc(16),ref=Memory.alloc(8);output.writeByteArray(new Uint8Array(24));weak.writeByteArray(new Uint8Array(16));ref.writePointer(recipe);const fn=new NativeFunction(m.base.add(0x43B3A10),'void',['pointer','pointer','uint32','pointer','pointer','uint8','uint8','uint8','uint64'],{scheduling:'exclusive'});fn(manager,output,index,ref,weak,0,1,1,uint64('18446744073709551615'));const life=output.readPointer();new NativeFunction(m.base.add(0x62F2A0),'void',['pointer'],{scheduling:'exclusive'})(output);return life;}
function releaseRecipe(recipe){recipe.add(8).writeS32(recipe.add(8).readS32()-1);if(recipe.add(8).readS32()===0)new NativeFunction(recipe.readPointer().readPointer(),'void',['pointer','uint32'],{scheduling:'exclusive'})(recipe,1);}
function rcText(value){const text=alloc(value.length+21);text.writeU64(value.length);text.add(8).writeU64(value.length);text.add(16).writeU32(1);text.add(20).writeUtf8String(value);return text;}
function dropText(text){const count=text.add(16).readS32();text.add(16).writeS32(count-1);if(count===1)free(text,text.add(8).readU64().add(21));}
function maximumHealth(life){const c=component(life,0x5F41D5C);if(c.isNull())return null;const bits=c.add(0x410).readU32()^c.add(0x414).readU32(),temp=Memory.alloc(4);temp.writeU32(bits>>>0);return temp.readFloat();}
function engineer(recipe){
 if(name!=='Int_Hyperdrive_Size2_Class1')throw Error('This bounded prototype only modifies the fixture FSD');
 const mods=[['mod_passive_power',0.15,1],['mod_mass',0.3,1],['mod_health',-0.15,1],['mod_fsd_optimised_mass',0.55,1],['special_fsd_heavy',1,3]],texts=[],rows=Memory.alloc(mods.length*16),vector=Memory.alloc(32);
 for(let i=0;i<mods.length;i++){const text=rcText(mods[i][0]);texts.push(text);rows.add(i*16).writePointer(text);rows.add(i*16+8).writeFloat(mods[i][1]);rows.add(i*16+12).writeU32(mods[i][2]);}
 vector.writeU64(mods.length);vector.add(8).writeU64(0);vector.add(16).writeU64(mods.length);vector.add(24).writePointer(rows);
 new NativeFunction(m.base.add(0x2139250),'void',['pointer','pointer','pointer','pointer'],{scheduling:'exclusive'})(recipe,vector,ptr(CONTEXT),ptr(0));
 const metadata=Memory.alloc(48);metadata.writeByteArray(new Uint8Array(48));const title=rcText('FSD_LongRange');metadata.writeU64(128673694);metadata.add(8).writeU64(300100);metadata.add(16).writePointer(title);metadata.add(24).writeU32(5);metadata.add(28).writeFloat(1);metadata.add(32).writeU32(1);
 new NativeFunction(m.base.add(0x438C5E0),'pointer',['pointer','pointer'],{scheduling:'exclusive'})(recipe.add(0x40),metadata);
 for(const t of texts)dropText(t);dropText(title);
 send({kind:'original-engineering-built',recipe:recipe.toString(),modifiers:recipe.add(0x30).readU64().toString(),modifiedResource:recipe.add(0x18).readPointer().toString()});
}
Interceptor.attach(m.base.add(0x2154C40),{onLeave(){if(phase===3)return;try{if(phase===4){if(Date.now()<test.after)return;const life=installedLife(),ref=new NativeFunction(m.base.add(0x439A040),'pointer',['pointer','uint32'],{scheduling:'exclusive'})(test.manager,test.index);if(life.isNull()||life.add(0xC0).readU32()!==8||!ref.readPointer().equals(test.original))return;releaseRecipe(test.original);releaseRecipe(test.recipe);phase=3;send({kind:'asset-loading-complete',name,restored:true});return;}
 if(phase===2){if(Date.now()<test.after)return;const life=installedLife();if(life.isNull()||life.add(0xC0).readU32()!==8)return;
 if(ammoRequested&&!test.ammunitionSet){const clip=component(life,0x5F41D40),hopper=component(life,0x5F5096C);new NativeFunction(m.base.add(0x2F5D420),'void',['pointer','uint32','uint8'],{scheduling:'exclusive'})(clip,23,0);new NativeFunction(m.base.add(0x2F5D600),'void',['pointer','uint64'],{scheduling:'exclusive'})(hopper,321);test.ammunitionSet=true;test.after=Date.now()+500;return;}
 const result={kind:'installed-ammunition-components',life:life.toString(),components:[]};
 if(ammoRequested){const clip=component(life,0x5F41D40),hopper=component(life,0x5F5096C);result.ammunition={clip:new NativeFunction(m.base.add(0x2F4D3D0),'uint32',['pointer'],{scheduling:'exclusive'})(clip),hopper:new NativeFunction(m.base.add(0x2F4EA30),'uint64',['pointer'],{scheduling:'exclusive'})(hopper).toString()};}
 result.maximumHealth=maximumHealth(life);result.originalMaximumHealth=test.beforeHealth;
 for(const type of [0x5F41D40,0x5F5096C]){const c=component(life,type);if(c.isNull())continue;const vt=c.readPointer();result.components.push({typeRva:type,pointer:c.toString(),vtableRva:rva(vt),methods:Array.from({length:14},(_,j)=>rva(vt.add(j*8).readPointer())),bytes:Array.from(new Uint8Array(c.readByteArray(0x180)))});}
 send(result);installRecipe(test.manager,test.index,test.original);test.after=Date.now()+500;phase=4;return;}
 if(phase!==0)return;phase=1;
 const vt=library.readPointer(),entry=vt.add(0x30).readPointer();if(rva(vt)===null||rva(entry)===null)throw Error('Resource library is unavailable');
 send({kind:'resource-library',vtableRva:rva(vt),loadRva:rva(entry),modifierVtableRva:rva(ptr(CONTEXT).readPointer())});
 const text=alloc(name.length+0x15);text.writeU64(name.length);text.add(8).writeU64(name.length);text.add(16).writeU32(1);text.add(20).writeUtf8String(name);
 const textRef=Memory.alloc(8);textRef.writePointer(text);const key=Memory.alloc(24);makeKey(key,textRef);
 const handle=Memory.alloc(8);handle.writePointer(ptr(0));
 const load=new NativeFunction(entry,'pointer',['pointer','pointer','pointer','uint32','uint32'],{scheduling:'exclusive'});
 load(library,handle,key,0,0);
 send({kind:'resource-loaded',name,handle:handle.readPointer().toString()});
 const recipe=alloc(0xC0);ctor(recipe,handle);recipe.add(8).writeS32(recipe.add(8).readS32()+1);reset(recipe);
 if(engineeringRequested)engineer(recipe);
 send({kind:'recipe-created',name,recipe:recipe.toString(),vtableRva:rva(recipe.readPointer()),resolved:recipe.add(24).readPointer().toString(),bytes:Array.from(new Uint8Array(recipe.readByteArray(0xC0)))});
 if(installRequested){const manager=component(ptr(SHIP),0x5F2C234);if(!manager.readPointer().equals(m.base.add(0x5599D68)))throw Error('Wrong native module manager');
 const definition=manager.add(0x150).readPointer(),slots=definition.add(8).readPointer(),count=definition.add(16).readU32();let index=-1;
 for(let i=0;i<count&&i<128;i++)if(slots.add(i*0x70).readPointer().readUtf8String()===slotName)index=i;
 if(index<0)throw Error('Fixture hardpoint is absent');const originalRef=new NativeFunction(m.base.add(0x439A040),'pointer',['pointer','uint32'],{scheduling:'exclusive'})(manager,index),original=originalRef.readPointer();original.add(8).writeS32(original.add(8).readS32()+1);
 const oldLife=manager.add(0x1D8).readPointer().add(index*0x30).readPointer();test={manager,index,original,recipe,beforeHealth:maximumHealth(oldLife),after:Date.now()+1000};const created=installRecipe(manager,index,recipe);phase=2;send({kind:'original-install-requested',life:created.toString(),index});
 }else{releaseRecipe(recipe);phase=3;}
 releaseHandle(handle);
 const count=text.add(16).readS32();text.add(16).writeS32(count-2);if(count===2)free(text,name.length+0x15);
 if(!installRequested)send({kind:'asset-loading-complete',name});
 }catch(e){send({kind:'asset-loading-error',message:String(e)});}}});
send({kind:'asset-loading-armed',name});
'''
source=source.replace('LIBRARY',json.dumps(owner['library'])).replace('CONTEXT',json.dumps(owner['modifierContext'])).replace('MODULE',json.dumps(a.module)).replace('SHIP',json.dumps(ship)).replace('INSTALL','true' if a.install_and_restore else 'false').replace('ENGINEERING','true' if a.engineering else 'false').replace('AMMUNITION','true' if a.ammunition else 'false')
lease=acquire_native_lease(launch);s=j=None;done=threading.Event();gate=threading.Lock()
try:
    with output.open('x',encoding='utf-8') as f:
        def message(v,data):
            row=v.get('payload') if v.get('type')=='send' else {'kind':'observer-error','message':v.get('description')}
            with gate:
                if not f.closed:f.write(json.dumps(row)+'\n');f.flush()
            if row.get('kind') in ('asset-loading-complete','asset-loading-error','observer-error'):done.set()
        s=frida.attach(r['ProcessId']);j=s.create_script(source);j.on('message',message);j.load()
        if not done.wait(20):raise TimeoutError('Original update thread did not complete the asset request')
finally:
    if j is not None and not s.is_detached:j.unload()
    if s is not None and not s.is_detached:s.detach()
    lease.close()
rows=[json.loads(x) for x in output.read_text().splitlines()]
if not any(x.get('kind')=='asset-loading-complete' for x in rows):raise RuntimeError('Native asset loading failed')
if a.ammunition and not any(x.get('ammunition')=={'clip':23,'hopper':'321'} for x in rows):raise RuntimeError('Native ammunition readback differs')
print('Original native resource load and recipe construction completed:',a.module)
