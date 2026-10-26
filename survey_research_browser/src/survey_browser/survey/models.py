from dataclasses import dataclass, field
from enum import StrEnum

class State(StrEnum):
    IDLE = 'IDLE'
    INITIALIZING = 'INITIALIZING'
    NAVIGATING = 'NAVIGATING'
    INSPECTING = 'INSPECTING'
    READY = 'READY'
    FILLING = 'FILLING'
    VALIDATING = 'VALIDATING'
    AWAITING_AUTHORIZED_VERIFICATION = 'AWAITING_AUTHORIZED_VERIFICATION'
    AWAITING_SUBMISSION_APPROVAL = 'AWAITING_SUBMISSION_APPROVAL'
    SUBMITTING = 'SUBMITTING'
    COMPLETED = 'COMPLETED'
    FAILED = 'FAILED'
    CANCELLED = 'CANCELLED'

@dataclass
class Question:
    key: str
    label: str
    kind: str
    selector: str
    required: bool = False
    options: list[str] = field(default_factory=list)
    minimum: str = ''
    maximum: str = ''

@dataclass
class Discovery:
    questions: list[Question]
    warnings: list[str]
    next_selector: str | None = None
    submit_selector: str | None = None
