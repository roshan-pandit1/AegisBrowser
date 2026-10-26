import json
import tempfile
import unittest
from pathlib import Path
from security_audit import audit_source, payload_files, write_sbom

ROOT = Path(__file__).resolve().parents[1]

class AuditTests(unittest.TestCase):
    def test_actual_source_gates(self):
        self.assertEqual(len(audit_source(ROOT)),7)

    def test_profile_payload_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'Profiles').mkdir();(root/'Profiles'/'Cookies').write_text('fixture')
            with self.assertRaises(ValueError):
                payload_files(root)

    def test_credentials_and_debug_files_rejected(self):
        for name in ('.env','.env.local','test.pfx','chromium.log','AegisBrowser.pdb','Cookies'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as d:
                root=Path(d);(root/name).write_text('fixture')
                with self.assertRaises(ValueError):
                    payload_files(root)

    def test_empty_payload_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);assets=root/'assets.json';assets.write_text('{"libraries":{}}')
            publish=root/'publish';publish.mkdir()
            with self.assertRaises(ValueError):
                write_sbom(assets,publish,root/'sbom.json')

    def test_inventory_uses_actual_assets_and_file_hashes(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);assets=root/'assets.json'
            assets.write_text(json.dumps({'libraries':{'Fixture.Package/1.2.3':{'type':'package'}}}))
            publish=root/'publish';publish.mkdir()
            for name in ('AegisBrowser.exe','CefSharp.BrowserSubprocess.exe','libcef.dll','coreclr.dll','hostfxr.dll','icudtl.dat'):
                (publish/name).write_bytes(b'explicit fake test payload')
            bom=write_sbom(assets,publish,root/'sbom.json')
            self.assertEqual(len(bom['components']),7)
            self.assertEqual(write_sbom(assets,publish,root/'sbom.json',verify=True),bom)
            (publish/'libcef.dll').write_bytes(b'changed test fixture')
            with self.assertRaises(ValueError):
                write_sbom(assets,publish,root/'sbom.json',verify=True)
            self.assertEqual(bom['components'][0]['purl'],'pkg:nuget/Fixture.Package@1.2.3')
            self.assertEqual(len(bom['components'][1]['hashes'][0]['content']),64)
            self.assertIn('Not a complete inventory',bom['metadata']['properties'][0]['value'])

if __name__=='__main__':
    unittest.main()
