from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

async def validate_fields(page: Page) -> list[dict]:
    return await page.locator('[data-srb-field]').evaluate_all(
        "els => els.map(e=>({field: Number(e.dataset.srbField), valid:e.checkValidity(), required:e.required}))")

async def fingerprint(page: Page) -> str:
    # Used transiently to recognize next-page changes; never stored in reports.
    return await page.evaluate("() => location.href + '|' + [...document.querySelectorAll('input,select,textarea,label,fieldset legend,[data-survey-page]')].map(e => [e.id,e.name,e.type,e.getAttribute('data-survey-page'),['LEGEND','LABEL'].includes(e.tagName)?e.textContent:''].join(':')).join('|')")
