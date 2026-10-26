# Survey Research Browser

A standalone Python desktop application for **authorized, synthetic survey testing**. It uses PySide6 for controls and a separate genuine Playwright Chromium window (or headless Chromium), with a worker thread keeping Qt responsive. It does not embed Chromium into Qt or modify its identity.

Implementation is present; **full GUI/Chromium execution and packaged binaries are not yet verified in the implementation environment**. See [BUILD-STATUS.md](BUILD-STATUS.md) for executed checks and exact blockers. No third-party production website has been tested.

## Setup and run

Use Python **3.12–3.14**, a desktop session, and an ordinary non-root account. Python 3.15 is excluded until the native dependencies support it. No API keys, paid services, or `.env` secrets are needed.

```sh
cd survey_research_browser
python3.12 -m venv .venv
# macOS/Linux:
source .venv/bin/activate
# Windows PowerShell instead: .venv\Scripts\Activate.ps1
python -m pip install -e ".[dev]"
python -m playwright install chromium
python -m survey_browser
```

Linux may need `python -m playwright install --with-deps chromium` with administrator approval for OS packages. Do not run the application as root or disable Chromium's sandbox. TLS certificate validation stays enabled.

The application defaults to `~/.survey-research-browser/settings.json` and saves runs beneath the configured data directory. To use the supplied explicit configuration:

```sh
python -m survey_browser --config config.example.yaml
```

Settings are JSON or YAML. `.env.example` is explanatory; environment variables never enable external hosts or submission. Save failures keep the previous in-memory settings active. Run settings cannot be changed mid-run.

## Local fixture walkthrough

In another activated terminal, serve only loopback:

```sh
python -m http.server 8000 --bind 127.0.0.1 --directory tests/fixtures
```

1. Load `tests/fixtures/answers.json` using **Load synthetic JSON profile**.
2. Target `http://127.0.0.1:8000/survey.html` and choose visible or headless Chromium before Start.
3. The application discovers native fields, displays their keys and labels, fills only explicit synthetic mappings, and validates native constraints. With submission disabled, it finishes as `validated_without_submission` and never clicks the final submit control.
4. For full local confirmation, enable **requests for final submission approval** in Settings. Each run waits at `AWAITING_SUBMISSION_APPROVAL` until you choose **Approve final submission** and confirm the dialog. Resume is not approval.
5. `multipage_survey.html` exercises explicit Next navigation. `challenge.html` pauses on a mock verification marker. Its local **Authorized operator** button simulates manual clearance; Resume rechecks the page and re-inspects it. This is not CAPTCHA solving.
6. Pause takes effect at safe action boundaries; an already running action may finish. Stop cancels pending automation, closes its context/browser/proxy, and records cancellation. Closing the GUI requests the same cleanup rather than terminating a live worker thread.
7. Inspect **Run details**, or export HTML/JSON and masked screenshot artifacts into a new run-specific directory. Cancelling the export dialog does nothing; write failures are displayed without discarding the in-memory report.

This walkthrough is documentation, **not a claim that manual acceptance was performed**.

## Authorization and network boundary

- Defaults authorize exactly `localhost` and `127.0.0.1`, not the entire local network. IPv6 loopback `::1` can be explicitly added without enabling external access.
- External targets require both an exact allowed host and the separate external authorization checkbox. No wildcard, suffix, credential-bearing URL, or non-HTTP(S) input is accepted.
- Explicit external hosts must resolve exclusively to globally routable addresses. Private, loopback, link-local, multicast and mixed public/private DNS results are refused. Internal company/private-IP targets are intentionally unsupported except the explicit loopback names.
- Every run starts a loopback-only forwarding proxy. It validates each destination, resolves it, then connects to a validated **numeric IP**. Redirects and subresource connections remain constrained even when browser route callbacks do not fire for redirected requests. HTTP Host headers are rewritten to the authorized destination. HTTPS CONNECT is an opaque tunnel; Chromium verifies the destination certificate itself, without interception certificates.
- Chromium uses this proxy even for localhost. Non-proxied WebRTC UDP is disabled as a network containment measure, not an identity modification. WebSockets, service workers, downloads and popup windows are disabled/closed in this controlled test runner. Unsupported network features may break some surveys.
- This application is **not an OS network sandbox**. For hostile content or strict enterprise egress controls, use an isolated machine/container and an administrator-managed firewall. The local proxy is not a production security gateway and must never be exposed beyond loopback.
- No proxy rotation, fake fingerprints, suppression of automation signals, CAPTCHA solver, payment integration, or real security-provider adapter exists.
- Challenge recognition is conservative and based on observable markers, password inputs, selected verification text and HTTP 401/403. It cannot recognize every custom challenge. Unsupported or ambiguous forms fail closed. Headless challenges require Stop and a fresh visible run for authorized manual handling.

**Submission scope:** enabling automation on a site authorizes navigation, field changes and explicitly configured intermediate Next actions, which can cause that site's own autosave/API traffic. The final-click approval gate governs this tool's final submit action; it cannot stop site-authored autosaves or a human from clicking in the visible browser. Use only controlled/authorized surveys. Unknown submit outcomes are reported `attempted_unconfirmed` and are never automatically retried.

## Question mapping and compatibility

Profiles must contain `name`, `synthetic: true`, and an `answers` object. Keys match the native control's `name`, then `id`, then accessible label **exactly**. Radio/checkbox groups use their shared name and exact option values. There is no fuzzy matching, generated identity, random answer selection or hidden-field guessing.

Supported native fields: text/email/URL/tel, textarea, radio groups, checkbox groups, single-select dropdowns, number, ISO date and native range ratings. Required native constraints are checked before advancing or requesting approval. Duplicate keys/options, unsupported custom widgets, file/password inputs and embedded survey frames require explicit/manual adaptation; they are not guessed. Sensitive field identifiers are refused in profiles.

By default, Next controls must carry `data-survey-next="true"`; change the **explicit next-page CSS selector** for an authorized site. Unconfigured Next/Continue-like buttons produce a warning instead of being guessed. SPAs should expose changing field identities/labels or a changing `data-survey-page` marker. Final confirmation requires a visible `data-survey-complete="true"` marker or the configured completion selector. A generic page load is not confirmation. These settings are operator-controlled test contracts, not vendor integrations.

Only connection-reset/connection-closed failures during initial navigation are retried, once. Field actions and submissions are never replayed after failure. A crashed browser requires a new run, with a fresh context and no persisted session.

## Persistence and privacy

SQLite/SQLAlchemy tables: runs, pages, questions, test_answers, events, errors, reports. Run IDs, status, counts, ordinal field types, mapped/unmapped flags, validation outcomes, timing and safe error codes are stored. Answer values, question text, page HTML, passwords, cookies, tokens, arbitrary console messages and exception messages are **not** persisted. The test_answers table records mapping outcomes, not respondent payloads. The operator's synthetic profile JSON remains their own explicit local input.

Persistent page references contain origin only; paths, queries and fragments are omitted because they may contain identifiers. Inspector labels are shown transiently in the GUI. Reports whitelist structured values and HTML-escape their content. Challenge/final screenshots are **fully masked**, including page text and inputs, so they establish artifact capture without leaking credentials or personal details; they are intentionally not useful visual evidence of a challenge. Session data is ephemeral. SQLite is not encrypted; no credential storage is offered. Use OS disk encryption if needed.

## Tests

```sh
python -m pytest -q
# Portable logic only (with dependencies installed):
python -m pytest -q -m "not browser and not gui"
# Real Chromium fixture integration:
python -m pytest -q -m browser
# Qt controls and export handling:
python -m pytest -q -m gui
# Dependency-free security/report/state checks:
PYTHONPATH=src python -m unittest discover -s tests -p test_core_stdlib.py -v
# Real local proxy transport checks:
PYTHONPATH=src python scripts/proxy_smoke.py
```

PowerShell: `$env:PYTHONPATH="src"` before the last two commands. Linux CI can use `QT_QPA_PLATFORM=offscreen` for Qt widget tests; that does not verify a visible desktop. Chromium installation and loopback binding must work. Browser tests fail rather than silently skipping when Chromium is missing. No test requires a production survey or credentials.

The included `.github/workflows/test.yml` is a standalone-project Windows CI workflow. Because this directory is currently nested in AegisBrowser, GitHub will not automatically discover that nested workflow. It is ready for use when this project is its own repository root. Existing AegisBrowser workflows were not modified or claimed to run it.

## Packaging

Build on each destination OS and architecture; PyInstaller does not cross-compile.

```sh
python scripts/package.py
```

This installs a matching Chromium distribution into `build/playwright-browsers`, bundles Python/Qt/Playwright/browser assets into an onedir application, copies fixture/config/docs, and produces `dist/SurveyResearchBrowser-<platform>.zip` plus its SHA-256 sidecar. A preinstalled matching browser cache can be used with `--skip-browser-install`. Never copy only the executable: keep the entire directory and `_internal` payload. Extract before launching `SurveyResearchBrowser.exe` on Windows or `SurveyResearchBrowser` on macOS/Linux. Platform-native packages are unsigned; signing/notarization and clean-machine launch testing remain release work. Linux still requires system GUI/Chromium libraries. Do not bypass OS trust prompts or disable security controls to launch an unverified build.

No packaged executable is claimed built until PyInstaller and launch checks actually succeed. A source ZIP is a convenience archive, not a tested standalone binary.

## Implementation layout

`gui/` contains Qt controls and a dedicated QThread that owns its asyncio/Playwright runtime. Thread-safe events carry stop/pause/resume/approval; signals carry results back. `browser/` owns Chromium, proxy, navigation, diagnostics and challenge detection. `survey/` owns typed questions, explicit transition policy, mapping, validation and execution. `storage/` owns SQLAlchemy transactions. `reporting/` owns constrained JSON/HTML export. `security.py` and `configuration.py` own immutable run authorization. `tests/fixtures/` contains local, synthetic examples only.

API references used: [Playwright network interception](https://playwright.dev/python/docs/network), [BrowserContext](https://playwright.dev/python/docs/api/class-browsercontext), and [Qt QThread](https://doc.qt.io/qtforpython-6/PySide6/QtCore/QThread.html).
