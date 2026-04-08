# LCE Save Doctor

Scan and repair corrupted Minecraft Legacy Console Edition (WIN64) save files.

Drag your `saveData.ms` onto the exe — it scans every chunk in every region for corruption (failed decompression, bad headers, unreadable payloads) and offers to fix them in-place.

## How it works

1. Decompresses the `saveData.ms` zlib container
2. Iterates every `.mcr` region file entry
3. For each chunk slot: validates offset bounds, decompresses (zlib or RLE+zlib), and verifies the payload structure
4. Reports all corrupted chunks with detailed error info
5. If you choose to repair: replaces corrupted chunks with empty regenerated chunks (bedrock floor, correct coordinates), renames the original to `saveData.ms.bk`, and writes the fixed file

## Usage

```
LCESaveDoctor.exe <path-to-saveData.ms>
```

Or just drag a `saveData.ms` file onto the exe.

## Building

Requires .NET 8 SDK.

```bash
cd src/LCESaveDoctor.Cli
dotnet publish -c Release
```
The output is at `src/LCESaveDoctor.Cli/bin/Release/net8.0/win-x64/publish/LCESaveDoctor.exe` — a single self-contained ~12MB exe, no .NET runtime needed.

## Project Structure

```
src/
  LCESaveDoctor.Core/       Shared library (scanner, regenerator, LCE format parsing)
  LCESaveDoctor.Cli/        Drag-and-drop CLI exe
```

## Credits

LCE save format parsing derived from [LCE-Save-Converter](https://github.com/veroxsity/LCE-Save-Converter).

## License

MIT
