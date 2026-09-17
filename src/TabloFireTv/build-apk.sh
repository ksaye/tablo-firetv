#!/usr/bin/env bash
# Build a signed Tablo for Fire TV APK.
#
# Usage:  ./build-apk.sh <version> <versionCode>
#         ./build-apk.sh 1.0.0 1
#
# <versionCode> is Android's integer version and must increase with every release.
#
# SIGNING: Android only installs an update over an app signed with the SAME key, so keep the
# keystore safe and reuse it for every release. Create one once:
#
#   keytool -genkeypair -v -keystore tablofiretv.keystore -alias tablofiretv \
#           -keyalg RSA -keysize 2048 -validity 10000
#
# and point these at it (environment, or a file named by TABLOFIRETV_KEYSTORE_ENV):
#
#   TABLOFIRETV_KEYSTORE=/path/to/tablofiretv.keystore
#   TABLOFIRETV_KEYSTORE_PASS=...
#
# Needs the .NET 10 SDK with the Android workload (`dotnet workload install maui-android`),
# a JDK 17 and the Android SDK. JAVA_HOME and ANDROID_HOME are honoured.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION=${1:?version, e.g. 1.0.0}
BUILD=${2:?versionCode, e.g. 1}

# shellcheck source=/dev/null
[ -n "${TABLOFIRETV_KEYSTORE_ENV:-}" ] && [ -f "$TABLOFIRETV_KEYSTORE_ENV" ] && source "$TABLOFIRETV_KEYSTORE_ENV"
: "${TABLOFIRETV_KEYSTORE:?set TABLOFIRETV_KEYSTORE}"
: "${TABLOFIRETV_KEYSTORE_PASS:?set TABLOFIRETV_KEYSTORE_PASS}"

ARGS=()
[ -n "${ANDROID_HOME:-}" ] && ARGS+=("-p:AndroidSdkDirectory=$ANDROID_HOME")
[ -n "${JAVA_HOME:-}" ] && ARGS+=("-p:JavaSdkDirectory=$JAVA_HOME")

dotnet publish "$HERE/TabloFireTv.csproj" \
  -c Release \
  -f net10.0-android \
  -p:AcceptAndroidSDKLicenses=True \
  -p:ApplicationDisplayVersion="$VERSION" \
  -p:ApplicationVersion="$BUILD" \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore="$TABLOFIRETV_KEYSTORE" \
  -p:AndroidSigningStorePass="$TABLOFIRETV_KEYSTORE_PASS" \
  -p:AndroidSigningKeyPass="$TABLOFIRETV_KEYSTORE_PASS" \
  -p:AndroidSigningKeyAlias=tablofiretv \
  "${ARGS[@]}"

# The signed APK, newest first.
find "$HERE/bin/Release" -name '*-Signed.apk' -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-
