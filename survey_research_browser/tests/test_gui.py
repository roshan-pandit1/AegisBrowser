import pytest
from PySide6.QtWidgets import QFileDialog, QMessageBox
from survey_browser.gui.main_window import MainWindow

@pytest.mark.gui
def test_window_and_export_cancel(qtbot,settings,tmp_path,monkeypatch):
    window=MainWindow(settings,tmp_path/'settings.json');qtbot.addWidget(window)
    window.show()
    assert window.start_button.isEnabled()
    assert not window.approve_button.isEnabled()
    window.last_record={'id':'test','status':'COMPLETED','screenshots':[]}
    monkeypatch.setattr(QFileDialog,'getExistingDirectory',lambda *args:'')
    window.export()
    assert not (tmp_path/'survey-run-test').exists()

@pytest.mark.gui
def test_export_failure_is_displayed(qtbot,settings,tmp_path,monkeypatch):
    window=MainWindow(settings,tmp_path/'settings.json');qtbot.addWidget(window)
    window.last_record={'id':'test','status':'COMPLETED','screenshots':[]}
    path=tmp_path/'not-directory';path.write_text('unchanged')
    monkeypatch.setattr(QFileDialog,'getExistingDirectory',lambda *args:str(path))
    warnings=[]
    monkeypatch.setattr(QMessageBox,'warning',lambda *args:warnings.append(args[1]))
    window.export()
    assert warnings==['Export failed']
