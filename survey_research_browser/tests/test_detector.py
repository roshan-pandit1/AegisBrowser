import asyncio
import pytest
from survey_browser.browser.controller import BrowserController
from survey_browser.survey.detector import discover
from survey_browser.survey.answers import fill, resolve
from survey_browser.survey.validation import validate_fields

@pytest.mark.browser
def test_discovery_and_all_field_types(settings,profile,site):
    async def check():
        browser=BrowserController(settings,lambda *args:None)
        try:
            await browser.start()
            await browser.navigate(site+'/survey.html')
            found=await discover(browser.page,settings.next_selector)
            assert not found.warnings
            assert {q.kind for q in found.questions} == {'text','textarea','radio','checkbox','select','number','date','rating'}
            assert len(found.questions)==8
            assert any(not v['valid'] for v in await validate_fields(browser.page))
            for q in found.questions:
                await fill(browser.page,q,resolve(q,profile))
            assert await browser.page.locator('[name=note]').input_value() == 'SYNTHETIC QA ONLY'
            assert await browser.page.locator('[name=option][value=beta]').is_checked()
            assert await browser.page.locator('[name=flags]:checked').count() == 2
            assert await browser.page.locator('[name=category]').input_value() == 'b'
            assert all(v['valid'] for v in await validate_fields(browser.page))
        finally:
            await browser.close()
    asyncio.run(check())

@pytest.mark.browser
def test_unapproved_redirect_blocked(settings,site):
    async def check():
        browser=BrowserController(settings,lambda *args:None)
        try:
            await browser.start()
            with pytest.raises(Exception):
                await browser.navigate(site+'/redirect')
        finally:
            await browser.close()
    asyncio.run(check())

@pytest.mark.browser
def test_authorized_redirect_and_history(settings,site):
    async def check():
        browser=BrowserController(settings,lambda *args:None)
        try:
            await browser.start()
            await browser.navigate(site+'/redirect-local')
            assert browser.page.url.endswith('/survey.html')
            await browser.navigate(site+'/page2.html')
            await browser.back()
            assert browser.page.url.endswith('/survey.html')
            await browser.forward()
            assert browser.page.url.endswith('/page2.html')
        finally:
            await browser.close()
    asyncio.run(check())
