# Previous single-navigation observation

The operator supplied the following result; it was not independently witnessed
by this workspace. The fixture ZIP SHA-256 was
`8982704387892C0D11620BA411A20DF118B5CEA228F790192939F754D730B0F4`;
the assembly was `0FA40AF41C149D88025A636AD21778F4246BE0A1F71DAEC9E799E0EC044AEF1B`.
All 12 inventoried files were reported verified inside the VM.

The reported host was Windows 11 Home build 26300 on an Apple Silicon ARM64 VM,
with x64 emulation, .NET and Windows Desktop Runtime 10.0.15, standard user, and
reported WebView2 runtime 158.0.4370.45. Isolation and explicit execution
authorization were not fully documented in the supplied transcript.

```text
1 10 2 PROCESS_START id=- uri=- header=- redirect=- detail=-
2 475 2 INITIALIZATION_START id=- uri=- header=- redirect=- detail=-
3 1035 2 RUNTIME_SELECTED id=- uri=- header=- redirect=- detail=158.0.4370.45
4 1050 2 INITIALIZATION_COMPLETE id=- uri=- header=- redirect=- detail=-
5 36714 2 DISPATCH_ENTER id=- uri=- header=- redirect=- detail=-
6 36723 2 DISPATCH_RETURN id=- uri=- header=- redirect=- detail=-
7 36749 2 CANDIDATE_START id=2 uri=True header=True redirect=False detail=-
8 36774 2 FIXTURE_RESPONSE id=- uri=True header=True redirect=- detail=-
9 36858 2 NAVIGATION_COMPLETED id=2 uri=True header=True redirect=- detail=SUCCESS
10 977136 2 CLOSE_REQUEST id=- uri=- header=- redirect=- detail=-
11 977140 2 DISPOSE_ENTER id=- uri=- header=- redirect=- detail=-
12 977252 2 DISPOSE_RETURN id=- uri=- header=- redirect=- detail=-
13 977253 2 FORM_CLOSED id=- uri=- header=- redirect=- detail=-
14 977441 2 MESSAGE_LOOP_RETURN id=- uri=- header=- redirect=- detail=-
15 977442 2 PROCESS_EXIT_INTENT id=- uri=- header=- redirect=- detail=ZERO
```

The operator separately reported process exit code zero. The `uri` and `header`
fields on `NAVIGATION_COMPLETED` mean active-ID-found and candidate-ID-match,
not repeated request comparisons. The observation supports one successful
navigation, one synchronous disposal return, and process exit in that specific
environment. It does not prove navigation ownership, two-control disposal, or
the cause of the original v3 shutdown failure. The later close-race fixture uses
`NavigateWithWebResourceRequest`; v3 used `NavigateToString`.
