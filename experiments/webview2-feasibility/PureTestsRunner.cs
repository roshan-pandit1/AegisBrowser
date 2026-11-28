using System;
using AegisBrowser.WebView2Feasibility;

var lifecycleChecks = await LifecycleCoordinatorTests.RunAsync();
var profileChecks = ProfileRootPolicyTests.Run();
Console.WriteLine($"PURE_TESTS lifecycle={lifecycleChecks} profile={profileChecks}");
