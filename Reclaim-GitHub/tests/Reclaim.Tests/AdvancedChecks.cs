using System.Buffers.Binary;
using Reclaim.Core;

namespace Reclaim.Tests;

internal static class AdvancedChecks
{
    private static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void U16(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
    private static void U32(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    public static void Run(string folder, Action<string, Action> test)
    {
        test("Exports use safe category/format folders and preserve case-insensitive format identity", () =>
        {
            string path = Path.Combine(folder, "format-folders.img"); Fixtures.CreateNtfs(path);
            var scan = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None);
            var items = scan.Items.Where(x => x.Name is "family.png" or "notes.txt" or "novel.custom").ToList();
            var noExtension = new RecoveryItem { Name = "LICENSE", OriginalPath = "LICENSE", Method = "Synthetic retained content", Size = 3, InitializedSize = 3, ResidentData = [1, 2, 3], State = RecoveryState.Resident };
            items.Add(noExtension);
            var saved = RecoveryEngine.Recover(scan, items, Path.Combine(folder, "format-output"), null, CancellationToken.None);
            Assert(saved.Saved == 4 && saved.Failed == 0);
            Assert(Directory.GetFiles(Path.Combine(saved.Folder, "Images", "PNG")).Length == 1);
            Assert(Directory.GetFiles(Path.Combine(saved.Folder, "Documents", "TXT")).Length == 1);
            Assert(Directory.GetFiles(Path.Combine(saved.Folder, "Other", "CUSTOM")).Length == 1);
            Assert(Directory.GetFiles(Path.Combine(saved.Folder, "Other", "No extension")).Length == 1);
            Assert(Categories.FormatOf("a.JPG") == Categories.FormatOf("b.jpg") && Categories.FormatOf("README") == "No extension");
        });
        test("Live callbacks publish recoverable content before scan completion", () =>
        {
            string path = Path.Combine(folder, "live.img"); Fixtures.CreateNtfs(path); ScanResult? view = null; int live = 0;
            var result = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, null, CancellationToken.None, x => view = x, item =>
            {
                Assert(view is not null && view.SourceLength > 0);
                if (item.Name == "notes.txt") { using var data = RecoveryContent.Open(view!, item); Assert(data.ReadByte() >= 0); }
                live++;
            });
            Assert(live == result.Items.Count && live > 0); Assert(view!.Items.Count == 0, "Live view must not share the worker's mutable list");
        });
        test("Bad-sector scan skips unreadable units, retries once and preserves later bytes", () =>
        {
            byte[] bytes = Enumerable.Repeat((byte)0x5a, 128 * 1024).ToArray(); var fault = new FaultStream(bytes, 8192, 4096);
            using var reader = DiskReader.FromStream(fault, true); byte[] result = reader.Bytes(0, bytes.Length);
            Assert(result[..8192].All(x => x == 0x5a)); Assert(result[8192..12288].All(x => x == 0)); Assert(result[12288..].All(x => x == 0x5a));
            Assert(reader.BadRanges.Count == 1 && reader.BadRanges[0] == new ByteRange(8192, 4096)); Assert(fault.Failures >= 2);
        });
        test("Strict recovery read never silently inserts zeros for a bad sector", () =>
        {
            using var reader = DiskReader.FromStream(new FaultStream(new byte[65536], 4096, 4096));
            bool failed = false; try { reader.Bytes(4096, 4096); } catch (IOException) { failed = true; } Assert(failed); Assert(reader.BadRanges.Count == 0);
            Assert(reader.Bytes(16384, 16).All(x => x == 0), "An unrelated failed read-ahead must not block readable bytes");
        });
        test("Lost-partition search finds deleted table entries without modifying disk bytes", () =>
        {
            string volume = Path.Combine(folder, "lost-volume.img"); Fixtures.CreateNtfs(volume);
            byte[] disk = new byte[6 * 1024 * 1024]; File.ReadAllBytes(volume).CopyTo(disk, 1024 * 1024);
            string path = Path.Combine(folder, "lost-table.img"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.LostPartitions, null, CancellationToken.None);
            Assert(found.LostPartitions.Any(x => x.Offset == 1024 * 1024 && x.FileSystem == "NTFS"));
            Assert(found.Items.Any(x => x.Name == "notes.txt")); Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(disk));
        });
        foreach (bool extents in new[] { false, true }) test("Deleted " + (extents ? "ext4 extent" : "ext2 block") + " inode recovers exact surviving bytes", () =>
        {
            string path = Path.Combine(folder, extents ? "sample-ext4.img" : "sample-ext2.img"); byte[] bytes = ExtFixture(extents); File.WriteAllBytes(path, bytes);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None);
            Assert(found.Items.Count == 1); var item = found.Items[0]; Assert(item.Name == "inode_12.pdf" && item.State == RecoveryState.Metadata);
            using var data = RecoveryContent.Open(found, item); byte[] output = new byte[item.Size]; data.ReadExactly(output); Assert(output.AsSpan().SequenceEqual(Fixtures.Pdf));
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes));
        });
        test("Reused ext data is marked at risk and cleared mappings are not invented", () =>
        {
            byte[] bytes = ExtFixture(true); bytes[3 * 1024 + 19 / 8] |= (byte)(1 << (19 % 8));
            string path = Path.Combine(folder, "ext-reused.img"); File.WriteAllBytes(path, bytes);
            Assert(RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None).Items.Single().State == RecoveryState.AtRisk);
            Array.Clear(bytes, 5 * 1024 + 11 * 128 + 40, 60); File.WriteAllBytes(path, bytes);
            Assert(RecoveryEngine.Scan(new(path, true), ScanMode.Quick, null, CancellationToken.None).Items.Count == 0);
        });
        test("Lost ext volume is detected behind a missing partition table", () =>
        {
            byte[] disk = new byte[4 * 1024 * 1024]; ExtFixture(true).CopyTo(disk, 1024 * 1024);
            string path = Path.Combine(folder, "lost-ext.img"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.LostPartitions, null, CancellationToken.None);
            Assert(found.LostPartitions.Any(x => x.FileSystem == "ext4" && x.Offset == 1024 * 1024)); Assert(found.Items.Any(x => x.Method == "ext4 deleted inode"));
        });
        test("Camera fMP4 reconstruction prepends initialization and maps exact fragment bytes", () =>
        {
            var (init, fragment) = CameraFixture(); byte[] disk = new byte[65536]; fragment.CopyTo(disk, 8192);
            string path = Path.Combine(folder, "camera.raw"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Camera, null, CancellationToken.None, cameraReference: init);
            var item = found.Items.Single(x => x.Method == "Camera fMP4 reconstruction");
            using var data = RecoveryContent.Open(found, item); byte[] result = new byte[item.Size]; data.ReadExactly(result);
            Assert(result.AsSpan().SequenceEqual(init.Bytes.Concat(fragment).ToArray()));
            data.Position = init.Bytes.Length - 3; var cross = new byte[12]; data.ReadExactly(cross); Assert(cross.AsSpan().SequenceEqual(result.AsSpan(init.Bytes.Length - 3, 12)));
        });
        test("Camera rejects unknown tracks, absolute-offset fragments and missing references", () =>
        {
            var (init, fragment) = CameraFixture(); byte[] disk = new byte[65536]; fragment[8 + 8 + 8 + 4 + 3] = 2; fragment.CopyTo(disk, 8192);
            string path = Path.Combine(folder, "camera-invalid.raw"); File.WriteAllBytes(path, disk);
            var found = RecoveryEngine.Scan(new(path, true), ScanMode.Camera, null, CancellationToken.None, cameraReference: init);
            Assert(found.Items.All(x => x.Method != "Camera fMP4 reconstruction"));
            bool failed = false; try { CameraRecovery.ParseInitialization(Box("ftyp", new byte[8]), Box("moov", new byte[8])); } catch (InvalidDataException) { failed = true; } Assert(failed);
        });
        test("Content validation identifies corrupt PNG and validates ZIP member CRCs", () =>
        {
            string path = Path.Combine(folder, "validate.img"); Fixtures.CreateNtfs(path); var scan = RecoveryEngine.Scan(new(path, true), ScanMode.Deep, null, CancellationToken.None);
            var png = scan.Items.Single(x => x.Name == "family.png"); Assert(FileAssessment.Check(scan, png, CancellationToken.None).Contains("checksums verified") && !png.ValidationFailed);
            var zip = scan.Items.Single(x => x.Name.EndsWith(".docx")); Assert(FileAssessment.Check(scan, zip, CancellationToken.None).Contains("checksums verified") && !zip.ValidationFailed);
            var bad = new RecoveryItem { Name = "bad.png", OriginalPath = "", Size = Fixtures.Png.Length, InitializedSize = Fixtures.Png.Length, ResidentData = Fixtures.Png.Select((x, i) => i == 30 ? (byte)(x ^ 1) : x).ToArray(), Method = "test", State = RecoveryState.Resident };
            Assert(FileAssessment.Check(scan, bad, CancellationToken.None).Contains("failed") && bad.ValidationFailed && bad.RecoveryChance.StartsWith("Low"));
        });
        test("Health JSON preserves unavailable counters rather than fabricating healthy values", () =>
        {
            var report = DriveHealth.Parse("[{\"Number\":1,\"Model\":\"USB\",\"Serial\":\"\",\"Health\":\"Unknown\",\"Status\":\"Online\",\"Temperature\":null,\"Wear\":null,\"PowerOnHours\":null,\"ReadErrors\":null,\"WriteErrors\":null,\"Note\":\"Unsupported\"}]").Single();
            Assert(report.Temperature is null && report.Wear is null && report.Health == "Unknown");
        });
    }
    internal static byte[] ExtFixture(bool extents)
    {
        byte[] b = new byte[2 * 1024 * 1024]; int s = 1024;
        U32(b, s, 32); U32(b, s + 4, 2048); U32(b, s + 20, 1); U32(b, s + 32, 2048); U32(b, s + 40, 32); U16(b, s + 56, 0xef53); U32(b, s + 76, 1); U16(b, s + 88, 128); U32(b, s + 96, extents ? 0x42u : 2u);
        U32(b, 2048, 3); U32(b, 2052, 4); U32(b, 2056, 5);
        int inode = 5 * 1024 + 11 * 128; U16(b, inode, 0x81a4); U32(b, inode + 4, (uint)Fixtures.Pdf.Length); U32(b, inode + 20, 1000);
        if (extents)
        {
            U32(b, inode + 32, 0x80000); U16(b, inode + 40, 0xf30a); U16(b, inode + 42, 1); U16(b, inode + 44, 4); U16(b, inode + 56, 1); U32(b, inode + 60, 20);
        }
        else U32(b, inode + 40, 20);
        Fixtures.Pdf.CopyTo(b, 20 * 1024); return b;
    }
    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[payload.Length + 8]; BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length); System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4); payload.CopyTo(box, 8); return box;
    }
    private static (CameraRecovery.Initialization Init, byte[] Fragment) CameraFixture()
    {
        byte[] trex = new byte[24]; BinaryPrimitives.WriteUInt32BigEndian(trex.AsSpan(4), 1); BinaryPrimitives.WriteUInt32BigEndian(trex.AsSpan(16), 32);
        var init = CameraRecovery.ParseInitialization(Box("ftyp", "isom0000"u8.ToArray()), Box("moov", Box("mvex", Box("trex", trex))));
        byte[] tfhd = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(tfhd, 0x020000); BinaryPrimitives.WriteUInt32BigEndian(tfhd.AsSpan(4), 1);
        byte[] trun = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(trun, 1); BinaryPrimitives.WriteUInt32BigEndian(trun.AsSpan(4), 1);
        byte[] moof = Box("moof", Box("traf", Box("tfhd", tfhd).Concat(Box("trun", trun)).ToArray()));
        BinaryPrimitives.WriteInt32BigEndian(moof.AsSpan(moof.Length - 4), moof.Length + 8);
        return (init, moof.Concat(Box("mdat", Enumerable.Repeat((byte)0x34, 32).ToArray())).ToArray());
    }
    private sealed class FaultStream(byte[] bytes, long bad, int length) : MemoryStream(bytes, false)
    {
        public int Failures { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (Position < bad + length && bad < Position + buffer.Length) { Failures++; throw new IOException("Injected media read error"); }
            return base.Read(buffer);
        }
    }
}
