#!/usr/bin/env python3
"""
HHT Bridge cloud relay - multi-shop bench dashboard (view only).
Hammer Hill Technologies.

Bench consoles push a snapshot + status every few seconds; owners/managers open one web page and see every
bench in every shop from anywhere. Runs on the droplet behind Caddy (TLS is Caddy's job):

    handle_path /bridge/* {
        reverse_proxy 127.0.0.1:8450
    }

Keys (config.json):
  publish_key  - per shop, set in each bench console (Share tab > Cloud). Can only PUSH for that shop.
  view_key     - per shop, lets a manager see that shop's benches.
  owner_view_key - sees every shop.
No remote control goes through the cloud (by design): control stays on the shop LAN with technician consent.

Standard library only.  python3 hht_bridge_relay.py --config /etc/hht-bridge-relay/config.json
"""
import argparse
import base64
import hmac
import json
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = "1.0"
MAX_BODY = 1024 * 1024          # 1 MB per publish (JPEG <= ~700 KB)
EXPIRE_SECONDS = 120
MIN_PUBLISH_INTERVAL = 0.9      # seconds per bench

DASHBOARD = """<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>HHT Bridge - all shops</title><style>
:root{--bg:#17181b;--panel:#222429;--line:#32353c;--blue:#2f7df6;--green:#34c772;--text:#f3f4f6;--muted:#9aa0aa}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px 'Segoe UI',system-ui,sans-serif}
header{display:flex;gap:10px;align-items:center;padding:10px 14px;background:var(--panel);border-bottom:1px solid var(--line)}
.logo{background:var(--blue);color:#fff;font-weight:700;border-radius:6px;padding:5px 7px;font-size:12px}.muted{color:var(--muted)}
h2{font-size:13px;color:var(--muted);margin:18px 14px 6px;text-transform:uppercase;letter-spacing:.05em}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:12px;padding:0 14px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden}
.card img{display:block;width:100%;aspect-ratio:16/9;object-fit:contain;background:#000}
.meta{padding:8px 10px;display:flex;gap:8px;flex-wrap:wrap;align-items:center}.name{font-weight:600;flex:1}
.pill{border:1px solid var(--line);border-radius:12px;padding:2px 9px;font-size:12px}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#555a63;margin-right:5px}.on{background:var(--green)}
.login{max-width:360px;margin:12vh auto;background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:22px}
input{width:100%;margin:10px 0;background:#111216;color:var(--text);border:1px solid var(--line);border-radius:6px;padding:8px}
button{background:var(--blue);color:#fff;border:0;border-radius:6px;padding:8px 14px;font:inherit;cursor:pointer}.err{color:#ff7b72}
</style></head><body>
<header><span class="logo">HHT</span><b>Bridge benches</b><span class="muted" id="count"></span></header>
<div id="main"></div>
<script>
var KEY=sessionStorage.getItem('hhtv')||'';
function esc(s){return String(s==null?'':s).replace(/[&<>"']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}
function login(msg){document.getElementById('main').innerHTML='<div class="login"><b>View key</b><input id="k" autocomplete="off"><div class="err">'+(msg||'')+'</div><button id="go">Open</button></div>';
 document.getElementById('go').onclick=function(){KEY=document.getElementById('k').value.trim();sessionStorage.setItem('hhtv',KEY);refresh();};}
async function refresh(){if(!KEY){login();return;}var r;try{r=await fetch('api/benches',{headers:{'X-HHT-View-Key':KEY},cache:'no-store'});}catch(e){return;}
 if(r.status===401||r.status===429){sessionStorage.removeItem('hhtv');KEY='';login(r.status===429?'Too many wrong keys.':'Wrong key.');return;}
 var list=await r.json(),t=Date.now(),shops={};list.forEach(function(b){(shops[b.shop_name]=shops[b.shop_name]||[]).push(b);});
 document.getElementById('count').textContent=list.length+' bench'+(list.length===1?'':'es');
 document.getElementById('main').innerHTML=Object.keys(shops).sort().map(function(s){return '<h2>'+esc(s)+'</h2><div class="grid">'+shops[s].map(function(b){
  return '<div class="card"><img alt="" src="api/bench/'+encodeURIComponent(b.shop)+'/'+encodeURIComponent(b.bench_id)+'.jpg?k='+encodeURIComponent(KEY)+'&t='+t+'"><div class="meta"><span class="name">'+esc(b.name)+'</span>'+
  (b.ticket?'<span class="pill">'+esc(b.ticket)+'</span>':'')+'<span class="pill"><span class="dot '+(b.video?'on':'')+'"></span>Video</span><span class="pill"><span class="dot '+(b.target_usb?'on':'')+'"></span>Control</span>'+
  '<span class="muted">'+b.age_seconds+'s ago</span></div></div>';}).join('')+'</div>';}).join('')||'<p class="muted" style="padding:14px">No benches are publishing.</p>';}
refresh();setInterval(function(){if(KEY)refresh();},5000);
</script></body></html>"""


class Relay:
    def __init__(self, config):
        self.owner_key = config.get("owner_view_key", "")
        self.shops = {s["id"]: s for s in config.get("shops", [])}
        self.benches = {}           # (shop, bench_id) -> dict
        self.lock = threading.Lock()
        self.failures = {}          # ip -> [count, first]
        for s in self.shops.values():
            if len(s.get("publish_key", "")) < 16 or len(s.get("view_key", "")) < 16:
                raise SystemExit("every shop needs publish_key and view_key of 16+ characters")

    def shop_for_publish(self, key):
        for s in self.shops.values():
            if key and hmac.compare_digest(key, s["publish_key"]):
                return s
        return None

    def view_scope(self, key):
        """None = invalid, '*' = all shops, else a shop id."""
        if not key:
            return None
        if self.owner_key and hmac.compare_digest(key, self.owner_key):
            return "*"
        for s in self.shops.values():
            if hmac.compare_digest(key, s["view_key"]):
                return s["id"]
        return None

    def locked_out(self, ip):
        f = self.failures.get(ip)
        return bool(f and f[0] >= 10 and time.time() - f[1] < 300)

    def fail(self, ip):
        f = self.failures.get(ip)
        if not f or time.time() - f[1] > 300:
            self.failures[ip] = [1, time.time()]
        else:
            f[0] += 1


def make_handler(relay):
    class H(BaseHTTPRequestHandler):
        server_version = "HHT-Bridge-Relay/" + VERSION
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            sys.stderr.write("%s %s\n" % (self.client_ip(), fmt % args))

        def client_ip(self):
            ip = self.client_address[0]
            if ip in ("127.0.0.1", "::1"):  # behind Caddy: trust its X-Forwarded-For only
                fwd = (self.headers.get("X-Forwarded-For") or "").split(",")[0].strip()
                return fwd or ip
            return ip

        def send(self, code, body=b"", ctype="application/json", extra=None):
            if isinstance(body, (dict, list)):
                body = json.dumps(body).encode()
            elif isinstance(body, str):
                body = body.encode()
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            self.wfile.write(body)

        def view_key(self, q):
            return self.headers.get("X-HHT-View-Key") or (q.get("k") or [""])[0]

        def do_GET(self):
            u = urllib.parse.urlparse(self.path)
            q = urllib.parse.parse_qs(u.query)
            if u.path in ("/", "/index.html"):
                return self.send(200, DASHBOARD, "text/html; charset=utf-8",
                                 {"Content-Security-Policy": "default-src 'self'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'"})
            if u.path == "/health":
                return self.send(200, {"ok": True, "version": VERSION})
            ip = self.client_ip()
            if relay.locked_out(ip):
                return self.send(429, {"ok": False, "error": "locked_out"})
            scope = relay.view_scope(self.view_key(q))
            if scope is None:
                relay.fail(ip)
                return self.send(401, {"ok": False, "error": "invalid_key"})
            now = time.time()
            with relay.lock:
                for k in [k for k, b in relay.benches.items() if now - b["time"] > EXPIRE_SECONDS]:
                    del relay.benches[k]
                visible = {k: b for k, b in relay.benches.items() if scope == "*" or k[0] == scope}
            if u.path == "/api/benches":
                out = [{"shop": k[0], "shop_name": relay.shops[k[0]].get("name", k[0]), "bench_id": k[1], "name": b["name"],
                        "ticket": b["ticket"], "video": b["video"], "target_usb": b["target_usb"], "viewers": b["viewers"],
                        "age_seconds": int(now - b["time"])} for k, b in sorted(visible.items(), key=lambda kv: (kv[0][0], kv[1]["name"]))]
                return self.send(200, out)
            parts = u.path.split("/")
            if len(parts) == 5 and parts[1] == "api" and parts[2] == "bench" and parts[4].endswith(".jpg"):
                b = visible.get((urllib.parse.unquote(parts[3]), urllib.parse.unquote(parts[4][:-4])))
                if not b or not b["jpeg"]:
                    return self.send(404, {"ok": False, "error": "not_found"})
                return self.send(200, b["jpeg"], "image/jpeg")
            self.send(404, {"ok": False, "error": "not_found"})

        def do_POST(self):
            u = urllib.parse.urlparse(self.path)
            if u.path != "/api/publish":
                return self.send(404, {"ok": False, "error": "not_found"})
            ip = self.client_ip()
            if relay.locked_out(ip):
                return self.send(429, {"ok": False, "error": "locked_out"})
            shop = relay.shop_for_publish(self.headers.get("X-HHT-Publish-Key") or "")
            try:
                n = int(self.headers.get("Content-Length", "0"))
            except ValueError:
                n = -1
            if n < 0 or n > MAX_BODY:
                return self.send(413, {"ok": False, "error": "too_large"})
            raw = self.rfile.read(n) if n else b""
            if shop is None:
                relay.fail(ip)
                return self.send(401, {"ok": False, "error": "invalid_key"})
            try:
                d = json.loads(raw)
                bench_id = str(d["bench_id"])[:40]
                if not bench_id.replace("-", "").replace("_", "").isalnum():
                    raise ValueError("bench_id")
                jpeg = base64.b64decode(d["jpeg_base64"]) if d.get("jpeg_base64") else b""
                if jpeg and not jpeg.startswith(b"\xff\xd8"):
                    raise ValueError("not a JPEG")
            except (ValueError, KeyError, TypeError, json.JSONDecodeError) as e:
                return self.send(400, {"ok": False, "error": "bad_request", "message": str(e)})
            key = (shop["id"], bench_id)
            now = time.time()
            with relay.lock:
                prev = relay.benches.get(key)
                if prev and now - prev["time"] < MIN_PUBLISH_INTERVAL:
                    return self.send(429, {"ok": False, "error": "too_fast"}, extra={"Retry-After": "1"})
                relay.benches[key] = {"name": str(d.get("name") or bench_id)[:40], "ticket": (str(d["ticket"])[:20] if d.get("ticket") else None),
                                      "video": bool(d.get("video")), "target_usb": bool(d.get("target_usb")),
                                      "viewers": int(d.get("viewers") or 0), "jpeg": jpeg, "time": now}
            self.send(200, {"ok": True})

    return H


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="/etc/hht-bridge-relay/config.json")
    ap.add_argument("--bind", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8450)
    a = ap.parse_args()
    with open(a.config) as f:
        relay = Relay(json.load(f))
    httpd = ThreadingHTTPServer((a.bind, a.port), make_handler(relay))
    print("HHT Bridge relay %s on %s:%d, %d shop(s)" % (VERSION, a.bind, a.port, len(relay.shops)), flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
