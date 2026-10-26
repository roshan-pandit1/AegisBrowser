"""Diagnostics deliberately omit arbitrary page text and exception messages."""
import logging
import re

SECRET = re.compile(r'(?i)(password|passwd|secret|token|api.?key|authorization|cookie|session)')

def sensitive_identifier(value: str) -> bool:
    return bool(SECRET.search(value))

def redact(value: object) -> object:
    if isinstance(value, dict):
        return {str(k): '[REDACTED]' if SECRET.search(str(k)) else redact(v)
                for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [redact(v) for v in value]
    if isinstance(value, str):
        # Arbitrary text cannot be reliably scrubbed: never write it to persistent diagnostics.
        return '[text omitted]'
    return value

class SafeFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        # Never format arguments, exception traces or vendor/browser messages.
        return f'{record.levelname}: diagnostic event ({record.name})'

def configure_logging() -> None:
    handler = logging.StreamHandler()
    handler.setFormatter(SafeFormatter())
    logging.basicConfig(level=logging.INFO, handlers=[handler], force=True)
