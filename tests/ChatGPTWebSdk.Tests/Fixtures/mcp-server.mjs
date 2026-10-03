// A real JSON-RPC MCP fixture, deliberately independent of the production MCP client.
import readline from 'node:readline';
import http from 'node:http';
const mode = process.argv[2] ?? 'stdio';
const tools = [
  {name:'add',description:'Adds two integers',inputSchema:{type:'object',properties:{a:{type:'integer'},b:{type:'integer'}},required:['a','b'],additionalProperties:false},annotations:{readOnlyHint:true}},
  {name:'echo',description:'Echoes structured input',inputSchema:{type:'object',properties:{value:{}},required:['value']},annotations:{readOnlyHint:true}},
  {name:'get_pid',description:'Returns this owned fixture process ID',inputSchema:{type:'object'},annotations:{readOnlyHint:true}}
];
function respond(request) {
  if (request.id === undefined) return null;
  let result;
  switch(request.method) {
    case 'initialize': result={protocolVersion:request.params.protocolVersion,capabilities:{tools:{}},serverInfo:{name:'websdk-test-mcp',version:'1.0.0'}}; break;
    case 'ping': result={}; break;
    case 'tools/list': result=request.params?.cursor === 'second' ? {tools:tools.slice(1)} : {tools:tools.slice(0,1),nextCursor:'second'}; break;
    case 'tools/call': {
      const {name,arguments:args={}}=request.params;
      if(name === 'add') result={content:[{type:'text',text:String(args.a+args.b)}],structuredContent:{sum:args.a+args.b},isError:false};
      else if(name === 'echo') result={content:[{type:'text',text:JSON.stringify(args.value)}],structuredContent:{value:args.value},isError:false};
      else if(name === 'get_pid') result={content:[{type:'text',text:String(process.pid)}],structuredContent:{pid:process.pid},isError:false};
      else result={content:[{type:'text',text:'Unknown fixture tool'}],isError:true};
      break;
    }
    default: return {jsonrpc:'2.0',id:request.id,error:{code:-32601,message:'Method not found'}};
  }
  return {jsonrpc:'2.0',id:request.id,result};
}
if(mode === 'stdio') {
  const input=readline.createInterface({input:process.stdin,crlfDelay:Infinity});
  input.on('line',line=> { const result=respond(JSON.parse(line)); if(result) process.stdout.write(JSON.stringify(result)+'\n'); });
  input.on('close',()=>process.exit(0));
} else {
  let legacy;
  const server=http.createServer(async(req,res)=> {
    if(req.method === 'GET' && req.url === '/sse') {
      res.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-cache'});
      res.write('event: endpoint\ndata: /messages\n\n'); legacy=res; return;
    }
    if(req.method === 'DELETE') {res.writeHead(204); res.end(); return;}
    if(req.method !== 'POST' || !['/mcp','/messages'].includes(req.url)) {res.writeHead(405); res.end(); return;}
    let body=''; for await(const part of req) body+=part;
    const result=respond(JSON.parse(body));
    if(req.url === '/messages') {
      res.writeHead(202); res.end();
      if(result) legacy.write('event: message\ndata: '+JSON.stringify(result)+'\n\n');
    } else if(!result) {res.writeHead(204); res.end();}
    else if(mode === 'http-sse') {
      res.writeHead(200,{'Content-Type':'text/event-stream','Mcp-Session-Id':'synthetic-session'});
      res.end('event: message\ndata: '+JSON.stringify(result)+'\n\n');
    } else {
      res.writeHead(200,{'Content-Type':'application/json','Mcp-Session-Id':'synthetic-session'});
      res.end(JSON.stringify(result));
    }
  });
  server.listen(0,'127.0.0.1',()=>console.log(JSON.stringify({port:server.address().port,pid:process.pid})));
}
