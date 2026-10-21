"""Bounded native control checks in the receipt-owned, network-isolated E game; no OS input."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import threading
import msvcrt
import frida
import re

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--launch', required=True)
p.add_argument('--out', required=True)
p.add_argument('--seconds', type=int, default=12)
p.add_argument('--action', choices=['observe', 'rejoin-open', 'flight-and-pause', 'flight-axis-and-pause', 'save-and-exit', 'save-and-quit'], default='observe')
p.add_argument('--dispatch-inventory', action='store_true', help='Bounded read-only native component callback inventory')
p.add_argument('--axis', choices=['forward', 'angular-x'], default='forward')
p.add_argument('--entity', help='Exact fixture ship ID; required for online synthetic flight')
args = p.parse_args()
root = Path(__file__).resolve().parents[2] / 'artifacts'
launch = Path(args.launch).resolve(strict=True)
output = Path(args.out).resolve()
if not 5 <= args.seconds <= 180 or not launch.is_relative_to(root) or not output.is_relative_to(root):
    raise ValueError('Bounded owned artifact paths required')
receipt = json.loads(launch.read_text())
if args.entity is not None and (not args.entity.isdigit() or not 0x200000000 <= int(args.entity) < 0xFFFFFFFF0000):
    raise ValueError('Expected one ordinary replicated native ship ID')
if receipt.get('online') and args.action == 'flight-axis-and-pause' and args.entity is None:
    raise ValueError('Online synthetic flight must select the exact fixture ship entity')
expected = r'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
runtime = (launch.parent / 'XFEEliteDangerous.OfflineRuntime.dll').resolve(strict=True)
if (receipt.get('mode') != 'in-process-offline' or not receipt['zeroNetworkCapabilities']
        or Path(receipt['ownedNativeRuntime']).resolve(strict=True) != runtime
        or receipt['imageSha256'] != 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988'
        or hashlib.sha256(Path(expected).read_bytes()).hexdigest() != receipt['imageSha256']):
    raise ValueError('Pinned isolated owned game required')
seed_entities = {}
for seed_file in launch.parent.glob('entity-*.jsonl'):
    if seed_file.is_symlink() or seed_file.stat().st_size > 16 * 1024 * 1024:
        continue
    for line in seed_file.read_text(encoding='utf-8').splitlines():
        try:
            record = json.loads(line)
            if (record.get('kind') == 'native-entity-observed'
                    and str(record.get('id', '')).isdigit()
                    and re.fullmatch(r'0x[0-9a-fA-F]{1,16}', record.get('lifecyclePointer', ''))
                    and len(seed_entities) < 512):
                seed_entities[record['id']] = {'id': record['id'], 'life': record['lifecyclePointer']}
        except (ValueError, TypeError):
            pass
source = r"""
const m=Process.getModuleByName('EliteDangerous64.exe');
if(m.path.toLowerCase()!==EXPECTED.toLowerCase()||Process.getModuleByName('XFEEliteDangerous.OfflineRuntime.dll').path.toLowerCase()!==RUNTIME.toLowerCase())throw new Error('Wrong owned process');
const seen=new Set();let lastFlow=0,flowThread=0,controlPointer=null,phase=0,started=0,previousThrottle=0,done=false,lastHoldSample=0;
const action=ACTION,targetEntity=TARGET_ENTITY,axisName=AXIS_NAME,axisOffset=axisName==='forward'?0x10:0x50,controls=new Set();
let configuredExitToMenu=false,configuredQuitGame=false,configuredResume=false,completedExitActivities=false;
const activityTypes=new Map(),completionHooks=new Map();
const completionQueries=new Map();
const flightObservationTimes=new Map();
let inputRootObserved=null;const dispatchedCallbacks=new Set();
let lastAxisSample=0;
const throttle=new NativeFunction(m.base.add(0x25E3790),'void',['pointer','float'],{scheduling:'exclusive'});
const clearThrottle=new NativeFunction(m.base.add(0x25E6DE0),'void',['pointer'],{scheduling:'exclusive'});
const flowAction=new NativeFunction(m.base.add(0x1D62CA0),'bool',['pointer','pointer'],{scheduling:'exclusive'});
const menuAction=new NativeFunction(m.base.add(0x4836060),'void',['pointer','pointer','int'],{scheduling:'exclusive'});
function rva(p){return p.compare(m.base)>=0&&p.compare(m.base.add(m.size))<0?p.sub(m.base).toUInt32():null;}
function identifier(p){try{const s=p.readUtf8String();return s.length<=127&&/^[A-Za-z][A-Za-z0-9_ ]{0,126}$/.test(s)?s:null;}catch(_){return null;}}
function configuredExpression(p){try{const s=p.readUtf8String();return s.length>0&&s.length<=512&&/^[\x20-\x7E\r\n\t]+$/.test(s)?s:null;}catch(_){return null;}}
function report(value){const key=JSON.stringify(value);if(!seen.has(key)){seen.add(key);send({...value,observedUtc:new Date().toISOString(),observedMilliseconds:Date.now()});}}
const nativeEntities=new Map(),physicsEvaluation=new Map(),physicsOwnerIds=new Map();let entitySample=0;
function explicitLocalAuthority(life){
 //645AE0 copies the authority at life+48 ->+10;4C30920 tests its+10 byte.
 try{const authority=life.add(0x48).readPointer();if(authority.isNull())return false;
  const machine=authority.add(0x10).readPointer();return !machine.isNull()&&machine.add(0x10).readU8()!==0;
 }catch(_){return false;}
}
function leafAddress(object,method){
 if(method.equals(m.base.add(0x3CB93C0))){
  //Pinned const getter selects the current native ship transform cache.
  return object.add(object.add(0x410).readU8()!==0||object.sub(8).readPointer().add(0x10).readU32()===0?0xF8:0x1E8);
 }
 const bytes=new Uint8Array(method.readByteArray(12));
 if(bytes[0]!==0x48)return null;
 if(bytes[1]===0x8D&&bytes[2]===0x81&&bytes[7]===0xC3)return object.add(method.add(3).readS32());
 if(bytes[1]===0x8D&&bytes[2]===0x41&&bytes[4]===0xC3)return object.add(method.add(3).readS8());
 if(bytes[1]===0x8B&&bytes[2]===0x81&&bytes[7]===0xC3)return object.add(method.add(3).readS32()).readPointer();
 if(bytes[1]===0x8B&&bytes[2]===0x41&&bytes[4]===0xC3)return object.add(method.add(3).readS8()).readPointer();
 return null;
}
function entityRecord(life,origin){
 try{
  if(life.isNull())return;
  const state=life.add(0xC0).readU32();if(state<1||state>9)return;
  const component=life.add(0xC8).readPointer();if(component.isNull())return;
  const metadata=component.add(0x20).readPointer(),name=metadata.add(0x10).readPointer();
  const kind=resourceName(name);if(kind===null)return;
  const id=life.add(0xB0).readU64().toString();
  const previous=nativeEntities.get(id);
  if(nativeEntities.size<2048&&(previous===undefined||!previous.life.equals(life))){
   nativeEntities.set(id,{life,kind});report({kind:'native-entity-observed',id,type:kind,state,origin,lifecyclePointer:life.toString()});
  }
 }catch(_){}
}
function sampleEntities(){
 const ordinal=++entitySample;
 for(const [id,value] of nativeEntities){
  try{
   if(Number(id)<0x200000000)continue;
   const life=value.life;if(life.add(0xB0).readU64().toString()!==id)continue;
   //649CB0 writes state8 immediately before the "State = Live" trace.
   const state=life.add(0xC0).readU32();if(state!==8)continue;
   const component=life.add(0xC8).readPointer(),query=component.add(8),table=query.readPointer();
   const getter=table.readPointer();
   report({kind:'native-query-interface',id,type:value.kind,state,explicitLocalAuthority:explicitLocalAuthority(life),getterRva:rva(getter),vtableRva:rva(table)});
   if(!getter.equals(m.base.add(0x8C6EF0)))continue;
   //8C6EF0 ->9C8380 is a const component table lookup. Read its exact
   //metadata/slot indirection directly; never call into a native object here.
   const root=component.sub(0x30),slots=root.add(0x10).readPointer().add(0x30).readPointer(),components=root.add(0x18).readPointer();
   for(const typeRva of [0x5F02CFC,0x5F2DF60]){
    const type=m.base.add(typeRva).readU32();if(type===0||type>100000)continue;
    const index=slots.add(type*16).readU64().toNumber();if(index>512)continue;
    const transform=components.add(index*8).readPointer();if(transform.isNull())continue;
    const transformTable=transform.readPointer(),method=transformTable.add(0x28).readPointer();
    report({kind:'native-component-slot',id,type:value.kind,nativeTypeId:type,typeRva,index,componentVtableRva:rva(transformTable),componentPointer:transform.toString()});
    if(method.equals(m.base.add(0x3CB93C0))){
     const candidates=[0xF8,0x1E8].map(offset=>({offset,position:[0x60,0x68,0x70].map(field=>transform.add(offset+field).readDouble())}));
     report({kind:'native-ship-transform-caches',id,ordinal,selector:transform.add(0x410).readU8(),ownerState:transform.sub(8).readPointer().add(0x10).readU32(),candidates});
    }
    if(transformTable.equals(m.base.add(0x5355318))){
     //30B8D50 writes this double world matrix at+D8. Observe the cache;
     //it is a void updater, so never interpret its RAX as an address.
     const matrix=transform.add(0xD8),position=[0x60,0x68,0x70].map(offset=>matrix.add(offset).readDouble());
     const rotation=[0,8,0x10,0x20,0x28,0x30,0x40,0x48,0x50].map(offset=>matrix.add(offset).readDouble());
     if(position.every(coordinate=>Number.isFinite(coordinate)&&Math.abs(coordinate)<=1e18)&&rotation.every(coordinate=>Number.isFinite(coordinate)&&Math.abs(coordinate)<1.001)&&rotation.reduce((sum,value)=>sum+value*value,0)>0.1)
      report({kind:'native-entity-position',id,type:value.kind,state,ordinal,coordinateSpace:'native-double-world-transform',address:null,position,rotation,
       writerRva:0x30B8D50,source:'read-only-native-world-matrix-cache'});
     continue;
    }
    const address=leafAddress(transform,method);
    if(address===null){
     report({kind:'native-transform-reader',id,type:value.kind,transformVtableRva:rva(transformTable),getterRva:rva(method),typeRva});continue;
    }
    const position=[0x60,0x68,0x70].map(offset=>address.add(offset).readDouble());
    if(position.some(coordinate=>!Number.isFinite(coordinate)||Math.abs(coordinate)>1e18))continue;
    const rotation=[0,8,0x10,0x20,0x28,0x30,0x40,0x48,0x50].map(offset=>address.add(offset).readDouble());
    report({kind:'native-entity-position',id,type:value.kind,state,ordinal,coordinateSpace:'native-double-world-transform',explicitLocalAuthority:explicitLocalAuthority(life),position,rotation,
     source:'read-only-native-transform-getter-equivalent',transformVtableRva:rva(transformTable),getterRva:rva(method),typeRva});break;
   }
  }catch(_){}
 }
}
for(const candidate of SEED_ENTITIES){
 try{const life=ptr(candidate.life);if(life.add(0xB0).readU64().toString()===candidate.id)entityRecord(life,'validated-owned-observation-cache');}catch(_){}
}
Interceptor.attach(m.base.add(0x6426B0),{onEnter(args){this.lifeReference=args[2];},onLeave(){
 try{entityRecord(this.lifeReference.readPointer(),'native-create-object-from-factory');}catch(_){}
}});
Interceptor.attach(m.base.add(0x8C2980),{onEnter(args){this.entityRoot=args[0];},onLeave(){
 try{entityRecord(this.entityRoot.add(0x58).readPointer(),'native-reflected-entity-constructor');}catch(_){}
}});
Interceptor.attach(m.base.add(0x62CCB0),{onEnter(args){this.component=args[0];},onLeave(){
 try{entityRecord(this.component.add(0x28).readPointer(),'native-common-object-constructor');}catch(_){}
}});
Interceptor.attach(m.base.add(0x649CB0),{onEnter(args){this.liveComponent=args[0];},onLeave(){
 try{entityRecord(this.liveComponent.add(0x28).readPointer(),'native-state-live-transition');}catch(_){}
}});
Interceptor.attach(m.base.add(0x64ACF0),{onEnter(args){
 try{if(this.returnAddress.equals(m.base.add(0x2E47E79))){
  const life=args[0].readPointer();entityRecord(life,'native-ship-physics-owner');
  const physics=physicsEvaluation.get(Process.getCurrentThreadId());if(physics===undefined)return;
  const id=life.add(0xB0).readU64().toString(),key=physics.toString();physicsOwnerIds.set(key,id);
  report({kind:'native-physics-owner',id,physicsPointer:key});
 }}catch(_){}
}});
function resourceName(p){try{const length=p.readU64().toNumber();if(length<1||length>128)return null;const value=p.add(0x14).readUtf8String(length);return /^[A-Za-z][A-Za-z0-9_. -]{0,127}$/.test(value)?value:null;}catch(_){return null;}}
function nativeType(table){try{const getter=table.add(8).readPointer();if(getter.readU8()!==0x48||getter.add(1).readU8()!==0x8D||getter.add(2).readU8()!==5||getter.add(7).readU8()!==0xC3)return null;return identifier(getter.add(7).add(getter.add(3).readS32()));}catch(_){return null;}}
function watchCompletion(object,table,type){
 if(type===null)return;activityTypes.set(object.toString(),type);
 const method=table.add(0x40).readPointer(),target=rva(method);if(target===null||completionHooks.has(target))return;
 completionHooks.set(target,Interceptor.attach(method,{
  onEnter(args){this.activityType=activityTypes.get(args[0].toString());},
  onLeave(result){if(this.activityType)report({kind:'native-activity-completion',type:this.activityType,complete:(result.toUInt32()&255)!==0,nativeRva:target});}
 }));
}
function namedAction(flow,csm,name){
 const definition=csm.add(0xF8).readPointer(),records=definition.add(0x10).readPointer(),count=definition.add(0x18).readU32();
 if(count>4096)throw new Error('Invalid native action count');
 for(let index=0;index<count;index++){
  const pointer=records.add(index*0x28).readPointer();
  if(identifier(pointer)===name){
   if(!flow.readPointer().equals(m.base.add(0x5236DB0))||!flow.readPointer().add(0x20).readPointer().equals(m.base.add(0x1D62CA0)))throw new Error('Native action interface changed');
   const accepted=flowAction(flow,pointer);report({kind:'native-action',name,accepted,nativeRva:0x1D62CA0,threadId:Process.getCurrentThreadId()});return accepted;
  }
 }
 throw new Error('Requested action is absent from the native definition');
}
function requireControl(){
 if(controlPointer===null||controls.size!==1||!controlPointer.readPointer().equals(m.base.add(0x529D790))
  ||!controlPointer.readPointer().add(0x158).readPointer().equals(m.base.add(0x25E3790))
  ||!controlPointer.readPointer().add(0x150).readPointer().equals(m.base.add(0x25E6DE0)))throw new Error('Native player control is not uniquely identified');
}
function flight(flow,csm,from,to){
 if(done||action==='observe'||from!==to)return;
 if(action==='save-and-exit'||action==='save-and-quit'||action==='rejoin-open'){
  if(action==='rejoin-open'&&phase===0&&from==='MainMenu'){phase=3;report({kind:'native-rejoin-existing-menu'});}
  if(phase===0&&(from==='InGame'||from==='PrePauseMenu')){phase=1;if(!namedAction(flow,csm,'Pause'))throw new Error('Native pause request rejected');}
  if(phase===0&&from==='Pause')phase=1;
  if(phase===0&&from==='SaveAndExit'&&completedExitActivities)phase=1;
  if(phase===1&&(from==='Pause'||(from==='SaveAndExit'&&completedExitActivities))){
   const quit=action==='save-and-quit';
   if(quit?!configuredQuitGame:!configuredExitToMenu)throw new Error('Native ExitOptions action was not verified');
   phase=2;if(!namedAction(flow,csm,quit?'Confirmed Quit Game':'Confirmed Quit To Main Menu'))throw new Error('Native configured save/exit action rejected');
   if(quit){done=true;report({kind:'test-done',outcome:'native-save-and-quit-requested'});}
  }
  if(phase===2&&from==='MainMenu'){
   if(action==='rejoin-open'){phase=3;report({kind:'native-rejoin-saved-menu'});}
   else{done=true;report({kind:'test-done',outcome:'saved-to-main-menu'});}
  }
  if(action==='rejoin-open'&&phase===5&&from==='InGame'){done=true;report({kind:'test-done',outcome:'native-open-reentered'});}
  return;
 }
 if(action==='flight-axis-and-pause'){
  if(phase===0&&from==='Pause'&&configuredResume){if(!namedAction(flow,csm,'InGame'))throw new Error('Native configured resume rejected');return;}
  if(phase===3&&from==='Pause'){done=true;report({kind:'test-done',outcome:'synthetic-native-axis-and-pause'});return;}
  if(from!=='InGame'||inputRootObserved===null)return;
  const elapsed=Date.now()-started;
  if(phase===0){if(started===0){started=Date.now();return;}if(elapsed<3000)return;started=Date.now();phase=1;report({kind:'native-axis-pulse-start',axis:axisName,axisOffset,value:0.25,inputVtableRva:0x5311220,consumerRva:0x2E47E40});}
  else if(phase===1&&elapsed>=4000){phase=2;report({kind:'native-axis-pulse-brake',value:0});}
  else if(phase===2&&elapsed>=9000){phase=3;if(!namedAction(flow,csm,'Pause'))throw new Error('Native pause request rejected');}
  return;
 }
 if(action==='flight-and-pause'&&phase===3&&from==='Pause'){done=true;report({kind:'test-done',outcome:'native-flight-pulse-and-pause'});return;}
 if(from!=='InGame')return;
 if(controlPointer===null){report({kind:'waiting-for-native-player-control'});return;}
 requireControl();const now=Date.now();
 if(phase===0){
  previousThrottle=controlPointer.add(0x680).readFloat();
  if(!Number.isFinite(previousThrottle)||(previousThrottle!==-100&&Math.abs(previousThrottle)>1))throw new Error('Unexpected prior native throttle override');
  started=now;phase=1;throttle(controlPointer,0.25);
  report({kind:'native-throttle',value:0.25,nativeRva:0x25E3790,observed:controlPointer.add(0x680).readFloat(),threadId:Process.getCurrentThreadId()});
 }else if(phase===1&&now-started>=4000){
  phase=2;throttle(controlPointer,0);report({kind:'native-throttle',value:0,nativeRva:0x25E3790,observed:controlPointer.add(0x680).readFloat()});
 }else if(phase===2&&now-started>=9000){
  phase=3;if(previousThrottle===-100)clearThrottle(controlPointer);else throttle(controlPointer,previousThrottle);
  report({kind:'native-throttle-restored',previousThrottle,observed:controlPointer.add(0x680).readFloat(),nativeRva:previousThrottle===-100?0x25E6DE0:0x25E3790});
  if(!namedAction(flow,csm,'Pause'))throw new Error('Native pause request rejected');
 }
}
// Synthetic input is applied only while the original physics input evaluator
// reads its IShipControl record. Each invocation restores the previous axis
// value on return. No transform, velocity, readiness or eligibility is written.
Interceptor.attach(m.base.add(0x2E47E40),{
 onEnter(args){
  physicsEvaluation.set(Process.getCurrentThreadId(),args[0]);
  try{
   if(action!=='flight-axis-and-pause'||done||(phase!==1&&phase!==2)||Process.getCurrentThreadId()!==flowThread)return;
   const physics=args[0];if(!physics.readPointer().equals(m.base.add(0x5337830)))throw new Error('Physics component changed');
   const entity=physicsOwnerIds.get(physics.toString()),owner=entity===undefined?undefined:nativeEntities.get(entity);
   if(owner===undefined||(targetEntity!==null&&entity!==targetEntity)||owner.kind!=='SideWinder'||owner.life.add(0xC0).readU32()!==8)return;
   const input=physics.add(0x3B0).readPointer();
   if(!input.readPointer().equals(m.base.add(0x5311220)))return;
   const inputRoot=input.sub(0x60);
   if(controlPointer===null||controls.size!==1||!inputRoot.readPointer().equals(m.base.add(0x53110F0))||!inputRoot.add(0x2F8).readPointer().equals(controlPointer))return;
   const axis=input.add(axisOffset),previous=axis.readFloat();
   if(!Number.isFinite(previous)||Math.abs(previous)>1)throw new Error('Unexpected native forward axis');
   this.syntheticAxis=axis;this.previousAxis=previous;axis.writeFloat(phase===1?0.25:0);
   if(Date.now()>lastAxisSample+1000){lastAxisSample=Date.now();report({kind:'native-axis-hold',axis:axisName,entity:physicsOwnerIds.get(physics.toString())||null,elapsedMilliseconds:Date.now()-started,applied:axis.readFloat(),original:previous,consumerRva:0x2E47E40});}
  }catch(error){report({kind:'native-axis-rejected',error:String(error)});}
 },
 onLeave(){if(this.syntheticAxis!==undefined)this.syntheticAxis.writeFloat(this.previousAxis);physicsEvaluation.delete(Process.getCurrentThreadId());}
});
function control(owner,target){
 try{
  const result={kind:'control-interface',targetRva:target,ownerVtableRva:rva(owner.readPointer()),interfaces:[]};
  if(target===0x2C6EA40&&result.ownerVtableRva===0x5313AF0){
   const candidate=owner.add(0x700).readPointer();
   if(candidate.readPointer().equals(m.base.add(0x529D790))){controls.add(candidate.toString());controlPointer=candidate;
    report({kind:'native-player-control-binding',pointer:candidate.toString(),candidates:controls.size});}
  }
  if(target===0x2D560E0&&owner.add(0x48).readS32()===-1){
   const physics=owner.add(0x50).readPointer();
   if(physics.readPointer().equals(m.base.add(0x5337960))){
    const fields={};for(const offset of [0x640,0x648,0x64C,0x650,0x654,0x658])fields['0x'+offset.toString(16)]=physics.add(offset).readFloat();
    report({kind:'native-ship-physics-values',values:fields,interfaceVtableRva:0x5337960});
   }
  }
  for(const offset of [0x50,0x700]){
   try{
    const service=owner.add(offset).readPointer(),table=service.readPointer(),tableRva=rva(table);
    if(tableRva===null)continue;
    const methods=[];
    for(let slot=0;slot<=0x190;slot+=8){const method=rva(table.add(slot).readPointer());if(method!==null)methods.push({slot,rva:method});}
    result.interfaces.push({offset,vtableRva:tableRva,methods});
   }catch(_){}
  }
  report(result);
 }catch(error){report({kind:'unavailable',targetRva:target,error:String(error)});}
}
for(const target of [0x33C47E0,0x2E630E0,0x2E6BF40])Interceptor.attach(m.base.add(target),{
 onEnter(args){
  try{
   const now=Date.now();if(now<(flightObservationTimes.get(target)||0)+1000)return;flightObservationTimes.set(target,now);
   const object=args[0],tableRva=rva(object.readPointer());
   const detail={kind:'native-flight-component',targetRva:target,callerRva:rva(this.returnAddress),vtableRva:tableRva,threadId:Process.getCurrentThreadId()};
   if(target===0x33C47E0){
    detail.throttleDemand=object.add(0x610).readFloat();detail.throttleInput=object.add(0x614).readFloat();
    detail.throttleMode=object.add(0x62D).readU8();
    const physics=object.add(0x758).readPointer();detail.physicsVtableRva=rva(physics.readPointer());
   }
   if(target===0x2E6BF40){
    detail.enabled=object.add(0xAC5).readU8();detail.speed=object.add(0x6A8).readFloat();
    detail.normalizedSpeed=object.add(0x6AC).readFloat();
    if(tableRva===0x5337830){
     const world=object.add(0x350).readPointer();detail.worldTransformPointer=world.toString();detail.worldTransformVtableRva=rva(world.readPointer());
     const input=object.add(0x3B0).readPointer(),inputTable=input.readPointer();
     detail.inputVtableRva=rva(inputTable);detail.inputType=nativeType(inputTable);
     detail.inputAxes=[0x10,0x20,0x30,0x40,0x50,0x60].map(offset=>({offset,value:input.add(offset).readFloat()}));
     if(detail.inputVtableRva===0x5311220){
      const inputRoot=input.sub(0x60);detail.inputRootVtableRva=rva(inputRoot.readPointer());detail.inputServices=[];
     if(detail.inputRootVtableRva===0x53110F0&&controlPointer!==null&&inputRoot.add(0x2F8).readPointer().equals(controlPointer))inputRootObserved=inputRoot;
      for(let offset=0x2D0;offset<=0x600;offset+=8){
       try{const service=inputRoot.add(offset).readPointer(),table=service.readPointer(),tableRva=rva(table);
        if(tableRva!==null&&rva(table.readPointer())!==null)detail.inputServices.push({offset,vtableRva:tableRva});
       }catch(_){}
      }
     }
    }
   }
   report(detail);
  }catch(error){report({kind:'flight-component-unavailable',targetRva:target,error:String(error)});}
 }
});
if(DISPATCH_INVENTORY)Interceptor.attach(m.base.add(0x1E7100),{
 onEnter(args){
  try{
   const context=args[0],first=args[1].toUInt32(),last=args[2].toUInt32();if(last<first||last-first>128)return;
   const dispatcher=context.add(8).readPointer(),callback=rva(dispatcher.readPointer().readPointer());
   if(callback===null||dispatchedCallbacks.has(callback)||inputRootObserved===null)return;
   const list=context.readPointer().add(0x40).readPointer(),links=[];
   for(let index=first;index<last;index++){
   const payload=list.add(index*8).readPointer();
   if(payload.compare(inputRootObserved)>=0&&payload.compare(inputRootObserved.add(0x400))<0)
    links.push({index,playerInput:true,payloadOffset:payload.sub(inputRootObserved).toInt32()});
   for(const offset of [0,8,0x10,0x18,0x20,0x28,0x30,0xC8]){
    try{const candidate=payload.add(offset).readPointer(),tableRva=rva(candidate.readPointer());
     if(inputRootObserved!==null&&candidate.equals(inputRootObserved))links.push({index,offset,playerInput:true});
     else if(tableRva!==null)links.push({index,offset,vtableRva:tableRva});
    }catch(_){}
   }
   }
   if(!links.some(link=>link.playerInput===true))return;dispatchedCallbacks.add(callback);
   report({kind:'native-component-dispatch',callbackRva:callback,links,nativeEntryRva:0x1E7100});
  }catch(error){report({kind:'dispatch-inventory-unavailable',error:String(error)});}
 }
});
for(const target of [0x2C6EA40,0x2D560E0])Interceptor.attach(m.base.add(target),{
 onEnter(args){control(args[0],target);},onLeave(){
  if(target!==0x2C6EA40||done||action!=='flight-and-pause'||(phase!==1&&phase!==2))return;
  try{
   if(Process.getCurrentThreadId()!==flowThread)return;requireControl();
   const before=controlPointer.add(0x680).readFloat(),value=phase===1?0.25:0;
   throttle(controlPointer,value);
   if(Date.now()>lastHoldSample+1000){lastHoldSample=Date.now();report({kind:'native-throttle-hold',elapsedMilliseconds:Date.now()-started,before,applied:value,nativeRva:0x25E3790});}
  }catch(error){report({kind:'control-hold-unavailable',error:String(error)});}
 }
});
for(const target of [0x25E37C0,0x25FDE20])Interceptor.attach(m.base.add(target),{
 onEnter(args){
  try{
   if(controlPointer===null||!args[0].equals(controlPointer))return;
   report({kind:'native-throttle-consumer',targetRva:target,callerRva:rva(this.returnAddress),
    override:controlPointer.add(0x680).readFloat(),phase,threadId:Process.getCurrentThreadId()});
  }catch(error){report({kind:'control-consumer-unavailable',error:String(error)});}
 }
});
Interceptor.attach(m.base.add(0x95A990),{
 onEnter(args){
  try{
   const method=args[0].add(0x28).readU32();if(method>=155)return;
   const observation={kind:'native-u32-rpc-send',method,callerRva:rva(this.returnAddress),methodVtableRva:rva(args[0].readPointer()),nativeRva:0x95A990};
   if(method===9){
    const callback=args[0].add(0x40).readPointer();
    const owner=args[0].add(0x38).readPointer().add(args[0].add(0x48).readS32());
    observation.callbackRva=rva(callback);observation.ownerVtableRva=rva(owner.readPointer());
    if(callback.equals(m.base.add(0x9861B0))){
     observation.serviceBindingIndex=owner.add(0x290).readS32();
     const service=owner.add(0x298).readPointer();
     if(!service.isNull()){
      const table=service.readPointer();observation.serviceVtableRva=rva(table);
      observation.serviceMethodRva=rva(table.add(0x58).readPointer());
     }
    }
   }
   report(observation);
  }catch(error){report({kind:'rpc-send-observer-unavailable',error:String(error)});}
 }
});
Interceptor.attach(m.base.add(0x96F150),{onEnter(args){
 try{
  const method=args[0];if(method.add(0x28).readU32()!==14)return;
  const owner=method.add(0x38).readPointer().add(method.add(0x48).readS32());
  function stringShape(value){
   const text=value.readPointer();if(text.isNull())return {length:0};
   const length=text.readU64().toNumber();if(length>4096)return {length,invalid:true};
   const result={length};
   if(length<=160){const content=text.add(0x14).readUtf8String(length);if(/^IP[46]:[A-Fa-f0-9.:,]+$/.test(content))result.route=content;}
   return result;
  }
  let binding=null;
  if(method.add(0x40).readPointer().equals(m.base.add(0x980B30))){
   const service=owner.add(0x2E0).readPointer();
   if(!service.isNull()){const table=service.readPointer();binding={index:owner.add(0x2D8).readS32(),vtableRva:rva(table),methods:[]};
    for(let slot=0;slot<=0x120;slot+=8)binding.methods.push({slot,rva:rva(table.add(slot).readPointer())});}
  }
  report({kind:'native-peer-info-result',nativeRva:0x96F150,callbackRva:rva(method.add(0x40).readPointer()),binding,
   ownerVtableRva:rva(owner.readPointer()),requestId:args[4].toUInt32(),identity:args[5].toString(),
   firstString:stringShape(args[6]),secondString:stringShape(args[7]),scalar:args[8].toUInt32()});
 }catch(error){report({kind:'peer-info-observer-unavailable',error:String(error)});}
}});
Interceptor.attach(m.base.add(0x4C42980),{onEnter(args){
 try{
  const peer=args[0],manager=peer.add(0x18).readPointer(),discovery=manager.add(0x2F8).readPointer();
  report({kind:'native-peer-state',machine:peer.add(0x28).readU64().toString(),state:peer.add(0x60).readU32(),
   infoState:peer.add(0xB8).readU32(),managerVtableRva:rva(manager.readPointer()),providerRva:rva(manager.readPointer().add(0x28).readPointer()),
   discoveryPresent:!discovery.isNull(),discoveryState:discovery.isNull()?null:discovery.add(0x60).readU32()});
 }catch(error){report({kind:'peer-state-unavailable',error:String(error)});}
}});
Interceptor.attach(m.base.add(0x2154C40),{
 onEnter(args){this.flowContext=args[1];},
 onLeave(){
  if(Date.now()<lastFlow+1000)return;lastFlow=Date.now();
  try{
   const flow=this.flowContext.sub(0x70).add(0xA0).readPointer();
   if(!flow.readPointer().equals(m.base.add(0x5236DB0)))return;
   const csm=flow.add(0x310).readPointer();if(!csm.readPointer().equals(m.base.add(0x5138DC0)))return;
   const from=identifier(csm.add(0x100).readPointer().readPointer()),to=identifier(csm.add(0x108).readPointer().readPointer());
   if(flowThread===0)flowThread=Process.getCurrentThreadId();
   if(flowThread!==Process.getCurrentThreadId())throw new Error('Native flow thread changed');
   sampleEntities();
   report({kind:'native-flow',from,to,threadId:flowThread});
   const objects=[];
   for(const offset of [0x118,0x120,0x128,0x130,0x138,0x178,0x180]){
    try{
     const object=csm.add(offset).readPointer(),table=object.readPointer(),tableRva=rva(table);if(tableRva===null)continue;
     const methods=[];for(let slot=0;slot<=0xA0;slot+=8){const method=rva(table.add(slot).readPointer());if(method!==null)methods.push({slot,rva:method});}
     objects.push({offset,vtableRva:tableRva,methods});
    }catch(_){}
   }
   report({kind:'native-csm-activities',from,to,objects});
   try{
    const group=csm.add(0x120).readPointer(),buckets=group.add(0x90).readPointer(),bucketCount=group.add(0x98).readU64().toNumber(),members=[];
    if(bucketCount>0&&bucketCount<=1024){
     for(let index=0;index<bucketCount;index++){
      const sentinel=buckets.add(index*8);let node=sentinel.readPointer(),visited=0;
      while(!node.isNull()&&!node.equals(sentinel)&&visited++<128){
       const object=node.add(0x10).readPointer(),table=object.readPointer(),tableRva=rva(table);
       const methods=[];if(tableRva!==null)for(let slot=0;slot<=0x88;slot+=8){const method=rva(table.add(slot).readPointer());if(method!==null)methods.push({slot,rva:method});}
       const type=nativeType(table),detail={type,vtableRva:tableRva,methods};
       watchCompletion(object,table,type);
       const completionTarget=rva(table.add(0x40).readPointer());
       if([0x80B960,0x1D5DE10,0x82B9C0,0x351B800,0xE309E0].includes(completionTarget)){
        let query=completionQueries.get(completionTarget);
        if(query===undefined){query=new NativeFunction(m.base.add(completionTarget),'bool',['pointer'],{scheduling:'exclusive'});completionQueries.set(completionTarget,query);}
        detail.originalCompletion=query(object);
       }
       if(type==='PrepareForExit'){
        detail.state=object.add(0x68).readU32();detail.waitingSave=object.add(0x70).readU8();detail.services=[];
        for(const offset of [0x30,0x38,0x40,0x48,0x50,0x58]){try{const service=object.add(offset).readPointer(),serviceTable=service.readPointer();detail.services.push({offset,vtableRva:rva(serviceTable)});}catch(_){}}
       }
       if(type==='SaveGameActivity'){detail.operationFlags=object.add(0x20).readU32();detail.complete=(detail.operationFlags>>>1&7)===0;}
       if(type==='FullScreenColourOverlayActivity'){detail.duration=object.add(0x50).readFloat();detail.elapsed=object.add(0x54).readFloat();detail.enabled=object.add(0x58).readU8();}
       members.push(detail);node=node.readPointer();
      }
     }
     report({kind:'native-state-group',from,to,bucketCount,members});
     completedExitActivities=from==='SaveAndExit'&&members.length===8&&members.every(member=>member.originalCompletion===true||member.originalCompletion===1);
    }
   }catch(error){report({kind:'state-group-unavailable',error:String(error)});}
   try{
    const life=csm.add(0x120).readPointer(),object=life.add(0xC8).readPointer(),table=object.readPointer(),tableRva=rva(table);
    if(tableRva!==null){
     const methods=[];for(let slot=0;slot<=0xA0;slot+=8){const method=rva(table.add(slot).readPointer());if(method!==null)methods.push({slot,rva:method});}
     report({kind:'native-current-activity',from,to,lifecycle:life.add(0xC0).readU32(),vtableRva:tableRva,methods});
    }
   }catch(_){}
   const definition=csm.add(0xF8).readPointer(),records=definition.add(0x10).readPointer(),count=definition.add(0x18).readU32();
   if(count>4096)return;const names=[];
   for(let index=0;index<count;index++){const name=identifier(records.add(index*0x28).readPointer());if(name&&/pause|quit|save|exit|ingame|map/i.test(name))names.push(name);}
   report({kind:'native-flow-actions',names});
   const definitions=[];
   for(let index=0;index<count;index++){
    const row=records.add(index*0x28),name=identifier(row.readPointer());if(!['InGame','Pause','SaveAndExit','ExitGameplay','MainMenu','RequestQuitOrReset'].includes(name))continue;
    const fields=[];for(const offset of [8,0x10,0x18,0x20]){
     const value=row.add(offset).readPointer();let direct=null,indirect=null,tableRva=null;
     try{direct=identifier(value);indirect=identifier(value.readPointer());tableRva=rva(value.readPointer());}catch(_){}
     fields.push({offset,raw:value.toString(),direct,indirect,rva:rva(value),tableRva});
    }
    definitions.push({name,fields});
   }
   report({kind:'native-flow-definitions',definitions});
   for(let index=0;index<count;index++){
    const state=records.add(index*0x28);if(identifier(state.readPointer())!=='Pause')continue;
    const array=state.add(8).readPointer(),length=state.add(0x10).readU32();if(length>128)continue;
    for(let item=0;item<length;item++){
     const description=array.add(item*8).readPointer();if(!description.readPointer().readPointer().equals(m.base.add(0x56138F0)))continue;
     const categories=description.add(8).readPointer(),categoryCount=description.add(0x10).readU32();if(categoryCount>128)continue;const rows=[];
     for(let category=0;category<categoryCount;category++){
      const row=categories.add(category*0x58),values=[];
      for(const offset of [8,0x10,0x18,0x20,0x28,0x30,0x38]){
       try{const pointer=row.add(offset).readPointer(),name=identifier(pointer.readPointer());if(name!==null)values.push({offset,name});}catch(_){}
      }
      const buttons=[],buttonCount=row.add(0x48).readU32();
      if(buttonCount<=128){const buttonArray=row.add(0x40).readPointer();for(let buttonIndex=0;buttonIndex<buttonCount;buttonIndex++){
       const button=buttonArray.add(buttonIndex*0x78),labels=[];
       for(let field=0;field<0x78;field+=8){try{const pointer=button.add(field).readPointer(),name=configuredExpression(pointer.readPointer());if(name!==null)labels.push({offset:field,name});}catch(_){}}
       buttons.push({buttonIndex,labels});
       if(category===0&&row.readU8()===1&&buttonIndex===0&&labels.some(label=>label.offset===0&&label.name==='InGame'))configuredResume=true;
       if(category===4&&row.readU8()===2&&buttonIndex===1&&labels.some(label=>label.offset===0&&label.name==='Confirmed Quit To Main Menu'))configuredExitToMenu=true;
       if(category===4&&row.readU8()===2&&buttonIndex===2&&labels.some(label=>label.offset===0&&label.name==='Confirmed Quit Game'))configuredQuitGame=true;
      }}
      rows.push({category,type:row.readU8(),values,buttons});
     }
     report({kind:'native-pause-configured-actions',rows});
    }
   }
   if(from==='SaveAndExit'){
    const row=csm.add(0x100).readPointer(),lists=[];
    for(const offset of [8,0x18]){
     const array=row.add(offset).readPointer(),words=[];
     for(let index=0;index<(offset===8?row.add(0x10).readU32():row.add(0x20).readU32()*3);index++){
      const value=array.add(index*8).readPointer();let direct=null,indirect=null,tableRva=null;
      try{direct=identifier(value);indirect=identifier(value.readPointer());tableRva=rva(value.readPointer());}catch(_){}
      words.push({offset:index*8,raw:value.toString(),direct,indirect,tableRva,rva:rva(value)});
     if(offset===8){
      const description=[];for(let field=0;field<0x40;field+=8){
       const member=value.add(field).readPointer();let label=null,table=null;try{label=resourceName(member);table=rva(member.readPointer());}catch(_){}
       description.push({offset:field,label,tableRva:table,raw:member.toString()});
      }
      words[words.length-1].description=description;
     }
     }
     lists.push({offset,words});
    }
    report({kind:'native-exit-definition-lists',lists});
   }
   flight(flow,csm,from,to);
  }catch(error){report({kind:'flow-unavailable',error:String(error)});}
 }
});
Interceptor.attach(m.base.add(0x4817860),{
 onEnter(args){this.menuActivity=args[0];},onLeave(){
  if(done||action==='observe')return;
  try{
   const a=this.menuActivity;if(!a.readPointer().equals(m.base.add(0x5611E60))||!a.add(0x80).readPointer().equals(m.base.add(0x5611F20)))return;
   const flow=a.add(0x118).readPointer();if(!flow.readPointer().equals(m.base.add(0x5236DB0)))return;
   const csm=flow.add(0x310).readPointer(),from=identifier(csm.add(0x100).readPointer().readPointer()),to=identifier(csm.add(0x108).readPointer().readPointer());
   if(from!==to||Process.getCurrentThreadId()!==flowThread)return;
   const rejoining=action==='rejoin-open'&&((phase===3&&from==='MainMenu')||(phase===4&&from==='ContinueMenu'));
   if(from!=='Pause'&&!rejoining)return;
   const wanted=rejoining?(phase===3?'ContinueMenu':'SetupPlayWithEveryone'):'SaveAndExit';
   const config=a.add(0x10).readPointer(),category=a.add(0x208).readU32(),categoryCount=config.add(0x10).readU32();
   if(category>=categoryCount||categoryCount>128)return;
   const record=config.add(8).readPointer().add(category*0x58),type=record.readU8();if(type<1||type>3)return;
   const buttons=record.add(0x40).readPointer(),count=record.add(0x48).readU32(),cache=a.add(0x248).readPointer();
   if(count>128||a.add(0x240).readU64().toNumber()!==count)return;
   const rows=[];let selected=-1;
   for(let i=0;i<count;i++){
    const button=buttons.add(i*0x78),cached=cache.add(i*0xC8);
    const left=new Uint8Array(button.readByteArray(0x78)),right=new Uint8Array(cached.add(0x30).readByteArray(0x78));
    if(left.some((value,index)=>value!==right[index]))throw new Error('Native menu definition/cache differs');
    const name=identifier(button.readPointer().readPointer()),flags=cached.readU16();
    rows.push({name,flags});if(name===wanted&&flags===0x0101){if(selected!==-1)throw new Error('Duplicate eligible native menu action');selected=i;}
   }
   report({kind:'native-pause-menu',rows});
   if(rejoining&&selected>=0){
    phase++;report({kind:'native-menu-action',name:wanted,nativeRva:0x4836060,threadId:flowThread});
    menuAction(a.add(0x80),ptr(0),selected);return;
   }
   if(action==='flight-and-pause'&&phase===3){done=true;report({kind:'test-done',outcome:'native-flight-pulse-and-pause'});}
   if(action==='save-and-exit'&&selected>=0&&phase<2){
    if(cache.add(selected*0xC8).readU16()!==0x0101)throw new Error('Native save action eligibility changed');
    phase=2;report({kind:'native-menu-action',name:'SaveAndExit',nativeRva:0x4836060,threadId:flowThread});menuAction(a.add(0x80),ptr(0),selected);
   }
  }catch(error){report({kind:'menu-unavailable',error:String(error)});}
 }
});
Interceptor.attach(m.base.add(0x4817DE0),{
 onEnter(args){this.pauseActivity=args[0];},onLeave(){
  try{
   const a=this.pauseActivity;if(!a.readPointer().equals(m.base.add(0x56137A8))
    ||!a.add(0x80).readPointer().equals(m.base.add(0x5613868)))return;
   const flow=a.add(0xC8).readPointer();if(!flow.readPointer().equals(m.base.add(0x5236DB0)))return;
   const csm=flow.add(0x310).readPointer(),from=identifier(csm.add(0x100).readPointer().readPointer()),to=identifier(csm.add(0x108).readPointer().readPointer());
   const config=a.add(0x10).readPointer(),count=config.add(0x10).readU32(),selected=a.add(0x108).readU32();
   if(count>128||selected>=count)return;const rows=[];
   for(let index=0;index<count;index++){
    const row=config.add(8).readPointer().add(index*0x58),values=[];
    for(const offset of [8,0x10,0x18,0x20,0x28,0x30,0x38]){
     try{const pointer=row.add(offset).readPointer(),name=identifier(pointer.readPointer());if(name!==null)values.push({offset,name});}catch(_){}
    }
    rows.push({index,type:row.readU8(),values});
   }
   report({kind:'native-pause-activity',from,to,selected,rows});
  }catch(error){report({kind:'pause-activity-unavailable',error:String(error)});}
 }
});
send({kind:'observer-ready',action,nativeCallsAuthorized:action!=='observe'});
""".replace('EXPECTED', json.dumps(expected)).replace('RUNTIME', json.dumps(str(runtime))).replace('ACTION', json.dumps(args.action)).replace('AXIS_NAME', json.dumps(args.axis)).replace('TARGET_ENTITY', json.dumps(args.entity)).replace('DISPATCH_INVENTORY', json.dumps(args.dispatch_inventory)).replace('SEED_ENTITIES', json.dumps(list(seed_entities.values())))
lease_path = launch.parent / 'native-control.lock'
if lease_path.is_symlink() or (lease_path.exists() and lease_path.is_junction()):
    raise ValueError('Diagnostic lease cannot be a filesystem link')
lease = lease_path.open('a+b')
lease.seek(0)
try:
    msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
except OSError:
    lease.close()
    raise RuntimeError('Another native diagnostic is attached; finish it before starting this check') from None
session = script = None
file = None
message_gate = threading.Lock()
finished = threading.Event()
failed = threading.Event()
try:
    file = output.open('x', encoding='utf-8')
    def message(value, data):
        record = value['payload'] if value.get('type') == 'send' else {'observerError': value.get('description', 'error')}
        with message_gate:
            if file.closed:
                return
            file.write(json.dumps(record) + '\n')
            file.flush()
        if 'observerError' in record or record.get('kind') == 'native-axis-rejected':
            failed.set()
            finished.set()
        elif record.get('kind') == 'test-done':
            finished.set()
    session = frida.attach(receipt['ProcessId'])
    script = session.create_script(source)
    script.on('message', message)
    script.load()
    deadline = time.monotonic() + args.seconds
    while time.monotonic() < deadline and not finished.is_set():
        finished.wait(min(1, deadline - time.monotonic()))
finally:
    try:
        if script is not None:
            script.unload()
            script.off('message', message)
    except frida.InvalidOperationError:
        pass
    try:
        if session is not None:
            session.detach()
    except frida.InvalidOperationError:
        pass
    if file is not None:
        with message_gate:
            file.write(json.dumps({'kind': 'observer-closed', 'failed': failed.is_set(), 'action': args.action}) + '\n')
            file.close()
    lease.close()
if failed.is_set() or (args.action != 'observe' and not finished.is_set()):
    print('Owned native control check failed: ' + args.action)
    raise SystemExit(2)
print('Owned native control check complete: ' + args.action)
