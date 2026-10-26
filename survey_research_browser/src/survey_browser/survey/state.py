from .models import State

TERMINAL = {State.COMPLETED, State.FAILED, State.CANCELLED}
TRANSITIONS = {
    State.IDLE: {State.INITIALIZING},
    State.INITIALIZING: {State.NAVIGATING},
    State.NAVIGATING: {State.INSPECTING, State.AWAITING_AUTHORIZED_VERIFICATION},
    State.INSPECTING: {State.READY, State.COMPLETED, State.AWAITING_AUTHORIZED_VERIFICATION},
    State.READY: {State.FILLING},
    State.FILLING: {State.VALIDATING, State.AWAITING_AUTHORIZED_VERIFICATION},
    State.VALIDATING: {State.NAVIGATING, State.AWAITING_SUBMISSION_APPROVAL, State.COMPLETED,
                       State.AWAITING_AUTHORIZED_VERIFICATION},
    State.AWAITING_AUTHORIZED_VERIFICATION: {State.INSPECTING},
    State.AWAITING_SUBMISSION_APPROVAL: {State.SUBMITTING, State.AWAITING_AUTHORIZED_VERIFICATION},
    State.SUBMITTING: {State.COMPLETED, State.AWAITING_AUTHORIZED_VERIFICATION},
}


def validate_transition(current: State, target: State) -> None:
    if current in TERMINAL or (target not in (State.FAILED, State.CANCELLED)
                              and target not in TRANSITIONS.get(current, set())):
        raise RuntimeError('Invalid state transition')
