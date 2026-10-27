import fs from 'node:fs';
import vm from 'node:vm';
import http from 'node:http';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
const source=fs.readFileSync('Android/自动选线.js','utf8');
const context=vm.createContext({});vm.runInContext(source,context);
const run=value=>JSON.parse(JSON.stringify(context.main(value)));
const original={mode:'rule',dns:{nameserver:['https://example.test/dns-query']},rules:['MATCH,PROXY'],proxies:[{name:'A',type:'ss',password:'TEST_ONLY'},{name:'B',type:'trojan',password:'TEST_ONLY'}], 'proxy-groups':[{name:'PROXY',type:'select',proxies:['DIRECT','A','B']},{name:'Nested',type:'select',proxies:['PROXY']},{name:'Region',type:'select',proxies:['A'],filter:'Hong Kong','exclude-type':'Trojan'}]};
const before=JSON.stringify(original);const result=run(original);
assert.equal(JSON.stringify(original),before,'input untouched');
assert.deepEqual(result.rules,original.rules);assert.deepEqual(result.dns,original.dns);assert.deepEqual(result.proxies,original.proxies);
assert.equal(result['proxy-groups'][0].proxies[0],'DIRECT','original first choice preserved');
const auto=result['proxy-groups'].find(group=>group.name==='[Pilot] 自动 · PROXY');
assert.deepEqual(auto.proxies,['A','B']);assert.equal(auto.type,'url-test');assert.equal(auto['empty-fallback'],'REJECT');
assert.equal(auto.lazy,true);assert.equal(auto.interval,300);assert.equal(auto.tolerance,80);
assert.equal(result['proxy-groups'].find(group=>group.name==='[Pilot] 自动 · Region')['exclude-type'].endsWith('|Trojan'),true);
assert.equal(result['proxy-groups'].some(group=>group.name==='[Pilot] 自动 · Nested'),false,'nested groups not flattened');
assert.deepEqual(run(result),result,'idempotent reapply');
assert.throws(()=>run({...original,'proxy-groups':[...original['proxy-groups'],{name:'[Pilot] 自动 · PROXY',type:'select',proxies:['A']}]}),/同名/);
assert.throws(()=>run({'proxy-groups':[{name:'empty',type:'select',proxies:['DIRECT']}]}),/未找到/);
const provider={...original,'proxy-providers':{p:{type:'http',url:'https://example.test/SECRET_NOT_A_REAL_SUBSCRIPTION','health-check':{enable:false}}},'proxy-groups':[{name:'ViaProvider',type:'select',use:['p']}]};
const providerResult=run(provider);assert.equal(providerResult['proxy-providers'].p.url,provider['proxy-providers'].p.url);assert.equal(providerResult['proxy-providers'].p['health-check'].enable,true);
assert.deepEqual(providerResult['proxy-groups'][1].use,['p']);
const all=run({...provider,'proxy-groups':[{name:'all',type:'select','include-all':true}]});assert.equal(all['proxy-providers'].p['health-check'].enable,true);
const existing={...provider,'proxy-providers':{p:{type:'http',url:'https://example.test/LOCAL','health-check':{enable:true,url:'https://example.test/check',interval:900}}}};
assert.deepEqual(run(existing)['proxy-providers'].p['health-check'],existing['proxy-providers'].p['health-check']);
console.log('Android script: input integrity, no direct fallback, filters, idempotence, collisions, providers, nested groups passed.');
let selected='美国 B';const requests=[];
const server=http.createServer(async(req,res)=>{
 requests.push({method:req.method,url:req.url});
 if(req.headers.authorization!=='Bearer local-test-secret'){res.writeHead(401);res.end('{}');return;}
 const url=new URL(req.url,'http://127.0.0.1');
 const send=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
 if(url.pathname==='/proxies')return send({proxies:{'代理 / #组':{type:'Selector',now:selected,all:['DIRECT','香港 / A #1','美国 B']},DIRECT:{type:'Direct'},'香港 / A #1':{type:'Shadowsocks'},'美国 B':{type:'VMess'}}});
 if(url.pathname==='/configs')return send({mode:'rule',secret:'THIS_MUST_NOT_BE_LOGGED'});
 if(url.pathname==='/connections')return send({downloadTotal:1050000,uploadTotal:2048,connections:[{metadata:{host:'PRIVATE_TEST_ONLY'}}]});
 if(url.pathname.startsWith('/proxies/')&&url.pathname.endsWith('/delay')){
  assert.equal(url.searchParams.get('expected'),'204');assert.equal(url.searchParams.get('timeout'),'4000');assert.equal(url.searchParams.get('url'),'https://www.gstatic.com/generate_204');
  const name=decodeURIComponent(url.pathname.slice(9,-6));if(name==='timeout'){res.writeHead(504);res.end('{}');return;}assert.equal(name,'香港 / A #1');return send({delay:125});
 }
 if(req.method==='PUT'&&decodeURIComponent(url.pathname)==='/proxies/代理 / #组'){
  let data='';for await(const chunk of req)data+=chunk;
  assert.deepEqual(JSON.parse(data),{name:'香港 / A #1'});selected=JSON.parse(data).name;res.writeHead(204);res.end();return;
 }
 res.writeHead(404);res.end('{}');
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const controller=`http://127.0.0.1:${server.address().port}`;
const process=spawn('build/PilotTests.exe',[controller],{stdio:['ignore','pipe','pipe']});let text='';
process.stdout.on('data',data=>text+=data);process.stderr.on('data',data=>text+=data);
const code=await new Promise(resolve=>process.on('close',resolve));server.close();
assert.equal(code,0,text);assert.equal(requests.filter(req=>req.method==='PUT').length,1,'only explicit selection mutation');
assert(requests.every(req=>['GET','PUT'].includes(req.method)));
assert(!text.includes('THIS_MUST_NOT_BE_LOGGED'));assert(!text.includes('PRIVATE_TEST_ONLY'));
fs.writeFileSync('build/verification.txt',text+'\nAndroid script and mock HTTP integration: PASS\n');
console.log(text.trim().split('\n').slice(-3).join('\n'));
console.log('HTTP integration: only one intentional PUT; canceled switch absent; credential and connection details absent from output.');
