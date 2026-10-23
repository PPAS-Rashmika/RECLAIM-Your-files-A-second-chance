using System.Text;
using Reclaim.Core;

namespace Reclaim.Tests;

internal static class Program
{
    private static int passed, failed;
    private static string folder = "";
    private static void Main(string[] args)
    {
        folder = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "tests"));
        Directory.CreateDirectory(folder); string image = Path.Combine(folder, "sample-ntfs.img"); Fixtures.CreateNtfs(image);
        ScanResult? scan = null;
        Test("NTFS deleted metadata finds every extension and excludes directories / live records", () =>
        {
            scan = RecoveryEngine.Scan(new(image, true), ScanMode.Quick, null, CancellationToken.None);
            Equal(11, scan.Items.Count); Assert(scan.Items.All(x => x.Method == "NTFS record"));
            Assert(scan.Items.Any(x => x.Name == "novel.custom" && x.Category == FileCategory.Other));
        });
        Test("Original folder reconstruction and parent sequence validation", () =>
        {
            Equal(@"Documents\Reports\archive.bin", scan!.Items.Single(x => x.Name == "archive.bin").OriginalPath);
            Equal(@"Lost paths\orphan.txt", scan.Items.Single(x => x.Name == "orphan.txt").OriginalPath);
            var filtered = RecoveryEngine.Scan(new(image, true, @"Documents\Reports"), ScanMode.Quick, null, CancellationToken.None);
            Equal(1, filtered.Items.Count); Equal("archive.bin", filtered.Items[0].Name);
        });
        Test("Allocation bitmap marks reused clusters and compressed data", () =>
        {
            Equal(RecoveryState.AtRisk, scan!.Items.Single(x => x.Name == "reused.png").State);
            Equal(RecoveryState.Unsupported, scan.Items.Single(x => x.Name == "compressed.txt").State);
            Equal(RecoveryState.Metadata, scan.Items.Single(x => x.Name == "family.png").State);
        });
        Test("Byte-for-byte recovery of resident, fragmented, extended and sparse files", () =>
        {
            var result = RecoveryEngine.Recover(scan!, scan!.Items.Where(x => x.CanRecover).ToList(), Path.Combine(folder, "recovered"), null, CancellationToken.None);
            Equal(10, result.Saved); Equal(0, result.Failed);
            byte[] Read(string stem) => File.ReadAllBytes(Directory.GetFiles(result.Folder, stem + "_*", SearchOption.AllDirectories).Single());
            Bytes(Fixtures.Notes, Read("notes")); Bytes(Fixtures.Png, Read("family"));
            Bytes(Fixtures.Fragment, Read("archive")); Bytes(Fixtures.Fragment, Read("extended")); Equal(0, Read("empty").Length);
            var sparse = Read("sparse"); Equal(9000, sparse.Length); Assert(sparse[..4096].All(x => x == 65)); Assert(sparse[4096..8192].All(x => x == 0)); Assert(sparse[8192..].All(x => x == 66));
            var initialized = Read("initialized"); Assert(initialized[..100].All(x => x == 88)); Assert(initialized[100..].All(x => x == 0));
            Assert(File.Exists(Path.Combine(result.Folder, "recovery-report.json")));
        });
        Test("Deep scan finds orphan PDF / DOCX and suppresses exact named PNG duplicate", () =>
        {
            var deep = RecoveryEngine.Scan(new(image, true), ScanMode.Deep, null, CancellationToken.None);
            var signatures = deep.Items.Where(x => x.State == RecoveryState.Signature).ToList();
            Assert(signatures.Any(x => x.Name.EndsWith(".pdf"))); Assert(signatures.Any(x => x.Name.EndsWith(".docx")));
            Assert(signatures.All(x => !x.Name.EndsWith(".png")));
            var outcome = RecoveryEngine.Recover(deep, signatures, Path.Combine(folder, "carved"), null, CancellationToken.None);
            Equal(signatures.Count, outcome.Saved);
            Bytes(Fixtures.Pdf, File.ReadAllBytes(Directory.GetFiles(outcome.Folder, "*.pdf", SearchOption.AllDirectories).Single()));
            Bytes(Fixtures.OfficeZip(), File.ReadAllBytes(Directory.GetFiles(outcome.Folder, "*.docx", SearchOption.AllDirectories).Single()));
        });
        Test("Fragmented master file table is read completely", () =>
        {
            string path = Path.Combine(folder, "fragmented-mft.img"); Fixtures.CreateNtfs(path, true);
            var parsed = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None); Equal(11, parsed.Items.Count);
        });
        Test("Sparse and signed negative NTFS run decoding", () =>
        {
            var runs = NtfsParser.DecodeRuns([0x11, 2, 20, 0x11, 1, 0xf6, 0x01, 3, 0]);
            Equal(20L, runs[0].PhysicalCluster); Equal(10L, runs[1].PhysicalCluster); Assert(runs[2].PhysicalCluster is null); Equal(3L, runs[2].VirtualCluster);
            Throws<InvalidDataException>(() => NtfsParser.DecodeRuns([0x11, 0, 2, 0]));
        });
        Test("Truncated extent and torn record are rejected", () =>
        {
            Throws<InvalidDataException>(() => NtfsVolume.ValidateRuns([new(0, 1, 10), new(2, 1, 12)], 8000, 4096, 100000));
            var record = Fixtures.Record(18, 3, false, false, [Fixtures.Filename("torn.txt", 5, 1), Fixtures.Resident(0x80, [1, 2])]); record[510] = 0;
            Throws<InvalidDataException>(() => NtfsParser.Parse(record, 512, 18));
        });
        Test("Unsafe output drive and path traversal names are blocked", () =>
        {
            Throws<InvalidOperationException>(() => RecoveryEngine.ValidateDestination(new(@"C:\Users\example", false), @"C:\recovered"));
            Assert(!RecoveryEngine.SafeFileName("../../CON.txt").Contains('/')); Equal("_CON.txt", RecoveryEngine.SafeFileName("CON.txt"));
        });
        Test("Cancellation finishes cleanly without modifying the image", () =>
        {
            byte[] before = File.ReadAllBytes(image);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var stopped = RecoveryEngine.Scan(new(image, true), ScanMode.Deep, null, cancel.Token); Assert(stopped.Cancelled);
            Bytes(before, File.ReadAllBytes(image));
        });
        Test("Raw signatures across a 4 MB scan boundary retain exact file bytes", () =>
        {
            byte[] data = new byte[5 * 1024 * 1024]; int start = 4 * 1024 * 1024 - 3; Fixtures.Pdf.CopyTo(data, start);
            string path = Path.Combine(folder, "boundary.raw"); File.WriteAllBytes(path, data);
            var deep = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, null, CancellationToken.None);
            Equal(1, deep.Items.Count); Equal((long)start, deep.Items[0].CarvedOffset); Equal((long)Fixtures.Pdf.Length, deep.Items[0].Size);
        });
        Test("Malformed PNG checksums and signatures without EOF are rejected", () =>
        {
            byte[] data = new byte[4096]; var badPng = (byte[])Fixtures.Png.Clone(); badPng[30] ^= 1;
            badPng.CopyTo(data, 32); Encoding.ASCII.GetBytes("%PDF-1.7\nTruncated").CopyTo(data, 500);
            string path = Path.Combine(folder, "malformed.raw"); File.WriteAllBytes(path, data);
            var deep = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, null, CancellationToken.None); Equal(0, deep.Items.Count);
        });
        Test("MBR disk image locates the NTFS partition offset", () =>
        {
            byte[] volume = File.ReadAllBytes(image); byte[] disk = new byte[volume.Length + 1024 * 1024];
            disk[510] = 0x55; disk[511] = 0xaa; disk[450] = 7; Fixtures.U32(disk, 454, 2048); Fixtures.U32(disk, 458, volume.Length / 512); volume.CopyTo(disk, 1024 * 1024);
            string path = Path.Combine(folder, "disk.img"); File.WriteAllBytes(path, disk);
            var parsed = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None); Equal(1024L * 1024, parsed.VolumeOffset); Equal(11, parsed.Items.Count);
        });
        Test("Signature boundaries recover image, audio, video and archive bytes exactly", () =>
        {
            var samples = Fixtures.Signatures(); byte[] raw = new byte[1024 * 1024]; int p = 256;
            foreach (var (_, bytes) in samples) { bytes.CopyTo(raw, p); p += 4096; }
            string path = Path.Combine(folder, "signatures.raw"); File.WriteAllBytes(path, raw);
            var deep = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, null, CancellationToken.None);
            foreach (var extension in samples.Keys)
                if (!deep.Items.Any(x => x.Name.EndsWith("." + extension))) throw new Exception("Missing signature: " + extension);
            Equal(samples.Count, deep.Items.Count);
            var outcome = RecoveryEngine.Recover(deep, deep.Items, Path.Combine(folder, "signature-results"), null, CancellationToken.None);
            Equal(samples.Count, outcome.Saved);
            foreach (var (extension, expected) in samples)
                Bytes(expected, File.ReadAllBytes(Directory.GetFiles(outcome.Folder, "*." + extension, SearchOption.AllDirectories).Single()));
        });
        Test("Stopping during deep scan retains files already found", () =>
        {
            byte[] data = new byte[8 * 1024 * 1024]; Fixtures.Pdf.CopyTo(data, 64);
            string path = Path.Combine(folder, "cancel.raw"); File.WriteAllBytes(path, data);
            using var cancel = new CancellationTokenSource();
            var stopped = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, p => { if (p.Phase == "Deep signature search" && p.Found > 0) cancel.Cancel(); }, cancel.Token);
            Assert(stopped.Cancelled); Equal(1, stopped.Items.Count);
        });
        Test("Source bytes remain unchanged through complete scans and recovery", () =>
        {
            string expected = Path.Combine(folder, "unchanged-copy.img"); Fixtures.CreateNtfs(expected);
            Bytes(File.ReadAllBytes(expected), File.ReadAllBytes(image));
        });
        Test("Recovery rejects an image changed after the scan", () =>
        {
            string path = Path.Combine(folder, "changed-source.img"); Fixtures.CreateNtfs(path);
            var previous = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            Throws<IOException>(() => RecoveryEngine.Recover(previous, previous.Items.Where(x => x.CanRecover).ToList(), Path.Combine(folder, "changed-output"), null, CancellationToken.None));
        });
        foreach (int bits in new[] { 12, 16, 32 }) Test("FAT" + bits + " deleted entries, unknown extensions, fragmented / inferred bytes and folder filters", () =>
        {
            string path = Path.Combine(folder, "sample-fat" + bits + ".img"); FatFixtures.Create(path, bits);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None);
            Equal(3, found.Items.Count); Equal("FAT" + bits, found.FileSystem);
            var contiguous = found.Items.Single(x => x.Name.EndsWith(".XYZ")); Equal(RecoveryState.AtRisk, contiguous.State);
            using var content = RecoveryContent.Open(found, contiguous); var bytes = new byte[900]; content.ReadExactly(bytes);
            Assert(bytes[..512].All(x => x == 65)); Assert(bytes[512..].All(x => x == 66));
            content.Position = 500; var cross = new byte[32]; content.ReadExactly(cross); Assert(cross[..12].All(x => x == 65)); Assert(cross[12..].All(x => x == 66));
            using var fragment = RecoveryContent.Open(found, found.Items.Single(x => x.Name.EndsWith(".BIN"))); fragment.ReadExactly(bytes);
            Assert(bytes[..512].All(x => x == 67)); Assert(bytes[512..].All(x => x == 68));
            var filtered = RecoveryEngine.Scan(new(path, true, "FOLDER"), ScanMode.Quick, null, CancellationToken.None); Equal(1, filtered.Items.Count);
            var outcome = RecoveryEngine.Recover(found, found.Items, Path.Combine(folder, "fat-output"), null, CancellationToken.None); Equal(3, outcome.Saved);
        });
        Test("exFAT deleted set checksums, arbitrary extensions, bitmap and fragmented / initialized bytes", () =>
        {
            string path = Path.Combine(folder, "sample-exfat.img"); FatFixtures.CreateExFat(path);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None); Equal(2, found.Items.Count);
            Assert(found.Items.All(x => x.State == RecoveryState.Metadata));
            using var content = RecoveryContent.Open(found, found.Items.Single(x => x.Name == "holiday.custom")); var bytes = new byte[700]; content.ReadExactly(bytes);
            Assert(bytes[..512].All(x => x == 65)); Assert(bytes[512..600].All(x => x == 66)); Assert(bytes[600..].All(x => x == 0));
            using var fragment = RecoveryContent.Open(found, found.Items.Single(x => x.Name == "fragment.bin")); bytes = new byte[900]; fragment.ReadExactly(bytes);
            Assert(bytes[..512].All(x => x == 67)); Assert(bytes[512..].All(x => x == 68));
            string bad = Path.Combine(folder, "bad-exfat.img"); FatFixtures.CreateExFat(bad, true);
            var rejected = RecoveryEngine.Scan(new(bad, true), ScanMode.Quick, null, CancellationToken.None); Equal(0, rejected.Items.Count); Assert(rejected.Warnings.Count > 0);
        });
        Test("All MBR partitions recover from their own byte offsets", () =>
        {
            string fatImage = Path.Combine(folder, "sample-fat12.img"); byte[] ntfs = File.ReadAllBytes(image), fat = File.ReadAllBytes(fatImage);
            int start = 2048 * 512, second = start + ntfs.Length; byte[] disk = new byte[second + fat.Length]; disk[510] = 0x55; disk[511] = 0xaa;
            void Partition(int at, byte type, int pos, int size) { disk[at + 4] = type; BitConverter.GetBytes(pos / 512).CopyTo(disk, at + 8); BitConverter.GetBytes(size / 512).CopyTo(disk, at + 12); }
            Partition(446, 7, start, ntfs.Length); Partition(462, 1, second, fat.Length); ntfs.CopyTo(disk, start); fat.CopyTo(disk, second);
            string path = Path.Combine(folder, "multi-partition.img"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None); Equal(2, found.Volumes.Count); Equal(14, found.Items.Count);
            using var a = RecoveryContent.Open(found, found.Items.Single(x => x.Name == "archive.bin")); var bytes = new byte[6000]; a.ReadExactly(bytes); Bytes(Fixtures.Fragment, bytes);
            using var b = RecoveryContent.Open(found, found.Items.Single(x => x.Name.EndsWith(".XYZ"))); bytes = new byte[900]; b.ReadExactly(bytes); Assert(bytes[0] == 65 && bytes[^1] == 66);
        });
        Test("Preview text / archive / hex reading stays bounded and source bytes are unchanged", () =>
        {
            var found = RecoveryEngine.Scan(new(image, true), ScanMode.Deep, null, CancellationToken.None);
            string text = PreviewText.Read(found, found.Items.Single(x => x.Name == "notes.txt")); Assert(text.Contains("Sinhala") || text.Contains("සිංහල"));
            var docx = found.Items.Single(x => x.Name.EndsWith(".docx")); Assert(PreviewText.Read(found, docx).Contains("DOCUMENT TEXT"));
            Assert(PreviewText.Hex(PreviewText.Head(found, docx, 16)).Contains("50 4B"));
            using var data = RecoveryContent.Open(found, found.Items.Single(x => x.Name == "sparse.dat")); data.Position = 4090; var bytes = new byte[20]; data.ReadExactly(bytes); Assert(bytes[..6].All(x => x == 65) && bytes[6..].All(x => x == 0));
        });
        foreach (int sector in new[] { 512, 4096 }) Test("GPT " + sector + " sector checksum validation and multi-partition recovery", () =>
        {
            int start = 256 * sector, second = start + 4 * 1024 * 1024;
            byte[] disk = new byte[second + 4 * 1024 * 1024]; disk[510] = 0x55; disk[511] = 0xaa; disk[450] = 0xee;
            var table = new byte[128 * 4]; table[0] = 1; table[128] = 1;
            void U64(byte[] b, int at, long value) => BitConverter.GetBytes(value).CopyTo(b, at);
            U64(table, 32, start / sector); U64(table, 40, (second - sector) / sector); U64(table, 160, second / sector); U64(table, 168, (disk.Length - sector) / sector);
            long tableOffset = 2L * sector; table.CopyTo(disk, tableOffset);
            var header = new byte[92]; "EFI PART"u8.CopyTo(header); BitConverter.GetBytes(0x10000u).CopyTo(header, 8); BitConverter.GetBytes(92).CopyTo(header, 12);
            U64(header, 24, 1); U64(header, 32, disk.Length / sector - 1); U64(header, 40, 256); U64(header, 48, disk.Length / sector - 2); U64(header, 72, 2);
            BitConverter.GetBytes(4).CopyTo(header, 80); BitConverter.GetBytes(128).CopyTo(header, 84); BitConverter.GetBytes(Crc32.Update(uint.MaxValue, table) ^ uint.MaxValue).CopyTo(header, 88);
            BitConverter.GetBytes(Crc32.Update(uint.MaxValue, header) ^ uint.MaxValue).CopyTo(header, 16); header.CopyTo(disk, sector);
            byte[] volume = File.ReadAllBytes(image); volume.CopyTo(disk, start); volume.CopyTo(disk, second);
            string path = Path.Combine(folder, "gpt-" + sector + ".img"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None); Equal(2, found.Volumes.Count); Equal(22, found.Items.Count);
            var file = found.Items.Single(x => x.Name == "archive.bin" && x.Volume!.Offset == second); using var data = RecoveryContent.Open(found, file); var bytes = new byte[6000]; data.ReadExactly(bytes); Bytes(Fixtures.Fragment, bytes);
            data.Dispose(); disk[sector + 88] ^= 1; File.WriteAllBytes(path, disk); using var reader = new DiskReader(new(path, true)); var warnings = new List<string>();
            var rejected = Partitions.Discover(reader, warnings); Equal("Raw", rejected[0].FileSystem); Assert(warnings.Any(x => x.Contains("checksum")));
        });
        AdvancedChecks.Run(folder, Test);
        int media = Array.IndexOf(args, "--media");
        if (media >= 0 && media + 1 < args.Length) MediaChecks.Run(folder, args[media + 1], Test);
        if (args.Contains("--health")) Test("Windows drive-health provider returns explicit health/unsupported counters", () =>
        {
            var reports = DriveHealth.Query(null, CancellationToken.None).GetAwaiter().GetResult(); Assert(reports.Count > 0); Assert(reports.All(x => !string.IsNullOrWhiteSpace(x.Model)));
            Console.WriteLine($"Read-only health query: {reports.Count} drives; {reports.Count(x => x.Temperature.HasValue)} expose temperature.");
        });
        Console.WriteLine($"\n{passed} passed, {failed} failed. Sample image: {image}");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Bytes(byte[] expected, byte[] actual) => Assert(expected.AsSpan().SequenceEqual(actual));
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
