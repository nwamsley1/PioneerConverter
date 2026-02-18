<p align="center">
  <img src="assets/Converter.svg" alt="PioneerConverter Logo" width="256" height="256"/>
</p>

# PioneerConverter

Convert Thermo `.raw` files into [Apache Arrow](https://arrow.apache.org/) tables ready for [Pioneer](https://github.com/nwamsley1/Pioneer.jl). The tool uses Thermo's [RawFileReader](https://github.com/thermofisherlsms/RawFileReader) and runs on Windows, macOS and Linux.

## Install

There are three ways to get **PioneerConverter**:

1. **Installers** – Download the installer for your platform from the releases page. Running the installer puts a `PioneerConverter` command on your `PATH`. Installers are available for:
    - macOS&nbsp;M1/M2 (ARM64)
    - macOS&nbsp;Intel (x64)
    - Linux (x64)
    - Windows (x64). 

   On Apple Silicon, Thermo RawFileReader is x64-only, so PioneerConverter runs via Rosetta 2.
   The macOS arm64 installer will install Rosetta automatically when needed.
3. **Precompiled binaries** – Zipped binaries are available for Linux, macOS, and Windows. Each archive contains a `bin` directory with the `PioneerConverter` executable and a `lib` directory with its dependencies. Extract them anywhere and, on Linux or macOS, you may need to run `chmod +x bin/PioneerConverter` before executing.
4. **Build from source** – If you prefer to build the tool yourself, follow the steps in the [Build from source](#build-from-source) section below.

## Usage

Provide a single `.raw` file or a directory containing them. By default, converted Arrow tables are written to `arrow_out` inside the input directory.

```bash
# convert a single file
PioneerConverter path/to/file.raw

# convert a directory with options
PioneerConverter path/to/dir -b 5000 -n 4

# convert and write output to a custom directory
PioneerConverter path/to/dir -o path/to/output

# incremental conversion: only process new .raw files
PioneerConverter path/to/dir --skip-existing

# print converter version
PioneerConverter --version
```

Options
- `-b, --batch-size`  number of scans per batch (default: 10000)
- `-o, --output-dir` output directory for `.arrow` files (default: `<input_dir>/arrow_out`)
- `--skip-existing` skip files whose existing `.arrow` output appears complete
- `-n, --concurrent-files` number of files to convert at the same time (default: 2)
- `-t, --threads-per-file` scan extraction threads used for each file (default: 3)
- `--scan-chunk-size` scan chunk size when using scan threads (default: 128)
- `--version` show program version
- `-h, --help`        show help

## Build from source

1. Install [.NET&nbsp;8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone the repository:
   ```bash
   git clone https://github.com/nwamsley1/PioneerConverter.git
   cd /path/to/PioneerConverter
   ```
3. Compile:
   ```bash
   chmod +x build.sh   # optional, only if you're getting permission error
   ./build.sh          # or ./build.sh macos|linux|windows
   ```

## CI validation

GitHub Actions validates published binaries in `validate-binaries.yml` across:

- Linux x64
- Windows x64
- macOS Intel (x64)
- macOS Apple Silicon (arm64)

Validation always checks binary startup and runs conversion smoke tests using
`tests/fixtures/raw/smoke.raw`.

## Output format

Each row in the Arrow table corresponds to a scan in the `.raw` file:

| Column Name | Data Type | Description |
|------------|-----------|-------------|
| mz_array | Vector{Union{Missing, Float32}} | List of masses for peaks in the centroided spectra |
| intensity_array | Vector{Union{Missing, Float32}} | List of intensities for peaks in the centroided spectra |
| scanHeader | String | A description of the scan |
| scanNumber | Int32 | Scan index of i'th scan in the `.raw` file |
| basePeakMz | Float32 | m/z of the base peak |
| basePeakIntensity | Float32 | Intensity of the base peak |
| packetType | Int32 | |
| retentionTime | Float32 | Retention time of the scan |
| lowMz | Float32 | Lowest m/z in the scan range |
| highMz | Float32 | Highest m/z in the scan range |
| TIC | Float32 | Total ion current |
| centerMz | Union{Float32, Missing} | For an MSn scan, the m/z center of the quadrupole isolation window |
| isolationWidthMz | Union{Float32, Missing} | For an MSn scan, the m/z width of the quadrupole isolation window |
| collisionEnergyField | Union{Float32, Missing} | Normalized collision energy |
| collisionEnergyEvField | Union{Float32, Missing} | Collision energy (eV) |
| msOrder | UInt8 | The n for an MSn scan |
