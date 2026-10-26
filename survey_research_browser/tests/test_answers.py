import pytest
from survey_browser.survey.answers import resolve, AnswerProfile
from survey_browser.survey.models import Question

def q(kind,**kwargs):
    return Question('test','Test',kind,'input',**kwargs)

def p(value):
    return AnswerProfile(name='Synthetic',synthetic=True,answers={'test':value})

@pytest.mark.parametrize('kind,value,options',[('radio','b',['a','b']),('select','a',['a','b']),('checkbox',['a'],['a','b']),('text','test',[]),('number',3,[]),('date','2026-01-01',[])])
def test_explicit_answers(kind,value,options):
    assert resolve(q(kind,options=options),p(value)) == value

def test_required_unmapped():
    with pytest.raises(ValueError):
        resolve(q('text',required=True),AnswerProfile(name='Test',synthetic=True,answers={}))

@pytest.mark.parametrize('kind,value,options',[('radio','unknown',['a']),('checkbox',['unknown'],['a']),('number',float('nan'),[]),('date','not-a-date',[]),('text',3,[])])
def test_invalid_answers(kind,value,options):
    with pytest.raises(ValueError):
        resolve(q(kind,options=options),p(value))

def test_profile_rejects_credentials(tmp_path):
    f=tmp_path/'p.json';f.write_text('{"name":"QA","synthetic":true,"answers":{"password":"secret"}}')
    with pytest.raises(ValueError):
        AnswerProfile.load(f)


def test_duplicate_choice_values_rejected():
    with pytest.raises(ValueError):
        resolve(q('radio', options=['on','on']), p('on'))
