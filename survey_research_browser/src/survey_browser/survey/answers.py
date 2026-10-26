from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

from pathlib import Path
import json
import math
from datetime import date
from pydantic import BaseModel, ConfigDict, Field
from .models import Question
from ..utils.logging import sensitive_identifier

class AnswerProfile(BaseModel):
    model_config = ConfigDict(extra='forbid', strict=True)
    name: str = Field(min_length=1, max_length=100)
    synthetic: bool
    answers: dict[str, str | int | float | bool | list[str]]

    @classmethod
    def load(cls, path: Path) -> 'AnswerProfile':
        if path.stat().st_size > 262144:
            raise ValueError('Answer profile is too large')
        result = cls.model_validate(json.loads(path.read_text()))
        if not result.synthetic:
            raise ValueError('Only explicitly synthetic test profiles are supported')
        if any(sensitive_identifier(key) for key in result.answers):
            raise ValueError('Profiles must not contain passwords, tokens or session secrets')
        return result


def resolve(question: Question, profile: AnswerProfile) -> object | None:
    if not profile.synthetic:
        raise ValueError('Synthetic profile acknowledgement required')
    if sensitive_identifier(question.key):
        raise ValueError('Sensitive fields must be handled manually')
    if len(question.options) != len(set(question.options)):
        raise ValueError('Duplicate option values are ambiguous')
    value = profile.answers.get(question.key)
    if value is None:
        if question.required:
            raise ValueError('Required question has no explicit answer mapping')
        return None
    if question.kind in ('radio', 'select'):
        if not isinstance(value, str) or value not in question.options:
            raise ValueError('Answer must exactly match an available option value')
        if question.required and not value:
            raise ValueError('Required choice is empty')
    elif question.kind == 'checkbox':
        if isinstance(value, bool) and len(question.options) == 1:
            value = question.options if value else []
        if not isinstance(value, list) or any(x not in question.options for x in value):
            raise ValueError('Checkbox answer must list explicit option values')
        if question.required and not value:
            raise ValueError('Required checkbox is not selected')
    elif question.kind in ('number', 'rating'):
        if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
            raise ValueError('Numeric answer required')
        if question.minimum and value < float(question.minimum):
            raise ValueError('Numeric answer is below minimum')
        if question.maximum and value > float(question.maximum):
            raise ValueError('Numeric answer exceeds maximum')
    elif question.kind == 'date':
        if not isinstance(value, str):
            raise ValueError('ISO date string required')
        date.fromisoformat(value)
    elif not isinstance(value, str) or (question.required and not value.strip()):
        raise ValueError('Text answer required')
    return value

async def fill(page: Page, question: Question, answer: object) -> None:
    target = page.locator(question.selector)
    if question.kind in ('checkbox', 'radio'):
        for index in range(await target.count()):
            control = target.nth(index)
            value = await control.input_value()
            selected = value in answer if question.kind == 'checkbox' else value == answer
            if question.kind == 'checkbox':
                await control.set_checked(selected)
            elif selected:
                await control.check()
    elif question.kind == 'select':
        await target.select_option(str(answer))
    elif question.kind == 'rating':
        await target.evaluate('(e,v)=>{e.value=String(v);e.dispatchEvent(new Event("input",{bubbles:true}));e.dispatchEvent(new Event("change",{bubbles:true}));}', answer)
    else:
        await target.fill(str(answer))
