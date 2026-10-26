"""Per-run forwarding proxy. Enforces destination policy before any TCP connection.
HTTPS uses an opaque CONNECT tunnel; Chromium itself verifies the original certificate.
"""
import select
import socket
import socketserver
import threading
from urllib.parse import urlsplit
from ..security import TargetPolicy

class GuardProxy:
    def __init__(self, policy: TargetPolicy) -> None:
        self.policy = policy
        self.stop = threading.Event()
        self.server = None
        self.thread = None
        self.sockets: set[socket.socket] = set()
        self.lock = threading.Lock()

    def start(self) -> str:
        owner = self
        class Handler(socketserver.StreamRequestHandler):
            rbufsize = 0
            def handle(self):
                upstream = None
                self.connection.settimeout(10)
                try:
                    line = self.rfile.readline(8193)
                    if len(line) > 8192:
                        raise ValueError('Request line too long')
                    method, target, version = line.decode('ascii').strip().split(' ')
                    headers = []
                    size = 0
                    while True:
                        header = self.rfile.readline(8193)
                        size += len(header)
                        if size > 65536 or not header:
                            raise ValueError('Invalid headers')
                        if header == b'\r\n':
                            break
                        headers.append(header)
                    url = 'https://' + target if method == 'CONNECT' else target
                    ip, port = owner.policy.resolve(url)
                    upstream = socket.create_connection((ip, port), timeout=10)
                    with owner.lock:
                        owner.sockets.update((self.connection, upstream))
                    if method == 'CONNECT':
                        self.wfile.write(b'HTTP/1.1 200 Connection Established\r\n\r\n')
                        self.wfile.flush()
                    else:
                        parts = urlsplit(url)
                        if parts.scheme != 'http':
                            raise ValueError('HTTPS requires CONNECT')
                        path = parts.path or '/'
                        if parts.query:
                            path += '?' + parts.query
                        # No proxy authentication/header forwarding or cross-host keepalive.
                        clean = [h for h in headers if h.split(b':', 1)[0].lower()
                                 not in (b'proxy-authorization', b'proxy-connection', b'connection', b'host')]
                        payload = (f'{method} {path} HTTP/1.1\r\nHost: {parts.netloc}\r\n'
                                   'Connection: close\r\n').encode('ascii') + b''.join(clean) + b'\r\n'
                        upstream.sendall(payload)
                    self.connection.settimeout(None)
                    upstream.settimeout(None)
                    while not owner.stop.is_set():
                        ready, _, _ = select.select([self.connection, upstream], [], [], .2)
                        for src in ready:
                            data = src.recv(65536)
                            if not data:
                                return
                            (upstream if src is self.connection else self.connection).sendall(data)
                except Exception:
                    try:
                        self.connection.sendall(b'HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n')
                    except OSError:
                        pass
                finally:
                    with owner.lock:
                        owner.sockets.discard(self.connection)
                        owner.sockets.discard(upstream)
                    if upstream:
                        upstream.close()
        class Server(socketserver.ThreadingTCPServer):
            daemon_threads = True
            allow_reuse_address = False
        self.server = Server(('127.0.0.1', 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return f'http://127.0.0.1:{self.server.server_address[1]}'

    def close(self) -> None:
        self.stop.set()
        with self.lock:
            for sock in self.sockets:
                try:
                    sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
        if self.server:
            self.server.shutdown()
            self.server.server_close()
        if self.thread:
            self.thread.join(timeout=2)
