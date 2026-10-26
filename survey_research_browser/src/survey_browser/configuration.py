from pathlib import Path
import json
import os
import tempfile
import yaml
from pydantic import BaseModel, ConfigDict, Field, StrictBool, field_validator

class Settings(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    allowed_domains: tuple[str, ...] = ("localhost", "127.0.0.1")
    external_enabled: StrictBool = False
    submission_enabled: StrictBool = False
    headed: StrictBool = True
    navigation_timeout_ms: int = Field(default=30000, ge=1000, le=120000)
    action_timeout_ms: int = Field(default=10000, ge=500, le=60000)
    max_pages: int = Field(default=20, ge=1, le=100)
    completion_selector: str = '[data-survey-complete="true"]'
    next_selector: str = '[data-survey-next="true"]'
    data_dir: Path = Path.home() / '.survey-research-browser'

    @field_validator('allowed_domains')
    @classmethod
    def domains(cls, values: tuple[str, ...]) -> tuple[str, ...]:
        import ipaddress
        result = []
        for value in values:
            value = value.strip().lower().rstrip('.')
            if not value or any(x in value for x in '/@*?#%\\'):
                raise ValueError('Use exact host names or IP addresses, without ports or wildcards')
            try:
                ipaddress.ip_address(value)
            except ValueError:
                if ':' in value or any(not (c.isalnum() or c in '.-') for c in value):
                    raise ValueError('Invalid host name')
                value = value.encode('idna').decode('ascii')
            result.append(value)
        if not result:
            raise ValueError('At least one authorized host is required')
        return tuple(dict.fromkeys(result))

    @field_validator('completion_selector', 'next_selector')
    @classmethod
    def selectors(cls, value: str) -> str:
        if not value.strip() or len(value) > 500:
            raise ValueError('Provide a nonempty, specific CSS selector')
        return value

    @classmethod
    def load(cls, path: Path) -> 'Settings':
        if not path.exists():
            return cls()
        if path.stat().st_size > 65536:
            raise ValueError('Configuration is too large')
        return cls.model_validate(yaml.safe_load(path.read_text()) or {})

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd, tmp = tempfile.mkstemp(dir=path.parent, prefix='.settings-')
        try:
            with os.fdopen(fd, 'w') as stream:
                stream.write(json.dumps(self.model_dump(mode='json'), indent=2))
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(tmp, path)
        finally:
            Path(tmp).unlink(missing_ok=True)
