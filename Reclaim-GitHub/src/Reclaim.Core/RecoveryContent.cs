using System.Security.Cryptography;

namespace Reclaim.Core;

// A bounded, seekable view of ONE recovered file. Preview and export use identical byte mapping.
public static class RecoveryContent
{
    public static void ValidateSource(ScanResult scan)
    {
        if (scan.Source.IsImage)
        {
            var info = new FileInfo(scan.Source.DevicePath);
            if (!info.Exists || info.Length != scan.SourceLength || info.LastWriteTimeUtc != scan.ImageLastWriteUtc)
                throw new IOException("The disk image changed since the scan. Scan it again.");
        }
        else if (!scan.Source.IsPhysicalDevice && scan.SourceVolumeIdentity is not null &&
            !string.Equals(scan.SourceVolumeIdentity, Native.VolumeIdentity(scan.Source.VolumeRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source drive changed since the scan. Scan again.");
    }
    public static Stream Open(ScanResult scan, RecoveryItem item)
    {
        if (!item.CanRecover) throw new InvalidDataException(item.Note);
        if (item.RecoveredPath is not null)
        {
            var info = new FileInfo(item.RecoveredPath);
            if (!info.Exists || info.Length != item.Size || info.LastWriteTimeUtc != item.RecoveredLastWriteUtc) throw new IOException("The recovered file changed. Run the advanced scan again.");
            return new FileStream(item.RecoveredPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        ValidateSource(scan);
        if (item.RecyclePath is not null)
        {
            var file = new FileStream(item.RecyclePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == item.Size) return file;
            file.Dispose(); throw new IOException("The Recycle Bin file changed since the scan.");
        }
        var volume = item.Volume ?? new(scan.VolumeOffset, scan.VolumeLength, scan.ClusterSize, scan.FileSystem, scan.BootHash ?? []);
        var reader = new DiskReader(scan.Source);
        try
        {
            if (reader.Length != scan.SourceLength || (volume.BootHash.Length > 0 && !SHA256.HashData(reader.Bytes(volume.Offset, 512)).AsSpan().SequenceEqual(volume.BootHash)))
                throw new IOException("The source volume changed since the scan. Scan again.");
            if (!scan.Source.IsImage)
            {
                if (item.RecordNumber is long recordId)
                {
                    var geometry = NtfsGeometry.FromBoot(reader.Bytes(volume.Offset, 512))!;
                    var record = new NtfsVolume(reader, volume.Offset, geometry).ReadRecord(recordId);
                    if (record is null || record.InUse || record.Sequence != item.RecordSequence)
                        throw new IOException("The deleted record has been reused since the scan.");
                }
                if (item.EntryOffset is long entry && item.EntryFingerprint is not null &&
                    !reader.Bytes(entry, item.EntryFingerprint.Length).AsSpan().SequenceEqual(item.EntryFingerprint))
                    throw new IOException("The deleted directory entry changed since the scan.");
            }
            if (item.ResidentData is not null) { reader.Dispose(); return new MemoryStream(item.ResidentData, false); }
            if (item.Runs is not null) NtfsVolume.ValidateRuns(item.Runs, item.Size, volume.ClusterSize, volume.Length);
            return new ContentStream(reader, item, volume);
        }
        catch { reader.Dispose(); throw; }
    }
    private sealed class ContentStream(DiskReader reader, RecoveryItem item, VolumeLayout volume) : Stream
    {
        private long position;
        private readonly NtfsStream? ntfs = item.Runs is null ? null : new(reader, volume.Offset, volume.ClusterSize, item.Runs, item.Size);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => item.Size;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (position >= Length) return 0;
            int count = (int)Math.Min(buffer.Length, Length - position); var output = buffer[..count];
            long initialized = item.CarvedOffset is not null || item.Pieces is not null ? Length : item.InitializedSize;
            int actual = (int)Math.Max(0, Math.Min(count, initialized - position));
            if (actual > 0)
            {
                var target = output[..actual];
                if (item.Pieces is not null)
                {
                    long logical = 0; int copied = 0;
                    foreach (var piece in item.Pieces)
                    {
                        if (piece.Length < 0 || logical > Length - piece.Length) throw new InvalidDataException("Invalid media piece mapping.");
                        if (position + copied >= logical && position + copied < logical + piece.Length)
                        {
                            long skip = position + copied - logical;
                            int n = (int)Math.Min(actual - copied, piece.Length - skip);
                            if (piece.Bytes is not null) piece.Bytes.AsSpan(checked((int)skip), n).CopyTo(target.Slice(copied, n));
                            else if (piece.Offset is long address) reader.ReadExactly(address + skip, target.Slice(copied, n));
                            else target.Slice(copied, n).Clear();
                            copied += n;
                            if (copied == actual) break;
                        }
                        logical += piece.Length;
                    }
                    if (copied != actual) throw new EndOfStreamException("Media pieces are incomplete.");
                }
                else if (ntfs is not null) ntfs.Read(position, target);
                else if (item.CarvedOffset is long offset) reader.ReadExactly(offset + position, target);
                else if (item.Extents is not null)
                {
                    long logical = 0; int copied = 0;
                    foreach (var extent in item.Extents)
                    {
                        if (position + copied < logical + extent.Length)
                        {
                            long skip = Math.Max(0, position + copied - logical);
                            int n = (int)Math.Min(actual - copied, extent.Length - skip);
                            reader.ReadExactly(extent.Offset + skip, target.Slice(copied, n)); copied += n;
                            if (copied == actual) break;
                        }
                        logical += extent.Length;
                    }
                    if (copied != actual) throw new EndOfStreamException("File extents are incomplete.");
                }
                else throw new InvalidDataException("No file data available.");
            }
            output[actual..].Clear(); position += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentException() }) + offset);
        protected override void Dispose(bool disposing) { if (disposing) reader.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
