"""Synthetic order tests only; no CEF hooks or compiler are executed."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('cef_phase_plan', Path(__file__).with_name('cef_phase_plan.py'))
phase = importlib.util.module_from_spec(spec)
spec.loader.exec_module(phase)


class PhasePlanTests(unittest.TestCase):
    def entries(self):
        return [dict(name='first', target='', enabled=True),
                dict(name='runhooks', target='', enabled=True),
                dict(name='v8_build', target='v8', enabled=True),
                dict(name='optional', target='', enabled=False)]

    def test_early_and_later_order_are_distinct(self):
        result = phase.make_phase_plan(self.entries())
        self.assertEqual([x['name'] for x in result['early']], ['runhooks'])
        self.assertEqual([x['name'] for x in result['later']],
                         ['first', 'runhooks', 'v8_build', 'optional'])
        self.assertEqual(result['later'][1]['action'], 'CHECK_OR_APPLY')
        self.assertEqual(result['later'][3]['action'], 'CONDITION_SKIPPED')
        self.assertEqual(result['status'], 'INCONCLUSIVE')
        self.assertEqual(result['compiler_input_provenance'], 'NOT_ESTABLISHED')

    def test_missing_early_patch_rejected(self):
        with self.assertRaisesRegex(phase.PhasePlanError, 'EARLY_PATCH_MISSING'):
            phase.make_phase_plan(self.entries()[:1])

    def test_duplicate_patch_rejected(self):
        entries = self.entries() + [self.entries()[0]]
        with self.assertRaisesRegex(phase.PhasePlanError, 'DUPLICATE'):
            phase.make_phase_plan(entries)

    def test_disabled_early_patch_is_unsupported(self):
        entries = self.entries()
        entries[1]['enabled'] = False
        with self.assertRaisesRegex(phase.PhasePlanError, 'EARLY_PATCH_CONFIGURATION_UNSUPPORTED'):
            phase.make_phase_plan(entries)

    def test_non_root_early_patch_is_unsupported(self):
        entries = self.entries()
        entries[1]['target'] = 'v8'
        with self.assertRaisesRegex(phase.PhasePlanError, 'EARLY_PATCH_CONFIGURATION_UNSUPPORTED'):
            phase.make_phase_plan(entries)

    def test_missing_or_non_boolean_condition_rejected(self):
        entries = self.entries()
        entries[1]['enabled'] = 'false'
        with self.assertRaisesRegex(phase.PhasePlanError, 'INCOMPLETE'):
            phase.make_phase_plan(entries)

    def test_empty_and_malformed_lists_rejected(self):
        for value in ([], None, {}, [None]):
            with self.subTest(value=value), self.assertRaises(phase.PhasePlanError):
                phase.make_phase_plan(value)


if __name__ == '__main__':
    unittest.main()
