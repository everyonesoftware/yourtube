# Android sign-in acceptance

The phone app starts with **Sign in to YouTube** or **Continue without signing in**. The local player also has an **Account** entry. This ports the existing experimental TV-interface flow in [GOOGLE-AUTH.md](GOOGLE-AUTH.md), not an official YouTube account or history API. Approval is explicit and happens in the user's browser; the app does not accept Google passwords or browser cookies. Review the identity and permissions shown by Google before consenting.

The first signed-in screen reads only the first page of History for the chosen YouTube identity. It is not a complete watch-event log, does not start network video playback, and never writes watch history. A failed request is not an empty feed. Account selection and the refresh credential are stored together, encrypted using an Android Keystore key. Access credentials, codes and returned History stay in memory. Sign-out clears app-local sign-in; provider access can be revoked separately in the Google account.

## Account-owner test

1. On a phone, open **YourTube → Sign in to YouTube → Open browser**. Enter the code shown in the app directly in the browser; do not share either credential or code. Check that canceling or declining leaves the app signed out. An expired code should offer a new one.
2. Try again. After approving, return to YourTube and choose the intended YouTube identity. Compare the returned first-page titles to YouTube History for the same identity. If more than one identity is available, switch and confirm the old titles are replaced, not mixed in.
3. Close and reopen the app. Verify that it restores the same identity and reads the feed again. Sign out and reopen once more; no account or history should be restored. Verify **Continue without signing in** still opens the local player.

Native tests use controlled responses and test media. Passing them does not establish live consent, account access, successful provider reads, or a published Android update.
