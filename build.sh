#!/bin/bash
set -e

# Function to print step information
print_step() {
    echo "-------------------------"
    echo "Step: $1"
    echo "-------------------------"
}

TARGET_OS="${1:-all}"
SKIP_MAC_ZIPS="${SKIP_MAC_ZIPS:-0}"

# Determine version from env or latest Git tag.
# Disable lazy-fetch so CI never attempts remote auth from this lookup.
VERSION="${VERSION:-$(GIT_NO_LAZY_FETCH=1 git describe --tags --abbrev=0 2>/dev/null || echo "0.0.0")}"
VERSION="${VERSION#v}"
VERSION="$(echo "$VERSION" | tr -d '\n')"

# Create dist directory if it doesn't exist
mkdir -p dist

build_macos() {
    rm -rf dist/PioneerConverter-osx-arm64 dist/PioneerConverter-osx-x64

    print_step "Building for macOS x64"
    dotnet publish PioneerConverter.csproj -c Release \
      -r osx-x64 \
      -p:PublishSingleFile=false \
      -p:PublishReadyToRun=false \
      -p:PublishTrimmed=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -p:Version="${VERSION}" \
      --self-contained true \
      -o dist/PioneerConverter-osx-x64

    mkdir -p dist/PioneerConverter-osx-x64/bin
    # For non-single-file macOS publishes, the runtime payload must live with the executable.
    find dist/PioneerConverter-osx-x64 -mindepth 1 -maxdepth 1 ! -name bin ! -name lib -exec mv {} dist/PioneerConverter-osx-x64/bin/ \;
    chmod +x dist/PioneerConverter-osx-x64/bin/PioneerConverter

    # RawFileReader's .NET assemblies are x64-only on macOS. Ship the same x64 payload
    # in the arm64 archive/package so Apple Silicon runs under Rosetta.
    cp -R dist/PioneerConverter-osx-x64 dist/PioneerConverter-osx-arm64
}

build_linux() {
    rm -rf dist/PioneerConverter-linux-x64

    print_step "Building for Linux x64"
    dotnet publish PioneerConverter.csproj -c Release \
      -r linux-x64 \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:PublishReadyToRun=false \
      -p:PublishTrimmed=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -p:Version="${VERSION}" \
      --self-contained true \
      -o dist/PioneerConverter-linux-x64

    chmod +x dist/PioneerConverter-linux-x64/PioneerConverter
    mkdir -p dist/PioneerConverter-linux-x64/bin
    mv dist/PioneerConverter-linux-x64/PioneerConverter dist/PioneerConverter-linux-x64/bin/
}

build_windows() {
    rm -rf dist/PioneerConverter-win-x64

    print_step "Building for Windows x64"
    dotnet publish PioneerConverter.csproj -c Release \
      -r win-x64 \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:PublishReadyToRun=false \
      -p:PublishTrimmed=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -p:Version="${VERSION}" \
      --self-contained true \
      -o dist/PioneerConverter-win-x64

    mkdir -p dist/PioneerConverter-win-x64/bin
    mv dist/PioneerConverter-win-x64/PioneerConverter.exe dist/PioneerConverter-win-x64/bin/
}

BUILT=()

case "$TARGET_OS" in
    macos)
        build_macos
        BUILT+=(PioneerConverter-osx-arm64 PioneerConverter-osx-x64)
        ;;
    linux)
        build_linux
        BUILT+=(PioneerConverter-linux-x64)
        ;;
    windows)
        build_windows
        BUILT+=(PioneerConverter-win-x64)
        ;;
    all)
        build_macos
        build_linux
        build_windows
        BUILT+=(PioneerConverter-osx-arm64 PioneerConverter-osx-x64 \
               PioneerConverter-linux-x64 PioneerConverter-win-x64)
        ;;
    *)
        echo "Unknown target OS: $TARGET_OS" >&2
        exit 1
        ;;
esac

print_step "Creating zip archives"
cd dist
for dir in "${BUILT[@]}"; do
    if [[ "$SKIP_MAC_ZIPS" == "1" && "$dir" == PioneerConverter-osx-* ]]; then
        echo "Skipping zip for $dir"
        continue
    fi
    archive="${dir}-${VERSION}.zip"
    rm -f "$archive"
    if command -v zip >/dev/null 2>&1; then
        zip -r "$archive" "$dir"
    elif command -v 7z >/dev/null 2>&1; then
        7z a "$archive" "$dir" >/dev/null
    elif command -v powershell.exe >/dev/null 2>&1; then
        powershell.exe -Command "Compress-Archive -Path '$dir' -DestinationPath '$archive'" >/dev/null
    else
        echo "No zip utility found" >&2
        exit 1
    fi
done
cd ..

echo "Build complete! Check the dist directory for the output files."
