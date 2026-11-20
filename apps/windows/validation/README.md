# YourTube native UI validation

The application consumes `docs/POC-CONTRACT.md` through a thin adapter over the real `YourTube.Core.YouTubeClient`. All fixture cards, tokens and delayed services are confined to this validation project. Production never uses them.

## Executable checks

From the repository root, with .NET 10 available:

```sh
dotnet run --project apps/windows/validation/YourTube.App.Validation.csproj
```

The runner exercises session restoration, refresh-token persistence, device authorization cancellation, stale history/account/refresh completions, pagination, safe structured errors, transport-error sanitization and close-versus-sign-out behavior. It also parses the actual embedded XAML, matches typed control lookups, requires native action wiring and checks self-contained publish defaults. No credentials or network are needed.

A Linux **compile-only** gate against the real pinned WinUI/Velopack packages:

```sh
dotnet build apps/windows/YourTube.App/YourTube.App.csproj -c Release \
  -p:OutputType=Library -p:WindowsAppSDKSelfContained=false \
  -p:SelfContained=false -p:PrepareForRunDependsOn=CopyFilesToOutputDirectory \
  -warnaserror

dotnet run --project apps/windows/validation/YourTube.App.Validation.csproj -- \
  "$PWD" "$PWD/apps/windows/YourTube.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/YourTube.dll"
```

The optional second runner argument checks the emitted Windows x64 PE, version `0.1.1.0` and actual `YourTube.App.Shell.xaml` manifest resource. For a CI `-p:Version=0.1.N` build, pass `0.1.N` as the third runner argument; assembly/file versions derive from that property, and Settings reads the running assembly version. The Linux build is a library for verification, **not** a runnable release or distributable. Native defaults remain a self-contained `YourTube.exe`; use the parent's Windows packaging pipeline. The standard native build starts with:

```powershell
dotnet publish apps/windows/YourTube.App/YourTube.App.csproj -c Release -r win-x64
```

## Windows acceptance still required

- Launch the native published/installed build; confirm runtime XAML, theme resources, merged PRI, taskbar identity and caption buttons resolve.
- Verify titlebar dragging, active/inactive colors, maximize/restore, keyboard operation, DPI scaling, focus and screen-reader announcements.
- Confirm clean-machine startup stays in restoration until Windows Credential Manager resolves. Retry and sign out after vault failures; verify only this app's resource/user pair is removed.
- Dan must explicitly authorize in the native app. Confirm no browser opens until **Open activation page in browser**, only allowlisted HTTPS activation pages launch, cancellation/expiry works, and no sensitive values appear in logs or chat.
- Observe actual account enumeration and actual signed-in history. Switch accounts while loading; refresh, load more and interrupt requests. Confirm old responses never repopulate history or stored credentials.
- Close/reopen to verify PasswordVault restoration and refresh rotation. Sign out/reopen to verify private state is gone. Closing is not signing out.
- Exercise recycled native ListView rows, pointer/keyboard video activation, invalid-ID rejection and canonical video URLs. History cards are not timestamped watch events.
- Exercise the parent's Settings updater and installed-app lifecycle smoke separately. The shell hosts and disposes `WindowsUpdatesPanel`, with startup auto-apply disabled before WinUI initialization.

Compile/source/state-test success does not establish native launch, credential-vault behavior, installed update acceptance or signed-in account access.
