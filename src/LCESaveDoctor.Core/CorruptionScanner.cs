using System.IO.Compression;
using System.Text;
using LCESaveDoctor.LceFormat;

namespace LCESaveDoctor;

public enum ChunkHealth
{
    Healthy,
    Corrupted,
    Missing
}

public sealed class ChunkDiagnosis
{
    public required string RegionEntry { get; init; }
    public required int LocalX { get; init; }
    public required int LocalZ { get; init; }
    public required int ChunkX { get; init; }
    public required int ChunkZ { get; init; }
    public required ChunkHealth Health { get; init; }
    public string? ErrorDetail { get; init; }
}

public sealed class ScanReport
{
    public int TotalRegions { get; set; }
    public int TotalChunkSlots { get; set; }
    public int EmptySlots { get; set; }
    public int HealthyChunks { get; set; }
    public int CorruptedChunks { get; set; }
    public List<ChunkDiagnosis> Corrupted { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Scans an LCE saveData.ms blob for corrupted chunks.
/// Works entirely in memory — no disk I/O after initial load.
/// </summary>
public static class CorruptionScanner
{
    private const int RegionSectorBytes = 4096;
    private const int ChunkHeaderBytes = 8;
    private const int FileEntrySize = 144;

    public static ScanReport Scan(byte[] rawBlob)
    {
        var report = new ScanReport();
        var entries = ParseEntries(rawBlob);

        var regionEntries = entries
            .Where(e => e.Name.EndsWith(".mcr", StringComparison.OrdinalIgnoreCase))
            .ToList();

        report.TotalRegions = regionEntries.Count;

        foreach (var entry in regionEntries)
        {
            byte[] regionBytes;
            try
            {
                regionBytes = new byte[entry.Length];
                Buffer.BlockCopy(rawBlob, entry.StartOffset, regionBytes, 0, entry.Length);
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Failed to read region {entry.Name}: {ex.Message}");
                continue;
            }

            if (!TryParseRegionCoords(entry.Name, out int regionX, out int regionZ))
            {
                report.Warnings.Add($"Couldn't parse region coords from {entry.Name}");
                continue;
            }

            ScanRegion(regionBytes, entry.Name, regionX, regionZ, report);
        }

        return report;
    }

    private static void ScanRegion(byte[] regionBytes, string entryName, int regionX, int regionZ, ScanReport report)
    {
        for (int localZ = 0; localZ < 32; localZ++)
        {
            for (int localX = 0; localX < 32; localX++)
            {
                report.TotalChunkSlots++;

                int slotIndex = localX + localZ * 32;
                int entryOffset = slotIndex * 4;

                if (entryOffset + 4 > regionBytes.Length)
                {
                    report.EmptySlots++;
                    continue;
                }

                uint offsetEntry = BitConverter.ToUInt32(regionBytes, entryOffset);
                if (offsetEntry == 0)
                {
                    report.EmptySlots++;
                    continue;
                }

                int chunkX = regionX * 32 + localX;
                int chunkZ = regionZ * 32 + localZ;

                var diagnosis = TryValidateChunk(regionBytes, entryName, localX, localZ, chunkX, chunkZ);

                if (diagnosis.Health == ChunkHealth.Healthy)
                    report.HealthyChunks++;
                else
                {
                    report.CorruptedChunks++;
                    report.Corrupted.Add(diagnosis);
                }
            }
        }
    }

    private static ChunkDiagnosis TryValidateChunk(byte[] regionBytes, string entryName, int localX, int localZ, int chunkX, int chunkZ)
    {
        try
        {
            int slotIndex = localX + localZ * 32;
            int entryOffset = slotIndex * 4;
            uint offsetEntry = BitConverter.ToUInt32(regionBytes, entryOffset);

            int sectorOffset = (int)(offsetEntry >> 8);
            int chunkPos = sectorOffset * RegionSectorBytes;

            if (chunkPos + ChunkHeaderBytes > regionBytes.Length)
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    $"Chunk header out of bounds (offset {chunkPos}, region size {regionBytes.Length})");
            }

            uint compressedLengthRaw = BitConverter.ToUInt32(regionBytes, chunkPos);
            bool usesRle = (compressedLengthRaw & 0x80000000) != 0;
            int compressedLength = (int)(compressedLengthRaw & 0x7FFFFFFF);
            int decompressedLength = (int)BitConverter.ToUInt32(regionBytes, chunkPos + 4);

            if (compressedLength <= 0)
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    "Compressed length is zero or negative");
            }

            if (chunkPos + ChunkHeaderBytes + compressedLength > regionBytes.Length)
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    $"Compressed data overflows region (needs {compressedLength} bytes at offset {chunkPos + ChunkHeaderBytes})");
            }

            byte[] compressed = new byte[compressedLength];
            Buffer.BlockCopy(regionBytes, chunkPos + ChunkHeaderBytes, compressed, 0, compressedLength);

            byte[] decompressed;
            try
            {
                decompressed = usesRle
                    ? LceCompression.Decompress(compressed, decompressedLength)
                    : LceCompression.DecompressZlibOnly(compressed);
            }
            catch (Exception ex)
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    $"Decompression failed: {ex.Message}");
            }

            if (decompressed.Length == 0)
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    "Decompressed to zero bytes");
            }

            if (!LceChunkPayloadCodec.TryReadChunkCoordinates(decompressed, out _, out _, out _))
            {
                return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                    "Failed to read chunk coordinates from payload — structure unrecognisable");
            }

            return new ChunkDiagnosis
            {
                RegionEntry = entryName,
                LocalX = localX,
                LocalZ = localZ,
                ChunkX = chunkX,
                ChunkZ = chunkZ,
                Health = ChunkHealth.Healthy
            };
        }
        catch (Exception ex)
        {
            return MakeCorrupt(entryName, localX, localZ, chunkX, chunkZ,
                $"Unexpected error: {ex.Message}");
        }
    }

    private static ChunkDiagnosis MakeCorrupt(string entry, int lx, int lz, int cx, int cz, string detail) =>
        new()
        {
            RegionEntry = entry,
            LocalX = lx,
            LocalZ = lz,
            ChunkX = cx,
            ChunkZ = cz,
            Health = ChunkHealth.Corrupted,
            ErrorDetail = detail
        };

    #region Container Parsing

    public static byte[] DecompressContainer(byte[] containerBytes)
    {
        if (containerBytes.Length < 8)
            throw new InvalidDataException("Invalid saveData.ms: too small");

        int compressedFlag = BitConverter.ToInt32(containerBytes, 0);
        if (compressedFlag != 0)
            return containerBytes;

        using var compressed = new MemoryStream(containerBytes, 8, containerBytes.Length - 8);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static List<ContainerFileEntry> ParseEntries(byte[] rawBlob)
    {
        var entries = new List<ContainerFileEntry>();
        if (rawBlob.Length < 12) return entries;

        int tableOffset = (int)BitConverter.ToUInt32(rawBlob, 0);
        int fileCount = (int)BitConverter.ToUInt32(rawBlob, 4);

        if (tableOffset < 0 || tableOffset >= rawBlob.Length || fileCount < 0)
            return entries;

        int pos = tableOffset;
        for (int i = 0; i < fileCount; i++)
        {
            if (pos + FileEntrySize > rawBlob.Length) break;

            string name = Encoding.Unicode.GetString(rawBlob, pos, 128).TrimEnd('\0');
            pos += 128;

            int length = (int)BitConverter.ToUInt32(rawBlob, pos);
            pos += 4;

            int startOffset = (int)BitConverter.ToUInt32(rawBlob, pos);
            pos += 4;

            long modified = BitConverter.ToInt64(rawBlob, pos);
            pos += 8;

            if (string.IsNullOrWhiteSpace(name) || length <= 0) continue;
            if (startOffset < 0 || startOffset + length > rawBlob.Length) continue;

            entries.Add(new ContainerFileEntry
            {
                Name = name,
                Length = length,
                StartOffset = startOffset,
                LastModifiedTime = modified
            });
        }

        return entries;
    }

    public static bool TryParseRegionCoords(string filename, out int regionX, out int regionZ)
    {
        regionX = 0;
        regionZ = 0;
        string withoutExt = Path.GetFileNameWithoutExtension(filename.Replace('/', Path.DirectorySeparatorChar));
        string[] parts = withoutExt.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        return int.TryParse(parts[^2], out regionX) && int.TryParse(parts[^1], out regionZ);
    }

    #endregion
}

public sealed class ContainerFileEntry
{
    public required string Name { get; init; }
    public required int Length { get; init; }
    public required int StartOffset { get; init; }
    public required long LastModifiedTime { get; init; }
}
