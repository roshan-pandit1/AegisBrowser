from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

from ..security import safe_origin

class Inspector:
    def __init__(self, emit) -> None:
        self.emit = emit

    def attach(self, page) -> None:
        page.on('console', lambda message: self.emit('console', {'type': message.type}))
        page.on('pageerror', lambda error: self.emit('script_error', {}))
        page.on('requestfailed', self.failed)
        page.on('response', lambda response: self.emit('response', {
            'origin': safe_origin(response.url), 'status': response.status}))
        page.on('requestfinished', lambda request: self.emit('request_finished', {
            'origin': safe_origin(request.url),
            'duration_ms': max(0, request.timing.get('responseEnd', 0))}))

    def failed(self, request) -> None:
        failure = request.failure or ''
        category = ('dns' if 'NAME_NOT_RESOLVED' in failure else
                    'tls' if 'CERT_' in failure or 'SSL_' in failure else
                    'timeout' if 'TIMED_OUT' in failure else
                    'blocked' if 'BLOCKED' in failure else 'connection')
        self.emit('network_failure', {'origin':safe_origin(request.url), 'type':category})

async def dom_snapshot(page: Page) -> list[dict]:
    # Live inspection only; never dump page HTML, values or authentication material.
    return await page.locator('input,select,textarea').evaluate_all(
        "els => els.map(e => ({tag:e.tagName, type:e.type, name:e.name, required:e.required}))")
