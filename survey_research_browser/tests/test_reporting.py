import json
import pytest
from survey_browser.reporting.reporter import export_report
from survey_browser.storage.database import Database
from survey_browser.storage.repositories import RunRepository

def test_reports_redact_and_escape(tmp_path):
    record={'id':'123','status':'COMPLETED','password':'secret','answers':[{'value':'private','mapped':True}],
            'events':[{'code':'event','origin':'https://user:secret@example.com/token?password=secret'}],
            'errors':[{'message':'secret <script>alert(1)</script>','code':'ValueError'}]}
    j,h=export_report(record,tmp_path)
    assert 'secret' not in j.read_text() and '<script>' not in h.read_text()
    assert json.loads(j.read_text())['status']=='COMPLETED'

def test_export_write_failure(tmp_path):
    path=tmp_path/'file';path.write_text('unchanged')
    with pytest.raises(OSError):
        export_report({'status':'FAILED'},path)
    assert path.read_text()=='unchanged'

def test_database_roundtrip(tmp_path):
    db=Database(tmp_path/'runs.db')
    record={'id':'run','status':'COMPLETED','pages':[],'questions':[], 'answers':[], 'events':[], 'errors':[]}
    try:
        repository=RunRepository(db);repository.save(record)
        assert repository.recent()==[record]
    finally:
        db.close()
