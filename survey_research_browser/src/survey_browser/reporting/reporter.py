import html
import json
import os
import tempfile
from pathlib import Path

# Only runner-generated structured fields are accepted. Arbitrary DOM, answers and exception
# messages do not belong in this schema. Exports must use sanitize, even for imported records.
ALLOWED = {'id','status','started','ended','duration_seconds','submitted','submission_status',
           'challenge_encountered','pages','questions','answers','events','errors','validations',
           'screenshots','coverage','page','field','kind','mapped','valid','required','code',
           'origin','http_status','duration_ms','total','answered','state','type'}
SAFE_TEXT = {
    'status': {'IDLE','INITIALIZING','NAVIGATING','INSPECTING','READY','FILLING','VALIDATING',
               'AWAITING_AUTHORIZED_VERIFICATION','AWAITING_SUBMISSION_APPROVAL','SUBMITTING',
               'COMPLETED','FAILED','CANCELLED'},
    'submission_status': {'not_submitted','validated_without_submission','attempted_unconfirmed','confirmed'},
    'kind': {'text','textarea','number','date','rating','checkbox','radio','select'},
    'code': {'state','challenge','challenge_still_present','screenshot_failed','console','script_error',
             'network_failure','response','request_finished','blocked_target','popup_closed','browser_crash',
             'cleanup_error','navigation_retry','Error','TimeoutError','RuntimeError','ValueError',
             'UnsupportedSurvey','AuthorizationError','CleanupFailure','StorageFailure','ReportWriteFailure',
             'OSError','PermissionError','FileNotFoundError','TargetClosedError'},
    'type': {'authentication','access_denied','verification','dns','tls','timeout','blocked','connection',
             'log','debug','info','error','warning','dir','dirxml','table','trace','clear','assert',
             'startGroup','startGroupCollapsed','endGroup','count','timeEnd'}
}
SAFE_TEXT['state'] = SAFE_TEXT['status']

def sanitize(value: object, key: str = '') -> object:
    from ..security import safe_origin
    if isinstance(value, dict):
        return {k: sanitize(v,k) for k,v in value.items() if k in ALLOWED}
    if isinstance(value, list):
        return [sanitize(v,key) for v in value]
    if isinstance(value, str):
        if key == 'origin':
            return safe_origin(value)
        if key == 'screenshots':
            import re
            return value if re.fullmatch(r'(challenge|final)-[0-9]+\.png', value) else '[omitted]'
        if key in SAFE_TEXT:
            return value if value in SAFE_TEXT[key] else '[omitted]'
        if key == 'id':
            import uuid
            try:
                return str(uuid.UUID(value))
            except ValueError:
                return '[omitted]'
        if key in ('started','ended'):
            from datetime import datetime
            try:
                return datetime.fromisoformat(value).isoformat()
            except ValueError:
                return '[omitted]'
        return '[omitted]'
    return value if isinstance(value, (int,float,bool)) or value is None else '[omitted]'

def _atomic(path: Path, text: str) -> None:
    fd, temporary = tempfile.mkstemp(dir=path.parent, prefix='.report-')
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)

def export_report(record: dict, directory: Path) -> tuple[Path, Path]:
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    clean = sanitize(record)
    payload = json.dumps(clean, indent=2, allow_nan=False)
    json_file, html_file = directory/'report.json', directory/'report.html'
    body = ('<!doctype html><html lang="en"><meta charset="utf-8">'
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">"
            '<title>Survey Research Browser report</title>'
            '<style>body{font:16px system-ui;max-width:960px;margin:40px auto;padding:20px;color:#18253a}pre{white-space:pre-wrap}</style>'
            '<h1>Synthetic survey test report</h1><p>Authorized testing only. No real participant claim.</p>'
            '<pre>' + html.escape(payload) + '</pre></html>')
    _atomic(json_file, payload)
    _atomic(html_file, body)
    return json_file, html_file
