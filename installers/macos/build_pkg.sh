#!/bin/bash
set -e

APPNAME="PioneerConverter"
VERSION="${VERSION:-1.0.0}"
PKGROOT="pkgroot"
PKG_ARCH="${PKG_ARCH:-$(uname -m)}"
PKGSCRIPTS=""

case "$PKG_ARCH" in
  arm64|aarch64)
    DIST="../../dist/${APPNAME}-osx-arm64"
    PKGFILE="${APPNAME}-arm64-${VERSION}.pkg"
    PKGSCRIPTS="$(dirname "$0")/scripts/arm64"
    ;;
  x64|x86_64)
    DIST="../../dist/${APPNAME}-osx-x64"
    PKGFILE="${APPNAME}-x64-${VERSION}.pkg"
    ;;
  *)
    echo "Unsupported architecture: $PKG_ARCH" >&2
    exit 1
    ;;
esac

rm -rf "$PKGROOT"
mkdir -p "$PKGROOT/usr/local/$APPNAME"
mkdir -p "$PKGROOT/usr/local/bin"

cp -R "$DIST"/* "$PKGROOT/usr/local/$APPNAME/"

cat <<WRAP > "$PKGROOT/usr/local/bin/PioneerConverter"
#!/bin/bash
/usr/local/$APPNAME/bin/PioneerConverter "\$@"
WRAP
chmod +x "$PKGROOT/usr/local/bin/PioneerConverter"

if [[ -n "$CODESIGN_IDENTITY" ]]; then
  echo "Codesigning binaries"
  while IFS= read -r -d '' file; do
      if file "$file" | grep -q 'Mach-O'; then
        codesign --verbose=4 --force --options runtime --timestamp \
          --entitlements "$(dirname "$0")/entitlements.plist" \
          --sign "$CODESIGN_IDENTITY" "$file"
      fi
    done < <(find "$PKGROOT/usr/local/$APPNAME" -type f -print0)
fi

UNSIGNED="${PKGFILE%.pkg}-unsigned.pkg"
PKGBUILD_EXTRA_ARGS=()

if [[ -n "$PKGSCRIPTS" ]]; then
  PKGBUILD_EXTRA_ARGS+=(--scripts "$PKGSCRIPTS")
fi

pkgbuild --root "$PKGROOT" \
  --identifier "edu.washu.goldfarblab.pioneerconverter" \
  --version "$VERSION" \
  --install-location "/" \
  "${PKGBUILD_EXTRA_ARGS[@]}" \
  "$UNSIGNED"

if [[ -n "$PKG_SIGN_IDENTITY" ]]; then
  echo "Signing package"
  productsign --sign "$PKG_SIGN_IDENTITY" \
    "$UNSIGNED" "$PKGFILE"
  rm "$UNSIGNED"
else
  mv "$UNSIGNED" "$PKGFILE"
fi

echo "Package created: $PKGFILE"
