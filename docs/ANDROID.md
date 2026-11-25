# YourTube for Android phones

The first development build is a native Android player, not a web wrapper. It can open a video selected from Android's file picker without broad storage permission. The app does not accept network URLs or request Internet permission yet. The phone build does not implement Google sign-in, Home, Subscriptions, History, or history reporting. Android TV and public APK publication are out of scope for this slice.

## Build and verify

Use JDK 17 or newer and Android SDK Platform 36. The Gradle wrapper pins the tooling. Android application ID: `com.everyonesoftware.yourtube`; minimum API 26.

```sh
bash apps/android/gradlew -p apps/android :app:assembleDebug :app:assembleDebugAndroidTest :app:lintDebug
adb -s <task-owned-serial> install -r apps/android/app/build/outputs/apk/debug/app-debug.apk
adb -s <task-owned-serial> install -r apps/android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb -s <task-owned-serial> shell am instrument -w -r com.everyonesoftware.yourtube.test/androidx.test.runner.AndroidJUnitRunner
```

Require a successful JUnit result, not only a zero `adb` exit code. The test APK packages the existing synthetic H.264/AAC DASH tracks; the application APK does not. The tests cover first rendered frame, selection of both tracks, clock advancement, paused seek and resume, plus player release on backgrounding and paused return at the checkpoint. They do not exercise a YouTube watch URL, physical speaker output, or activity recreation. A development APK is not the public release identity.

## Next acceptance gates

1. Pass a validated public video ID to credential-free resolution, attach only resolver-owned resources to the native player, and test one known public video on a real phone. Preserve Windows' transport boundaries for signed URLs, redirects, range validation, cancellation and sanitized errors. Do not treat the fixture as provider evidence.
2. Separately verify browser-based owner authorization, identity selection, and read-only feeds. Keep refresh credentials protected by Android Keystore, never expose them to playback, and clear private data on sign-out. Do not claim owner acceptance from fixture tests.
3. Establish independent Android versioning/signing and a verified release feed before wiring update discovery or advertising an Android download. Do not reuse the Windows package feed.

## CI artifact (not a public release)

The main **Build and release** workflow calls the pinned `workflows-common` Android job. It runs Android lint, compiles the instrumentation APK, and packages a release-variant **unsigned** APK. On pushes it compares Android sources, build tooling, the main workflow, and the shared playback fixture with the newest ancestral source found in the ten most recent successful main workflow runs that had an exact, non-expired Android artifact. If no baseline qualifies, it builds. Manual dispatch always selects Android. Use **Re-run failed jobs** to retry a failed CI build. The existing Windows publisher neither downloads this artifact nor adds it to GitHub Releases or the README's Current downloads table.

The run retains `android-unsigned-<source-sha>` with `YourTube-0.1.<run number>-unsigned.apk`, its SHA-256 sidecar, and `build.json` (exact source, package ID, version name/code, unsigned state). This artifact is for validation, not installation. The CI job receives no signing secrets and does not execute the emulator tests. Public distribution requires an approved YourTube-only signing identity, verified signed packaging, native smoke, and separate Android publication baselines and inventory.
