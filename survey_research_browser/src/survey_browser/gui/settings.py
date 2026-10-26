from PySide6.QtWidgets import (QDialog, QFormLayout, QLineEdit, QCheckBox,
                              QSpinBox, QDialogButtonBox, QMessageBox, QLabel)
from ..configuration import Settings

class SettingsDialog(QDialog):
    def __init__(self, settings: Settings, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle('Authorized targets and settings')
        self.result_settings = settings
        layout = QFormLayout(self)
        notice = QLabel('Authorize only sites you own or have explicit permission to test.\n'
                        'Final submission requires both this opt-in and approval during each run.')
        notice.setWordWrap(True)
        layout.addRow(notice)
        self.domains = QLineEdit(', '.join(settings.allowed_domains))
        self.external = QCheckBox('I authorize these exact external hosts')
        self.external.setChecked(settings.external_enabled)
        self.submission = QCheckBox('Enable requests for final submission approval')
        self.submission.setChecked(settings.submission_enabled)
        self.timeout = QSpinBox()
        self.timeout.setRange(1,120)
        self.timeout.setValue(settings.navigation_timeout_ms//1000)
        self.next_selector = QLineEdit(settings.next_selector)
        self.complete_selector = QLineEdit(settings.completion_selector)
        layout.addRow('Allowed hosts (comma-separated)', self.domains)
        layout.addRow(self.external)
        layout.addRow(self.submission)
        layout.addRow('Navigation timeout (seconds)', self.timeout)
        layout.addRow('Explicit next-page CSS selector',self.next_selector)
        layout.addRow('Completion CSS selector',self.complete_selector)
        buttons = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.save)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

    def save(self) -> None:
        try:
            values = self.result_settings.model_dump()
            values.update(allowed_domains=[x.strip() for x in self.domains.text().split(',')],
                          external_enabled=self.external.isChecked(),
                          submission_enabled=self.submission.isChecked(),
                          navigation_timeout_ms=self.timeout.value()*1000,
                          next_selector=self.next_selector.text(),
                          completion_selector=self.complete_selector.text())
            self.result_settings = Settings.model_validate(values)
        except ValueError:
            QMessageBox.warning(self,'Invalid settings','Use exact hosts without schemes, ports or wildcards, and nonempty selectors.')
            return
        self.accept()
