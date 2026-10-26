"""Build on the destination OS; bundles matching installed Playwright Chromium."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys

def main() -> None:
    parser=argparse.ArgumentParser()
    parser.add_argument('--skip-browser-install',action='store_true')
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    os.chdir(root)
    browsers=root/'build'/'playwright-browsers'
    browsers.mkdir(parents=True,exist_ok=True)
    env={**os.environ,'PLAYWRIGHT_BROWSERS_PATH':str(browsers)}
    if not args.skip_browser_install:
        subprocess.run([sys.executable,'-m','playwright','install','chromium'],env=env,check=True)
    if not any(browsers.glob('chromium-*')):
        raise SystemExit('Install matching Playwright Chromium before packaging')
    subprocess.run([sys.executable,'-m','PyInstaller','--noconfirm','--clean','--onedir',
                   '--windowed','--name','SurveyResearchBrowser','--paths','src',
                   '--collect-all','playwright',
                   '--collect-all','survey_browser',
                   '--add-data',str(browsers)+os.pathsep+'playwright-browsers',
                   'scripts/entrypoint.py'],env=env,check=True)
    payload=root/'dist'/'SurveyResearchBrowser'
    shutil.copy2(root/'README.md',payload/'README.md')
    shutil.copy2(root/'config.example.yaml',payload/'config.example.yaml')
    shutil.copytree(root/'tests'/'fixtures',payload/'fixtures',dirs_exist_ok=True)
    archive=Path(shutil.make_archive(str(root/'dist'/f'SurveyResearchBrowser-{sys.platform}'), 'zip',payload.parent,payload.name))
    with archive.open('rb') as stream:
        digest=hashlib.file_digest(stream,'sha256').hexdigest()
    archive.with_suffix('.zip.sha256').write_text(digest+'  '+archive.name+'\n')
    print(archive)

if __name__=='__main__':
    main()
