// Independent .NET Framework rescue console for WinPE-NetFX. C# 5 compatible.
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
[assembly: System.Reflection.AssemblyVersion("2.2.0.0")]

internal static class Rescue
{
    private static bool stopping;
    private static void CheckStop() { if (stopping) throw new OperationCanceledException("Recovery stopped. Completed exports are retained."); }
    private sealed class Item { public long Offset, Size; public string Extension; }
    private static string Arg(string[] args, string key) { int p = Array.IndexOf(args, key); return p >= 0 && p + 1 < args.Length ? args[p + 1] : null; }
    private static int Main(string[] args)
    {
        try
        {
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stopping = true; };
            Console.WriteLine("RECLAIM RESCUE 2.2.0 - read-only source recovery\nJPEG / PNG / PDF signatures. Original names/folders and deletion state are unknown.\n");
            string source = Arg(args, "--image") ?? Arg(args, "--disk"); string destination = Arg(args, "--out");
            if (source == null)
            {
                try { using (var q = new ManagementObjectSearcher("SELECT Index,Model,Size FROM Win32_DiskDrive")) foreach (ManagementObject d in q.Get()) Console.WriteLine("PhysicalDrive{0}: {1} ({2} bytes)", d["Index"], d["Model"], d["Size"]); }
                catch (ManagementException) { Console.WriteLine("Disk inventory unavailable. Enter the exact disk/image path."); }
                Console.Write("\nSource physical disk number or .img path: "); source = Console.ReadLine();
                int number; if (int.TryParse(source, out number) && number >= 0) source = @"\\.\PhysicalDrive" + number;
            }
            if (String.IsNullOrWhiteSpace(source)) return 1;
            if (destination == null) { Console.Write("Output folder on ANOTHER physical disk (e.g. E:\\Recovered): "); destination = Console.ReadLine(); }
            ProtectDestination(source, destination);
            using (var disk = new Disk(source))
            {
                var items = Scan(disk); Console.WriteLine("\nFound {0} candidates. Unreadable 4 KiB areas skipped: {1}. Export uses strict reads.", items.Count, disk.BadUnits);
                bool all = Array.IndexOf(args, "--all") >= 0; HashSet<int> selected = new HashSet<int>();
                if (!all)
                {
                    Console.Write("Save all, comma-separated file numbers, or q: "); string choice = Console.ReadLine() ?? "q";
                    if (choice.Equals("q", StringComparison.OrdinalIgnoreCase)) return 0;
                    all = choice.Equals("all", StringComparison.OrdinalIgnoreCase);
                    if (!all) foreach (string part in choice.Split(',')) { int n; if (int.TryParse(part.Trim(), out n) && n >= 1 && n <= items.Count) selected.Add(n - 1); }
                }
                disk.Validate(); string folder = Path.Combine(Path.GetFullPath(destination), "Reclaim_Rescue_" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(folder);
                int saved = 0, failed = 0;
                using (var report = new StreamWriter(Path.Combine(folder, "rescue-report.tsv"), false, Encoding.UTF8))
                {
                    report.WriteLine("Offset\tSize\tFormat\tStatus\tFile\tNote");
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!all && !selected.Contains(i)) continue;
                        Item item = items[i]; string path = Path.Combine(folder, "found_" + item.Offset.ToString("X12") + "." + item.Extension), partial = path + ".partial";
                        try
                        {
                            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                for (long p = 0; p < item.Size;) { CheckStop(); int n = (int)Math.Min(1024 * 1024, item.Size - p); byte[] bytes = disk.Read(item.Offset + p, n, false); output.Write(bytes, 0, n); p += n; }
                            File.Move(partial, path); saved++; report.WriteLine(item.Offset + "\t" + item.Size + "\t" + item.Extension + "\tsaved\t" + Path.GetFileName(path) + "\tStructure candidate; review contents");
                        }
                        catch (IOException ex) { failed++; report.WriteLine(item.Offset + "\t" + item.Size + "\t" + item.Extension + "\tfailed\t\t" + ex.Message.Replace('\t', ' ')); }
                        finally { if (File.Exists(partial)) File.Delete(partial); }
                    }
                }
                Console.WriteLine("Saved: {0}; Failed: {1}\n{2}", saved, failed, folder);
                return failed == 0 ? 0 : 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    private static void ProtectDestination(string source, string destination)
    {
        if (String.IsNullOrWhiteSpace(destination)) throw new IOException("An output folder is required.");
        string full = Path.GetFullPath(destination), root = Path.GetPathRoot(full);
        if (root.Length != 3 || root[1] != ':') throw new IOException("Choose a local output drive.");
        for (string p = full; !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Output junctions and mounted-folder redirection are not allowed.");
        if (!source.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase)) return;
        int sourceDisk; if (!int.TryParse(source.Substring(17), out sourceDisk)) throw new IOException("Invalid source disk number.");
        bool verified = false;
        using (var q = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + root.Substring(0, 2) + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
            foreach (ManagementObject p in q.Get())
            { verified = true; if (Convert.ToInt32(p["DiskIndex"]) == sourceDisk) throw new IOException("Output is on the source physical disk. Choose a different disk/USB."); }
        if (!verified) throw new IOException("Cannot verify the output physical disk. Choose a normal USB/local drive.");
    }
    private static List<Item> Scan(Disk disk)
    {
        var items = new List<Item>(); long covered = -1;
        for (long offset = 0; offset < disk.Length;)
        {
            CheckStop();
            int step = (int)Math.Min(4 * 1024 * 1024, disk.Length - offset), n = (int)Math.Min(step + 32L, disk.Length - offset); byte[] bytes = disk.Read(offset, n, true);
            for (int p = 0; p < step && p + 8 <= n; p++)
            {
                long at = offset + p; if (at < covered) continue;
                string extension = null; long end = -1;
                try
                {
                    if (bytes[p] == 0xff && bytes[p + 1] == 0xd8 && bytes[p + 2] == 0xff) { end = Jpeg(disk, at); extension = "jpg"; }
                    else if (bytes[p] == 137 && Encoding.ASCII.GetString(bytes, p + 1, 3) == "PNG") { end = Png(disk, at); extension = "png"; }
                    else if (bytes[p] == '%' && Encoding.ASCII.GetString(bytes, p, 5) == "%PDF-") { long eof = Find(disk, at + 8, Limit(disk, at), new byte[] {37,37,69,79,70}); if (eof >= 0) end = eof + 5; extension = "pdf"; }
                }
                catch (IOException) { }
                if (end <= at) continue;
                items.Add(new Item { Offset = at, Size = end - at, Extension = extension }); covered = end;
                Console.WriteLine("[{0}] {1} at {2:X12}, {3:N0} bytes", items.Count, extension, at, end - at);
                if (items.Count >= 100000) { Console.WriteLine("100,000-result safety limit reached."); return items; }
            }
            offset += step; if ((offset / (4 * 1024 * 1024)) % 64 == 0) Console.WriteLine("Scanned {0:N0} / {1:N0} MB", offset / 1048576, disk.Length / 1048576);
        }
        return items;
    }
    private static long Limit(Disk disk, long at) { return Math.Min(disk.Length, at + 512L * 1024 * 1024); }
    private static long Find(Disk disk, long start, long end, byte[] marker)
    {
        for (long p = start; p < end;)
        {
            CheckStop();
            int step = (int)Math.Min(65536, end - p), n = (int)Math.Min(step + marker.Length - 1L, end - p); byte[] b = disk.Read(p, n, true);
            for (int i = 0; i <= n - marker.Length; i++) { bool same = true; for (int j = 0; j < marker.Length; j++) if (b[i + j] != marker[j]) { same = false; break; } if (same) return p + i; }
            p += step;
        }
        return -1;
    }
    private static long Jpeg(Disk disk, long start)
    {
        long end = Limit(disk, start), p = start + 2; bool frame = false;
        while (p + 4 <= end)
        {
            CheckStop();
            byte[] h = disk.Read(p, 4, true); if (h[0] != 0xff) return -1; int id = h[1]; if (id == 0xff) { p++; continue; }
            if (id == 0 || id == 0xd8 || id == 0xd9) return -1;
            if (id == 1 || id >= 0xd0 && id <= 0xd7) { p += 2; continue; }
            int size = h[2] * 256 + h[3]; if (size < 2 || size > end - p - 2) return -1;
            if (id >= 0xc0 && id <= 0xcf && id != 0xc4 && id != 0xc8 && id != 0xcc) frame = true;
            p += 2 + size;
            if (id == 0xda) { if (!frame) return -1; long eof = Find(disk, p, end, new byte[] {255,217}); return eof < 0 ? -1 : eof + 2; }
        }
        return -1;
    }
    private static uint Big(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
    private static uint Crc(uint crc, byte[] b, int start, int count)
    { for (int i = start; i < start + count; i++) { crc ^= b[i]; for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); } return crc; }
    private static long Png(Disk disk, long start)
    {
        byte[] magic = disk.Read(start, 8, true); byte[] expected = {137,80,78,71,13,10,26,10};
        for (int i = 0; i < 8; i++) if (magic[i] != expected[i]) return -1;
        long p = start + 8, end = Limit(disk, start); bool first = true, data = false;
        while (p + 12 <= end)
        {
            CheckStop();
            byte[] h = disk.Read(p, 8, true); long size = Big(h, 0); string type = Encoding.ASCII.GetString(h, 4, 4);
            if (size > end - p - 12 || first && (type != "IHDR" || size != 13)) return -1;
            uint crc = Crc(uint.MaxValue, h, 4, 4);
            for (long part = 0; part < size;) { int n = (int)Math.Min(65536, size - part); byte[] b = disk.Read(p + 8 + part, n, true); crc = Crc(crc, b, 0, n); part += n; }
            if ((crc ^ uint.MaxValue) != Big(disk.Read(p + 8 + size, 4, true), 0)) return -1;
            p += size + 12; first = false; if (type == "IDAT") data = true; if (type == "IEND") return size == 0 && data ? p : -1;
        }
        return -1;
    }
    private sealed class Disk : IDisposable
    {
        private readonly FileStream stream; private readonly string path; private readonly bool image; private readonly DateTime modified;
        public long Length { get; private set; } public long BadUnits { get; private set; }
        public Disk(string path)
        {
            this.path = path; image = !path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase);
            if (image) { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); Length = stream.Length; modified = File.GetLastWriteTimeUtc(path); }
            else
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^\\\\\.\\PhysicalDrive[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new IOException("Choose a physical disk or a normal image file.");
                SafeFileHandle handle = CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open source. Administrator access may be required."); }
                byte[] size = new byte[8]; int returned;
                if (!DeviceIoControl(handle, 0x7405c, IntPtr.Zero, 0, size, 8, out returned, IntPtr.Zero)) { handle.Dispose(); throw new IOException("Cannot read disk length."); }
                Length = BitConverter.ToInt64(size, 0); stream = new FileStream(handle, FileAccess.Read, 1, false);
            }
        }
        public void Validate() { if (image && (new FileInfo(path).Length != Length || File.GetLastWriteTimeUtc(path) != modified)) throw new IOException("Source image changed. Scan again."); }
        public byte[] Read(long offset, int count, bool skip)
        {
            if (offset < 0 || count < 0 || offset > Length - count) throw new IOException("Invalid source range.");
            try { return Direct(offset, count); } catch (IOException) { if (!skip) throw; }
            byte[] result = new byte[count];
            for (int done = 0; done < count;)
            {
                long at = offset + done; int n = (int)Math.Min(count - done, 4096 - at % 4096);
                try { byte[] part = Direct(at, n); Buffer.BlockCopy(part, 0, result, done, n); }
                catch (IOException) { BadUnits++; }
                done += n;
            }
            return result;
        }
        private byte[] Direct(long offset, int count)
        {
            long aligned = image ? offset : offset / 4096 * 4096; int skip = (int)(offset - aligned);
            int length = image ? count : (int)Math.Min(((skip + count + 4095L) / 4096) * 4096, Length - aligned);
            byte[] buffer = new byte[length]; IOException last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { stream.Position = aligned; int done = 0; while (done < length) { int n = stream.Read(buffer, done, length - done); if (n == 0) throw new EndOfStreamException(); done += n; } byte[] result = new byte[count]; Buffer.BlockCopy(buffer, skip, result, 0, count); return result; }
                catch (IOException ex) { last = ex; }
            }
            throw last;
        }
        public void Dispose() { stream.Dispose(); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
