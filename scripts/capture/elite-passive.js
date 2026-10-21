// Passive observation of the explicitly selected, hash-verified game process.
// No replacement callbacks, memory writes, request rewriting, or native actions.
'use strict';
const image = Process.getModuleByName('EliteDangerous64.exe').base;
const requests = new Map(), contexts = new Map(), installedCallbacks = new Set();
let serial = 0;
const maximumBody = 16 * 1024 * 1024;
const allowedHeaders = new Set(['nonce', 'uncompressedsize', 'content-length', 'content-type', 'last-modified', 'encrypted']);
function text(pointer, maximum) {
    if (pointer.isNull()) return '';
    const bytes = new Uint8Array(pointer.readByteArray(maximum));
    const end = bytes.indexOf(0);
    if (end < 0) throw new Error('unterminated bounded text');
    return String.fromCharCode.apply(null, bytes.subarray(0, end));
}
function shortText(pointer) { return pointer.isNull() ? '' : pointer.readUtf8String(); }
function emit(value, bytes) { value.utc = new Date().toISOString(); send(value, bytes); }
function request(handle) {
    const id = handle.toString();
    if (!requests.has(id)) requests.set(id, { id: ++serial, handle: id, options: new Map(), path: '', query: '', host: '', method: '', headers: {}, responseHeaders: {}, chunks: [], uploadChunks: [], uploadLength: 0, length: 0, truncated: false, finished: false });
    return requests.get(id);
}
function url(value, state) {
    const match = /^(https?):\/\/(\[[^\]]+\]|[^\/:]+)(?::([0-9]+))?([^?]*)(?:\?(.*))?$/.exec(value);
    if (!match) return;
    state.scheme = match[1]; state.host = match[2]; state.port = Number(match[3] || (match[1] === 'https' ? 443 : 80));
    state.path = match[4] || '/';
    // Authentication request credentials are unnecessary for an offline identity.
    state.query = /\/(?:user\/(?:login|logout)|comms\/login)$/.test(state.path) ? '' : match[5] || '';
}
function append(state, bytes) {
    if (state.length + bytes.byteLength > maximumBody) { state.truncated = true; return; }
    state.chunks.push(new Uint8Array(bytes)); state.length += bytes.byteLength;
}
function callback(address, kind) {
    const key = kind + ':' + address;
    if (address.isNull() || installedCallbacks.has(key)) return;
    installedCallbacks.add(key);
    Interceptor.attach(address, {
        onEnter(args) {
            this.state = contexts.get(kind + ':' + args[3]);
            this.pointer = args[0]; this.length = args[1].toUInt32() * args[2].toUInt32();
            if (!this.state || this.length > maximumBody) return;
            try {
                if (kind === 'body') append(this.state, this.pointer.readByteArray(this.length));
                if (kind === 'header') {
                    const line = this.pointer.readUtf8String(this.length).trim();
                    const status = /^HTTP\/\S+\s+([0-9]{3})/.exec(line);
                    if (status) { this.state.status = Number(status[1]); this.state.responseHeaders = {}; }
                    const split = line.indexOf(':');
                    if (split > 0 && allowedHeaders.has(line.slice(0, split).toLowerCase())) this.state.responseHeaders[line.slice(0, split).toLowerCase()] = line.slice(split + 1).trim();
                }
            } catch (error) { this.state.truncated = true; }
        },
        onLeave(result) {
            if(kind!=='upload' || !this.state)return;
            const length=result.toUInt32();
            if(length===0 || length>this.length || this.state.uploadLength+length>maximumBody)return;
            try { this.state.uploadChunks.push(new Uint8Array(this.pointer.readByteArray(length))); this.state.uploadLength+=length; }
            catch (_) { this.state.truncated=true; }
        }
    });
}
function linkCallbacks(state) {
    for (const [kind, functionOption, contextOption] of [['body',20011,10001], ['header',20079,10029], ['upload',20012,10009]]) {
        const functionPointer = state.options.get(functionOption), context = state.options.get(contextOption);
        if (functionPointer && context) { contexts.set(kind + ':' + context, state); callback(functionPointer, kind); }
    }
}
function finish(state, result) {
    if (!state || state.finished || !state.path.startsWith('/2.0/')) return;
    state.finished = true;
    if(state.uploadLength>0){
        const upload=new Uint8Array(state.uploadLength);let cursor=0;
        for(const chunk of state.uploadChunks){upload.set(chunk,cursor);cursor+=chunk.length;}
        emit({type:'http-request-body',id:state.id,path:state.path,method:state.method||'GET',headers:state.headers,query:state.query,length:state.uploadLength},upload.buffer);
    }
    const output = new Uint8Array(state.length); let offset = 0;
    for (const chunk of state.chunks) { output.set(chunk, offset); offset += chunk.length; }
    emit({ type:'http-response', id:state.id, method:state.method || 'GET', scheme:state.scheme, host:state.host, port:state.port,
        path:state.path, query:state.query, requestHeaders:state.headers, headers:state.responseHeaders,
        status:state.status || 0, curlResult:result, length:state.length, truncated:state.truncated }, output.buffer);
    state.chunks = [];
}
Interceptor.attach(image.add(0x48D5770), {
    onEnter(args) { this.state = request(args[0]); this.option = args[1].toInt32(); this.value = args[2]; },
    onLeave(result) {
        if (result.toInt32() !== 0) return;
        const state = this.state; state.options.set(this.option, this.value);
        try {
            if (this.option === 10002) { state.finished = false; state.chunks = []; state.length = 0; state.uploadChunks=[];state.uploadLength=0;state.truncated = false; state.responseHeaders = {}; url(shortText(this.value), state); }
            if (this.option === 10036) state.method = shortText(this.value);
            if (this.option === 46 && !this.value.isNull()) state.method = 'PUT';
            if (this.option === 47 && !this.value.isNull()) state.method = 'POST';
            if (this.option === 10023) {
                state.headers = {};
                let item = this.value;
                for (let index=0; !item.isNull() && index<128; index++, item=item.add(8).readPointer()) {
                    const line = shortText(item.readPointer()), split = line.indexOf(':');
                    if (split>0 && allowedHeaders.has(line.slice(0,split).toLowerCase())) state.headers[line.slice(0,split).toLowerCase()] = line.slice(split+1).trim();
                }
            }
            if ([20011,10001,20079,10029,20012,10009].includes(this.option)) linkCallbacks(state);
        } catch (error) { emit({type:'capture-error', stage:'option', error:String(error)}); }
    }
});
Interceptor.attach(image.add(0x48D6A50), { onEnter(args) {
    const state=request(args[1]);
    try{
        const body=state.options.get(10015), size=state.options.get(60);
        if(body && !body.isNull() && size && size.toInt32()>0 && size.toInt32()<=maximumBody){state.uploadChunks=[new Uint8Array(body.readByteArray(size.toInt32()))];state.uploadLength=size.toInt32();}
    }catch(_){}
    emit({type:'http-start',id:state.id,method:state.method||'GET',host:state.host,path:state.path});
} });
Interceptor.attach(image.add(0x48D6E10), { onLeave(result) {
    try { if (!result.isNull() && result.readS32()===1) finish(requests.get(result.add(8).readPointer().toString()),result.add(16).readS32()); }
    catch (error) { emit({type:'capture-error',stage:'complete',error:String(error)}); }
} });
Interceptor.attach(image.add(0x48D56E0), { onEnter(args) {
    const handle=args[0].toString(), state=requests.get(handle);
    if (state) { finish(state,-1); for(const [key,value] of contexts)if(value===state)contexts.delete(key); requests.delete(handle); }
} });
// Main game flow observations never call game methods or select menus.
let nextFlow=0, previousFlow='';
function identifier(pointer) {
    const value=pointer.readUtf8String(64).split('\0')[0];
    return /^[A-Za-z_][A-Za-z0-9_]{0,62}$/.test(value)?value:'?';
}
Interceptor.attach(image.add(0x2154C40), {
    onEnter(args) { this.flowContext=args[1]; },
    onLeave() {
        if(Date.now()<nextFlow)return; nextFlow=Date.now()+500;
        try {
            const flow=this.flowContext.sub(0x70).add(0xA0).readPointer();
            if(!flow.readPointer().equals(image.add(0x5236DB0)))return;
            const csm=flow.add(0x310).readPointer();
            if(!csm.readPointer().equals(image.add(0x5138DC0)))return;
            const from=csm.add(0x100).readPointer(), to=csm.add(0x108).readPointer();
            const current=from.isNull()?'Null':identifier(from.readPointer()), target=to.isNull()?'Null':identifier(to.readPointer());
            const value=current+'>'+target;
            if(value!==previousFlow){previousFlow=value;emit({type:'game-flow',current,target});}
        } catch (_) { }
    }
});
for(const [name,rva] of [['load-game',0x1D82470],['read-native-save',0x2404620]])
    Interceptor.attach(image.add(rva),{onEnter(){emit({type:'native-milestone',name});}});
// The verified Powerplay JSON reader also receives cached plaintext on a cold
// start. Observing it distinguishes a valid cached catalogue from an HTTP 304.
Interceptor.attach(image.add(0x1418580), {onEnter(args){
    try {
        const value=args[1].readPointer();
        if(value.isNull())return;
        const length=value.readU64().toNumber();
        if(length<=0 || length>maximumBody)return;
        emit({type:'native-data-document',id:++serial,path:'/2.0/elite/powerplay2/powers/list',source:'native-plaintext-reader-cache-or-http',length},value.add(0x14).readByteArray(length));
    }catch(error){emit({type:'capture-error',stage:'powerplay-plaintext-reader',error:String(error)});}
}});
const winsock=Process.getModuleByName('ws2_32.dll');
const getLocal=new NativeFunction(winsock.getExportByName('getsockname'),'int',['pointer','pointer','pointer']);
const getRemote=new NativeFunction(winsock.getExportByName('getpeername'),'int',['pointer','pointer','pointer']);
const addressBuffer=Memory.alloc(128), addressLength=Memory.alloc(4), observedEndpoints=new Set();
function endpoint(socket,remote){
    addressLength.writeU32(128);
    if((remote?getRemote:getLocal)(socket,addressBuffer,addressLength)!==0)return null;
    const family=addressBuffer.readU16();
    if(family!==2 && family!==23)return null;
    const port=addressBuffer.add(2).readU8()*256+addressBuffer.add(3).readU8();
    const bytes=new Uint8Array(addressBuffer.add(family===2?4:8).readByteArray(family===2?4:16));
    const ip=family===2?Array.from(bytes).join('.'):Array.from({length:8},(_,i)=>(bytes[i*2]*256+bytes[i*2+1]).toString(16)).join(':');
    return{ip,port,family};
}
for(const name of ['send','recv','sendto','recvfrom','WSASend','WSARecv','WSASendTo','WSARecvFrom','connect','bind']) {
    const entry=winsock.findExportByName(name); if(!entry)continue;
    Interceptor.attach(entry,{onEnter(args){
        const previousError=this.lastError;
        try{
            const local=endpoint(args[0],false), remote=endpoint(args[0],true), key=args[0]+JSON.stringify(local)+JSON.stringify(remote);
            if(local && !observedEndpoints.has(key)){observedEndpoints.add(key);emit({type:'socket-endpoint',api:name,local,remote});}
        }catch(_){}finally{this.lastError=previousError;}
    }});
}
emit({type:'ready',pid:Process.id,mode:'passive',nativeGameHashVerified:true,networkRequestsChanged:false});
