#!/usr/bin/env python3
"""
HHT Bridge Box - virtual media agent (Raspberry Pi in USB gadget mode).
Hammer Hill Technologies.

Presents an ISO to the customer's PC as a USB CD-ROM (boot Windows/Linux installers, rescue disks)
or an .img as a removable USB disk - without a flash drive. The technician console uploads images
and mounts/ejects them over HTTPS.

API (every call needs header  X-HHT-Token: <token>):
  GET    /status                    -> {"mounted","mode","images":[{"name","size"}],"free","gadget","version"}
  PUT    /images/<name.iso|.img>    body = file bytes (streamed to disk)
  DELETE /images/<name>
  POST   /mount?name=<name>&mode=cdrom|disk
  POST   /eject

Python 3 standard library only. Run as root (configfs). See install.sh.
"""
import argparse
import hmac
import json
import os
import re
import shutil
import ssl
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = "1.0"
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 ._()\-]{0,115}\.(iso|img)$", re.IGNORECASE)
LUN = "/sys/kernel/config/usb_gadget/hhtvm/functions/mass_storage.usb0/lun.0"
CHUNK = 1024 * 1024


class Media:
    """Owns the mass-storage LUN. In dry-run mode it only records state (for tests on a normal PC)."""

    def __init__(self, image_dir, dry_run):
        self.image_dir = image_dir
        self.dry_run = dry_run
        self.lock = threading.Lock()
        self.mounted = None
        self.mode = None
        os.makedirs(image_dir, exist_ok=True)

    def gadget_active(self):
        return self.dry_run or os.path.isdir(LUN)

    def _write(self, attr, value):
        if self.dry_run:
            return
        with open(os.path.join(LUN, attr), "w") as f:
            f.write(value)

    def eject(self):
        with self.lock:
            # forced_eject detaches even if the host has the medium locked (Windows Setup does)
            if not self.dry_run and os.path.exists(os.path.join(LUN, "forced_eject")):
                self._write("forced_eject", "1")
            else:
                self._write("file", "")
            self.mounted, self.mode = None, None

    def mount(self, name, mode):
        path = os.path.join(self.image_dir, name)
        if not os.path.isfile(path):
            raise FileNotFoundError(name)
        self.eject()
        with self.lock:
            cdrom = mode == "cdrom"
            self._write("cdrom", "1" if cdrom else "0")
            self._write("ro", "1" if cdrom else "0")
            self._write("removable", "1")
            self._write("file", path)
            self.mounted, self.mode = name, mode

    def images(self):
        out = []
        for n in sorted(os.listdir(self.image_dir)):
            p = os.path.join(self.image_dir, n)
            if os.path.isfile(p) and NAME_RE.match(n):
                out.append({"name": n, "size": os.path.getsize(p)})
        return out


def make_handler(media, token):
    token_b = token.encode()

    class Handler(BaseHTTPRequestHandler):
        server_version = "HHT-VMedia/" + VERSION
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))

        def _send(self, code, obj=None, text=None):
            body = json.dumps(obj).encode() if obj is not None else (text or "").encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json" if obj is not None else "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _authorized(self):
            got = (self.headers.get("X-HHT-Token") or "").encode()
            if hmac.compare_digest(got, token_b):
                return True
            time.sleep(0.5)  # slow down guessing
            self._send(401, text="bad token")
            return False

        def _route(self):
            u = urllib.parse.urlparse(self.path)
            return u.path, urllib.parse.parse_qs(u.query)

        def _image_name(self, path):
            name = urllib.parse.unquote(path[len("/images/"):])
            if not NAME_RE.match(name) or "/" in name or name != os.path.basename(name):
                self._send(400, text="invalid image name")
                return None
            return name

        def do_GET(self):
            if not self._authorized():
                return
            path, _ = self._route()
            if path == "/status":
                du = shutil.disk_usage(media.image_dir)
                self._send(200, {"mounted": media.mounted, "mode": media.mode, "images": media.images(),
                                 "free": du.free, "gadget": media.gadget_active(), "version": VERSION})
            else:
                self._send(404, text="not found")

        def do_PUT(self):
            if not self._authorized():
                return
            path, _ = self._route()
            if not path.startswith("/images/"):
                return self._send(404, text="not found")
            name = self._image_name(path)
            if name is None:
                return
            try:
                length = int(self.headers.get("Content-Length", "-1"))
            except ValueError:
                length = -1
            if length < 0:
                return self._send(411, text="Content-Length required")
            if length > shutil.disk_usage(media.image_dir).free - 64 * 1024 * 1024:
                return self._send(507, text="not enough space on the module")
            if media.mounted == name:
                return self._send(409, text="image is mounted; eject first")
            tmp = os.path.join(media.image_dir, ".upload-%d.part" % threading.get_ident())
            remaining = length
            try:
                with open(tmp, "wb") as f:
                    while remaining > 0:
                        chunk = self.rfile.read(min(CHUNK, remaining))
                        if not chunk:
                            raise IOError("upload interrupted")
                        f.write(chunk)
                        remaining -= len(chunk)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(tmp, os.path.join(media.image_dir, name))
            except Exception as e:  # noqa: BLE001
                try:
                    os.remove(tmp)
                except OSError:
                    pass
                return self._send(500, text="upload failed: %s" % e)
            self._send(200, {"ok": True, "name": name, "size": length})

        def do_DELETE(self):
            if not self._authorized():
                return
            path, _ = self._route()
            if not path.startswith("/images/"):
                return self._send(404, text="not found")
            name = self._image_name(path)
            if name is None:
                return
            if media.mounted == name:
                return self._send(409, text="image is mounted; eject first")
            p = os.path.join(media.image_dir, name)
            if not os.path.isfile(p):
                return self._send(404, text="no such image")
            os.remove(p)
            self._send(200, {"ok": True})

        def do_POST(self):
            if not self._authorized():
                return
            path, q = self._route()
            # drain any body
            n = int(self.headers.get("Content-Length", "0") or 0)
            if n:
                self.rfile.read(min(n, 65536))
            if path == "/eject":
                media.eject()
                return self._send(200, {"ok": True})
            if path == "/mount":
                name = (q.get("name") or [""])[0]
                mode = (q.get("mode") or ["cdrom"])[0]
                if not NAME_RE.match(name) or mode not in ("cdrom", "disk"):
                    return self._send(400, text="bad name or mode")
                try:
                    media.mount(name, mode)
                except FileNotFoundError:
                    return self._send(404, text="no such image")
                except OSError as e:
                    return self._send(500, text="mount failed: %s" % e)
                return self._send(200, {"ok": True, "mounted": name, "mode": mode})
            self._send(404, text="not found")

    return Handler


def main():
    ap = argparse.ArgumentParser(description="HHT Bridge Box virtual media agent")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8444)
    ap.add_argument("--images", default="/var/lib/hht-vmedia/images")
    ap.add_argument("--token-file", default="/etc/hht-vmedia/token")
    ap.add_argument("--cert", default="/etc/hht-vmedia/cert.pem")
    ap.add_argument("--key", default="/etc/hht-vmedia/key.pem")
    ap.add_argument("--no-tls", action="store_true", help="testing only")
    ap.add_argument("--dry-run", action="store_true", help="do not touch configfs (testing on a normal PC)")
    a = ap.parse_args()

    with open(a.token_file) as f:
        token = f.read().strip()
    if len(token) < 16:
        sys.exit("token must be at least 16 characters")

    media = Media(a.images, a.dry_run)
    httpd = ThreadingHTTPServer((a.bind, a.port), make_handler(media, token))
    if not a.no_tls:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        ctx.load_cert_chain(a.cert, a.key)
        httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True)
    print("HHT virtual media agent %s on %s:%d (%s)%s" % (VERSION, a.bind, a.port, "https" if not a.no_tls else "http",
                                                          " DRY-RUN" if a.dry_run else ""), flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
