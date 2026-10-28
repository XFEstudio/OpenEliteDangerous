"""Extract only public module identities and engineering definitions from the owned capture export."""
import hashlib
import json
from pathlib import Path
import re

root=Path(__file__).resolve().parents[1]
capture=root/'artifacts/online-solo-capture-20261020'
definitions,blueprints,specials,sources={},{},{},{}
def canonical(name):return name.replace('Hpt_Multicannon_','Hpt_MultiCannon_').replace('Int_PowerPlant_','Int_Powerplant_')
def family(name):
    name=canonical(name)
    if name.startswith('Int_Hyperdrive_'):return 'Int_Hyperdrive_'
    if '_Armour_' in name:return name.split('_Armour_')[0]+'_Armour_'
    if name.startswith('Hpt_'):return '_'.join(name.split('_')[:2])+'_'
    return name.split('_Size')[0]+'_'
def walk(o):
    if isinstance(o,dict):
        name=o.get('name')
        if isinstance(name,str) and re.fullmatch(r'(?:Hpt_|Int_|[A-Za-z0-9]+_Armour_)[A-Za-z0-9_]+',name) and isinstance(o.get('id'),int) and 0<o['id']<2**32-1:
            definitions.setdefault(canonical(name),set()).add(o['id'])
        if o.get('recipeName') and isinstance(o.get('modifiers'),dict) and isinstance(o['modifiers'].get('modifiers'),list) and isinstance(name,str):
            data=o['modifiers']
            state={'engineerId':data['engineerID'],'recipeId':data['recipeID'],'recipeName':o['recipeName'],'level':o['recipeLevel'],
                   'progress':data.get('progress',0),'craftCount':o.get('craftCount',1),
                   'modifiers':[{k:v for k,v in m.items() if k in ('name','value','type')} for m in data['modifiers'] if m.get('type')==1]}
            key=hashlib.sha256(json.dumps([family(name),state],sort_keys=True).encode()).hexdigest()[:24]
            blueprints[key]={'key':key,'modulePrefix':family(name),'state':state}
        for value in o.values():walk(value)
    elif isinstance(o,list):
        for value in o:walk(value)
for path in sorted(capture.glob('**/decoded/http-response-*.json')):
    try:document=json.loads(path.read_text(encoding='utf-8-sig'))
    except (ValueError,UnicodeError):continue
    before=(len(definitions),len(blueprints),len(specials));walk(document)
    if isinstance(document,dict) and isinstance(document.get('recipes'),list):
        for row in document['recipes']:
            if isinstance(row.get('special'),str):specials[row['special']]={'name':row['special'],'tags':row.get('tag',[])}
    if before!=(len(definitions),len(blueprints),len(specials)):sources[path.relative_to(capture).as_posix()]=hashlib.sha256(path.read_bytes()).hexdigest()
catalogue={'schema':1,'nativeImageSha256':'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988',
           'source':'Public definition fields extracted from the user-owned 2026-10-20 capture; no commander, token or inventory data.',
           'modules':[{'id':next(iter(ids)),'name':name} for name,ids in sorted(definitions.items()) if len(ids)==1],
           'blueprints':list(blueprints.values()),'specials':list(specials.values()),'sourceSha256':sources}
if not all(catalogue[k] for k in ('modules','blueprints','specials')):raise ValueError('Incomplete source evidence')
out=root/'Local/data/native-refit.json';out.write_text(json.dumps(catalogue,indent=2)+'\n',encoding='utf-8')
print({k:len(catalogue[k]) for k in ('modules','blueprints','specials')})
