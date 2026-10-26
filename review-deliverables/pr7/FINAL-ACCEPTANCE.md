# Final human acceptance — pending, not performed

Candidate: feature 75fbf6a59b4565fc38f16bf63a73938cb68edb4e; VERSION.txt build source d156032f4b874d5f14ac78bad4cfa00a2563a6a8 (identical source tree). Use the complete verified archive, not an older artifact.

When human acceptance is authorized, use Windows 10/11 x64, install the Microsoft Visual C++ x64 Redistributable if required, extract AegisBrowser-v0.1.0-rc1-win-x64-portable.zip completely, and launch AegisBrowser/AegisBrowser.exe. No .NET SDK is required. Record Windows version, scaling and reproduction steps with each result.

- [ ] Startup, local diagnostics, normal browsing, Back/Forward, reload/stop and search selection work.
- [ ] Keyboard focus/shortcuts, resizing and 100/150/200% scaling remain usable.
- [ ] Lab shows Not Run → Running → Passed; repeated runs display distinct request IDs.
- [ ] Fresh fixture denies protected access; Allow grants only its own session; another fixture tab remains denied.
- [ ] Page/window reset revokes access; repeated runs leave no stale grant.
- [ ] Lab tabs coexist with ordinary tabs, profiles, privacy modes and search; closing either leaves the other usable.
- [ ] Exported JSON matches the displayed result and contains no session secret; cancel/save failures preserve usable UI and allow retry.
- [ ] Closing the lab/application during startup or evaluation terminates cleanly; reopening and recovery from failed/interrupted requests work.
- [ ] Settings and normal persistent profile data survive restart without inheriting temporary lab state.
- [ ] Real-site OAuth, origin permissions/hardware, DevTools and cold offline diagnostics meet the existing broader acceptance plan.

All boxes are pending human verification. Keep PR #7 draft and unmerged until explicitly authorized otherwise.
