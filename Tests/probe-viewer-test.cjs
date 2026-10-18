const fs=require('fs'),vm=require('vm');
const html=fs.readFileSync('Tools/ballistic-probe-viewer.html','utf8');
const source=html.split('<script>')[1].split('</script>')[0];
const nodes={};
function node(){return {value:'0',textContent:'',innerHTML:'',append(){},setAttribute(){}}}
const context={document:{getElementById(id){return nodes[id]??=(node())},createElementNS(){return node()}},setInterval(){},console};
vm.createContext(context);vm.runInContext(source,context);
nodes.phase.value='optic-pre-render';nodes.range.value='100';
vm.runInContext(`rows=[];refs=[{kind:'reference',frame:10,mrad:1,status:'test only',origin:[0,.1,0],axis:[0,0,1],nativeOrigin:[0,0,0],theory:[[0,0,0],[0,.2,10],[0,-1,100]],crossings:[[0,.1,5],[0,.1,60]],candidates:[]}];shots=[{phase:'optic-pre-render',frame:10,rawMuzzle:[0,0,0],muzzle:[0,0,0],direction:[0,0,1],path:[[0,0,0],[0,.1,5]]}];draw();`,context);
if(!nodes.details.textContent.includes('远交点：60.0 m')||!nodes.details.textContent.includes('相差 0 帧'))throw Error('replay lost roots/frame identity');
vm.runInContext('shots[0].frame=12;draw()',context);
if(!nodes.details.textContent.includes('相差 2 帧'))throw Error('mismatch not reported');
vm.runInContext('refs[0].crossings=[];draw()',context);
if(!nodes.details.textContent.includes('近交点：—；远交点：—'))throw Error('missing roots not explicit');
console.log('PASS viewer script and replay: two roots, distinct frames, missing roots (synthetic fixture only)');
