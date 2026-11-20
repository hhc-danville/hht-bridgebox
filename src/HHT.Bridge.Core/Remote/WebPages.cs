namespace HHT.Bridge.Core.Remote;

/// <summary>Self-contained web pages served by <see cref="BridgeWebServer"/> (no external resources).</summary>
internal static class WebPages
{
    private const string Style = """
        <style>
        :root{--bg:#17181b;--panel:#222429;--line:#32353c;--blue:#2f7df6;--green:#34c772;--amber:#e2a33a;--text:#f3f4f6;--muted:#9aa0aa}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px 'Segoe UI',system-ui,sans-serif}
        header{display:flex;align-items:center;gap:10px;padding:8px 12px;background:var(--panel);border-bottom:1px solid var(--line);flex-wrap:wrap}
        .logo{background:var(--blue);color:#fff;font-weight:700;border-radius:6px;padding:5px 7px;font-size:12px}
        .title{font-weight:600}.muted{color:var(--muted)}.pill{border:1px solid var(--line);border-radius:12px;padding:2px 10px;font-size:12px;background:#2b2e35}
        .dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#555a63;margin-right:6px}.on{background:var(--green)}.warn{background:var(--amber)}
        button{background:#2b2e35;color:var(--text);border:1px solid var(--line);border-radius:6px;padding:6px 10px;font:inherit;cursor:pointer}
        button.primary{background:var(--blue);border-color:var(--blue)}button:disabled{opacity:.5}
        input{background:#111216;color:var(--text);border:1px solid var(--line);border-radius:6px;padding:7px 9px;font:inherit}
        .login{max-width:360px;margin:12vh auto;background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:24px}
        .login input{width:100%;margin:10px 0}
        .err{color:#ff7b72}
        </style>
        """;

    private const string LoginJs = """
        function getKey(){var h=new URLSearchParams(location.hash.slice(1)).get('k');if(h){sessionStorage.setItem('hhtk',h);history.replaceState(null,'',location.pathname);}return sessionStorage.getItem('hhtk')||'';}
        function q(k){return 'k='+encodeURIComponent(k);}
        async function check(k){try{var r=await fetch('/api/status?'+q(k),{cache:'no-store'});return r.ok?await r.json():(r.status==429?'locked':null);}catch(e){return null;}}
        function showLogin(msg,onKey){document.body.insertAdjacentHTML('beforeend','<div class="login" id="login"><div class="title">HHT Bridge &mdash; access key</div><div class="muted" style="margin-top:6px">Shown in the Bridge Console on the bench (Share tab).</div><input id="key" placeholder="XXXX-XXXX-XXXX-XXXX" autocomplete="off" autocapitalize="characters"><div class="err" id="lerr">'+(msg||'')+'</div><button class="primary" id="go">Connect</button></div>');
          var go=async function(){var k=document.getElementById('key').value.trim();var s=await check(k);if(s&&s!=='locked'){sessionStorage.setItem('hhtk',k);document.getElementById('login').remove();onKey(k,s);}else document.getElementById('lerr').textContent=s==='locked'?'Too many wrong keys. Wait 5 minutes.':'Wrong key.';};
          document.getElementById('go').onclick=go;document.getElementById('key').onkeydown=function(e){if(e.key==='Enter')go();};}
        async function start(onKey){var k=getKey();var s=k?await check(k):null;if(s&&s!=='locked')onKey(k,s);else{sessionStorage.removeItem('hhtk');showLogin(k?'Saved key no longer works.':'',onKey);}}
        """;

    public const string Viewer = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>HHT Bridge — Remote view</title>" + Style + """
        <style>
        #app{display:none;height:100vh;flex-direction:column}#stage{flex:1;position:relative;background:#000;overflow:hidden;touch-action:none}
        #video{position:absolute;inset:0;width:100%;height:100%;object-fit:contain;user-select:none;-webkit-user-drag:none}
        #bar{display:flex;gap:6px;flex-wrap:wrap;padding:6px 10px;background:var(--panel);border-top:1px solid var(--line);align-items:center}
        #banner{position:absolute;top:8px;left:50%;transform:translateX(-50%);background:#2f7df6cc;padding:4px 12px;border-radius:6px;font-weight:600;display:none}
        #txt{flex:1;min-width:140px}
        </style></head><body>
        <div id="app">
          <header><span class="logo">HHT</span><span class="title" id="bench">Bridge</span><span class="muted" id="ticket"></span>
            <span class="pill"><span class="dot" id="dv"></span><span id="sv">Video</span></span>
            <span class="pill"><span class="dot" id="dc"></span><span id="sc">Control</span></span>
            <span style="flex:1"></span>
            <button id="ctl" class="primary">Take control</button><button id="fs">Full screen</button><a href="/dashboard" style="color:var(--muted)">Dashboard</a></header>
          <div id="stage"><img id="video" alt=""><div id="banner">Controlling target — press Esc twice quickly or click Release to stop</div></div>
          <div id="bar">
            <button data-s="ctrl-alt-del">Ctrl+Alt+Del</button><button data-s="esc">Esc</button><button data-s="del">Del</button><button data-s="f2">F2</button>
            <button data-s="f10">F10</button><button data-s="f11">F11</button><button data-s="f12">F12</button><button data-s="win">Win</button><button data-s="alt-tab">Alt+Tab</button>
            <input id="txt" placeholder="Type text into target (US layout)"><button id="send">Type</button><button id="rel">Release keys</button>
          </div>
        </div>
        <script>
        """ + LoginJs + """
        var KEY='',ws=null,control=false,buttons=0,lastEsc=0,pend=null;
        var img=document.getElementById('video'),stage=document.getElementById('stage'),banner=document.getElementById('banner'),ctl=document.getElementById('ctl');
        function setDot(id,state){document.getElementById(id).className='dot'+(state==='on'?' on':state==='warn'?' warn':'');}
        async function poll(){var s=await check(KEY);if(!s||s==='locked')return;document.getElementById('bench').textContent=s.benchName;document.title='HHT Bridge — '+s.benchName;
          document.getElementById('ticket').textContent=s.ticket?('Ticket '+s.ticket):'';
          setDot('dv',s.video?'on':'');document.getElementById('sv').textContent=s.video?(s.width+'x'+s.height):'No video';
          setDot('dc',s.controlConnected?(s.targetUsb?'on':'warn'):'');document.getElementById('sc').textContent=!s.controlConnected?'No control cable':(s.targetUsb?'Target ready':'Target not detected');
          ctl.disabled=!s.controlAllowed&&!control;ctl.title=s.controlAllowed?'':'The technician has not allowed remote control';}
        function send(o){if(ws&&ws.readyState===1&&control)ws.send(JSON.stringify(o));}
        function setControl(on){control=on;banner.style.display=on?'block':'none';ctl.textContent=on?'Release control':'Take control';stage.style.cursor=on?'none':'default';if(on)stage.focus();}
        function connectWs(){if(ws){ws.close();ws=null;}ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/api/ws?'+q(KEY));
          ws.onmessage=function(e){var m=JSON.parse(e.data);if(m.t==='hello'){setControl(m.control);if(!m.control)alert(m.reason==='busy'?'Someone else is controlling this bench.':'Remote control is not allowed on this bench right now.');}if(m.t==='revoked'){setControl(false);alert('The technician took back control.');}};
          ws.onclose=function(){setControl(false);ws=null;};}
        ctl.onclick=function(){if(control){send({t:'r'});ws&&ws.close();setControl(false);}else connectWs();};
        document.getElementById('fs').onclick=function(){(document.fullscreenElement?document.exitFullscreen():stage.requestFullscreen()).catch(function(){});};
        document.getElementById('rel').onclick=function(){send({t:'r'});};
        document.getElementById('send').onclick=function(){var t=document.getElementById('txt');if(t.value){send({t:'txt',s:t.value});t.value='';}};
        document.querySelectorAll('[data-s]').forEach(function(b){b.onclick=function(){send({t:'s',id:b.getAttribute('data-s')});};});
        function norm(e){var r=img.getBoundingClientRect(),iw=img.naturalWidth||16,ih=img.naturalHeight||9,s=Math.min(r.width/iw,r.height/ih),w=iw*s,h=ih*s,ox=r.left+(r.width-w)/2,oy=r.top+(r.height-h)/2;
          var x=(e.clientX-ox)/w,y=(e.clientY-oy)/h;return(x<0||x>1||y<0||y>1)?null:{x:x,y:y};}
        function mouse(e,wheel){if(!control)return;var p=norm(e);if(!p)return;var o={t:'m',x:p.x,y:p.y,b:buttons,w:wheel||0};
          if(!wheel&&e.type==='pointermove'){pend=o;return;}send(o);}
        (function flush(){if(pend){send(pend);pend=null;}requestAnimationFrame(flush);})();
        var bmap={0:1,1:4,2:2};
        stage.addEventListener('pointerdown',function(e){if(!control)return;e.preventDefault();stage.setPointerCapture(e.pointerId);buttons|=bmap[e.button]||1;mouse(e);});
        stage.addEventListener('pointerup',function(e){if(!control)return;e.preventDefault();buttons&=~(bmap[e.button]||1);mouse(e);});
        stage.addEventListener('pointermove',function(e){mouse(e);});
        stage.addEventListener('wheel',function(e){if(!control)return;e.preventDefault();mouse(e,e.deltaY>0?-1:1);},{passive:false});
        stage.addEventListener('contextmenu',function(e){if(control)e.preventDefault();});
        function key(e,down){if(!control||e.target.id==='txt')return;e.preventDefault();
          if(down&&e.code==='Escape'){var now=Date.now();if(now-lastEsc<400){send({t:'r'});setControl(false);ws&&ws.close();return;}lastEsc=now;}
          if(!e.repeat)send({t:'k',c:e.code,d:down});}
        document.addEventListener('keydown',function(e){key(e,true);});document.addEventListener('keyup',function(e){key(e,false);});
        window.addEventListener('blur',function(){send({t:'r'});buttons=0;});
        start(function(k){KEY=k;document.getElementById('app').style.display='flex';img.src='/api/stream?'+q(KEY);poll();setInterval(poll,2000);});
        </script></body></html>
        """;

    public const string Dashboard = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>HHT Bridge — Bench dashboard</title>" + Style + """
        <style>
        #grid{display:none;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:14px;padding:14px}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden;cursor:pointer}
        .card img{display:block;width:100%;aspect-ratio:16/9;object-fit:contain;background:#000}
        .meta{padding:8px 10px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}.name{font-weight:600;flex:1}
        </style></head><body>
        <header><span class="logo">HHT</span><span class="title">Bench dashboard</span><span class="muted" id="count"></span><span style="flex:1"></span><a href="/" style="color:var(--muted)">This bench</a></header>
        <div id="grid"></div>
        <script>
        """ + LoginJs + """
        var KEY='';
        function esc(s){return String(s==null?'':s).replace(/[&<>"']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}
        async function refresh(){var r;try{r=await fetch('/api/benches?'+q(KEY),{cache:'no-store'});}catch(e){return;}if(!r.ok)return;var list=await r.json();
          document.getElementById('count').textContent=list.length+' bench'+(list.length===1?'':'es');var g=document.getElementById('grid');var t=Date.now();
          var html=list.map(function(b){var url=b.isSelf?'/':('https://'+b.address+':'+b.port+'/#k='+encodeURIComponent(KEY));
            return '<div class="card" data-url="'+esc(url)+'"><img alt="" src="/api/bench/'+encodeURIComponent(b.id)+'/snapshot.jpg?'+q(KEY)+'&t='+t+'">'+
              '<div class="meta"><span class="name">'+esc(b.name)+(b.isSelf?' <span class="muted">(this PC)</span>':'')+'</span>'+
              (b.ticket?'<span class="pill">Ticket '+esc(b.ticket)+'</span>':'')+
              '<span class="pill"><span class="dot '+(b.video?'on':'')+'"></span>Video</span>'+
              '<span class="pill"><span class="dot '+(b.targetUsb?'on':'')+'"></span>Control</span>'+
              (b.viewers?'<span class="pill">'+b.viewers+' watching</span>':'')+'</div></div>';}).join('');
          g.innerHTML=html;g.querySelectorAll('.card').forEach(function(c){c.onclick=function(){window.open(c.getAttribute('data-url'),'_blank','noopener');};});}
        start(function(k){KEY=k;document.getElementById('grid').style.display='grid';refresh();setInterval(refresh,2500);});
        </script></body></html>
        """;

    public const string SignOff = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Service sign-off</title>" + Style + """
        <style>
        .wrap{max-width:720px;margin:0 auto;padding:16px}.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px;margin-top:12px}
        #pad{width:100%;height:220px;background:#fff;border-radius:8px;touch-action:none;display:block}
        #summary{white-space:pre-wrap;line-height:1.45}.row{display:flex;gap:8px;align-items:center;margin-top:10px;flex-wrap:wrap}
        input[type=text]{flex:1;min-width:200px}.big{font-size:18px;padding:10px 18px}
        </style></head><body>
        <header><span class="logo">HHT</span><span class="title" id="shop">Service sign-off</span></header>
        <div class="wrap">
          <div class="card" id="codeCard"><div class="title">Enter the 6-digit code from your technician</div>
            <div class="row"><input id="code" inputmode="numeric" maxlength="6" placeholder="123456" autocomplete="off"><button class="primary" id="go">Continue</button></div>
            <div class="err" id="cerr"></div></div>
          <div id="form" style="display:none">
            <div class="card"><div class="muted" id="ticket"></div><div id="summary" style="margin-top:8px"></div><div style="margin-top:8px;font-weight:600" id="total"></div></div>
            <div class="card">
              <div class="row"><input type="text" id="name" placeholder="Your full name" autocomplete="name"></div>
              <div class="muted" style="margin:10px 0 6px">Sign below</div>
              <canvas id="pad"></canvas>
              <div class="row"><button id="clear">Clear</button></div>
              <label class="row"><input type="checkbox" id="agree"> I confirm the work above was completed and I received my computer.</label>
              <div class="row"><button class="primary big" id="submit">Sign</button><span class="err" id="serr"></span></div>
            </div>
          </div>
          <div class="card" id="done" style="display:none"><div class="title">Thank you!</div><div class="muted" style="margin-top:6px">Your signature has been recorded. You can hand the tablet back.</div></div>
        </div>
        <script>
        var CODE='',pad=document.getElementById('pad'),ctx=pad.getContext('2d'),drawn=false,down=false,last=null;
        function esc(s){return String(s==null?'':s).replace(/[&<>"']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}
        function size(){var r=pad.getBoundingClientRect(),d=window.devicePixelRatio||1;pad.width=r.width*d;pad.height=r.height*d;ctx.setTransform(d,0,0,d,0,0);ctx.lineWidth=2.5;ctx.lineCap='round';ctx.strokeStyle='#111';drawn=false;}
        function pos(e){var r=pad.getBoundingClientRect();return{x:e.clientX-r.left,y:e.clientY-r.top};}
        pad.addEventListener('pointerdown',function(e){down=true;last=pos(e);pad.setPointerCapture(e.pointerId);});
        pad.addEventListener('pointermove',function(e){if(!down)return;var p=pos(e);ctx.beginPath();ctx.moveTo(last.x,last.y);ctx.lineTo(p.x,p.y);ctx.stroke();last=p;drawn=true;});
        pad.addEventListener('pointerup',function(){down=false;});
        document.getElementById('clear').onclick=function(){ctx.clearRect(0,0,pad.width,pad.height);drawn=false;};
        document.getElementById('go').onclick=async function(){var c=document.getElementById('code').value.trim();var r=await fetch('/api/sign/info?code='+encodeURIComponent(c),{cache:'no-store'});
          if(!r.ok){document.getElementById('cerr').textContent=r.status===429?'Too many tries. Ask the technician.':'That code is not valid.';return;}
          var j=await r.json();CODE=c;document.getElementById('shop').textContent=j.shop+' — service sign-off';document.getElementById('ticket').textContent=j.ticket?('Ticket '+j.ticket):'';
          document.getElementById('summary').innerHTML=esc(j.summary);document.getElementById('total').textContent=j.total?('Total: '+j.total):'';
          document.getElementById('codeCard').style.display='none';document.getElementById('form').style.display='block';size();};
        document.getElementById('submit').onclick=async function(){var n=document.getElementById('name').value.trim(),e=document.getElementById('serr');
          if(n.length<2){e.textContent='Please type your name.';return;}if(!drawn){e.textContent='Please sign in the box.';return;}if(!document.getElementById('agree').checked){e.textContent='Please tick the box to confirm.';return;}
          var r=await fetch('/api/sign?code='+encodeURIComponent(CODE),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:n,agree:true,png:pad.toDataURL('image/png')})});
          if(r.ok){document.getElementById('form').style.display='none';document.getElementById('done').style.display='block';}else e.textContent='Could not save: '+(await r.text());};
        window.addEventListener('resize',function(){if(!drawn)size();});
        </script></body></html>
        """;
}
