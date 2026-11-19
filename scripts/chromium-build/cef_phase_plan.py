"""Describe the pinned CEF patch order; this is not operation evidence.

The pinned automator calls `apply_patch('runhooks')` before gclient runhooks.
Later `gclient_hook.py` invokes patcher on the *entire* configured sequence,
including the early patch. The patcher may skip an already-applied patch after a
reverse check; this planner cannot establish whether that check succeeded.
"""


class PhasePlanError(ValueError):
    pass


def make_phase_plan(entries):
    """Return the early and later plan, rejecting ambiguous input.

    `entries` is the already parsed, independently authenticated patch plan,
    with `name`, `target` and boolean `enabled`. This function does not parse
    configuration, verify patch bytes, run hooks, compare files, or approve a
    compiler invocation.
    """
    if not isinstance(entries, list) or not entries:
        raise PhasePlanError('EMPTY_OR_INVALID_PATCH_PLAN')
    seen = set()
    later = []
    early = None
    for entry in entries:
        if not isinstance(entry, dict):
            raise PhasePlanError('INVALID_PATCH_ENTRY')
        name, target, enabled = entry.get('name'), entry.get('target'), entry.get('enabled')
        if not isinstance(name, str) or not name or not isinstance(target, str) or type(enabled) is not bool:
            raise PhasePlanError('INCOMPLETE_PATCH_ENTRY')
        if name in seen:
            raise PhasePlanError('DUPLICATE_PATCH_ENTRY')
        seen.add(name)
        if name == 'runhooks':
            # Upstream calls this by name with the default Chromium root,
            # without consulting a condition in patch.cfg.
            if target or not enabled:
                raise PhasePlanError('EARLY_PATCH_CONFIGURATION_UNSUPPORTED')
            early = {'name': name, 'target': target, 'action': 'APPLY_BEFORE_GCLIENT_RUNHOOKS'}
        later.append({'name': name, 'target': target, 'enabled': enabled,
                      'action': 'CHECK_OR_APPLY' if enabled else 'CONDITION_SKIPPED'})
    if early is None:
        raise PhasePlanError('EARLY_PATCH_MISSING_FROM_CONFIG')
    return {'schema': 'aegis-cef-phase-plan-v1', 'status': 'INCONCLUSIVE',
            'early': [early], 'later': later, 'hooks_completed': 'NOT_ESTABLISHED',
            'compiler_input_provenance': 'NOT_ESTABLISHED'}
