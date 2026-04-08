# LCE Save Doctor

Scan and repair corrupted Minecraft Legacy Console Edition (WIN64) save files.

Drag your `saveData.ms` onto the exe — it scans every chunk in every region for corruption and offers to fix them in-place.

## Download

Grab the latest `LCESaveDoctor.exe` from [Releases](https://github.com/veroxsity/LCE-Save-Doctor/releases). No .NET runtime needed — it's a self-contained single-file exe.

## What it does

1. Decompresses the `saveData.ms` zlib container
2. Iterates every `.mcr` region file entry inside the container
3. For each chunk slot: validates offset bounds, tries decompression (zlib or RLE+zlib), verifies the payload structure can be read
4. Reports all corrupted chunks with detailed error info
5. If you choose to repair:
   - Replaces corrupted chunks with empty regenerated chunks (bedrock floor at Y=0, correct chunk coordinates, full skylight)
   - Renames the original file to `saveData.ms.bk`
   - Writes the fixed `saveData.ms` in the same directory

## Usage

**Drag and drop** — drag a `saveData.ms` file onto `LCESaveDoctor.exe`

**Command line:**
```
LCESaveDoctor.exe C:\path\to\saveData.ms
```

Example output:
```
=== LCE Save Doctor ===

File:      saveData.ms
Directory: C:\Users\You\LCE Saves

Reading saveData.ms... OK (14832KB)
Scanning chunks... Done

  Regions:           4
  Total chunk slots: 4096
  Empty slots:       3012
  Healthy chunks:    1081
  Corrupted chunks:  3

Corrupted chunks:
  chunk (12, -5) in r.0.-1.mcr
    Decompression failed: Invalid data
  chunk (12, -4) in r.0.-1.mcr
    Compressed data overflows region
  chunk (13, -5) in r.0.-1.mcr
    Failed to read chunk coordinates from payload

Fix 3 corrupted chunk(s)? (y/n): y

Regenerating corrupted chunks... Done!

  Backup:  C:\Users\You\LCE Saves\saveData.ms.bk
  Fixed:   C:\Users\You\LCE Saves\saveData.ms
```

## Corruption detection

The scanner checks each chunk for:
- Offset table entries pointing outside the region file
- Zero or negative compressed length values
- Compressed data extending past the end of the region
- Failed zlib or RLE+zlib decompression
- Payloads that don't match either legacy NBT or compressed chunk storage format

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd src/LCESaveDoctor.Cli
dotnet publish -c Release
```

Output: `bin/Release/net8.0/win-x64/publish/LCESaveDoctor.exe`

## Project structure

```
src/
  LCESaveDoctor.Core/       Shared library (scanner, regenerator, LCE format IO)
  LCESaveDoctor.Cli/        Drag-and-drop CLI exe
```

## Credits

LCE save format parsing derived from [LCE-Save-Converter](https://github.com/veroxsity/LCE-Save-Converter).

## License

MIT
