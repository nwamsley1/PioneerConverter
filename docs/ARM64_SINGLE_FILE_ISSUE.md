# ARM64 Single-File Publishing Issue

## Problem

When running PioneerConverter on macOS ARM64 (Apple Silicon), the application fails with:

```
System.IO.FileNotFoundException: Could not load file or assembly
'ThermoFisher.CommonCore.RawFileReader, Version=7.1.21.0, Culture=neutral,
PublicKeyToken=1aef06afb5abd953'. The system cannot find the file specified.
```

This affects:
- GitHub release pkg files for ARM64
- Local builds from source on ARM64 Macs

Windows x64 and macOS x64 builds work correctly.

## Root Cause

This is a .NET SDK bug where `PublishSingleFile=true` does not properly embed manually-referenced assemblies (via `<Reference>` with `HintPath`) when building **natively** on ARM64.

### Build Behavior Comparison

| Build Method | Host | Target | Single-File Works? |
|--------------|------|--------|-------------------|
| Cross-compile | Linux x64 | osx-arm64 | **Yes** - assemblies embedded |
| Cross-compile | ARM64 Mac | osx-x64 | **Yes** - assemblies embedded |
| Native compile | ARM64 Mac | osx-arm64 | **No** - assemblies NOT embedded |

### Evidence

On an ARM64 Mac after running `./build.sh macos`:

```bash
# x64 build (cross-compiled) - WORKS
$ ./dist/PioneerConverter-osx-x64/bin/PioneerConverter test.raw
Execution Time: 9706 ms

# ARM64 build (native) - FAILS
$ ./dist/PioneerConverter-osx-arm64/bin/PioneerConverter test.raw
System.IO.FileNotFoundException: Could not load file or assembly 'ThermoFisher.CommonCore.RawFileReader...'
```

### Why CI Builds Fail

The GitHub Actions workflow:
1. `ubuntu-latest` cross-compiles ARM64 binary → **correct** (assemblies embedded)
2. `macos-latest` rebuilds ARM64 natively via `./build_installers.sh` → **broken** (assemblies not embedded)
3. The pkg installer uses the broken native ARM64 build

## Workarounds

### For Users (Immediate)

Use the x64 build on ARM64 Macs - it runs via Rosetta 2 with minimal performance impact:
```bash
# Download the x64 pkg instead of ARM64
```

### For CI (Permanent Fix)

Modify the GitHub Actions workflow so `macos-latest` downloads the pre-built ARM64 binaries from `ubuntu-latest` instead of rebuilding them natively. This ensures the pkg installers use correctly cross-compiled binaries.

**Changes needed in `.github/workflows/build.yml`:**
1. Have `ubuntu-latest` upload all binaries as artifacts
2. Have `macos-latest` download those artifacts instead of rebuilding
3. Only run the pkg creation scripts, not the full build

### Alternative: Use NuGet Packages

The ThermoFisher libraries are available as NuGet packages. Using `<PackageReference>` instead of `<Reference>` with `HintPath` may resolve the bundling issue, as NuGet packages are handled differently by the build system.

## Related Issues

- [dotnet/sdk#14489](https://github.com/dotnet/sdk/issues/14489) - PublishSingleFile doesn't produce true single file
- [dotnet/sdk#10969](https://github.com/dotnet/sdk/issues/10969) - HintPath of local assembly reference is ignored
- [dotnet/runtime#74973](https://github.com/dotnet/runtime/issues/74973) - Cross-compilation issues on M1 Mac

## Files Involved

- `build.sh` - Build script with `PublishSingleFile=true`
- `build_installers.sh` - Calls `./build.sh all` which rebuilds on macOS
- `.github/workflows/build.yml` - CI workflow that rebuilds on `macos-latest`
- `PioneerConverter.csproj` - Uses `<Reference>` with `HintPath` for ThermoFisher DLLs
