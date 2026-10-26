import socket
import pytest
from survey_browser.configuration import Settings
from survey_browser.security import TargetPolicy, AuthorizationError, safe_origin

@pytest.mark.parametrize('url',['file:///etc/passwd','javascript:alert(1)','http://example.com',
    'http://localhost.evil.test','http://u:p@localhost/','http://127.0.0.1\n.evil/','http://localhost:0/'])
def test_denied(url):
    with pytest.raises(AuthorizationError):
        TargetPolicy(Settings()).validate(url)

def test_local_target():
    assert TargetPolicy(Settings()).validate('http://127.0.0.1:8000/form') == ('127.0.0.1',8000)

def test_redirect_policy():
    p=TargetPolicy(Settings())
    assert p.validate_redirect('http://localhost/a','/b') == 'http://localhost/b'
    with pytest.raises(AuthorizationError):
        p.validate_redirect('http://localhost/a','https://unapproved.example/b')

@pytest.mark.parametrize('ip',['127.0.0.1','10.0.0.1','169.254.169.254','::1','192.168.1.1','100.64.0.1'])
def test_external_private_resolution_denied(monkeypatch,ip):
    monkeypatch.setattr(socket,'getaddrinfo',lambda *a,**k:[(0,0,0,'',(ip,443))])
    p=TargetPolicy(Settings(allowed_domains=('owned.example',),external_enabled=True))
    with pytest.raises(AuthorizationError):
        p.resolve('https://owned.example')

def test_explicit_external_opt_in():
    p=TargetPolicy(Settings(allowed_domains=('owned.example',)))
    with pytest.raises(AuthorizationError):
        p.validate('https://owned.example')

def test_public_ip_is_pinned(monkeypatch):
    monkeypatch.setattr(socket,'getaddrinfo',lambda *a,**k:[(0,0,0,'',('93.184.216.34',443))])
    p=TargetPolicy(Settings(allowed_domains=('owned.example',),external_enabled=True))
    assert p.resolve('https://owned.example') == ('93.184.216.34',443)

def test_url_redaction():
    assert safe_origin('https://user:secret@example.com/path-secret?token=secret#password') == 'https://example.com'
