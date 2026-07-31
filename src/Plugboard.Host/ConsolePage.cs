namespace Plugboard.Host;

// The built-in web UI. ONE self-contained page, served (via content negotiation)
// for /console, /, /catalog, /tools, /health and /info when a browser asks for
// text/html. The page routes on its own pathname and fetches the matching endpoint
// as JSON (Accept: application/json) to render it - so every built-in endpoint has
// a human interface, while machines still get raw JSON from the same URL.
public static class ConsolePage
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>Plugboard</title>
<style>
  :root{--bg:#0f172a;--card:#1e293b;--line:#334155;--txt:#e2e8f0;--dim:#94a3b8;
        --green:#22c55e;--blue:#38bdf8;--amber:#f59e0b;--red:#ef4444;}
  *{box-sizing:border-box;margin:0;padding:0;}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--txt);
       font-size:13px;line-height:1.5;padding:22px;max-width:1280px;margin:0 auto;}
  header{display:flex;align-items:center;gap:12px;margin-bottom:14px;}
  h1{font-size:20px;font-weight:700;letter-spacing:.5px;}
  .pill{display:inline-flex;align-items:center;gap:7px;padding:3px 11px;border-radius:20px;
        background:var(--card);border:1px solid var(--line);font-size:12px;}
  .dot{width:9px;height:9px;border-radius:50%;background:var(--dim);}
  .dot.on{background:var(--green);box-shadow:0 0 7px var(--green);}
  .dot.off{background:var(--red);}
  nav{display:flex;gap:6px;margin-bottom:18px;flex-wrap:wrap;}
  nav a{padding:6px 13px;border-radius:7px;background:var(--card);border:1px solid var(--line);
        color:var(--dim);text-decoration:none;font-size:12px;font-weight:600;}
  nav a.active{color:var(--blue);border-color:var(--blue);}
  nav a:hover{color:var(--txt);}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:14px;}
  .u{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px;}
  .u h2{font-size:15px;font-weight:700;display:flex;align-items:center;gap:8px;}
  .tag{font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;padding:2px 8px;border-radius:10px;}
  .tag.svc{background:rgba(56,189,248,.15);color:var(--blue);}
  .tag.conn{background:rgba(34,197,94,.15);color:var(--green);}
  .tag.get{background:rgba(34,197,94,.15);color:var(--green);}
  .tag.post{background:rgba(245,158,11,.15);color:var(--amber);}
  .desc{color:var(--dim);font-size:12px;margin:6px 0;}
  .req{display:inline-block;margin:2px 4px 0 0;padding:2px 8px;border-radius:8px;font-size:11px;background:rgba(245,158,11,.15);color:var(--amber);}
  .routes{margin-top:10px;border-top:1px solid var(--line);padding-top:8px;}
  .r{font-family:'Courier New',monospace;font-size:11px;color:var(--dim);padding:2px 0;}
  .r b{color:var(--txt);}
  .m{display:inline-block;width:42px;color:var(--blue);}
  .count{color:var(--dim);font-size:12px;font-weight:400;margin-left:auto;}
  table.kv{width:100%;border-collapse:collapse;}
  table.kv td{padding:5px 8px;border-bottom:1px solid var(--line);font-size:12px;vertical-align:top;}
  table.kv td.k{color:var(--dim);width:190px;font-weight:600;}
  table.kv td.v{font-family:'Courier New',monospace;color:var(--txt);word-break:break-word;}
  .p{font-family:'Courier New',monospace;font-size:11px;color:var(--dim);padding:2px 0 2px 10px;}
  .p b{color:var(--txt);} .p .ty{color:var(--blue);} .p .rq{color:var(--amber);}
  a.tool-path{color:var(--blue);text-decoration:none;font-family:'Courier New',monospace;}
  .ex{margin-top:10px;position:relative;}
  .ex .lbl{font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:var(--dim);margin-bottom:4px;}
  .ex code{display:block;background:#0b1220;border:1px solid var(--line);border-radius:6px;padding:9px 10px;
           font-family:'Courier New',monospace;font-size:11px;color:#cbd5e1;white-space:pre-wrap;word-break:break-all;}
  .ex button.copy{position:absolute;top:0;right:0;background:var(--card);border:1px solid var(--line);color:var(--dim);
                  border-radius:6px;padding:2px 9px;font-size:10px;cursor:pointer;}
  .ex button.copy:hover{color:var(--blue);border-color:var(--blue);}
</style>
</head>
<body>
  <header>
    <h1>Plugboard</h1>
    <span class="pill"><span class="dot" id="dot"></span><span id="hstatus">checking</span></span>
  </header>
  <nav id="nav"></nav>
  <div id="body"></div>

<script>
  const esc = s => (s==null?'':String(s)).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
  const dur = s => { s=Math.floor(s||0); const h=Math.floor(s/3600),m=Math.floor(s%3600/60); return h?`${h}h ${m}m`:`${m}m ${s%60}s`; };
  const getJson = async p => (await fetch(p,{headers:{Accept:'application/json'}})).json();
  const B = () => document.getElementById('body');
  function cp(b){ navigator.clipboard.writeText(b.parentElement.querySelector('code').textContent); b.textContent='copied'; setTimeout(()=>b.textContent='copy',1200); }
  let AUTH = false;
  // Capability routes (/con, /svc) require Sec-Fetch-Site: same-origin, i.e. the call must
  // come from a page SERVED by the gateway (opened via "Serve via Gateway"). A raw curl or a
  // cross-site page gets 401. There is no key/token.
  const gated = t => AUTH && /^\/(con|svc)\b/.test(t.path);
  const authNote = t => gated(t) ? `\n# note: call from a gateway-served page (same-origin); a raw curl returns 401` : '';
  function example(t){
    const url = location.origin + t.path;
    if(t.method!=='POST') return `curl ${url}`+authNote(t);
    const body = t.sample ? JSON.stringify(t.sample) : '{}';
    return `curl -X POST ${url} \\\n  -H "Content-Type: application/json" \\\n  -d '${body}'`+authNote(t);
  }

  const TABS = [['/console','Home'],['/catalog','Catalog'],['/tools','Tools'],['/health','Health'],['/info','Info']];
  function view(){ let p=location.pathname.replace(/\/+$/,'')||'/'; if(p==='/'||p==='/console') return 'home'; return p.slice(1); }
  function renderNav(){
    const v=view();
    document.getElementById('nav').innerHTML = TABS.map(([href,label])=>{
      const active = (href==='/console'&&v==='home') || href==='/'+v;
      return `<a class="${active?'active':''}" href="${href}">${label}</a>`;
    }).join('');
  }
  async function setStatus(){
    try{ const h=(await getJson('/health')).data;
      document.getElementById('dot').className='dot on';
      document.getElementById('hstatus').textContent = `${h.status} · up ${dur(h.uptimeSeconds)}`;
    }catch(e){ document.getElementById('dot').className='dot off'; document.getElementById('hstatus').textContent='unreachable'; }
  }
  const kv = obj => `<div class="u"><table class="kv">`+Object.entries(obj).map(([k,v])=>
      `<tr><td class="k">${esc(k)}</td><td class="v">${esc(typeof v==='object'?JSON.stringify(v):v)}</td></tr>`).join('')+`</table></div>`;

  async function renderHome(){
    const [h,i]=await Promise.all([getJson('/health'),getJson('/info')]);
    B().innerHTML = `<div class="grid">
      <div class="u"><h2>Health</h2>${kv(h.data)}</div>
      <div class="u"><h2>Info</h2>${kv(i.data)}</div></div>`;
  }
  async function renderHealth(){ B().innerHTML = kv((await getJson('/health')).data); }
  async function renderInfo(){ B().innerHTML = kv((await getJson('/info')).data); }

  async function renderCatalog(){
    const c=(await getJson('/catalog')).data, byP={};
    for(const r of c.routes){ (byP[r.plugin]||=[]).push(r); }
    B().innerHTML = `<div class="grid">`+c.plugins.map(p=>{
      const routes=byP[p.name]||[], isSvc=routes.some(r=>r.route.startsWith('/svc/'));
      const reqs=(p.requires||[]).map(x=>`<span class="req">needs: ${esc(x)}</span>`).join('');
      const rs=routes.map(r=>{ const ps=(r.parameters||[]).map(x=>x.name+(x.required?'*':'')).join(', ');
        return `<div class="r"><span class="m">${r.method}</span><b>${esc(r.route)}</b>${ps?(' ('+esc(ps)+')'):''}${r.summary?(' - '+esc(r.summary)):''}</div>`;}).join('');
      return `<div class="u"><h2>${esc(p.displayName||p.name)} <span class="tag ${isSvc?'svc':'conn'}">${isSvc?'service':'connector'}</span>`
        +`<span class="count">${p.routeCount} route${p.routeCount==1?'':'s'}</span></h2>`
        +(p.description?`<div class="desc">${esc(p.description)}</div>`:'')+reqs+`<div class="routes">${rs}</div></div>`;
    }).join('')+`</div>`;
  }

  async function renderTools(){
    const t=(await getJson('/tools')).data;
    B().innerHTML = `<div class="grid">`+t.tools.map(tool=>{
      const props=(tool.inputSchema&&tool.inputSchema.properties)||{}, req=(tool.inputSchema&&tool.inputSchema.required)||[];
      const params=Object.keys(props).length? Object.entries(props).map(([n,s])=>{
        const isreq=req.includes(n);
        return `<div class="p"><b>${esc(n)}</b> <span class="ty">${esc(s.type||'')}${s.items?('&lt;'+esc(s.items.type)+'&gt;'):''}</span>`
          +(isreq?' <span class="rq">required</span>':'')+(s.enum?(' ['+s.enum.map(esc).join('|')+']'):'')+(s.description?(' - '+esc(s.description)):'')+`</div>`;
      }).join('') : `<div class="p" style="color:var(--dim)">no declared inputs</div>`;
      return `<div class="u"><h2><span class="tag ${tool.method==='GET'?'get':'post'}">${esc(tool.method)}</span> `
        +`<a class="tool-path" href="${esc(tool.path)}">${esc(tool.path)}</a></h2>`
        +(tool.description?`<div class="desc">${esc(tool.description)}</div>`:'')
        +`<div class="routes">${params}</div>`
        +`<div class="ex"><div class="lbl">Example call</div><button class="copy" onclick="cp(this)">copy</button><code>${esc(example(tool))}</code></div>`
        +`</div>`;
    }).join('')+`</div>`;
  }

  async function main(){
    renderNav(); setStatus();
    try{ AUTH = !!(await getJson('/info')).data.authRequired; }catch(e){}
    try{
      const v=view();
      if(v==='home') await renderHome();
      else if(v==='catalog') await renderCatalog();
      else if(v==='tools') await renderTools();
      else if(v==='health') await renderHealth();
      else if(v==='info') await renderInfo();
      else B().innerHTML = `<div class="u">Unknown view.</div>`;
    }catch(e){ B().innerHTML = `<div class="u" style="color:var(--red)">Error: ${esc(e.message)}</div>`; }
  }
  main();
</script>
</body>
</html>
""";
}
