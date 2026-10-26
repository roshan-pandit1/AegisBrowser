"""Real loopback-only proxy smoke check, independent of third-party packages."""
from http.client import HTTPConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import threading
from types import SimpleNamespace
from survey_browser.browser.guard import GuardProxy
from survey_browser.security import TargetPolicy


def main() -> None:
    class Fixture(BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            data = b'local-fixture'
            self.send_response(200)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_POST(self) -> None:
            data = self.rfile.read(int(self.headers['Content-Length']))
            self.send_response(200)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args) -> None:
            pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Fixture)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    guard = GuardProxy(TargetPolicy(SimpleNamespace(allowed_domains=('127.0.0.1',), external_enabled=False)))
    try:
        proxy_url = guard.start()
        proxy_port = int(proxy_url.rsplit(':', 1)[1])
        for method, body in (('GET', None), ('POST', b'synthetic-test')):
            connection = HTTPConnection('127.0.0.1', proxy_port, timeout=5)
            try:
                connection.request(method, f'http://127.0.0.1:{server.server_port}/', body)
                response = connection.getresponse()
                assert response.status == 200
                assert response.read() == (b'local-fixture' if method == 'GET' else body)
            finally:
                connection.close()
        connection = HTTPConnection('127.0.0.1', proxy_port, timeout=5)
        try:
            connection.request('GET', 'http://unapproved.invalid/')
            assert connection.getresponse().status == 403
        finally:
            connection.close()
        print('PASS: loopback GET, POST body forwarding and unapproved host denial')
    finally:
        guard.close()
        server.shutdown()
        server.server_close()
        thread.join(2)
    print('PASS: proxy and fixture cleanup')


if __name__ == '__main__':
    main()
