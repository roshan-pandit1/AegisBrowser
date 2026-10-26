import asyncio
import json
import shutil
from pathlib import Path
from PySide6.QtCore import QThread, Signal, Slot, QTimer
from PySide6.QtWidgets import (QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QFormLayout,
    QLineEdit, QPushButton, QCheckBox, QComboBox, QLabel, QPlainTextEdit, QTableWidget,
    QTableWidgetItem, QSplitter, QFileDialog, QMessageBox, QHeaderView)
from ..configuration import Settings
from ..security import TargetPolicy
from ..survey.answers import AnswerProfile
from ..survey.runner import SurveyRunner, Controls
from ..survey.models import State
from ..reporting.reporter import export_report
from .settings import SettingsDialog
from .run_details import RunDetails

class RunWorker(QThread):
    update = Signal(str, object)
    def __init__(self, settings: Settings, profile: AnswerProfile, url: str, parent=None) -> None:
        super().__init__(parent)
        self.settings,self.profile,self.url = settings,profile,url
        self.controls = Controls()

    def run(self) -> None:
        try:
            runner = SurveyRunner(self.settings,self.profile,self.update.emit,self.controls)
            asyncio.run(runner.run(self.url))
        except Exception as error:
            self.update.emit('fatal',type(error).__name__)

class MainWindow(QMainWindow):
    def __init__(self, settings: Settings, config_path: Path) -> None:
        super().__init__()
        self.settings,self.config_path = settings,config_path
        self.worker = None
        self.last_record = None
        self.last_directory = None
        self.closing = False
        self.current_state = State.IDLE.value
        self.profiles: list[AnswerProfile] = []
        self.setWindowTitle('Survey Research Browser — authorized synthetic testing')
        self.resize(1080,760)
        root = QWidget()
        layout = QVBoxLayout(root)
        banner = QLabel('Synthetic test runs only • Exact authorized hosts • No automated challenge handling')
        banner.setWordWrap(True)
        layout.addWidget(banner)
        form = QFormLayout()
        self.url = QLineEdit('http://127.0.0.1:8000/survey.html')
        self.url.setAccessibleName('Target survey URL')
        self.allowed = QLabel(', '.join(settings.allowed_domains))
        self.allowed.setWordWrap(True)
        self.profile_combo = QComboBox()
        self.load_profile_button = QPushButton('Load synthetic JSON profile…')
        self.load_profile_button.clicked.connect(self.load_profile)
        row = QHBoxLayout()
        row.addWidget(self.profile_combo,1)
        row.addWidget(self.load_profile_button)
        form.addRow('&Target URL', self.url)
        form.addRow('Allowed hosts', self.allowed)
        form.addRow('Answer profile', row)
        layout.addLayout(form)
        controls = QHBoxLayout()
        self.start_button = QPushButton('&Start')
        self.pause_button = QPushButton('&Pause')
        self.resume_button = QPushButton('&Resume / recheck')
        self.stop_button = QPushButton('S&top')
        self.approve_button = QPushButton('Approve final submission')
        self.visible = QCheckBox('Visible Chromium')
        self.visible.setChecked(settings.headed)
        self.settings_button = QPushButton('Settings…')
        for button in (self.start_button,self.pause_button,self.resume_button,self.stop_button,
                       self.approve_button,self.visible,self.settings_button):
            controls.addWidget(button)
        layout.addLayout(controls)
        self.status = QLabel(State.IDLE.value)
        self.status.setAccessibleName('Execution status')
        layout.addWidget(self.status)
        splitter = QSplitter()
        self.questions = QTableWidget(0,4)
        self.questions.setHorizontalHeaderLabels(['Explicit key','Question','Type','Required'])
        self.questions.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
        self.questions.setEditTriggers(QTableWidget.NoEditTriggers)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(1000)
        splitter.addWidget(self.questions)
        splitter.addWidget(self.log)
        layout.addWidget(splitter,1)
        bottom = QHBoxLayout()
        self.details_button = QPushButton('Run details')
        self.export_button = QPushButton('Export HTML / JSON report…')
        bottom.addWidget(self.details_button)
        bottom.addWidget(self.export_button)
        bottom.addStretch()
        layout.addLayout(bottom)
        self.setCentralWidget(root)
        self.start_button.clicked.connect(self.start_run)
        self.pause_button.clicked.connect(self.pause_run)
        self.resume_button.clicked.connect(self.resume_run)
        self.stop_button.clicked.connect(self.stop_run)
        self.approve_button.clicked.connect(self.approve)
        self.settings_button.clicked.connect(self.edit_settings)
        self.details_button.clicked.connect(self.show_details)
        self.export_button.clicked.connect(self.export)
        self.set_active(False)

    def set_active(self, active: bool) -> None:
        for control in (self.url,self.profile_combo,self.load_profile_button,
                        self.visible,self.settings_button,self.start_button):
            control.setEnabled(not active)
        for control in (self.pause_button,self.resume_button,self.stop_button):
            control.setEnabled(active)
        self.approve_button.setEnabled(active and self.current_state == State.AWAITING_SUBMISSION_APPROVAL.value)
        self.export_button.setEnabled(not active and self.last_record is not None)
        self.details_button.setEnabled(self.last_record is not None)

    @Slot()
    def load_profile(self) -> None:
        path,_ = QFileDialog.getOpenFileName(self,'Synthetic answer profile','','JSON (*.json)')
        if not path:
            return
        try:
            profile = AnswerProfile.load(Path(path))
        except Exception:
            QMessageBox.warning(self,'Invalid profile','Profile must be valid synthetic JSON without secret fields.')
            return
        self.profiles.append(profile)
        self.profile_combo.addItem(profile.name)
        self.profile_combo.setCurrentIndex(len(self.profiles)-1)

    @Slot()
    def start_run(self) -> None:
        if self.worker and self.worker.isRunning():
            return
        if not self.profiles:
            QMessageBox.information(self,'Answer profile required','Load an explicitly synthetic JSON answer profile first.')
            return
        try:
            TargetPolicy(self.settings).validate(self.url.text().strip())
        except ValueError as error:
            QMessageBox.warning(self,'Target refused',str(error))
            return
        self.log.clear()
        self.questions.setRowCount(0)
        self.last_record = None
        run_settings = Settings.model_validate({**self.settings.model_dump(),'headed':self.visible.isChecked()})
        self.worker = RunWorker(run_settings,self.profiles[self.profile_combo.currentIndex()],self.url.text().strip(),self)
        self.worker.update.connect(self.on_update)
        self.worker.finished.connect(self.worker_finished)
        self.set_active(True)
        self.worker.start()

    @Slot(str, object)
    def on_update(self, kind: str, payload: object) -> None:
        if kind == 'state':
            self.current_state = str(payload)
            self.status.setText(self.current_state)
            self.approve_button.setEnabled(self.current_state == State.AWAITING_SUBMISSION_APPROVAL.value)
        elif kind == 'questions':
            self.questions.setRowCount(len(payload))
            for index, question in enumerate(payload):
                for column,key in enumerate(('key','label','kind','required')):
                    self.questions.setItem(index,column,QTableWidgetItem(str(question[key])))
        elif kind == 'finished':
            self.last_record = payload
            self.last_directory = self.settings.data_dir/'runs'/payload['id']
            self.log.appendPlainText('Run finished. Inspect sanitized details for confirmation and errors.')
        elif kind == 'fatal':
            self.status.setText('FAILED')
            self.log.appendPlainText('Worker failed: '+str(payload))
        elif kind == 'verification':
            self.log.appendPlainText('Automation paused. Handle the verification only if authorized, then choose Resume / recheck. In headless mode, Stop and restart visibly.')
        else:
            self.log.appendPlainText(json.dumps(payload))

    @Slot()
    def worker_finished(self) -> None:
        finished = self.worker
        self.worker = None
        if finished:
            finished.deleteLater()
        self.set_active(False)
        if self.closing:
            QTimer.singleShot(0,self.close)

    @Slot()
    def pause_run(self) -> None:
        if self.worker:
            self.worker.controls.paused.set()
            self.status.setText(self.current_state+' — pause requested; current action may finish')

    @Slot()
    def resume_run(self) -> None:
        if self.worker:
            self.worker.controls.paused.clear()
            self.worker.controls.resume.set()
            self.status.setText(self.current_state)

    @Slot()
    def stop_run(self) -> None:
        if self.worker:
            self.worker.controls.stop.set()
            self.status.setText('Stopping and cleaning up…')

    @Slot()
    def approve(self) -> None:
        if not self.worker or self.current_state != State.AWAITING_SUBMISSION_APPROVAL.value:
            return
        result = QMessageBox.question(self,'Authorize final submission',
            'Submit this synthetic test response to the authorized survey now?',
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
        if result == QMessageBox.Yes:
            self.worker.controls.approval.set()
            self.approve_button.setEnabled(False)

    @Slot()
    def edit_settings(self) -> None:
        dialog = SettingsDialog(self.settings,self)
        if dialog.exec():
            try:
                dialog.result_settings.save(self.config_path)
            except OSError:
                QMessageBox.warning(self,'Settings not saved','Cannot write settings. Previous settings remain active.')
                return
            self.settings = dialog.result_settings
            self.allowed.setText(', '.join(self.settings.allowed_domains))

    @Slot()
    def show_details(self) -> None:
        if self.last_record:
            RunDetails(self.last_record,self).exec()

    @Slot()
    def export(self) -> None:
        if not self.last_record:
            return
        chosen = QFileDialog.getExistingDirectory(self,'Export run report')
        if not chosen:
            return
        destination = Path(chosen)/('survey-run-'+self.last_record['id'])
        try:
            export_report(self.last_record,destination)
            for name in self.last_record['screenshots']:
                source = self.last_directory/name
                if source.is_file() and source.resolve() != (destination/name).resolve():
                    shutil.copyfile(source,destination/name)
        except OSError:
            QMessageBox.warning(self,'Export failed','Cannot write the complete report. Choose a writable directory; partial files may exist.')
            return
        QMessageBox.information(self,'Export complete','Sanitized HTML, JSON and masked screenshots exported.')

    def closeEvent(self, event) -> None:
        if self.worker and self.worker.isRunning():
            self.closing = True
            self.stop_run()
            event.ignore()
        else:
            event.accept()
