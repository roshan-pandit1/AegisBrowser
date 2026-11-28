# Prior close-before-start observation

The operator supplied this trace and reported process exit code 0; this workspace
did not independently witness the run. The reported ZIP SHA-256 was
`5481FAF8A82D3786F87BF7523D675F71A92785FB44A301F0B5D81433C364F0A6`, and
all 12 inventory files were reported verified in the VM. The reported platform
was Windows 11 Home build 26300 on an ARM64 VM with x64 emulation, .NET and
Windows Desktop 10.0.15, standard user and WebView2 158.0.4370.45. VM isolation
and execution authorization were not fully documented in the supplied transcript.

```text
1 4 2 PROCESS_START id=- uri=- header=- redirect=- detail=-
2 279 2 INITIALIZATION_START id=- uri=- header=- redirect=- detail=-
3 846 2 RUNTIME_SELECTED id=- uri=- header=- redirect=- detail=158.0.4370.45
4 855 2 INITIALIZATION_COMPLETE id=- uri=- header=- redirect=- detail=-
5 2788 2 DISPATCH_ENTER id=- uri=- header=- redirect=- detail=-
6 2790 2 DISPATCH_RETURN id=- uri=- header=- redirect=- detail=-
7 2790 2 AUTOMATIC_CLOSE_REQUEST id=- uri=- header=- redirect=- detail=-
8 2793 2 CLOSE_REQUEST id=- uri=- header=- redirect=- detail=-
9 2793 2 PRECONDITION_MET_CLOSE_BEFORE_START id=- uri=- header=- redirect=- detail=-
10 2793 2 CLOSE_REFUSED_UNRESOLVED id=- uri=- header=- redirect=- detail=-
11 2813 2 CANDIDATE_START_CANCELLED id=2 uri=True header=True redirect=False detail=AFTER_DISPATCH_RETURN
12 2822 2 FIXTURE_RESPONSE id=- uri=True header=True redirect=- detail=-
13 2840 2 NAVIGATION_COMPLETED id=2 uri=True header=True redirect=- detail=OPERATION_CANCELED
14 2841 2 RACE_OBSERVATION_MATCHED id=- uri=- header=- redirect=- detail=-
15 2841 2 CLOSE_REQUEST id=- uri=- header=- redirect=- detail=-
16 2841 2 DISPOSE_ENTER id=- uri=- header=- redirect=- detail=-
17 2875 2 DISPOSE_RETURN id=- uri=- header=- redirect=- detail=-
18 2876 2 FORM_CLOSED id=- uri=- header=- redirect=- detail=-
19 3026 2 MESSAGE_LOOP_RETURN id=- uri=- header=- redirect=- detail=-
20 3026 2 PROCESS_EXIT_INTENT id=- uri=- header=- redirect=- detail=ZERO
```

On `NAVIGATION_COMPLETED`, `uri` and `header` mean active-ID-found and
candidate-ID-match, not repeated URI/header checks. Resource handling occurred
after cancellation was requested, so cancellation is not evidence that native
resource activity is over. This single-control observation cannot authenticate
dispatch ownership, establish two-control behavior, or prove the cause of the
v3 hang. The fixture used `NavigateWithWebResourceRequest`; v3 used
`NavigateToString`.
