# Build-path static audit

Date: 2026-11-27. Scope: the designated `safe-engine-transition` worktree
and read-only inspection of selected installed SDK and cached package files.
**BLOCKED [STATIC-ANALYSIS] [NOT_RUN]:** no build has been classified
SAFE_WORKSPACE. The complete resolved import graph, compiled MSBuild tasks,
restore behavior, and all filesystem writes have not been audited.

## Repository source inspected

The inspected project XML comprises:

- `AegisBrowser.Core/AegisBrowser.Core.csproj` and
  `AegisBrowser/AegisBrowser.csproj`;
- `AegisBrowser.Tests/AegisBrowser.Tests.csproj`,
  `AegisBrowser.BrowserTests/AegisBrowser.BrowserTests.csproj` and
  `AegisBrowser.FilteringTests/AegisBrowser.FilteringTests.csproj`;
- `tests/acceptance/DependencyCompat/DependencyCompat.csproj`.

The five main project files contain no explicit XML `Target`, `Exec`,
`Import` or `UsingTask` elements. This observation does not cover
SDK or package imports. The compatibility project has a custom
`RequireCompatibilityGeneration` target containing an `Error` task
before `CollectPackageReferences`. No explicit pre/post-build hook or
output/restore-path property was found in those six project XML files by the
bounded property-name scan. Dynamically imported and generated properties,
task implementations and conditions were not fully inspected.
[SOURCE-VERIFIED] [STATIC-ANALYSIS]

`global.json` pins SDK `10.0.404` with roll-forward disabled.
The inspected main projects target .NET 10; the browser projects declare
Windows Forms and `win-x64`. The direct package references include
CefSharp `154.0.340` and .NET SQLite/ProtectedData `10.0.15`.
The compatibility project also contains older conditional references; a
complete resolved dependency graph was not inferred from the declarations.
[SOURCE-VERIFIED] [STATIC-ANALYSIS]

No `Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props` or repository-level NuGet configuration was
found on the inspected worktree-to-volume-root ancestor chain. A user-level
NuGet configuration is present outside the worktree. Its values and source
URLs were not displayed or audited; no conclusion about secrets, feed
behavior, or authorization is drawn from the absence of a credential section.
[STATIC-ANALYSIS] [UNVERIFIED]

## Selected external imports inspected

The installed SDK's `Microsoft.NET.Sdk.targets` file contains 29
`Import` elements, 32 `Target` elements, one `Exec` in a target
named `Run`, and 15 `UsingTask` declarations. The presence of that
`Exec` does not establish that a particular build invokes it. Its
transitive imports and compiled task bodies were not fully audited. The
selected SDK entry props/targets and Windows Desktop targets likewise contain
additional imports. [STATIC-ANALYSIS] [UNVERIFIED]

The cached CefSharp Common `154.0.340` transitive targets inspected
contain five named targets and output-copy metadata. The selected cached CEF
`154.0.34` Windows x64 props contain a resource-copy target whose
destination expression refers to `TargetDir`. Its invocation and
resolved destination for a particular build were not established. The cached
packages, their other assets, generated NuGet imports, and all compiled task
code have not been comprehensively audited. [STATIC-ANALYSIS] [UNVERIFIED]

The four primary project `obj/project.assets.json` files were absent in
this worktree during inspection. Therefore the final generated
`.nuget.g.props` / `.nuget.g.targets` imports and resolved package
selection cannot be verified here without a separately audited restore.
No restore was run. [STATIC-ANALYSIS] [NOT_RUN]

## Potential writes and decision

At inspection time `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` and
`DOTNET_CLI_HOME` were unset; the observed package cache and user NuGet
configuration are outside the designated worktree. `TEMP`, `TMP`,
`APPDATA` and `LOCALAPPDATA` were set to paths outside it. A restore
or SDK task may use caches, temporary files, user state, or configured feeds;
these observations do not prove which specific writes would occur for a given
command. Output and cache redirection alone would not establish that every
transitive task is safe. [STATIC-ANALYSIS] [UNVERIFIED]

**BLOCKED [NOT_RUN]:** neither restore nor compilation has a fully audited
SAFE_WORKSPACE execution path. Next: inspect the exact installed SDK and
package import/task graph and all target conditions for one specified command;
resolve its inputs without unaudited restore; establish and verify that every
output, cache, temporary and user-state write is confined to the designated
worktree; and obtain any separately required human authorization. Until those
facts are established, do not run the build, an indirect build hook, or a
package command. A successful build would not itself prove the path safe.

No repository script, build, test, package tool, Git command, policy change, or
write outside the designated worktree was performed for this audit.
[DEVELOPMENT-SAFE] [STATIC-ANALYSIS]
