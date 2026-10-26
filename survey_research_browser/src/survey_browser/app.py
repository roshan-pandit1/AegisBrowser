import argparse
import os
import sys
from pathlib import Path
from .configuration import Settings
from .utils.logging import configure_logging

def main() -> int:
    parser = argparse.ArgumentParser(description='Authorized synthetic survey testing')
    parser.add_argument('--config',type=Path,default=Path.home()/'.survey-research-browser'/'settings.json')
    args = parser.parse_args()
    if getattr(sys,'frozen',False):
        browsers = Path(sys._MEIPASS)/'playwright-browsers'
        if browsers.exists():
            os.environ['PLAYWRIGHT_BROWSERS_PATH'] = str(browsers)
    configure_logging()
    from PySide6.QtWidgets import QApplication, QMessageBox
    from .gui.main_window import MainWindow
    app = QApplication(sys.argv[:1])
    app.setApplicationName('Survey Research Browser')
    try:
        settings = Settings.load(args.config)
    except Exception:
        QMessageBox.critical(None,'Invalid configuration','Configuration could not be loaded. It was not replaced or silently relaxed.')
        return 2
    window = MainWindow(settings,args.config)
    window.show()
    return app.exec()
