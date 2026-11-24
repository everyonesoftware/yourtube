# YourTube

A personal YouTube client. The first native Windows proof of concept implements SmartTube-style Google device authorization and read-only YouTube history browsing. Google consent and real account history must be tested by the account owner; this is an unsupported TV-interface experiment, not an official YouTube Data API integration.

## Current downloads

<!-- current-downloads:start -->
| Platform | Version | Download | Status |
| --- | --- | --- | --- |
| Windows x64 | 0.1.51 | [Installer](https://github.com/everyonesoftware/yourtube/releases/download/v0.1.51/YourTubeWindows-win-x64-Setup.exe) · [SHA-256](https://github.com/everyonesoftware/yourtube/releases/download/v0.1.51/YourTubeWindows-win-x64-Setup.exe.sha256) | Proof of concept; authenticated account acceptance pending |
<!-- current-downloads:end -->

## Windows history proof of concept

1. Install the Windows build from the table above when published, then open **YourTube**.
2. Choose **Sign in**, open the displayed Google/YouTube activation page in your browser, and enter the code shown **in the app**. Do not share the code or your password in chat. Review Google's device/permission description before consenting; cancel if it is not acceptable.
3. Select your YouTube account/channel, then refresh history. **Load more** retrieves the next page.
4. **Sign out** clears the local credential and displayed history. Revoke the device's Google access separately through your Google account if desired.

No browser extension, embedded password form, Google Cloud client registration, or exported browser cookies. A refresh token is stored in Windows Credential Locker; the access token and fetched history stay in memory. The app retrieves video cards/resume information, **not a complete timestamped watch-event export**. Empty, paused, unavailable, and signed-out history must not be mistaken for a working feed. Google may change or reject this private TV-client flow.

The native Windows **Subscriptions** view shows subscribed channel names/new-content markers and the latest TV subscription video feed, with **Refresh** and video **Load more** under the same selected account. Authenticated subscriptions acceptance remains an account-owner gate; see [subscriptions protocol and validation](docs/SUBSCRIPTIONS.md) for evidence and limitations.

See [authentication details](docs/GOOGLE-AUTH.md), [build and verification](docs/WINDOWS.md), and [third-party notices](THIRD-PARTY-NOTICES.md). Other platforms remain mockups/planned and are not part of this Windows proof of concept.

## Target platforms

All four platforms are in scope for both mockups and production builds:

| Platform | Mockup | Production build |
| --- | --- | --- |
| Web | Existing interactive concept | Planned |
| Windows | Existing interactive concept | Native history proof of concept |
| Android (phone/tablet) | Existing phone concept; tablet adaptation planned | Planned |
| Android TV | Interactive keyboard-only TV concept | Planned |

Android TV requires its own TV-oriented mockup and interface, designed for remote/D-pad navigation, visible focus, readable viewing-distance typography, video browsing, playback, and account sign-in. Do not treat the phone mockup as the TV design.

## Current mockups

- Artifact: `mockups/yourtube-concept.html`
- Rebuild: `node mockups/build.mjs`
- Serve: `node mockups/serve.mjs` (port 8841)

The current artifact covers web, Windows, Android phone, and Android TV. Select **Android TV** in the viewer, or append `?view=tv` to open directly on the TV. Use arrow keys to navigate, Enter to select/play/pause, Escape or Backspace to go back, and Tab to leave the TV focus area. Mouse, touch, and wheel input do not operate the TV screen. Search includes a D-pad keyboard; playback and account pairing are simulated.

Browser checks: `npm ci && npm run test:mockups` with the mockup server running. The tests use Playwright; set `CHROMIUM_PATH` to a local Chromium executable when needed. Web, Android, and Android TV production clients remain planned.

All accounts, feeds, video metadata, and playback in the mockups are illustrative. History represents activity watched in this client, not an imported YouTube watch-history feed. Saved items are client-managed.
