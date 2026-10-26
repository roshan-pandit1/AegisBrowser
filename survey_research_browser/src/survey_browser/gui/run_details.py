import json
from PySide6.QtWidgets import QDialog, QVBoxLayout, QPlainTextEdit, QDialogButtonBox
from ..reporting.reporter import sanitize

class RunDetails(QDialog):
    def __init__(self, record: dict, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle('Sanitized run details')
        self.resize(720,520)
        layout = QVBoxLayout(self)
        text = QPlainTextEdit(json.dumps(sanitize(record),indent=2))
        text.setReadOnly(True)
        layout.addWidget(text)
        buttons = QDialogButtonBox(QDialogButtonBox.Close)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
