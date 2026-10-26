from functools import partial
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
import threading
import pytest
from survey_browser.configuration import Settings
from survey_browser.survey.answers import AnswerProfile

FIXTURES = Path(__file__).parent/'fixtures'

@pytest.fixture
def settings(tmp_path):
    return Settings(data_dir=tmp_path, headed=False, action_timeout_ms=2000, navigation_timeout_ms=5000)

@pytest.fixture
def profile():
    return AnswerProfile.load(FIXTURES/'answers.json')

@pytest.fixture
def site():
    class Handler(SimpleHTTPRequestHandler):
        def log_message(self,*args):
            pass
        def do_GET(self):
            if self.path == '/redirect':
                self.send_response(302)
                self.send_header('Location','http://unapproved.invalid/private')
                self.end_headers()
            elif self.path == '/redirect-local':
                self.send_response(302)
                self.send_header('Location','/survey.html')
                self.end_headers()
            else:
                super().do_GET()
    server = ThreadingHTTPServer(('127.0.0.1',0),partial(Handler,directory=str(FIXTURES)))
    thread = threading.Thread(target=server.serve_forever,daemon=True)
    thread.start()
    try:
        yield f'http://127.0.0.1:{server.server_port}'
    finally:
        server.shutdown()
        server.server_close()
        thread.join(2)
