"""Static patch contracts only; these do not test compilation or sandboxing."""
from pathlib import Path
import unittest


PATCH = (Path(__file__).parent / "0001-experimental-sandbox-info.patch").read_text(encoding="utf-8")
CHILD_PATCH = (Path(__file__).parent / "0002-experimental-child-opt-in.patch").read_text(encoding="utf-8")
DIAGNOSTIC_PATCH = (Path(__file__).parent / "0003-experimental-renderer-diagnostics.patch").read_text(encoding="utf-8")
MANAGED_NATIVE_HOST = (Path(__file__).resolve().parents[2] / "tests" / "research" /
                       "p0managed" / "NativeHost" / "main.cpp").read_text(encoding="utf-8")
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

    def test_child_patch_only_appends_the_fixed_test_marker(self):
        added = [line[1:] for line in CHILD_PATCH.splitlines() if line.startswith("+") and not line.startswith("+++")]
        self.assertEqual(sum("AppendSwitch(" in line for line in added), 1)
        self.assertIn('commandLine->AppendSwitch("aegis-p309f-child-opt-in");', "\n".join(added))
        self.assertNotIn('AppendSwitch("no-sandbox")', "\n".join(added))

    def test_diagnostic_patch_is_opt_in_bounded_and_marks_claims(self):
        added = "\n".join(line[1:] for line in DIAGNOSTIC_PATCH.splitlines()
                          if line.startswith("+") and not line.startswith("+++"))
        for required in ('AEGIS_P0_MANAGED_EXECUTE', 'source_process == PID_RENDERER',
                         'frame->IsMain()', 'argList->GetSize() == 1',
                         'argList->GetType(0) == VTYPE_INT', 'InterlockedIncrement',
                         '<= 32', 'P309I_SENTINEL_OPEN_RESULT',
                         'not evidence of process identity or sandbox enforcement'):
            self.assertIn(required, added)
        self.assertNotIn('AppendSwitch("no-sandbox")', added)

    def test_managed_renderer_has_no_premature_native_access_probe(self):
        for forbidden in ('RendererSentinelProbe.h', 'ReadOpenClaim(',
                          'P309I_SENTINEL_OPEN_RESULT', 'P309I_RENDERER_V1',
                          'CreateFileA(', 'CreateFileW('):
            self.assertNotIn(forbidden, MANAGED_NATIVE_HOST)
        for required in ('diagnostic("P309I_CONTEXT", 0)',
                         'diagnostic("P309I_PID", pid)',
                         'frame->SendProcessMessage(PID_BROWSER, notification)'):
            self.assertIn(required, MANAGED_NATIVE_HOST)


if __name__ == "__main__":
    unittest.main()
