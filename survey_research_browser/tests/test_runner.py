import asyncio
import json
import pytest
from survey_browser.survey.runner import SurveyRunner, Controls
from survey_browser.survey.models import State

@pytest.mark.browser
def test_dry_run_never_submits(settings,profile,site):
    result=asyncio.run(SurveyRunner(settings,profile).run(site+'/survey.html'))
    assert result['status']=='COMPLETED'
    assert not result['submitted']
    assert result['submission_status']=='validated_without_submission'
    assert result['coverage']=={'total':8,'answered':8}

@pytest.mark.browser
def test_submission_approval_and_multipage(settings,profile,site):
    settings=type(settings).model_validate({**settings.model_dump(),'submission_enabled':True})
    async def check():
        controls=Controls()
        states=[]
        runner=SurveyRunner(settings,profile,lambda kind,payload:states.append(payload) if kind=='state' else None,controls)
        task=asyncio.create_task(runner.run(site+'/multipage_survey.html'))
        async def wait():
            while runner.state != State.AWAITING_SUBMISSION_APPROVAL:
                assert not task.done(), task.result() if task.done() else ''
                await asyncio.sleep(.05)
        await asyncio.wait_for(wait(),15)
        await asyncio.sleep(.2)
        assert not task.done() and not runner.record['submitted']
        controls.approval.set()
        result=await asyncio.wait_for(task,15)
        assert result['status']=='COMPLETED' and result['submission_status']=='confirmed'
        assert len(result['pages'])==2
        assert State.SUBMITTING.value in states
    asyncio.run(check())

@pytest.mark.browser
def test_challenge_requires_recheck_then_can_continue(settings,profile,site):
    async def check():
        controls=Controls()
        runner=SurveyRunner(settings,profile,controls=controls)
        task=asyncio.create_task(runner.run(site+'/challenge.html'))
        async def wait_for_pause():
            while runner.state != State.AWAITING_AUTHORIZED_VERIFICATION:
                assert not task.done()
                await asyncio.sleep(.05)
        await asyncio.wait_for(wait_for_pause(),15)
        controls.resume.set()
        await asyncio.sleep(.3)
        assert runner.state==State.AWAITING_AUTHORIZED_VERIFICATION
        # This is a local mock operator action, not a real CAPTCHA solver.
        await runner.controller.page.get_by_role('button',name='Authorized operator: clear fixture challenge').click()
        controls.resume.set()
        result=await asyncio.wait_for(task,15)
        assert result['status']=='COMPLETED' and result['challenge_encountered']
        assert result['screenshots']
    asyncio.run(check())

@pytest.mark.browser
def test_cancel_at_approval_cleans_context(settings,profile,site):
    settings=type(settings).model_validate({**settings.model_dump(),'submission_enabled':True})
    async def check():
        controls=Controls()
        runner=SurveyRunner(settings,profile,controls=controls)
        task=asyncio.create_task(runner.run(site+'/survey.html'))
        async def wait():
            while runner.state != State.AWAITING_SUBMISSION_APPROVAL:
                assert not task.done()
                await asyncio.sleep(.05)
        await asyncio.wait_for(wait(),15)
        controls.stop.set()
        result=await asyncio.wait_for(task,10)
        assert result['status']=='CANCELLED' and not result['submitted']
        assert not runner.controller.browser.is_connected()
    asyncio.run(check())

def test_failure_cleanup_and_report(settings,profile):
    class BrokenBrowser:
        crashed=False
        closed=False
        def __init__(self,*args): pass
        async def start(self): raise RuntimeError('token=DO_NOT_PERSIST')
        async def close(self): self.closed=True
    runner=SurveyRunner(settings,profile,controller_factory=BrokenBrowser)
    result=asyncio.run(runner.run('http://localhost'))
    assert result['status']=='FAILED' and runner.controller.closed
    assert 'DO_NOT_PERSIST' not in json.dumps(result)
    assert (runner.directory/'report.json').is_file()

def test_illegal_transition(settings,profile):
    runner=SurveyRunner(settings,profile)
    with pytest.raises(RuntimeError):
        runner.transition(State.SUBMITTING)

def test_cancel_before_launch(settings,profile):
    controls=Controls();controls.stop.set()
    runner=SurveyRunner(settings,profile,controls=controls)
    result=asyncio.run(runner.run('http://localhost'))
    assert result['status']=='CANCELLED'


def test_cancel_during_startup(settings, profile):
    class SlowBrowser:
        crashed = False
        closed = False
        def __init__(self, *args):
            pass
        async def start(self):
            await asyncio.sleep(30)
        async def close(self):
            self.closed = True
    async def check():
        controls = Controls()
        runner = SurveyRunner(settings, profile, controls=controls, controller_factory=SlowBrowser)
        task = asyncio.create_task(runner.run('http://localhost'))
        await asyncio.sleep(.1)
        controls.stop.set()
        result = await asyncio.wait_for(task, 3)
        assert result['status'] == 'CANCELLED' and runner.controller.closed
    asyncio.run(check())


def test_report_failure_is_not_success(settings, profile, monkeypatch):
    import survey_browser.survey.runner as module
    class BrokenBrowser:
        crashed = False
        def __init__(self, *args):
            pass
        async def start(self):
            raise RuntimeError('expected fixture failure')
        async def close(self):
            pass
    def no_write(*args):
        raise PermissionError('unwritable')
    monkeypatch.setattr(module, 'export_report', no_write)
    result = asyncio.run(SurveyRunner(settings, profile, controller_factory=BrokenBrowser).run('http://localhost'))
    assert result['status'] == 'FAILED'
    assert {'code': 'ReportWriteFailure'} in result['errors']


@pytest.mark.browser
def test_repeated_runs_create_new_contexts(settings, profile, site):
    async def check():
        first = SurveyRunner(settings, profile)
        result1 = await first.run(site+'/survey.html')
        second = SurveyRunner(settings, profile)
        result2 = await second.run(site+'/survey.html')
        assert result1['status'] == result2['status'] == 'COMPLETED'
        assert result1['id'] != result2['id']
        assert not first.controller.browser.is_connected()
        assert not second.controller.browser.is_connected()
    asyncio.run(check())
