import pytest
from survey_browser.configuration import Settings

def test_safe_defaults():
    s=Settings()
    assert not s.external_enabled and not s.submission_enabled
    assert s.allowed_domains == ('localhost','127.0.0.1')

def test_settings_roundtrip(tmp_path):
    path=tmp_path/'config.json'
    Settings(data_dir=tmp_path).save(path)
    assert Settings.load(path).data_dir == tmp_path

@pytest.mark.parametrize('host',['*.example.com','https://example.com','example.com:443','', 'user@example.com'])
def test_invalid_domains(host):
    with pytest.raises(ValueError):
        Settings(allowed_domains=(host,))

def test_corrupt_settings_not_overwritten(tmp_path):
    path=tmp_path/'settings.json'
    path.write_text('{broken')
    with pytest.raises(Exception):
        Settings.load(path)
    assert path.read_text() == '{broken'
