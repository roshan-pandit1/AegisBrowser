global using System;
global using System.Collections.Generic;

if (args.Length > 1 || (args.Length == 1 && args[0] != "--probe-entry")) return 2;
var decision = AegisBrowser.Testing.TestGovernance.Evaluate(
    AegisBrowser.Testing.TestGovernance.ProgramKind.Core,
    new[] { "--governance-run", "A", "governance-self-test" });
var allowed = decision.Disposition == AegisBrowser.Testing.TestGovernance.Disposition.RunSelfTest;
if (args.Length == 1)
{
    Console.WriteLine(allowed ? "ENTRY_PROBE=PERMITTED_SYNTHETIC" : "ENTRY_PROBE=REFUSED");
    return 0;
}
if (!allowed) return 2;
if (TestGovernanceTests.Run() != 0) return 1;
var assertions = InventoryValidationTests.Run();
Console.WriteLine($"PASS [SYNTHETIC]: {assertions} in-memory inventory checks; no protected operation started.");
return 0;
