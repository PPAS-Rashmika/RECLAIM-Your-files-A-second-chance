using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;

namespace Reclaim.Core;

public static class RecoveryEngine
{
    public static ScanResult Scan(ScanSource source, ScanMode mode, Action<ScanProgress>? progress, CancellationToken token)
    {
        var result = new ScanResult { Source = source };
        progress ??= _ => { };
        try
        {
            progress(new("Checking Recycle Bin", 0, 1, 0));
            result.Items.AddRange(RecycleBin.Scan(source, token, result.Warnings));
            token.ThrowIfCancellationRequested();
            using var reader = new DiskReader(source);
            result.SourceLength = reader.Length;
            if (source.IsImage) result.ImageLastWriteUtc = File.GetLastWriteTimeUtc(source.DevicePath);
            else if (!source.IsPhysicalDevice) result.SourceVolumeIdentity = Native.VolumeIdentity(source.VolumeRoot);
            var partitions = Partitions.Discover(reader, result.Warnings);
            foreach (var partition in partitions)
            {
                token.ThrowIfCancellationRequested();
                long offset = partition.Offset;
                result.VolumeOffset = offset;
                var boot = reader.Length - offset >= 512 ? reader.Bytes(offset, 512) : new byte[512];
                result.BootHash = reader.Length - offset >= 512 ? SHA256.HashData(boot) : [];
                NtfsGeometry? geometry;
                try { geometry = NtfsGeometry.FromBoot(boot); }
                catch (Exception ex) when (ex is IOException or OverflowException)
                { geometry = null; result.Warnings.Add("Damaged NTFS boot geometry: " + ex.Message + " Deep scan can search signatures."); }
                try { result.VolumeLength = VolumeLength(boot, partition.Length, geometry); }
                catch (Exception ex) when (ex is IOException or OverflowException)
                { result.VolumeLength = partition.Length; geometry = null; result.Warnings.Add("Untrusted boot length: " + ex.Message + " Using the bounded partition range."); }
                NtfsVolume? ntfs = null;
                FatVolume? fat = null;
                var volume = new VolumeLayout(offset, result.VolumeLength, geometry?.ClusterSize ?? 0, partition.FileSystem, result.BootHash);
                void Add(RecoveryItem item) { item.Volume = volume; result.Items.Add(item); }
                if (geometry is not null)
                {
                    result.FileSystem = "NTFS"; result.ClusterSize = geometry.ClusterSize;
                    try
                    {
                        ntfs = new NtfsVolume(reader, offset, geometry);
                        ntfs.Scan(source.FolderFilter, progress, token, result.Warnings, Add);
                    }
                    catch (Exception ex) when (ex is IOException or OverflowException)
                    {
                        result.Warnings.Add("NTFS metadata could not be read: " + ex.Message + " Deep scan can search whole-volume signatures.");
                        ntfs = null;
                    }
                }
                else
                {
                    result.ClusterSize = 0; result.FileSystem = partition.FileSystem;
                    if (partition.FileSystem is "FAT" or "exFAT")
                    {
                        try
                        {
                            fat = new FatVolume(reader, offset, result.VolumeLength);
                            result.ClusterSize = fat.ClusterSize; result.FileSystem = fat.FileSystem;
                            volume = volume with { ClusterSize = fat.ClusterSize, FileSystem = fat.FileSystem };
                            fat.Scan(source.FolderFilter, progress, token, result.Warnings, Add);
                        }
                        catch (Exception ex) when (ex is IOException or OverflowException)
                        { fat = null; result.Warnings.Add("FAT directory metadata could not be read: " + ex.Message); }
                    }
                    else result.Warnings.Add("Partition at " + Categories.Size(offset) + " has no supported directory metadata. Use Deep scan for file signatures.");
                }
                result.Volumes.Add(volume);
                if (mode == ScanMode.Deep)
                {
                    if (source.FolderFilter.Length > 0)
                        result.Warnings.Add("Folder selection filters named results. Deep signatures search the volume because their original folder is unknown.");
                    List<ByteRange> ranges;
                    if (ntfs?.Bitmap is not null)
                    {
                        progress(new("Finding free space", 0, 1, result.Items.Count));
                        ranges = ntfs.Bitmap.FreeRanges(offset, geometry!.ClusterSize, token).ToList();
                    }
                    else if (fat?.HasAllocationMap == true) ranges = fat.FreeRanges(token).ToList();
                    else
                    {
                        ranges = [new(offset, result.VolumeLength)];
                        result.Warnings.Add("Scanning all bytes: signature results can include existing files and embedded content. Deletion status is unknown.");
                    }
                    long total = ranges.Sum(x => x.Length);
                    // Suppress a carved duplicate only if an NTFS file occupies exactly the same contiguous bytes.
                    var known = result.Items.Where(x => x.Volume == volume && x.Runs is { Count: 1 } && x.Runs[0].PhysicalCluster is not null)
                        .Select(x => (Offset: offset + x.Runs![0].PhysicalCluster!.Value * result.ClusterSize, x.Size))
                        .Concat(result.Items.Where(x => x.Volume == volume && x.Extents is { Count: 1 }).Select(x => (x.Extents![0].Offset, x.Size))).ToHashSet();
                    new SignatureScanner(reader, token).Scan(ranges, total, progress, result.Items.Count, item =>
                    {
                        if (!known.Contains((item.CarvedOffset!.Value, item.Size))) Add(item);
                    });
                }
            }
            if (result.Volumes.Count > 0)
            {
                var first = result.Volumes[0]; result.VolumeOffset = first.Offset; result.VolumeLength = first.Length;
                result.BootHash = first.BootHash; result.ClusterSize = first.ClusterSize;
                result.FileSystem = string.Join(" + ", result.Volumes.Select(x => x.FileSystem).Distinct());
            }
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        return result;
    }

    public static long FindVolume(DiskReader reader) => Partitions.Discover(reader, []).FirstOrDefault(x => x.FileSystem != "Raw")?.Offset ?? 0;

    private static long VolumeLength(byte[] boot, long available, NtfsGeometry? ntfs)
    {
        long length = available;
        if (ntfs is not null) length = ntfs.VolumeLength;
        else if (boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8))
        {
            int shift = boot[108]; long sectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(72));
            if (shift is >= 9 and <= 12 && sectors > 0) length = checked(sectors * (1L << shift));
        }
        else if (boot.AsSpan(82, 5).SequenceEqual("FAT32"u8) || boot.AsSpan(54, 3).SequenceEqual("FAT"u8))
        {
            int sector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
            long sectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19));
            if (sectors == 0) sectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32));
            if (sector is 512 or 1024 or 2048 or 4096 && sectors > 0) length = checked(sectors * sector);
        }
        if (length <= 0 || length > available) throw new InvalidDataException("The source image or volume is truncated.");
        return length;
    }

    public static void ValidateDestination(ScanSource source, string destination)
    {
        string target = Path.GetFullPath(destination);
        if (target.StartsWith(@"\\.\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a normal output folder.");
        string? root = Path.GetPathRoot(target);
        if (!source.IsImage)
        {
            if (!source.IsPhysicalDevice && string.Equals(root, source.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different drive or USB device for recovered files. Saving to the source drive can overwrite deleted data.");
            // Prevent folder junctions or mount points from silently redirecting output back to the source.
            for (string? p = target; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar)))
            {
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Choose an output folder without junctions, symbolic links or mounted volumes.");
                if (p == root) break;
            }
            if (OperatingSystem.IsWindows())
            {
                string existing = target;
                while (!Directory.Exists(existing))
                    existing = Path.GetDirectoryName(existing.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new IOException("Cannot resolve the destination drive.");
                if (source.IsPhysicalDevice && Native.VolumeDisks(Native.VolumeIdentity(existing)).Contains(int.Parse(source.DevicePath[17..])))
                    throw new InvalidOperationException("The destination is on the physical disk being scanned. Choose another disk or USB device.");
                if (!source.IsPhysicalDevice && string.Equals(Native.VolumeIdentity(existing), Native.VolumeIdentity(source.VolumeRoot), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Source and destination refer to the same volume. Choose a different drive or USB device.");
            }
        }
        else if (File.Exists(target) && string.Equals(target, Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The image cannot be used as the output folder.");
    }

    public static RecoveryOutcome Recover(ScanResult scan, IReadOnlyList<RecoveryItem> selected, string destination, Action<ScanProgress>? progress, CancellationToken token)
    {
        ValidateDestination(scan.Source, destination);
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one file to recover.");
        RecoveryContent.ValidateSource(scan);
        long required = selected.Where(x => x.CanRecover).Sum(x => x.Size);
        string outputRoot = Path.GetFullPath(destination);
        if (Path.GetPathRoot(outputRoot) is string root && !root.StartsWith(@"\\"))
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required + 1024 * 1024) throw new IOException("Not enough free space on the destination drive.");
        }
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputRoot);
        string folder = Path.Combine(outputRoot, "Reclaim_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        List<string> errors = []; int saved = 0, failed = 0; bool cancelled = false;
        var manifest = new List<object>();
        try
        {
            for (int index = 0; index < selected.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var item = selected[index]; string? output = null;
                string? partial = null; string status = "failed"; string error = "";
                try
                {
                    if (!item.CanRecover) throw new InvalidDataException(item.Note);
                    string category = Path.Combine(folder, item.Category.ToString()); Directory.CreateDirectory(category);
                    string safeName = SafeFileName(item.Name);
                    // Include the unique item ID to avoid collisions without overwriting any existing file.
                    output = Path.Combine(category, Path.GetFileNameWithoutExtension(safeName) + "_" + item.Id.ToString("N")[..8] + Path.GetExtension(safeName));
                    partial = output + ".partial";
                    using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                    {
                        using var content = RecoveryContent.Open(scan, item);
                        CopyStream(content, stream, item.Size, token);
                        if (stream.Length != item.Size) throw new IOException("Recovered size does not match the scan.");
                        stream.Flush(true);
                    }
                    File.Move(partial, output); partial = null;
                    saved++; status = "saved";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { failed++; error = ex.Message; errors.Add(item.Name + ": " + error); }
                finally { if (partial is not null && File.Exists(partial)) { try { File.Delete(partial); } catch (IOException) { } } }
                manifest.Add(new { item.Name, item.OriginalPath, item.Size, item.Method, Condition = item.Condition, item.Note, Status = status, Output = output, Error = error });
                progress?.Invoke(new("Recovering selected files", index + 1, selected.Count, saved, item.Name));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "recovery-report.json"), JsonSerializer.Serialize(new
            { Source = scan.Source.Description, scan.FileSystem, Cancelled = cancelled, Saved = saved, Failed = failed, Warnings = scan.Warnings, Files = manifest }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return new(saved, failed, cancelled, folder, errors);
    }
    private static void CopyStream(Stream source, Stream target, long size, CancellationToken token)
    {
        byte[] buffer = new byte[1024 * 1024]; long copied = 0;
        while (copied < size)
        {
            token.ThrowIfCancellationRequested(); int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, size - copied));
            if (n == 0) throw new EndOfStreamException("Source file was truncated."); target.Write(buffer, 0, n); copied += n;
        }
    }
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        string clean = new(name.Select(x => invalid.Contains(x) || char.IsControl(x) ? '_' : x).ToArray());
        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0 || clean is "." or "..") clean = "recovered_file";
        if (clean.Length > 170) clean = clean[..150] + Path.GetExtension(clean)[..Math.Min(20, Path.GetExtension(clean).Length)];
        string stem = clean.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) clean = "_" + clean;
        return clean;
    }
}
