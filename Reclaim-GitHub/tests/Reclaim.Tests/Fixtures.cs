using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Reclaim.Core;

namespace Reclaim.Tests;

internal static class Fixtures
{
    public const int Cluster = 4096, RecordSize = 1024, Records = 64;
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");
    public static readonly byte[] Notes = Encoding.UTF8.GetBytes("Recovered from a deleted NTFS resident record.\nසිංහල / English\n");
    public static readonly byte[] Fragment = Enumerable.Range(0, 6000).Select(x => (byte)(x * 7 % 251)).ToArray();
    public static readonly byte[] Pdf = CreatePdf();
    private static byte[] CreatePdf()
    {
        string contents = "BT /F1 18 Tf 40 200 Td (Reclaim recovery preview) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 360 280] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {contents.Length} >>\nstream\n{contents}endstream"];
        var text = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length; text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int at in offsets) text.Append($"{at:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
    public static Dictionary<string, byte[]> Signatures()
    {
        var wav = new byte[48]; "RIFF"u8.CopyTo(wav); U32(wav, 4, 40); "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); U32(wav, 16, 16);
        U16(wav, 20, 1); U16(wav, 22, 1); U32(wav, 24, 8000); U32(wav, 28, 8000); U16(wav, 32, 1); U16(wav, 34, 8);
        "data"u8.CopyTo(wav.AsSpan(36)); U32(wav, 40, 4); wav.AsSpan(44, 4).Fill(128);
        var bmp = new byte[58]; "BM"u8.CopyTo(bmp); U32(bmp, 2, 58); U32(bmp, 10, 54); U32(bmp, 14, 40); U32(bmp, 18, 1); U32(bmp, 22, 1); U16(bmp, 26, 1); U16(bmp, 28, 24); U32(bmp, 34, 4);
        var mp4 = new byte[44]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mp4, 24); "ftypisom"u8.CopyTo(mp4.AsSpan(4)); "isommp42"u8.CopyTo(mp4.AsSpan(16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mp4.AsSpan(24), 8); "moov"u8.CopyTo(mp4.AsSpan(28));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mp4.AsSpan(32), 12); "mdat"u8.CopyTo(mp4.AsSpan(36));
        byte[] seven = new byte[34]; new byte[] {0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4}.CopyTo(seven, 0); U64(seven, 20, 2); seven[32] = 1;
        U32(seven, 28, Crc32.Update(0xffffffff, seven.AsSpan(32, 2)) ^ 0xffffffff); U32(seven, 8, Crc32.Update(0xffffffff, seven.AsSpan(12, 20)) ^ 0xffffffff);
        byte[] jpeg = [0xff,0xd8,0xff,0xc0,0,17,8,0,1,0,1,3,1,0x11,0,2,0x11,0,3,0x11,0,0xff,0xda,0,12,3,1,0,2,0,3,0,0,63,0,1,0xff,0,0xff,0xd9];
        return new() { ["png"] = Png, ["gif"] = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), ["wav"] = wav, ["bmp"] = bmp,
            ["mp4"] = mp4, ["7z"] = seven, ["jpg"] = jpeg, ["docx"] = OfficeZip(), ["pdf"] = Pdf };
    }
    public static byte[] OfficeZip()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("word/document.xml"); entry.LastWriteTime = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open()); writer.Write("<document>Recovery fixture</document>");
        }
        return memory.ToArray();
    }
    public static void CreateNtfs(string path, bool fragmentedMft = false)
    {
        byte[] disk = new byte[4 * 1024 * 1024];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(disk, 3); U16(disk, 11, 512); disk[13] = 8;
        U64(disk, 40, disk.Length / 512); U64(disk, 48, 4); disk[64] = unchecked((byte)-10); disk[510] = 0x55; disk[511] = 0xaa;
        int mftSize = Records * RecordSize;
        var records = new byte[Records][];
        var mftRuns = fragmentedMft ? new List<DataRun> { new(0, 8, 4), new(8, 8, 70) } : [new DataRun(0, 16, 4)];
        records[0] = Record(0, 1, true, false, [Nonresident(0x80, mftRuns, mftSize)]);
        records[5] = Record(5, 1, true, true, [Filename(".", 5, 1)]);
        records[6] = Record(6, 1, true, false, [Nonresident(0x80, [new(0, 1, 20)], 128)]);
        records[16] = Record(16, 2, true, true, [Filename("Documents", 5, 1)]);
        records[17] = Record(17, 3, false, true, [Filename("Reports", 16, 2)]);
        records[18] = Record(18, 3, false, false, [Filename("notes.txt", 16, 2), Resident(0x80, Notes)]);
        records[19] = Record(19, 3, false, false, [Filename("family.png", 16, 2), Nonresident(0x80, [new(0, 1, 30)], Png.Length)]);
        Png.CopyTo(disk, 30 * Cluster);
        records[20] = Record(20, 3, false, false, [Filename("archive.bin", 17, 3), Nonresident(0x80, [new(0, 1, 31), new(1, 1, 33)], Fragment.Length)]);
        Fragment.AsSpan(0, Cluster).CopyTo(disk.AsSpan(31 * Cluster)); Fragment.AsSpan(Cluster).CopyTo(disk.AsSpan(33 * Cluster));
        records[21] = Record(21, 3, false, false, [Filename("novel.custom", 16, 2), Resident(0x80, [0, 1, 2, 3, 255])]);
        records[22] = Record(22, 3, false, false, [Filename("sparse.dat", 16, 2), Nonresident(0x80, [new(0, 1, 35), new(1, 1, null), new(2, 1, 37)], 9000)]);
        disk.AsSpan(35 * Cluster, Cluster).Fill(65); disk.AsSpan(37 * Cluster, Cluster).Fill(66);
        records[23] = Record(23, 3, false, false, [Filename("empty.txt", 16, 2), Resident(0x80, [])]);
        records[24] = Record(24, 3, false, false, [Filename("reused.png", 16, 2), Nonresident(0x80, [new(0, 1, 38)], Png.Length)]);
        disk.AsSpan(38 * Cluster, Cluster).Fill(99);
        records[25] = Record(25, 3, false, false, [Filename("compressed.txt", 16, 2), Nonresident(0x80, [new(0, 1, 39)], 100, flags: 1)]);
        records[26] = Record(26, 3, false, false, [Filename("extended.bin", 16, 2), Nonresident(0x80, [new(0, 1, 40)], Fragment.Length), AttributeList((26, 3), (27, 3))]);
        records[27] = Record(27, 3, false, false, [Nonresident(0x80, [new(1, 1, 41)], 0)], 26, 3);
        Fragment.AsSpan(0, Cluster).CopyTo(disk.AsSpan(40 * Cluster)); Fragment.AsSpan(Cluster).CopyTo(disk.AsSpan(41 * Cluster));
        records[28] = Record(28, 3, false, false, [Filename("orphan.txt", 16, 999), Resident(0x80, "Parent directory was reused."u8.ToArray())]);
        records[29] = Record(29, 3, false, false, [Filename("initialized.dat", 16, 2), Nonresident(0x80, [new(0, 1, 42)], 1000, initialized: 100)]);
        disk.AsSpan(42 * Cluster, Cluster).Fill(88);
        // The allocation bitmap: MFT, system metadata and a reused file cluster are allocated.
        foreach (var run in mftRuns) for (long c = run.PhysicalCluster!.Value; c < run.PhysicalCluster + run.ClusterCount; c++) Mark(disk, c);
        Mark(disk, 0); Mark(disk, 20); Mark(disk, 38);
        for (int i = 0; i < Records; i++)
        {
            if (records[i] is null) continue;
            int byteOffset = i * RecordSize;
            int pos = fragmentedMft && byteOffset >= 8 * Cluster ? 70 * Cluster + byteOffset - 8 * Cluster : 4 * Cluster + byteOffset;
            records[i].CopyTo(disk, pos);
        }
        // Signature files with no MFT record, including one spanning the scanner's ordinary header overlap.
        Pdf.CopyTo(disk, 50 * Cluster + 37); OfficeZip().CopyTo(disk, 52 * Cluster + 11);
        File.WriteAllBytes(path, disk);
    }
    private static void Mark(byte[] disk, long cluster) => disk[20 * Cluster + cluster / 8] |= (byte)(1 << (int)(cluster % 8));
    public static byte[] Record(long number, ushort seq, bool inUse, bool dir, byte[][] attrs, long baseRecord = 0, ushort baseSeq = 0)
    {
        byte[] record = new byte[RecordSize]; "FILE"u8.CopyTo(record); U16(record, 4, 48); U16(record, 6, 3); U16(record, 16, seq);
        U16(record, 20, 56); U16(record, 22, (ushort)((inUse ? 1 : 0) | (dir ? 2 : 0))); U32(record, 28, RecordSize);
        U64(record, 32, baseRecord | ((long)baseSeq << 48)); U32(record, 44, number);
        int p = 56; foreach (var attr in attrs) { attr.CopyTo(record, p); p += attr.Length; }
        U32(record, p, uint.MaxValue); U32(record, 24, p + 8);
        U16(record, 48, 0xa55a);
        for (int i = 1; i <= 2; i++) { record[48 + i * 2] = record[i * 512 - 2]; record[49 + i * 2] = record[i * 512 - 1]; U16(record, i * 512 - 2, 0xa55a); }
        return record;
    }
    public static byte[] Filename(string name, long parent, ushort sequence)
    {
        byte[] value = new byte[66 + name.Length * 2]; U64(value, 0, parent | ((long)sequence << 48));
        U64(value, 16, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()); value[64] = (byte)name.Length; value[65] = 1;
        Encoding.Unicode.GetBytes(name).CopyTo(value, 66); return Resident(0x30, value);
    }
    public static byte[] Resident(uint type, byte[] value)
    {
        byte[] a = new byte[(24 + value.Length + 7) / 8 * 8]; U32(a, 0, type); U32(a, 4, a.Length); U32(a, 16, value.Length); U16(a, 20, 24); value.CopyTo(a, 24); return a;
    }
    public static byte[] Nonresident(uint type, List<DataRun> runs, long size, ushort flags = 0, long? initialized = null)
    {
        var encoded = new List<byte>(); long prev = 0;
        foreach (var run in runs)
        {
            // Four-byte signed LCN deltas allow testing negative and fragmented runs.
            encoded.Add(run.PhysicalCluster is null ? (byte)0x01 : (byte)0x41); encoded.Add((byte)run.ClusterCount);
            if (run.PhysicalCluster is long cluster) { encoded.AddRange(BitConverter.GetBytes((int)(cluster - prev))); prev = cluster; }
        }
        encoded.Add(0); byte[] a = new byte[(64 + encoded.Count + 7) / 8 * 8]; U32(a, 0, type); U32(a, 4, a.Length); a[8] = 1;
        U16(a, 12, flags); U64(a, 16, runs[0].VirtualCluster); U64(a, 24, runs[^1].VirtualCluster + runs[^1].ClusterCount - 1); U16(a, 32, 64);
        U64(a, 40, runs.Sum(x => x.ClusterCount) * Cluster); U64(a, 48, size); U64(a, 56, initialized ?? size); encoded.ToArray().CopyTo(a, 64); return a;
    }
    public static byte[] AttributeList(params (long Number, ushort Sequence)[] references)
    {
        byte[] entries = new byte[references.Length * 32];
        for (int i = 0; i < references.Length; i++)
        { int p = i * 32; U32(entries, p, 0x80); U16(entries, p + 4, 32); U64(entries, p + 8, i); U64(entries, p + 16, references[i].Number | ((long)references[i].Sequence << 48)); }
        return Resident(0x20, entries);
    }
    public static void U16(byte[] b, int p, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), n);
    public static void U32(byte[] b, int p, long n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), checked((uint)n));
    public static void U64(byte[] b, int p, long n) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), n);
}
