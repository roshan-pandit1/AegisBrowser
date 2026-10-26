from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

from pathlib import Path

async def detect_challenge(page: Page) -> str | None:
    for frame in page.frames:
        for selector, kind in (
            ('input[type=password]:visible', 'authentication'),
            ('[data-challenge]:visible, iframe[src*="captcha"]:visible, iframe[title*="CAPTCHA"]:visible, .g-recaptcha:visible, .h-captcha:visible', 'verification'),
            ('[data-access-denied]:visible', 'access_denied'),
        ):
            if await frame.locator(selector).count():
                return kind
        text = (await frame.locator('body').inner_text(timeout=2000)).lower()
        if any(term in text for term in ('verify you are human', 'complete the captcha',
                                         'access denied', 'unusual traffic', 'verification required')):
            return 'verification'
    return None

async def redacted_screenshot(page: Page, path: Path) -> None:
    # Full-page mask intentionally trades visual detail for confidentiality, including challenge text.
    await page.screenshot(path=str(path), full_page=False,
                          mask=[page.locator('html')], mask_color='#18253a',
                          style="html,body{background:#18253a!important} *,*::before,*::after{visibility:hidden!important;background-image:none!important;box-shadow:none!important}")
