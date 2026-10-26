"""Dependency-free source/payload gates and a package+file CycloneDX inventory.
These checks do not prove native sandboxing, C# compilation, or CVE absence.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import re
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote


FORBIDDEN_NAMES = {'cookies','cookies-journal','login data','history','web data','local state',
                   '.profile.lock','privacy.json','search.json','chromium.log','cef.log'}
FORBIDDEN_DIRS = {'profiles','labprofiles','cache','user data','.git'}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def audit_source(root: Path) -> list[str]:
    passed = []
    projects = list(root.glob('AegisBrowser*/**/*.csproj'))
    versions = set()
    for project in projects:
        for package in ET.parse(project).findall('.//PackageReference'):
            if package.get('Include','').startswith('CefSharp.'):
                versions.add(package.get('Version'))
    require(len(versions) == 1 and None not in versions and not any('*' in v for v in versions),
            'CefSharp references must be pinned consistently')
    passed.append('consistent pinned CefSharp versions')
    program = (root/'AegisBrowser/Program.cs').read_text()
    require('CommandLineArgsDisabled = true' in program, 'Chromium argument forwarding must remain disabled')
    passed.append('application command-line forwarding disabled')
    production = '\n'.join(p.read_text() for p in (root/'AegisBrowser').glob('*.cs'))
    dangerous = re.compile(r'"(?:--)?(?:no-sandbox|disable-web-security|ignore-certificate-errors|allow-running-insecure-content|disable-site-isolation-trials|remote-debugging-port)(?:[="]|$)',re.I)
    require(not dangerous.search(production), 'Unreviewed security-disabling startup switch')
    require(not re.search(r'(?:IgnoreCertificateErrors|WebSecurityDisabled)\s*=\s*true', production), 'Insecure CEF setting')
    passed.append('no explicit dangerous production startup switches/settings')
    html = (root/'AegisBrowser/diagnostics.html').read_text()
    handlers = (root/'AegisBrowser/SecurityHandlers.cs').read_text()
    policy = re.search(r'ContentPolicy = "([^"]+)"',handlers)
    require(policy is not None, 'Missing diagnostics response policy')
    for tag in ('script','style'):
        values = re.findall(rf'<{tag}>(.*?)</{tag}>',html,re.S)
        require(len(values)==1, 'Expected exactly one hashed inline '+tag)
        digest = base64.b64encode(hashlib.sha256(values[0].encode()).digest()).decode()
        require('sha256-'+digest in html and 'sha256-'+digest in policy[1], 'Stale diagnostics CSP '+tag+' hash')
    require("'unsafe-inline'" not in html, 'Unrestricted inline diagnostics scripts/styles')
    passed.append('diagnostics inline script/style CSP hashes match')
    require("frame-ancestors 'none'" in policy[1] and 'resource.Headers.Clear()' in handlers
            and 'BrowserPolicy.CanServeDiagnostics' in handlers, 'Diagnostics boundary/header gate missing')
    passed.append('diagnostics response and request boundary hooks present')
    props = ET.parse(root/'Directory.Build.props')
    require(props.findtext('.//NuGetAudit') == 'true' and props.findtext('.//NuGetAuditMode') == 'all',
            'Transitive NuGet audit must be enabled')
    errors = props.findtext('.//WarningsAsErrors') or ''
    require(all(code in errors for code in ('NU1900','NU1903','NU1904')), 'Missing audit failure gates')
    passed.append('transitive NuGet auditing and failure gates configured')
    workflow = (root/'.github/workflows/windows.yml').read_text()
    require('runs-on: windows-latest' in workflow and "dotnet-version: '8.0.x'" in workflow,
            'Windows runner / .NET 8 setup missing')
    passed.append('Windows runner and .NET 8 SDK configured')
    return passed


def payload_files(root: Path) -> list[Path]:
    files = []
    for path in root.rglob('*'):
        relative = path.relative_to(root)
        require(not path.is_symlink() and not (hasattr(path,'is_junction') and path.is_junction()),
                'Links/junctions are not allowed in release payloads')
        parts = {part.lower() for part in relative.parts}
        require(not parts.intersection(FORBIDDEN_DIRS), 'Profile/cache directory found in payload')
        require(path.name.lower() not in FORBIDDEN_NAMES and not path.name.lower().startswith('.env')
                and path.suffix.lower() not in {'.pfx','.key','.pem','.log','.pdb'}, 'Private/debug material found in payload')
        if path.is_file():
            files.append(path)
    return sorted(files)


def write_sbom(assets: Path, publish: Path, output: Path, verify: bool = False) -> dict:
    data = json.loads(assets.read_text())
    components = []
    for identifier, package in sorted(data['libraries'].items()):
        if package.get('type') != 'package':
            continue
        name, version = identifier.rsplit('/',1)
        ref = 'pkg:nuget/'+quote(name,safe='')+'@'+quote(version,safe='')
        components.append({'type':'library','bom-ref':ref,'name':name,'version':version,'purl':ref})
    files = payload_files(publish)
    require(files, 'Publish directory is empty')
    names = {path.name.lower() for path in files}
    for name in ('AegisBrowser.exe','CefSharp.BrowserSubprocess.exe','libcef.dll','coreclr.dll','hostfxr.dll','icudtl.dat'):
        require(name.lower() in names, 'Missing required payload file: '+name)
    for path in files:
        if path.resolve() == output.resolve():
            continue
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream,'sha256').hexdigest()
        name = path.relative_to(publish).as_posix()
        components.append({'type':'file','bom-ref':'file:'+name,'name':name,
                           'hashes':[{'alg':'SHA-256','content':digest}]})
    bom = {'bomFormat':'CycloneDX','specVersion':'1.5','serialNumber':'urn:uuid:'+str(uuid.uuid4()),'version':1,
           'metadata':{'timestamp':datetime.now(timezone.utc).isoformat(),
                       'component':{'type':'application','name':'AegisBrowser'},
                       'properties':[{'name':'aegis:scope','value':'Resolved NuGet packages and published file hashes. Not a complete inventory of Chromium internal third-party source components.'}]},
           'components':components}
    if verify:
        existing = json.loads(output.read_text())
        require(existing.get('bomFormat') == 'CycloneDX' and existing.get('specVersion') == '1.5'
                and existing.get('components') == components, 'Existing inventory does not match assets/payload')
        return existing
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(bom,indent=2)+'\n')
    return bom


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[1])
    parser.add_argument('--assets',type=Path)
    parser.add_argument('--publish',type=Path)
    parser.add_argument('--sbom',type=Path)
    parser.add_argument('--verify-inventory',action='store_true')
    args = parser.parse_args()
    for check in audit_source(args.root):
        print('PASS static:',check)
    if args.assets or args.publish or args.sbom:
        require(all((args.assets,args.publish,args.sbom)), 'Supply assets, publish and sbom together')
        bom = write_sbom(args.assets,args.publish,args.sbom,args.verify_inventory)
        print('PASS inventory:',len(bom['components']),'package/file components')


if __name__ == '__main__':
    main()
