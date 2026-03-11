# Linux Installer

The `build_deb.sh` script creates a Debian package that installs the application under `/usr/local/PioneerConverter`, with the executable and its self-contained runtime payload under `/usr/local/PioneerConverter/bin`, and places a wrapper script `PioneerConverter` in `/usr/local/bin`.

## Building

Run the script on a Debian-based system:

```bash
./build_deb.sh
```

The resulting `PioneerConverter-linux-x64-<version>.deb` can be installed
with `dpkg -i` where `<version>` is the release tag.
