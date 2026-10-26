import asyncio
from pathlib import Path
from playwright.async_api import async_playwright
from .guard import GuardProxy
from .inspector import Inspector
from .navigation import navigate, history
from ..configuration import Settings
from ..security import TargetPolicy, AuthorizationError

class BrowserController:
    def __init__(self, settings: Settings, emit) -> None:
        self.settings = settings
        self.policy = TargetPolicy(settings)
        self.emit = emit
        self.proxy = GuardProxy(self.policy)
        self.playwright = self.browser = self.context = self.page = None
        self.crashed = False
        self.last_status = None

    async def start(self) -> None:
        proxy_url = await asyncio.to_thread(self.proxy.start)
        self.playwright = await async_playwright().start()
        self.browser = await self.playwright.chromium.launch(
            headless=not self.settings.headed, chromium_sandbox=True,
            proxy={'server': proxy_url},
            args=['--proxy-bypass-list=<-loopback>',
                  '--force-webrtc-ip-handling-policy=disable_non_proxied_udp'])
        self.context = await self.browser.new_context(
            ignore_https_errors=False, service_workers='block', accept_downloads=False)
        self.context.set_default_navigation_timeout(self.settings.navigation_timeout_ms)
        self.context.set_default_timeout(self.settings.action_timeout_ms)
        await self.context.route('**/*', self._route)
        await self.context.route_web_socket('**/*', lambda ws: ws.close())
        self.page = await self.context.new_page()
        self.page.on('crash', self._crash)
        self.page.on('response', self._response)
        self.page.on('dialog', lambda dialog: dialog.dismiss())
        self.context.on('page', self._new_page)
        Inspector(self.emit).attach(self.page)

    async def _route(self, route) -> None:
        try:
            self.policy.validate(route.request.url)
        except AuthorizationError:
            self.emit('blocked_target', {})
            await route.abort('blockedbyclient')
        else:
            await route.continue_()

    async def _new_page(self, page) -> None:
        if page is not self.page:
            await page.close()
            self.emit('popup_closed', {})

    def _response(self, response) -> None:
        if response.request.is_navigation_request() and response.frame == self.page.main_frame:
            self.last_status = response.status

    def _crash(self, *args) -> None:
        self.crashed = True
        self.emit('browser_crash', {})

    async def navigate(self, url: str) -> None:
        # Retry at most once for connection establishment failures only; never repeat actions/submits.
        from playwright.async_api import Error
        for attempt in range(2):
            try:
                response = await navigate(self.page, url, self.policy)
                self.last_status = response.status if response else None
                return
            except Error as error:
                transient = any(code in str(error) for code in ('ERR_CONNECTION_RESET','ERR_CONNECTION_CLOSED'))
                if attempt or not transient:
                    raise
                self.emit('navigation_retry', {})
                await asyncio.sleep(.25)

    async def screenshot(self, path: Path) -> None:
        from .challenges import redacted_screenshot
        await redacted_screenshot(self.page, path)

    async def inspect(self) -> list[dict]:
        from .inspector import dom_snapshot
        return await dom_snapshot(self.page)

    async def back(self) -> None:
        await history(self.page, self.policy)

    async def forward(self) -> None:
        await history(self.page, self.policy, forward=True)

    async def close(self) -> None:
        for resource in (self.context, self.browser):
            if resource:
                try:
                    await resource.close()
                except Exception:
                    self.emit('cleanup_error', {})
        if self.playwright:
            try:
                await self.playwright.stop()
            except Exception:
                self.emit('cleanup_error', {})
        await asyncio.to_thread(self.proxy.close)
