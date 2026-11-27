# Repository instructions

- Preserve existing web/Windows/Android/Android TV mockups. They contain illustrative data only; never feed it to production clients.
- Current implementation slice is **native Windows Google device authorization + real YouTube history + read-only subscriptions (channels and latest videos)**. Subscriptions are source-implemented, not yet published or account-owner accepted. Other platforms remain planned, not silently removed.
- Android phone development has started in `apps/android/`. See `docs/ANDROID.md` for its offline playback slice and verification commands. Do not use the Windows update feed or advertise an Android release before verified Android publication.
- Project code, tests and build automation must not use Python.
- Native Windows uses C#/.NET 10 + WinUI, runtime embedded XAML, Music-style extended titlebar, Windows Credential Locker, and user-initiated Velopack Settings updates. No web wrapper or embedded Google password form.
- Authentication is an unsupported SmartTube-style YouTube TV interface experiment. Do not claim official history API support or completed authenticated acceptance without real account-owner evidence. Do not claim history cards are timestamped watch events.
- Never log or commit client credentials, device/user codes, OAuth tokens, passwords, raw authenticated responses, or browser cookies. Keep access tokens/history in memory and refresh tokens in Credential Locker. Do not weaken endpoint or video-link allowlists.
- Read `docs/POC-CONTRACT.md`, `docs/GOOGLE-AUTH.md`, `docs/WINDOWS.md`, and `THIRD-PARTY-NOTICES.md` before changing the native flow.
- Use one shared checkout. Preserve preexisting work. No commit/push unless user-authorized (Dan's build requests authorize this by standing preference).
- Test behaviors before implementing; run protocol/state/updater tests, validation contracts, native compilation, and native installed-app smoke separately. Linux compilation is not Windows runtime acceptance. Real Google consent/history remain a distinct user gate.
- Keep one main push workflow. Compare native inputs against last verified published source; docs/mockup/control-plane changes retain original Windows binaries. Publish genuine Setup/full nupkg/feed, verify remote bytes, then refresh the release description. Never overwrite published binaries.
- Live YouTube playback checks are advisory and never block releases. Keep deterministic suites, installed-app smoke and package integrity checks mandatory. Report live availability separately from native playback tested with controlled media.
- Publish every GitHub Release as a full release. Staged CI artifacts are not releases. The README has a permanent Latest release link; release tooling must not rewrite it.
- `scripts/current-downloads.mjs --write` updates only the managed table in the Windows release description; release body GET/PATCH is non-atomic with human edits. Keep CI serialization and reject visible concurrent changes.
