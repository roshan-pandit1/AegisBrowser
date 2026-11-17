"""Static patch contracts only; these do not test compilation or sandboxing."""
from pathlib import Path
import unittest


PATCH = (Path(__file__).parent / "0001-experimental-sandbox-info.patch").read_text(encoding="utf-8")
ADDED = "\n".join(line[1:] for line in PATCH.splitlines() if line.startswith("+") and not line.startswith("+++"))


class PrototypeContract(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
