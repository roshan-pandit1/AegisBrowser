"""Static patch contracts only; these do not test compilation or sandboxing."""
from pathlib import Path
import unittest


PATCH = (Path(__file__).parent / "0001-experimental-sandbox-info.patch").read_text(encoding="utf-8")
CHILD_PATCH = (Path(__file__).parent / "0002-experimental-child-opt-in.patch").read_text(encoding="utf-8")
ADDED = "\n".join(line[1:] for line in PATCH.splitlines() if line.startswith("+") and not line.startswith("+++"))


class PrototypeContract(unittest.TestCase):
    def test_result_decoder_dependencies_are_present(self):
        root = Path(__file__).resolve().parents[2]
        for relative in ("tests/research/p309g/result-lib.ps1",
                         "tests/research/p309f/run-result-lib.ps1"):
            self.assertTrue((root / relative).is_file(), relative)

    def test_explicit_sandbox_is_passed_to_both_cef_entrypoints(self):
        self.assertIn("CefInitialize(main_args, settings, app.get(), sandboxInfo.ToPointer())", ADDED)
        self.assertEqual(ADDED.count("CefExecuteProcess(cefMainArgs,"), 3)
        for line in ADDED.splitlines():
            if "return CefExecuteProcess(" in line:
                self.assertIn("sandboxInfo.ToPointer()", line)

    def test_explicit_rejection_of_missing_bootstrap_info(self):
        self.assertEqual(ADDED.count("instance == IntPtr::Zero || sandboxInfo == IntPtr::Zero"), 4)
        self.assertEqual(ADDED.count("Experimental sandbox fork: use "), 4)

    def test_same_executable_and_argument_restrictions(self):
        self.assertIn("!String::IsNullOrEmpty(cefSettings->BrowserSubprocessPath)", ADDED)
        self.assertIn("!cefSettings->CommandLineArgsDisabled", ADDED)
        self.assertIn("cefSettings->CefCommandLineArgs->Count != 0", ADDED)
        self.assertIn("if (performDependencyCheck)", ADDED)

    def test_child_patch_only_appends_the_fixed_test_marker(self):
        added = [line[1:] for line in CHILD_PATCH.splitlines() if line.startswith("+") and not line.startswith("+++")]
        self.assertEqual(sum("AppendSwitch(" in line for line in added), 1)
        self.assertIn('commandLine->AppendSwitch("aegis-p309f-child-opt-in");', "\n".join(added))
        self.assertNotIn('AppendSwitch("no-sandbox")', "\n".join(added))


if __name__ == "__main__":
    unittest.main()
