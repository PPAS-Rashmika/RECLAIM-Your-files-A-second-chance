using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Core;

// Read-only ext2/3/4 undelete. Deleted inodes must retain their original mapping;
// a cleared extent tree is never replaced with a guessed contiguous file.
public sealed class ExtVolume
{
    private readonly DiskReader reader;
    private readonly long offset, length, blocks, inodes, perGroup, inodesPerGroup;
    private readonly int blockSize, inodeSize, descriptorSize;
    private readonly uint incompat, roCompat;
    private readonly byte[] descriptors;
    private readonly Dictionary<int, byte[]> bitmaps = [];
    public string FileSystem { get; }
    public int BlockSize => blockSize;
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    public static bool IsExt(ReadOnlySpan<byte> super) => super.Length >= 256 && BinaryPrimitives.ReadUInt16LittleEndian(super[56..]) == 0xef53;
    public static long DeclaredLength(byte[] super)
    {
        if (!IsExt(super) || U32(super, 24) > 6) throw new InvalidDataException("Invalid ext superblock.");
        long count = U32(super, 4);
        if ((U32(super, 96) & 0x80) != 0) count = checked(count + ((long)U32(super, 336) << 32));
        return checked(count * (1024L << (int)U32(super, 24)));
    }
    public ExtVolume(DiskReader reader, long offset, long available)
    {
        this.reader = reader; this.offset = offset;
        var s = reader.Bytes(offset + 1024, 1024); length = DeclaredLength(s);
        if (length <= 2048 || length > available) throw new InvalidDataException("Truncated ext volume.");
        blockSize = 1024 << (int)U32(s, 24); blocks = length / blockSize;
        inodes = U32(s, 0); perGroup = U32(s, 32); inodesPerGroup = U32(s, 40);
        incompat = U32(s, 96); roCompat = U32(s, 100);
        if ((incompat & (0x10u | 0x1000u)) != 0 || (roCompat & 0x200) != 0)
            throw new InvalidDataException("ext meta_bg, directory-data and bigalloc layouts are not supported.");
        inodeSize = U32(s, 76) == 0 ? 128 : U16(s, 88);
        descriptorSize = (incompat & 0x80) != 0 ? U16(s, 254) : 32;
        if (perGroup == 0 || perGroup > blockSize * 8L || inodesPerGroup == 0 || inodesPerGroup > blockSize * 8L ||
            inodeSize < 128 || inodeSize > blockSize || inodeSize % 4 != 0 || descriptorSize is < 32 or > 1024)
            throw new InvalidDataException("Invalid ext group geometry.");
        long groups = (blocks + perGroup - 1) / perGroup;
        if (groups > 131072 || groups * descriptorSize > 16 * 1024 * 1024) throw new InvalidDataException("ext group table exceeds the safety limit.");
        descriptors = reader.Bytes(offset + (blockSize == 1024 ? 2L : 1L) * blockSize, checked((int)(groups * descriptorSize)));
        FileSystem = (incompat & 0x40) != 0 ? "ext4" : (U32(s, 92) & 4) != 0 ? "ext3" : "ext2";
    }
    private long GroupAddress(int group, int field)
    {
        int at = checked(group * descriptorSize); long block = U32(descriptors, at + field);
        if (descriptorSize >= 64 && (incompat & 0x80) != 0) block = checked(block + ((long)U32(descriptors, at + field + 32) << 32));
        if (block <= 0 || block >= blocks) throw new InvalidDataException("ext metadata address is outside the volume.");
        return offset + block * blockSize;
    }
    public void Scan(string folder, Action<ScanProgress> progress, CancellationToken token, List<string> warnings, Action<RecoveryItem> found)
    {
        warnings.Add("ext recovery uses surviving deleted inode mappings. Original directory names are not reconstructed. Cleared ext3/ext4 mappings require signatures; journal replay is not performed.");
        if (folder.Length > 0) { warnings.Add("The selected folder cannot be matched to ext deleted inode numbers. Clear the folder selection to include ext results."); return; }
        if ((roCompat & 0x400) != 0) warnings.Add("ext metadata checksums are not verified; ext candidates are marked Needs review.");
        long limit = Math.Min(inodes, 4_000_000); int count = 0, skipped = 0, bitmapGroup = -1; byte[] inodeBitmap = [];
        for (long number = 1; number <= limit; number++)
        {
            token.ThrowIfCancellationRequested();
            if (number % 1024 == 1) progress(new("Reading " + FileSystem + " deleted inodes", number, limit, count));
            try
            {
                int group = checked((int)((number - 1) / inodesPerGroup));
                if (group * descriptorSize >= descriptors.Length) break;
                if ((U16(descriptors, group * descriptorSize + 18) & 2) != 0) { number = Math.Min(limit, (group + 1) * inodesPerGroup); continue; }
                if (bitmapGroup != group) { inodeBitmap = reader.Bytes(GroupAddress(group, 4), blockSize); bitmapGroup = group; }
                long local = (number - 1) % inodesPerGroup;
                if ((inodeBitmap[local / 8] & (1 << (int)(local % 8))) != 0) continue;
                long address = checked(GroupAddress(group, 8) + local * inodeSize);
                if (address > offset + length - inodeSize) continue;
                byte[] inode = reader.Bytes(address, inodeSize);
                if ((U16(inode, 0) & 0xf000) != 0x8000 || U16(inode, 26) != 0 || U32(inode, 20) == 0) continue;
                long size = checked(U32(inode, 4) + ((long)U32(inode, 108) << 32));
                if (size <= 0 || size > length) continue;
                uint flags = U32(inode, 32);
                if ((flags & (4u | 0x800u | 0x10000000u)) != 0) { skipped++; continue; }
                var mapping = new SortedDictionary<long, (long Block, long Count, bool Zero)>();
                var seen = new HashSet<long>();
                // EXT4_EXTENTS_FL is 0x00080000; inline and encrypted data are rejected above separately.
                if ((flags & 0x80000) != 0) ReadExtents(inode.AsSpan(40, 60).ToArray(), mapping, seen, token, 0);
                else
                {
                    long logical = 0;
                    for (int i = 0; i < 12; i++, logical++) AddPointer(U32(inode, 40 + i * 4), logical, mapping);
                    for (int level = 1; level <= 3 && logical * blockSize < size; level++)
                    {
                        long span = 1; for (int j = 0; j < level; j++) span = checked(span * (blockSize / 4));
                        ReadIndirect(U32(inode, 84 + level * 4), level, logical, size, mapping, seen, token);
                        logical += span;
                    }
                }
                if (mapping.Count == 0 || mapping.Values.All(x => x.Block == 0 && !x.Zero)) { skipped++; continue; }
                var pieces = new List<ContentPiece>(); long cursor = 0; bool reused = false;
                foreach (var (logical, entry) in mapping)
                {
                    long start = checked(logical * blockSize), bytes = checked(entry.Count * blockSize);
                    if (start >= size) break;
                    if (start < cursor) throw new InvalidDataException("Overlapping ext extents.");
                    if (start > cursor) pieces.Add(new(start - cursor));
                    bytes = Math.Min(bytes, size - start);
                    pieces.Add(new(bytes, entry.Zero || entry.Block == 0 ? null : offset + entry.Block * blockSize));
                    if (!entry.Zero && entry.Block != 0)
                        for (long b = entry.Block; b < entry.Block + entry.Count; b++) { token.ThrowIfCancellationRequested(); if (Allocated(b)) reused = true; }
                    cursor = start + bytes;
                }
                if (cursor < size) pieces.Add(new(size - cursor));
                if (pieces.All(x => x.Offset is null)) { skipped++; continue; }
                string ext = GuessExtension(pieces);
                count++; found(new RecoveryItem
                {
                    Name = $"inode_{number}.{ext}", OriginalPath = "Lost inode names", Size = size, InitializedSize = size,
                    Method = FileSystem + " deleted inode", Pieces = pieces, State = reused || (roCompat & 0x400) != 0 || (incompat & 4) != 0 ? RecoveryState.AtRisk : RecoveryState.Metadata,
                    EntryOffset = address, EntryFingerprint = inode,
                    Note = "Deleted inode with surviving block mapping. Original filename/path are unknown. " + (reused ? "One or more blocks are allocated now." : "Mapped data blocks are free now."),
                    Modified = DateTimeOffset.FromUnixTimeSeconds(U32(inode, 16)).UtcDateTime
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException) { skipped++; }
            if (count >= 250_000) { warnings.Add("ext result safety limit reached."); break; }
        }
        if (limit < inodes) warnings.Add("ext inode scan stopped at the 4-million inode safety limit.");
        if (skipped > 0) warnings.Add($"ext: {skipped:N0} cleared, unsupported or damaged inode mappings were skipped.");
    }
    private bool Allocated(long block)
    {
        long first = blockSize == 1024 ? 1 : 0;
        int group = checked((int)((block - first) / perGroup)); long index = (block - first) % perGroup;
        if (group < 0 || group * descriptorSize >= descriptors.Length) throw new InvalidDataException("Invalid ext allocation group.");
        if ((U16(descriptors, group * descriptorSize + 18) & 1) != 0) return true;
        if (!bitmaps.TryGetValue(group, out var bitmap))
        {
            if (bitmaps.Count > 256) bitmaps.Clear();
            bitmaps[group] = bitmap = reader.Bytes(GroupAddress(group, 0), blockSize);
        }
        return (bitmap[index / 8] & (1 << (int)(index % 8))) != 0;
    }
    private void AddPointer(long block, long logical, SortedDictionary<long, (long, long, bool)> mapping)
    {
        if (mapping.Count > 262144 || block >= blocks) throw new InvalidDataException("ext mapping exceeds bounds.");
        if (block != 0) mapping.Add(logical, (block, 1, false));
    }
    private void ReadIndirect(long block, int level, long logical, long size, SortedDictionary<long, (long, long, bool)> map, HashSet<long> seen, CancellationToken token)
    {
        if (block == 0) return;
        if (block >= blocks || !seen.Add(block) || seen.Count > 8192) throw new InvalidDataException("Invalid ext indirect tree.");
        byte[] bytes = reader.Bytes(offset + block * blockSize, blockSize); long step = 1;
        for (int i = 1; i < level; i++) step *= blockSize / 4;
        for (int i = 0; i < blockSize / 4 && logical * blockSize < size; i++, logical += step)
        {
            token.ThrowIfCancellationRequested(); uint value = U32(bytes, i * 4);
            if (level == 1) AddPointer(value, logical, map); else ReadIndirect(value, level - 1, logical, size, map, seen, token);
        }
    }
    private void ReadExtents(byte[] node, SortedDictionary<long, (long, long, bool)> map, HashSet<long> seen, CancellationToken token, int recursion)
    {
        if (node.Length < 12 || U16(node, 0) != 0xf30a || recursion > 5) throw new InvalidDataException("Invalid ext extent header.");
        int count = U16(node, 2), depth = U16(node, 6);
        if (depth > 5 || count > (node.Length - 12) / 12 || count > U16(node, 4)) throw new InvalidDataException("Invalid ext extent count.");
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); int p = 12 + i * 12; long logical = U32(node, p);
            if (depth == 0)
            {
                int raw = U16(node, p + 4); bool zero = raw > 32768; int countBlocks = zero ? raw - 32768 : raw;
                long block = ((long)U16(node, p + 6) << 32) | U32(node, p + 8);
                if (countBlocks == 0 || block <= 0 || block > blocks - countBlocks || map.Count > 262144) throw new InvalidDataException("Invalid ext extent bounds.");
                map.Add(logical, (block, countBlocks, zero));
            }
            else
            {
                long block = U32(node, p + 4) | ((long)U16(node, p + 8) << 32);
                if (block <= 0 || block >= blocks || !seen.Add(block) || seen.Count > 8192) throw new InvalidDataException("Invalid ext extent index.");
                byte[] child = reader.Bytes(offset + block * blockSize, blockSize);
                if (U16(child, 6) != depth - 1) throw new InvalidDataException("Inconsistent ext extent depth.");
                ReadExtents(child, map, seen, token, recursion + 1);
            }
        }
    }
    private string GuessExtension(List<ContentPiece> pieces)
    {
        var first = pieces[0]; if (first.Offset is null || first.Length < 8) return "bin";
        var h = reader.Bytes(first.Offset.Value, 8);
        if (h.AsSpan().StartsWith("%PDF-"u8)) return "pdf";
        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4e && h[3] == 0x47) return "png";
        if (h[0] == 0xff && h[1] == 0xd8) return "jpg";
        if (h[0] == 0x50 && h[1] == 0x4b) return "zip";
        return "bin";
    }
}
