import json
from .database import Database, Run, PageRecord, QuestionRecord, TestAnswer, Event, Error, Report

class RunRepository:
    def __init__(self, database: Database) -> None:
        self.database = database

    def save(self, record: dict) -> None:
        with self.database.session.begin() as session:
            session.add(Run(id=record['id'], status=record['status'], record=json.dumps(record)))
            for cls, name in ((PageRecord, 'pages'), (QuestionRecord, 'questions'),
                              (TestAnswer, 'answers'), (Event, 'events'), (Error, 'errors')):
                for item in record[name]:
                    session.add(cls(run_id=record['id'], record=json.dumps(item)))
            session.add(Report(run_id=record['id'], record=json.dumps(record)))

    def recent(self, limit: int = 50) -> list[dict]:
        from sqlalchemy import select
        with self.database.session() as session:
            return [json.loads(row.record) for row in session.scalars(select(Run).order_by(Run.id.desc()).limit(limit))]
