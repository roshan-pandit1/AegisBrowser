from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

from ..security import TargetPolicy

async def navigate(page: Page, url: str, policy: TargetPolicy) -> None:
    policy.validate(url)
    response = await page.goto(url, wait_until='domcontentloaded')
    policy.validate(page.url)
    if response and response.status >= 400 and response.status not in (401,403):
        raise RuntimeError('Target returned an unsuccessful HTTP status')
    return response

async def history(page: Page, policy: TargetPolicy, forward: bool = False) -> None:
    await (page.go_forward() if forward else page.go_back())
    policy.validate(page.url)
