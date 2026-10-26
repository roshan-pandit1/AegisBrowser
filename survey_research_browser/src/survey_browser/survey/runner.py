import asyncio
import threading
import time
import uuid
from datetime import datetime, timezone
from ..browser.controller import BrowserController
from ..browser.challenges import detect_challenge, redacted_screenshot
from ..security import TargetPolicy, safe_origin
from ..reporting.reporter import export_report, sanitize
from ..storage.database import Database
from ..storage.repositories import RunRepository
from .models import State
from .detector import discover
from .answers import resolve, fill, AnswerProfile
from .validation import validate_fields, fingerprint
from ..configuration import Settings

from .state import validate_transition

class UnsupportedSurvey(RuntimeError):
    pass

class SceneChanged(RuntimeError):
    pass

class Controls:
    """Only threading Events cross the Qt/asyncio boundary; no Playwright objects do."""
    def __init__(self) -> None:
        self.stop = threading.Event()
        self.paused = threading.Event()
        self.resume = threading.Event()
        self.approval = threading.Event()

class SurveyRunner:
    def __init__(self, settings: Settings, profile: AnswerProfile, emit=lambda *args: None,
                 controls: Controls | None = None, controller_factory=BrowserController):
        self.settings, self.profile, self.emit = settings, profile, emit
        self.controls = controls or Controls()
        self.controller = controller_factory(settings, self.event)
        self.state = State.IDLE
        self.outcome = State.FAILED
        self.record = {'id': str(uuid.uuid4()), 'status': self.state.value,
                       'started': datetime.now(timezone.utc).isoformat(), 'ended': None,
                       'duration_seconds': 0, 'submitted': False, 'submission_status':'not_submitted',
                       'challenge_encountered':False, 'pages':[], 'questions':[], 'answers':[],
                       'events':[], 'errors':[], 'validations':[], 'screenshots':[],
                       'coverage':{'total':0,'answered':0}}
        self.directory = settings.data_dir/'runs'/self.record['id']

    def event(self, code: str, details: dict) -> None:
        safe = sanitize({'code':code, **details})
        if len(self.record['events']) < 5000:
            self.record['events'].append(safe)
        self.emit('event', safe)

    def transition(self, target: State) -> None:
        validate_transition(self.state, target)
        self.state = target
        self.record['status'] = target.value
        self.event('state', {'state':target.value})
        self.emit('state', target.value)

    async def checkpoint(self) -> None:
        if self.controls.stop.is_set():
            raise asyncio.CancelledError()
        while self.controls.paused.is_set():
            if self.controls.stop.is_set():
                raise asyncio.CancelledError()
            await asyncio.sleep(.05)
        if self.controller.crashed:
            raise RuntimeError('Browser crashed; start a new isolated run')

    async def challenge(self) -> None:
        kind = ('authentication' if self.controller.last_status == 401 else
                'access_denied' if self.controller.last_status == 403 else
                await detect_challenge(self.controller.page))
        if not kind:
            return
        self.record['challenge_encountered'] = True
        self.transition(State.AWAITING_AUTHORIZED_VERIFICATION)
        self.controls.resume.clear()
        self.event('challenge', {'type':kind, 'origin':safe_origin(self.controller.page.url)})
        name = f'challenge-{len(self.record["screenshots"])}.png'
        try:
            await redacted_screenshot(self.controller.page, self.directory/name)
            self.record['screenshots'].append(name)
        except Exception:
            self.event('screenshot_failed', {})
        self.emit('verification', {'headed':self.settings.headed})
        while True:
            await self.checkpoint()
            if self.controls.resume.is_set():
                self.controls.resume.clear()
                TargetPolicy(self.settings).validate(self.controller.page.url)
                if self.controller.last_status not in (401,403) and not await detect_challenge(self.controller.page):
                    self.transition(State.INSPECTING)
                    raise SceneChanged()
                self.event('challenge_still_present', {})
            await asyncio.sleep(.1)

    async def _wait_changed(self, before: str) -> None:
        deadline = time.monotonic() + self.settings.navigation_timeout_ms/1000
        while time.monotonic() < deadline:
            await self.checkpoint()
            try:
                if await fingerprint(self.controller.page) != before:
                    await self.controller.page.wait_for_load_state('domcontentloaded')
                    TargetPolicy(self.settings).validate(self.controller.page.url)
                    return
            except Exception as error:
                if 'Execution context was destroyed' not in str(error):
                    raise
            await asyncio.sleep(.1)
        raise TimeoutError('Next page did not change')

    async def _execute(self, url: str) -> None:
        self.transition(State.INITIALIZING)
        TargetPolicy(self.settings).validate(url)
        await self.checkpoint()
        await self.controller.start()
        self.transition(State.NAVIGATING)
        await self.controller.navigate(url)
        for page_index in range(self.settings.max_pages):
            await self.checkpoint()
            try:
                await self.challenge()
                if self.state != State.INSPECTING:
                    self.transition(State.INSPECTING)
                page = self.controller.page
                TargetPolicy(self.settings).validate(page.url)
                if await page.locator(self.settings.completion_selector).filter(visible=True).count():
                    if self.record['submitted']:
                        self.record['submission_status'] = 'confirmed'
                    self.outcome = State.COMPLETED
                    return
                if self.record['submission_status'] == 'attempted_unconfirmed':
                    raise UnsupportedSurvey('Submission outcome unknown; automatic resubmission is forbidden')
                found = await discover(page, self.settings.next_selector)
                self.emit('questions', [{'key':q.key,'label':q.label,'kind':q.kind,'required':q.required}
                                        for q in found.questions])
                if found.warnings:
                    self.emit('warnings', found.warnings)
                    raise UnsupportedSurvey('Unsupported or ambiguous survey controls')
                if not found.questions and not found.next_selector and not found.submit_selector:
                    raise UnsupportedSurvey('No supported questions or completion marker')
                self.record['pages'].append({'page':page_index,'origin':safe_origin(page.url)})
                expected = await fingerprint(page)
                resolved = [(q, resolve(q, self.profile)) for q in found.questions]
                self.transition(State.READY)
                await self.checkpoint()
                self.transition(State.FILLING)
                for index, (question, answer) in enumerate(resolved):
                    await self.checkpoint()
                    await self.challenge()
                    if await fingerprint(page) != expected:
                        raise UnsupportedSurvey('Survey changed during filling')
                    self.record['questions'].append({'page':page_index,'field':index,
                                                     'kind':question.kind,'required':question.required})
                    self.record['coverage']['total'] += 1
                    if answer is not None:
                        await fill(page, question, answer)
                        self.record['coverage']['answered'] += 1
                    self.record['answers'].append({'page':page_index,'field':index,'mapped':answer is not None})
                self.transition(State.VALIDATING)
                await self.checkpoint()
                await self.challenge()
                validation = await validate_fields(page)
                self.record['validations'].extend({'page':page_index, **v} for v in validation)
                if not all(v['valid'] for v in validation):
                    raise ValueError('Browser field validation failed')
                if found.next_selector:
                    self.transition(State.NAVIGATING)
                    await page.locator(found.next_selector).click()
                    await self._wait_changed(expected)
                    continue
                if not found.submit_selector:
                    raise UnsupportedSurvey('Final submit control is missing or ambiguous')
                if not self.settings.submission_enabled:
                    self.record['submission_status'] = 'validated_without_submission'
                    self.outcome = State.COMPLETED
                    return
                self.controls.approval.clear()
                self.transition(State.AWAITING_SUBMISSION_APPROVAL)
                while not self.controls.approval.is_set():
                    await self.checkpoint()
                    await asyncio.sleep(.1)
                self.controls.approval.clear()
                await self.checkpoint()
                await self.challenge()
                if await fingerprint(page) != expected:
                    raise UnsupportedSurvey('Page changed after submission approval was requested')
                validation = await validate_fields(page)
                if not all(v['valid'] for v in validation):
                    raise ValueError('Validation changed before submission')
                self.transition(State.SUBMITTING)
                self.record['submission_status'] = 'attempted_unconfirmed'
                await page.locator(found.submit_selector).filter(visible=True).click()
                self.record['submitted'] = True
                deadline = time.monotonic() + self.settings.navigation_timeout_ms/1000
                while not await page.locator(self.settings.completion_selector).filter(visible=True).count():
                    await self.checkpoint()
                    await self.challenge()
                    if time.monotonic() > deadline:
                        raise TimeoutError('Submission confirmation was not observed')
                    await asyncio.sleep(.1)
                await self.challenge()
                TargetPolicy(self.settings).validate(page.url)
                self.record['submission_status'] = 'confirmed'
                name = f'final-{len(self.record["screenshots"])}.png'
                await redacted_screenshot(page, self.directory/name)
                self.record['screenshots'].append(name)
                self.outcome = State.COMPLETED
                return
            except SceneChanged:
                # Reinspect after operator handling; never reuse prior answer locations or approval.
                self.controls.approval.clear()
                continue
        raise RuntimeError('Maximum page count reached')

    async def _cancel_monitor(self, task) -> None:
        while not task.done():
            if self.controls.stop.is_set():
                task.cancel()
                return
            await asyncio.sleep(.05)

    async def run(self, url: str) -> dict:
        start = time.monotonic()
        self.directory.mkdir(parents=True, exist_ok=False, mode=0o700)
        task = asyncio.create_task(self._execute(url))
        monitor = asyncio.create_task(self._cancel_monitor(task))
        try:
            await task
        except asyncio.CancelledError:
            self.outcome = State.CANCELLED
        except Exception as error:
            self.record['errors'].append({'code':type(error).__name__})
            self.outcome = State.FAILED
        finally:
            self.record['status'] = self.outcome.value
            monitor.cancel()
            await asyncio.gather(monitor, return_exceptions=True)
            try:
                await asyncio.wait_for(self.controller.close(), timeout=20)
            except Exception:
                self.record['errors'].append({'code':'CleanupFailure'})
                self.record['status'] = State.FAILED.value
            self.record['ended'] = datetime.now(timezone.utc).isoformat()
            self.record['duration_seconds'] = round(time.monotonic()-start,3)
            self.record = sanitize(self.record)
            try:
                export_report(self.record, self.directory)
            except Exception:
                self.record['errors'].append({'code':'ReportWriteFailure'})
                self.record['status'] = State.FAILED.value
            db = None
            try:
                db = Database(self.settings.data_dir/'runs.sqlite3')
                RunRepository(db).save(self.record)
            except Exception:
                self.record['errors'].append({'code':'StorageFailure'})
                self.record['status'] = State.FAILED.value
                # Reflect a storage failure in the independent report if its destination works.
                try:
                    export_report(self.record, self.directory)
                except Exception:
                    pass
            finally:
                if db:
                    db.close()
            validate_transition(self.state, State(self.record['status']))
            self.state = State(self.record['status'])
            self.emit('state',self.state.value)
            self.emit('finished',self.record)
        return self.record
