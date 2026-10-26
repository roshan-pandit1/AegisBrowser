"""Dependency-free checks; also collected by pytest when dependencies are installed."""
import hashlib
import json
import socket
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from survey_browser.security import TargetPolicy, AuthorizationError, safe_origin
from survey_browser.reporting.reporter import export_report, sanitize
from survey_browser.utils.logging import redact, sensitive_identifier
from survey_browser.survey.models import State, Question


def policy(hosts=('localhost', '127.0.0.1'), external=False):
    return TargetPolicy(SimpleNamespace(allowed_domains=hosts, external_enabled=external))


class SecurityChecks(unittest.TestCase):
    def test_default_local_targets(self):
        for host in ('localhost', '127.0.0.1'):
            self.assertEqual(policy().validate(f'http://{host}:8000/a'), (host, 8000))

    def test_unsafe_urls_rejected(self):
        for target in ('https://external.example', 'file:///etc/passwd', 'javascript:x',
                       'http://u:secret@localhost', 'http://localhost.evil', 'http://localhost:0',
                       'http://localhost:99999', 'http://localhost\n', 'http://localhost\\evil'):
            with self.subTest(target=target), self.assertRaises(AuthorizationError):
                policy().validate(target)

    def test_external_requires_both_host_and_opt_in(self):
        with self.assertRaises(AuthorizationError):
            policy(('owned.example',)).validate('https://owned.example')
        self.assertEqual(policy(('owned.example',), True).validate('https://owned.example'),
                         ('owned.example', 443))

    def test_redirect_validation(self):
        self.assertEqual(policy().validate_redirect('http://localhost/one', '/two'),
                         'http://localhost/two')
        with self.assertRaises(AuthorizationError):
            policy().validate_redirect('http://localhost/one', '//unapproved.example/two')

    def test_private_external_dns_denied(self):
        for address in ('127.0.0.1', '::1', '10.0.0.1', '192.168.0.1', '169.254.169.254',
                        '100.64.0.1', '0.0.0.0', '::ffff:127.0.0.1'):
            with self.subTest(address=address), patch.object(socket, 'getaddrinfo', return_value=[
                    (0, 0, 0, '', (address, 443))]), self.assertRaises(AuthorizationError):
                policy(('owned.example',), True).resolve('https://owned.example')

    def test_mixed_dns_denied(self):
        with patch.object(socket, 'getaddrinfo', return_value=[
            (0, 0, 0, '', ('93.184.216.34', 443)), (0, 0, 0, '', ('10.0.0.1', 443))]):
            with self.assertRaises(AuthorizationError):
                policy(('owned.example',), True).resolve('https://owned.example')

    def test_public_destination_numeric_pin(self):
        with patch.object(socket, 'getaddrinfo', return_value=[(0, 0, 0, '', ('93.184.216.34', 443))]):
            self.assertEqual(policy(('owned.example',), True).resolve('https://owned.example'),
                             ('93.184.216.34', 443))

    def test_localhost_dns_cannot_escape(self):
        with patch.object(socket, 'getaddrinfo', return_value=[(0, 0, 0, '', ('93.184.216.34', 80))]):
            with self.assertRaises(AuthorizationError):
                policy().resolve('http://localhost')

    def test_url_redaction(self):
        self.assertEqual(safe_origin('https://u:secret@example.com/private/token?secret=x#secret'),
                         'https://example.com')


class StateChecks(unittest.TestCase):
    def test_explicit_happy_path(self):
        from survey_browser.survey.state import validate_transition
        path = [State.IDLE, State.INITIALIZING, State.NAVIGATING, State.INSPECTING,
                State.READY, State.FILLING, State.VALIDATING,
                State.AWAITING_SUBMISSION_APPROVAL, State.SUBMITTING, State.COMPLETED]
        for before, after in zip(path, path[1:]):
            validate_transition(before, after)

    def test_no_submission_shortcut(self):
        from survey_browser.survey.state import validate_transition
        for state in (State.IDLE, State.READY, State.FILLING, State.VALIDATING,
                      State.AWAITING_AUTHORIZED_VERIFICATION):
            with self.subTest(state=state), self.assertRaises(RuntimeError):
                validate_transition(state, State.SUBMITTING)

    def test_terminal_states_are_final(self):
        from survey_browser.survey.state import validate_transition
        for state in (State.COMPLETED, State.FAILED, State.CANCELLED):
            for target in State:
                with self.subTest(state=state, target=target), self.assertRaises(RuntimeError):
                    validate_transition(state, target)

    def test_challenge_requires_reinspection(self):
        from survey_browser.survey.state import validate_transition
        validate_transition(State.FILLING, State.AWAITING_AUTHORIZED_VERIFICATION)
        validate_transition(State.AWAITING_AUTHORIZED_VERIFICATION, State.INSPECTING)
        with self.assertRaises(RuntimeError):
            validate_transition(State.AWAITING_AUTHORIZED_VERIFICATION, State.SUBMITTING)


class ReportChecks(unittest.TestCase):
    def test_redaction_omits_arbitrary_messages(self):
        value = {'password': 'secret', 'events': [{'code': 'state', 'message': 'secret'}],
                 'answers': [{'value': 'private', 'mapped': True}]}
        result = json.dumps(sanitize(value))
        self.assertNotIn('secret', result)
        self.assertNotIn('private', result)
        self.assertIn('mapped', result)

    def test_reports_are_real_files(self):
        with tempfile.TemporaryDirectory() as directory:
            j, h = export_report({'status': 'COMPLETED', 'password': 'SECRET'}, Path(directory))
            self.assertEqual(json.loads(j.read_text())['status'], 'COMPLETED')
            self.assertIn('Synthetic survey test report', h.read_text())
            self.assertNotIn('SECRET', h.read_text())
            self.assertEqual(len(hashlib.sha256(j.read_bytes()).hexdigest()), 64)

    def test_failed_export_does_not_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'existing'; path.write_text('original')
            with self.assertRaises(OSError):
                export_report({'status': 'FAILED'}, path)
            self.assertEqual(path.read_text(), 'original')

    def test_report_html_escapes_content(self):
        with tempfile.TemporaryDirectory() as directory:
            _, h = export_report({'code': '<script>alert(1)</script>'}, Path(directory))
            self.assertNotIn('<script>', h.read_text())

    def test_screenshot_path_traversal_omitted(self):
        result = sanitize({'screenshots':['../../secret.png','challenge-0.png']})
        self.assertEqual(result['screenshots'], ['[omitted]', 'challenge-0.png'])

    def test_logging_redaction(self):
        self.assertTrue(sensitive_identifier('api_key'))
        self.assertEqual(redact({'token': 'secret', 'text': 'private'}),
                         {'token': '[REDACTED]', 'text': '[text omitted]'})

    def test_state_and_question_models(self):
        self.assertEqual({s.value for s in State}, {'IDLE','INITIALIZING','NAVIGATING','INSPECTING','READY','FILLING','VALIDATING','AWAITING_AUTHORIZED_VERIFICATION','AWAITING_SUBMISSION_APPROVAL','SUBMITTING','COMPLETED','FAILED','CANCELLED'})
        self.assertFalse(Question('a','A','text','input').required)


if __name__ == '__main__':
    unittest.main()
