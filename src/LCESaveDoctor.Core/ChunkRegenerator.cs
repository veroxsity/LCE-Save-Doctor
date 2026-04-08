using System.IO.Compression;
using System.Text;
using LCESaveDoctor.LceFormat;
using fNbt;

namespace LCESaveDoctor;

/// <summary>
/// Rebuilds a saveData.ms container, replacing corrupted chunks with
/// empty regenerated chunks (bedrock floor + proper coords).
/// </summary>
public static class ChunkRegenerator
{
    private const int RegionSectorBytes = 4096;
    private const int ChunkHeaderBytes = 8;

    public static byte[] Regenerate(byte[] rawBlob, List<ChunkDiagnosis> corrupted)
    {
        if (corrupted.Count == 0)
            return RecompressContainer(rawBlob);

        var byRegion = corrupted
            .GroupBy(c => c.RegionEntry)
            .ToDictionary(g => g.Key, g => g.ToList());

        var entries = CorruptionScanner.ParseEntries(rawBlob);

        var fileEntries = new List<(string Name, byte[] Data, long Modified)>();

        foreach (var entry in entries)
        {
            byte[] entryData = new byte[entry.Length];
            Buffer.BlockCopy(rawBlob, entry.StartOffset, entryData, 0, entry.Length);

            if (byRegion.TryGetValue(entry.Name, out var corruptedChunks))
                entryData = PatchRegion(entryData, corruptedChunks);

            fileEntries.Add((entry.Name, entryData, entry.LastModifiedTime));
        }

        short originalVersion = BitConverter.ToInt16(rawBlob, 8);
        short currentVersion = BitConverter.ToInt16(rawBlob, 10);

        return BuildContainer(fileEntries, originalVersion, currentVersion);
    }

    private static byte[] PatchRegion(byte[] regionBytes, List<ChunkDiagnosis> corrupted)
    {
        byte[] patched = (byte[])regionBytes.Clone();

        foreach (var chunk in corrupted)
        {
            byte[] emptyPayload = GenerateEmptyChunk(chunk.ChunkX, chunk.ChunkZ);
            byte[] compressed = LceCompression.CompressZlibOnly(emptyPayload);

            int totalSize = ChunkHeaderBytes + compressed.Length;
            int sectorsNeeded = (totalSize + RegionSectorBytes - 1) / RegionSectorBytes;

            if (sectorsNeeded >= 256) continue;

            // Append at end of region, aligned to sector boundary
            int alignedEnd = ((patched.Length + RegionSectorBytes - 1) / RegionSectorBytes) * RegionSectorBytes;
            int sectorNumber = alignedEnd / RegionSectorBytes;

            int newSize = alignedEnd + sectorsNeeded * RegionSectorBytes;
            byte[] grown = new byte[newSize];
            Buffer.BlockCopy(patched, 0, grown, 0, patched.Length);
            patched = grown;

            // Write chunk header + compressed data
            int writePos = sectorNumber * RegionSectorBytes;
            BitConverter.TryWriteBytes(patched.AsSpan(writePos), (uint)compressed.Length);
            BitConverter.TryWriteBytes(patched.AsSpan(writePos + 4), (uint)emptyPayload.Length);
            Buffer.BlockCopy(compressed, 0, patched, writePos + ChunkHeaderBytes, compressed.Length);

            // Update offset table
            int slotIndex = chunk.LocalX + chunk.LocalZ * 32;
            int offsetTablePos = slotIndex * 4;
            uint offsetEntry = (uint)((sectorNumber << 8) | sectorsNeeded);
            BitConverter.TryWriteBytes(patched.AsSpan(offsetTablePos), offsetEntry);

            // Update timestamp table
            int timestampPos = RegionSectorBytes + slotIndex * 4;
            if (timestampPos + 4 <= patched.Length)
            {
                BitConverter.TryWriteBytes(patched.AsSpan(timestampPos),
                    (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
        }

        return patched;
    }

    /// <summary>
    /// Generates an empty LCE compressed-storage chunk at the given world coordinates.
    /// Bedrock at Y=0, air everywhere else, full skylight.
    /// </summary>
    private static byte[] GenerateEmptyChunk(int chunkX, int chunkZ)
    {
        byte[] blocks = new byte[16 * 128 * 16]; // YZX order
        byte[] data = new byte[blocks.Length / 2];
        byte[] skyLight = new byte[blocks.Length / 2];
        byte[] blockLight = new byte[blocks.Length / 2];
        byte[] heightMap = new byte[256];
        byte[] biomes = new byte[256];

        // Bedrock floor at Y=0
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                blocks[0 * 256 + z * 16 + x] = 7; // bedrock
                heightMap[z * 16 + x] = 1;
            }
        }

        // Full skylight
        Array.Fill(skyLight, (byte)0xFF);

        var level = new NbtCompound("Level")
        {
            new NbtInt("xPos", chunkX),
            new NbtInt("zPos", chunkZ),
            new NbtLong("LastUpdate", 0),
            new NbtLong("InhabitedTime", 0),
            new NbtByteArray("Blocks", blocks),
            new NbtByteArray("Data", data),
            new NbtByteArray("SkyLight", skyLight),
            new NbtByteArray("BlockLight", blockLight),
            new NbtByteArray("HeightMap", heightMap),
            new NbtByteArray("Biomes", biomes),
            new NbtShort("TerrainPopulatedFlags", 3),
        };

        return LceChunkPayloadCodec.EncodeCompressedStorage(level);
    }

    private static byte[] BuildContainer(
        List<(string Name, byte[] Data, long Modified)> files,
        short originalVersion, short currentVersion)
    {
        int headerSize = 12;
        int dataSize = files.Sum(f => f.Data.Length);
        int footerSize = files.Count * 144;
        int totalSize = headerSize + dataSize + footerSize;

        byte[] blob = new byte[totalSize];

        // Write file data after header
        int dataEnd = headerSize;
        var entryInfo = new List<(string Name, int StartOffset, int Length, long Modified)>();

        foreach (var (name, fileData, modified) in files)
        {
            Buffer.BlockCopy(fileData, 0, blob, dataEnd, fileData.Length);
            entryInfo.Add((name, dataEnd, fileData.Length, modified));
            dataEnd += fileData.Length;
        }

        // Header
        BitConverter.TryWriteBytes(blob.AsSpan(0), (uint)dataEnd);
        BitConverter.TryWriteBytes(blob.AsSpan(4), (uint)files.Count);
        BitConverter.TryWriteBytes(blob.AsSpan(8), originalVersion);
        BitConverter.TryWriteBytes(blob.AsSpan(10), currentVersion);

        // Footer
        int pos = dataEnd;
        foreach (var (name, startOffset, length, modified) in entryInfo.OrderBy(e => e.StartOffset))
        {
            byte[] nameBytes = new byte[128];
            byte[] encoded = Encoding.Unicode.GetBytes(name);
            int copyLen = Math.Min(encoded.Length, 126);
            Buffer.BlockCopy(encoded, 0, nameBytes, 0, copyLen);
            Buffer.BlockCopy(nameBytes, 0, blob, pos, 128);
            pos += 128;

            BitConverter.TryWriteBytes(blob.AsSpan(pos), (uint)length);
            pos += 4;

            BitConverter.TryWriteBytes(blob.AsSpan(pos), (uint)startOffset);
            pos += 4;

            BitConverter.TryWriteBytes(blob.AsSpan(pos), modified);
            pos += 8;
        }

        // Compress: [0 int][decompSize int][zlib data]
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlibStream.Write(blob, 0, pos);
        }
        byte[] compressedData = compressedStream.ToArray();

        byte[] output = new byte[8 + compressedData.Length];
        BitConverter.TryWriteBytes(output.AsSpan(0), 0);
        BitConverter.TryWriteBytes(output.AsSpan(4), pos);
        Buffer.BlockCopy(compressedData, 0, output, 8, compressedData.Length);

        return output;
    }

    private static byte[] RecompressContainer(byte[] rawBlob)
    {
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlibStream.Write(rawBlob, 0, rawBlob.Length);
        }
        byte[] compressedData = compressedStream.ToArray();

        byte[] output = new byte[8 + compressedData.Length];
        BitConverter.TryWriteBytes(output.AsSpan(0), 0);
        BitConverter.TryWriteBytes(output.AsSpan(4), rawBlob.Length);
        Buffer.BlockCopy(compressedData, 0, output, 8, compressedData.Length);

        return output;
    }
}
