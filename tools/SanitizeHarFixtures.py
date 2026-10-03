"""Create structurally faithful response fixtures; credentials and private text never enter the output."""
import json, sys, urllib.parse, re, hashlib, uuid, copy

safe = {'text','multimodal_text','image_asset_pointer','user_editable_context','model_editable_context','reasoning_recap','thoughts','code','execution_output','audio','video','user','assistant','system','tool','developer','all','final','analysis','commentary','in_progress','finished_successfully','finished_partial','add','append','replace','remove','patch','v1','image/png','image/jpeg'}
def identifier(s):
    return str(uuid.UUID(hashlib.sha256(s.encode()).hexdigest()[:32]))
def redact(v,key=''):
    if isinstance(v,dict): return {(identifier(k) if re.fullmatch(r'[a-f0-9-]{36}',k) else k):redact(x,k) for k,x in v.items()}
    if isinstance(v,list): return [redact(x,key) for x in v]
    if isinstance(v,str):
        if v in safe or (key=='p' and (v.startswith('/') or v=='')):return v
        if re.fullmatch(r'[a-f0-9-]{36}',v):return identifier(v)
        if v.startswith('sediment://'):return 'sediment://file_'+identifier(v[11:]).replace('-','')
        if v.startswith('file_'):return 'file_'+identifier(v).replace('-','')
        if key in ('token','url','download_url','upload_url','title','prompt','user_profile','user_instructions'):return '[redacted]'
        return 'x'*len(v)
    return v
def apply(root,op,path,value):
    if path=='':return copy.deepcopy(value)
    keys=path.split('/')[1:];parent=root
    for k in keys[:-1]:parent=parent[int(k)] if isinstance(parent,list) else parent[k.replace('~1','/').replace('~0','~')]
    k=keys[-1];k=int(k) if isinstance(parent,list) and k!='-' else k
    if op=='append':
        if isinstance(parent[k],list):parent[k].extend(copy.deepcopy(value))
        elif isinstance(parent[k],dict):parent[k].update(copy.deepcopy(value))
        else:parent[k]+=value
    elif op=='remove':del parent[k]
    elif k=='-' and isinstance(parent,list):parent.append(copy.deepcopy(value))
    else:parent[k]=copy.deepcopy(value)
    return root

har=json.load(open(sys.argv[1],encoding='utf-8-sig')); fixtures=[]
for e in har['log']['entries']:
    if urllib.parse.urlparse(e['request']['url']).path!='/backend-api/f/conversation':continue
    states={};slot=0;operation=None;path=None;selected=None;final_text='';assets=set();events=[]
    for block in re.split(r'\r?\n\r?\n',e['response']['content'].get('text','')):
        kind=next((x[6:].strip() for x in block.splitlines() if x.startswith('event:')),None)
        data='\n'.join(x[5:].lstrip() for x in block.splitlines() if x.startswith('data:'))
        if not data:continue
        if data=='[DONE]':events.append({'event':kind,'data':data});continue
        try:v=json.loads(data)
        except:continue
        if not isinstance(v,dict):events.append({'event':kind,'data':data});continue
        # Metadata tokens are removed rather than anonymized into reusable credentials.
        if 'token' in v:v['token']='[redacted]'
        events.append({'event':kind,'data':json.dumps(redact(v),separators=(',',':'))})
        slot=v.get('c',slot)
        if isinstance(v.get('v'),dict) and 'message' in v['v']:
            states[slot]=copy.deepcopy(v['v']);operation='add';path=''
        elif 'v' in v and ('o' in v or operation):
            operation=v.get('o',operation);path=v.get('p',path)
            if operation=='patch':
                child_op=child_path=None
                for p in v['v']:
                    child_op=p.get('o',child_op);child_path=p.get('p',child_path)
                    states[slot]=apply(states[slot],child_op,child_path,p.get('v'))
            elif operation in ('append','replace','add','remove'):states[slot]=apply(states[slot],operation,path,v.get('v'))
        message=states.get(slot,{}).get('message',{});meta=message.get('metadata',{})
        if message.get('author',{}).get('role') in ('assistant','tool') and not meta.get('is_paragen_stream'):
            for p in message.get('content',{}).get('parts',[]):
                if isinstance(p,dict) and p.get('content_type')=='image_asset_pointer':assets.add(p['asset_pointer'])
        if message.get('author',{}).get('role')=='assistant' and message.get('channel') in (None,'final') and not meta.get('is_paragen_stream') and not meta.get('is_visually_hidden_from_conversation') and message.get('content',{}).get('content_type') in ('text','multimodal_text'):
            if message.get('channel') is None and not any(isinstance(p,str) and p for p in message['content']['parts']) and not message.get('end_turn'):continue
            selected=selected or message['id']
            if selected==message['id']:final_text=''.join(p for p in message['content']['parts'] if isinstance(p,str))
    fixtures.append({'name':'captured-turn-'+str(len(fixtures)+1),'events':events,'expectedMessageId':identifier(selected) if selected else None,'expectedTextLength':len(final_text),'expectedAssetCount':len(assets)})
json.dump(fixtures,open(sys.argv[2],'w',encoding='utf-8'),indent=2)
print('Wrote',len(fixtures),'redacted turn fixtures.')
