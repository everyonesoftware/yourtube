#!/usr/bin/env bash
# CI packaging only: never stage a debug-signed APK or receive release keys.
set -euo pipefail

[[ ${RELEASE_SIGNING_ENABLED:-} == false ]] || { printf 'Android CI must run without signing.\n' >&2; exit 1; }
[[ ${RELEASE_VERSION:-} =~ ^0\.1\.[1-9][0-9]{0,8}$ ]] || { printf 'Invalid Android build version.\n' >&2; exit 1; }
[[ ${RELEASE_ARTIFACT_DIR:-} == artifacts/android-ci ]] || { printf 'Unexpected Android staging path.\n' >&2; exit 1; }
[[ ! -L artifacts && ! -e $RELEASE_ARTIFACT_DIR && ! -L $RELEASE_ARTIFACT_DIR ]] || {
    printf 'Android staging directory must not already exist.\n' >&2; exit 1;
}
source_sha=$(git rev-parse --verify HEAD)
[[ -z ${GITHUB_SHA:-} || $source_sha == "$GITHUB_SHA" ]] || { printf 'Android checkout does not match the workflow SHA.\n' >&2; exit 1; }

bash apps/android/gradlew -p apps/android -PyourtubeBuildVersion="$RELEASE_VERSION" \
    :app:lintDebug :app:assembleDebugAndroidTest :app:assembleRelease --console=plain --max-workers=2

apk=apps/android/app/build/outputs/apk/release/app-release-unsigned.apk
sdk_bin="${ANDROID_HOME:?Android SDK required}/build-tools/36.0.0"
badging=$("$sdk_bin/aapt" dump badging "$apk")
package_line=${badging%%$'\n'*}
version_code=${RELEASE_VERSION##*.}
[[ $package_line == "package: name='com.everyonesoftware.yourtube' versionCode='$version_code' versionName='$RELEASE_VERSION' "* ]] || {
    printf 'Unexpected APK identity or version.\n' >&2; exit 1;
}
if signing_report=$(LC_ALL=C "$sdk_bin/apksigner" verify "$apk" 2>&1) ||
    [[ $signing_report != $'DOES NOT VERIFY\nERROR: Missing META-INF/MANIFEST.MF' ]]; then
    printf 'Could not confirm that the release APK is unsigned.\n' >&2
    exit 1
fi

name="YourTube-$RELEASE_VERSION-unsigned.apk"
mkdir -p -- "$RELEASE_ARTIFACT_DIR"
cp -- "$apk" "$RELEASE_ARTIFACT_DIR/$name"
(
    cd -- "$RELEASE_ARTIFACT_DIR"
    sha256sum "$name" > "$name.sha256"
    printf '{"platform":"android","packageId":"com.everyonesoftware.yourtube","version":"%s","versionCode":%s,"sourceSha":"%s","signed":false}\n' \
        "$RELEASE_VERSION" "$version_code" "$source_sha" > build.json
)
