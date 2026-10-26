"""Exact-host authorization and DNS checks. No identity or TLS modifications."""
from __future__ import annotations
from typing import TYPE_CHECKING
import ipaddress
import socket
from urllib.parse import urlsplit
if TYPE_CHECKING:
    from .configuration import Settings

class AuthorizationError(ValueError):
    pass

LOOPBACK_NAMES = {'localhost', '127.0.0.1', '::1'}

class TargetPolicy:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    def validate(self, url: str) -> tuple[str, int]:
        if any(ord(c) < 33 for c in url) or '\\' in url:
            raise AuthorizationError('Whitespace and control characters are not allowed in targets')
        try:
            parts = urlsplit(url)
            if parts.scheme not in ('http', 'https') or not parts.hostname:
                raise ValueError()
            if parts.username is not None or parts.password is not None:
                raise ValueError()
            host = parts.hostname.lower().rstrip('.').encode('idna').decode('ascii')
            port = parts.port if parts.port is not None else (443 if parts.scheme == 'https' else 80)
            if port < 1:
                raise ValueError()
        except (ValueError, UnicodeError):
            raise AuthorizationError('Only HTTP(S) targets without credentials are supported') from None
        if host not in self.settings.allowed_domains:
            raise AuthorizationError('Target host is not explicitly authorized')
        if host not in LOOPBACK_NAMES and not self.settings.external_enabled:
            raise AuthorizationError('External target authorization is disabled')
        return host, port

    def resolve(self, url: str) -> tuple[str, int]:
        host, port = self.validate(url)
        addresses = sorted({item[4][0] for item in socket.getaddrinfo(
            host, port, type=socket.SOCK_STREAM)})
        if not addresses:
            raise AuthorizationError('Target did not resolve')
        for address in addresses:
            ip = ipaddress.ip_address(address)
            if host in LOOPBACK_NAMES:
                if not ip.is_loopback:
                    raise AuthorizationError('Loopback host resolved outside loopback')
            elif not ip.is_global:
                raise AuthorizationError('External targets must resolve exclusively to public IPs')
        # The proxy connects to this numeric IP: no second DNS resolution or rebinding window.
        return addresses[0], port

    def validate_redirect(self, source: str, destination: str) -> str:
        from urllib.parse import urljoin
        target = urljoin(source, destination)
        self.validate(target)
        return target


def safe_origin(url: str) -> str:
    """Never persist query strings, fragments, userinfo or potentially secret paths."""
    try:
        parts = urlsplit(url)
        host = parts.hostname or ''
        if ':' in host:
            host = '[' + host + ']'
        return f'{parts.scheme}://{host}' + (f':{parts.port}' if parts.port else '')
    except ValueError:
        return '[invalid-url]'
