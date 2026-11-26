#!/usr/bin/env bash
# Never stage a debug-signed APK.
set -euo pipefail
set +x

[[ ${RELEASE_SIGNING_ENABLED:-} == true || ${RELEASE_SIGNING_ENABLED:-} == false ]] || { printf 'Invalid signing mode.\n' >&2; exit 1; }
if [[ $RELEASE_SIGNING_ENABLED == true ]]; then
    [[ ${GITHUB_EVENT_NAME:-} == workflow_dispatch && ${GITHUB_REF:-} == refs/heads/main && ${GITHUB_REPOSITORY:-} == everyonesoftware/yourtube ]] || {
        printf 'Android signing requires a manual YourTube main run.\n' >&2; exit 1;
    }
    for setting in RELEASE_KEYSTORE_BASE64 RELEASE_KEYSTORE_PASSWORD RELEASE_KEY_ALIAS RELEASE_KEY_PASSWORD; do
        [[ -n ${!setting:-} ]] || { printf 'Missing Android signing input: %s\n' "$setting" >&2; exit 1; }
    done
fi
[[ ${RELEASE_VERSION:-} =~ ^0\.1\.[1-9][0-9]{0,8}$ ]] || { printf 'Invalid Android build version.\n' >&2; exit 1; }
[[ ${RELEASE_ARTIFACT_DIR:-} == artifacts/android-ci ]] || { printf 'Unexpected Android staging path.\n' >&2; exit 1; }
[[ ! -L artifacts && ! -e $RELEASE_ARTIFACT_DIR && ! -L $RELEASE_ARTIFACT_DIR ]] || {
    printf 'Android staging directory must not already exist.\n' >&2; exit 1;
}
source_sha=$(git rev-parse --verify HEAD)
[[ -z ${GITHUB_SHA:-} || $source_sha == "$GITHUB_SHA" ]] || { printf 'Android checkout does not match the workflow SHA.\n' >&2; exit 1; }
if [[ $RELEASE_SIGNING_ENABLED == true ]]; then
    [[ ${GITHUB_SHA:-} == "$source_sha" && -z $(git status --porcelain --untracked-files=all) ]] || {
        printf 'Signed Android packaging requires the exact, clean workflow checkout.\n' >&2; exit 1;
    }
fi

env -u RELEASE_KEYSTORE_BASE64 -u RELEASE_KEYSTORE_PASSWORD -u RELEASE_KEY_ALIAS -u RELEASE_KEY_PASSWORD \
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
fingerprint=null
if [[ $RELEASE_SIGNING_ENABLED == true ]]; then
    umask 077
    private=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/yourtube-signing.XXXXXXXX")
    trap 'rm -rf -- "$private"' EXIT
    printf '%s' "$RELEASE_KEYSTORE_BASE64" | base64 --decode > "$private/key.jks"
    unset RELEASE_KEYSTORE_BASE64
    name="YourTube-$RELEASE_VERSION.apk"
    "$sdk_bin/apksigner" sign --ks "$private/key.jks" --ks-type JKS --ks-key-alias "$RELEASE_KEY_ALIAS" \
        --ks-pass env:RELEASE_KEYSTORE_PASSWORD --key-pass env:RELEASE_KEY_PASSWORD --out "$private/$name" "$apk"
    unset RELEASE_KEYSTORE_PASSWORD RELEASE_KEY_PASSWORD RELEASE_KEY_ALIAS
    # Public certificate; private signing material remains in GitHub Actions secrets.
    signer=a33a5795b34a59d9c7df53f787c2c112508cd7c81d5be78392a9e6a247ab21ee
    verified=$(LC_ALL=C "$sdk_bin/apksigner" verify --verbose --print-certs "$private/$name")
    signers=$(printf '%s\n' "$verified" | awk '/^Signer #[0-9]+ certificate SHA-256 digest: / {print $NF}')
    [[ $signers == "$signer" ]] || { printf 'Unexpected Android signer.\n' >&2; exit 1; }
    apk="$private/$name"
    fingerprint="\"$signer\""
    umask 022
fi
mkdir -p -- "$RELEASE_ARTIFACT_DIR"
cp -- "$apk" "$RELEASE_ARTIFACT_DIR/$name"
(
    cd -- "$RELEASE_ARTIFACT_DIR"
    sha256sum "$name" > "$name.sha256"
    printf '{"platform":"android","packageId":"com.everyonesoftware.yourtube","version":"%s","versionCode":%s,"sourceSha":"%s","signed":%s,"signerSha256":%s}\n' \
        "$RELEASE_VERSION" "$version_code" "$source_sha" "$RELEASE_SIGNING_ENABLED" "$fingerprint" > build.json
)
