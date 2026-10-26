# UI redesign recovery

Intended branch: feature/ui-white-chrome
Saved head: f49f05d4e9e71cbb8c9c3bc8fae60bcf37497d69
Required base: 75fbf6a59b4565fc38f16bf63a73938cb68edb4e

The temporary checkout and SDK are no longer present. The original workspace is dirty and remains untouched. Two patches and redesign.bundle preserve the UI commits. The bundle requires the base commit from GitHub. GitHub DNS failed on this continuation; no publication or remote SHA verification succeeded.

Run the following from a terminal with working GitHub access. Use a fresh destination; do not replace an existing checkout:

```sh
git clone --single-branch --branch feature/security-lab-mock-allow https://github.com/roshan-pandit1/AegisBrowser.git "$HOME/AegisBrowser-ui-recovery"
cd "$HOME/AegisBrowser-ui-recovery"
git bundle verify "/Volumes/Samsung SSD/Project/AegisBrowser/review-deliverables/ui-white-chrome/redesign.bundle"
git fetch "/Volumes/Samsung SSD/Project/AegisBrowser/review-deliverables/ui-white-chrome/redesign.bundle" HEAD
git switch -c feature/ui-white-chrome f49f05d4e9e71cbb8c9c3bc8fae60bcf37497d69
git status --short --branch
git diff --check 75fbf6a59b4565fc38f16bf63a73938cb68edb4e..HEAD
git ls-remote origin refs/heads/feature/ui-white-chrome
```

Inspect any existing remote branch before pushing. If absent or an ancestor, use a normal push:

```sh
git push origin feature/ui-white-chrome
git ls-remote origin refs/heads/feature/ui-white-chrome
```

Verify Windows CI on this exact head, including native tests, smoke and portable packaging. Do not use PR #7 artifacts to validate this redesign. Do not merge or request human acceptance yet.

Saved self-contained output: artifacts/ui-white-chrome/win-x64 in the original workspace. All 846 files were rehashed and match publish-manifest.json with no extras or missing files. Windows execution and portable ZIP verification remain unperformed. Earlier cross-compilation/test results are in BUILD-STATUS.md; no SDK is currently available to repeat them.

The consumer-site issue requires the affected public URL, privacy mode and lists. Production filtering was not changed without reproduction. The added deterministic product-navigation test still needs Windows execution. History is omitted; profile switching remains the existing CLI behavior.
