from pathlib import Path
from sqlalchemy import create_engine, String, Text, ForeignKey
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

class Base(DeclarativeBase):
    pass

class Run(Base):
    __tablename__ = 'runs'
    id: Mapped[str] = mapped_column(String, primary_key=True)
    status: Mapped[str] = mapped_column(String)
    record: Mapped[str] = mapped_column(Text)

class PageRecord(Base):
    __tablename__ = 'pages'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class QuestionRecord(Base):
    __tablename__ = 'questions'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class TestAnswer(Base):
    __tablename__ = 'test_answers'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class Event(Base):
    __tablename__ = 'events'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class Error(Base):
    __tablename__ = 'errors'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class Report(Base):
    __tablename__ = 'reports'
    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[str] = mapped_column(ForeignKey('runs.id'))
    record: Mapped[str] = mapped_column(Text)

class Database:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        # Create privately before SQLite opens it; no credentials or raw answers are stored.
        if not path.exists():
            path.touch(mode=0o600)
        self.engine = create_engine('sqlite:///' + str(path.resolve()))
        Base.metadata.create_all(self.engine)
        self.session = sessionmaker(self.engine)

    def close(self) -> None:
        self.engine.dispose()
