using LCESaveDoctor;

if (args.Length == 0)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== LCE Save Doctor ===");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("Usage: Drag a saveData.ms file onto this exe");
    Console.WriteLine("       or run: LCESaveDoctor.exe <path-to-saveData.ms>");
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    return;
}

string inputPath = args[0];

if (!File.Exists(inputPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"File not found: {inputPath}");
    Console.ResetColor();
    WaitAndExit(1);
    return;
}

string directory = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
string fileName = Path.GetFileName(inputPath);

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=== LCE Save Doctor ===");
Console.ResetColor();
Console.WriteLine();
Console.WriteLine($"File:      {fileName}");
Console.WriteLine($"Directory: {directory}");
Console.WriteLine();

// Read and decompress
Console.Write("Reading saveData.ms... ");
byte[] containerBytes;
byte[] rawBlob;
try
{
    containerBytes = File.ReadAllBytes(inputPath);
    rawBlob = CorruptionScanner.DecompressContainer(containerBytes);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"OK ({containerBytes.Length / 1024}KB)");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAILED");
    Console.WriteLine($"  {ex.Message}");
    Console.ResetColor();
    WaitAndExit(1);
    return;
}

// Scan
Console.Write("Scanning chunks... ");
var report = CorruptionScanner.Scan(rawBlob);

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Done");
Console.ResetColor();
Console.WriteLine();

// Report
Console.WriteLine($"  Regions:          {report.TotalRegions}");
Console.WriteLine($"  Total chunk slots: {report.TotalChunkSlots}");
Console.WriteLine($"  Empty slots:      {report.EmptySlots}");
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"  Healthy chunks:   {report.HealthyChunks}");
Console.ResetColor();

if (report.CorruptedChunks > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  Corrupted chunks: {report.CorruptedChunks}");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  Corrupted chunks: 0");
    Console.ResetColor();
}

if (report.Warnings.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    foreach (var w in report.Warnings)
        Console.WriteLine($"  Warning: {w}");
    Console.ResetColor();
}

Console.WriteLine();

// List corrupted chunks
if (report.CorruptedChunks > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Corrupted chunks:");
    Console.ResetColor();
    foreach (var c in report.Corrupted)
    {
        Console.Write($"  chunk ({c.ChunkX}, {c.ChunkZ})");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($" in {c.RegionEntry}");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    {c.ErrorDetail}");
        Console.ResetColor();
    }
    Console.WriteLine();
    // Ask to fix
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write($"Fix {report.CorruptedChunks} corrupted chunk(s)? (y/n): ");
    Console.ResetColor();

    var key = Console.ReadKey();
    Console.WriteLine();
    Console.WriteLine();

    if (key.KeyChar is 'y' or 'Y')
    {
        Console.Write("Regenerating corrupted chunks... ");
        try
        {
            byte[] fixedContainer = ChunkRegenerator.Regenerate(rawBlob, report.Corrupted);

            // Rename original to .bk
            string backupPath = inputPath + ".bk";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(inputPath, backupPath);

            // Write fixed file
            File.WriteAllBytes(inputPath, fixedContainer);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Done!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"  Backup:  {backupPath}");
            Console.WriteLine($"  Fixed:   {inputPath}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED");
            Console.WriteLine($"  {ex.Message}");
            Console.ResetColor();
        }
    }
    else
    {
        Console.WriteLine("Skipped — no changes made.");
    }
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("No corruption found — save is clean!");
    Console.ResetColor();
}

Console.WriteLine();
WaitAndExit(0);

static void WaitAndExit(int code)
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(code);
}
