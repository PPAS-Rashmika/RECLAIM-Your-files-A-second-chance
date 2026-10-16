using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Tests;

internal static class FatFixtures
{
    public static void Create(string path, int bits)
    {
        int sectors = bits == 12 ? 4000 : bits == 16 ? 6000 : 70000;
        int fatSectors = bits == 12 ? 12 : bits == 16 ? 24 : 550;
        int reserved = bits == 32 ? 32 : 1, rootSectors = bits == 32 ? 0 : 4;
        byte[] image = new byte[sectors * 512]; image[0] = 0xeb;
        U16(image, 11, 512); image[13] = 1; U16(image, 14, reserved); image[16] = 1; U16(image, 17, bits == 32 ? 0 : 64);
        if (sectors <= ushort.MaxValue) U16(image, 19, sectors); else U32(image, 32, sectors);
        if (bits == 32) { U32(image, 36, fatSectors); U32(image, 44, 2); Encoding.ASCII.GetBytes("FAT32   ").CopyTo(image, 82); }
        else { U16(image, 22, fatSectors); Encoding.ASCII.GetBytes("FAT" + bits + "   ").CopyTo(image, 54); }
        image[510] = 0x55; image[511] = 0xaa;
        int root = (reserved + fatSectors) * 512, heap = root + rootSectors * 512;
        int Address(int c) => heap + (c - 2) * 512;
        if (bits == 32) root = Address(2);
        void Fat(int c, int next)
        {
            int at = reserved * 512;
            if (bits == 12)
            {
                at += c + c / 2; int current = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at));
                U16(image, at, (c & 1) == 0 ? (current & 0xf000) | (next & 0xfff) : (current & 0xf) | ((next & 0xfff) << 4));
            }
            else if (bits == 16) U16(image, at + c * 2, next); else U32(image, at + c * 4, next);
        }
        int eoc = bits == 12 ? 0xfff : bits == 16 ? 0xffff : 0x0fffffff;
        Fat(0, eoc); Fat(1, eoc); if (bits == 32) Fat(2, eoc); Fat(12, eoc); Fat(8, 10); Fat(10, eoc);
        void Entry(int at, string name, int first, int size, bool deleted = true, bool dir = false)
        { Encoding.ASCII.GetBytes(name).CopyTo(image, at); if (deleted) image[at] = 0xe5; image[at + 11] = dir ? (byte)0x10 : (byte)0x20; U16(image, at + 26, first); U32(image, at + 28, size); }
        Entry(root, "CUSTOM  XYZ", 5, 900); Entry(root + 32, "FRAG    BIN", 8, 900); Entry(root + 64, "FOLDER     ", 12, 0, false, true);
        Entry(Address(12), "MYFILE  TXT", 14, 100); Array.Fill(image, (byte)84, Address(14), 100);
        Array.Fill(image, (byte)65, Address(5), 512); Array.Fill(image, (byte)66, Address(6), 388);
        Array.Fill(image, (byte)67, Address(8), 512); Array.Fill(image, (byte)68, Address(10), 388);
        File.WriteAllBytes(path, image);
    }
    public static void CreateExFat(string path, bool corrupt = false)
    {
        var image = new byte[128 * 512]; image[0] = 0xeb; Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(image, 3);
        U64(image, 72, 128); U32(image, 80, 16); U32(image, 84, 1); U32(image, 88, 24); U32(image, 92, 64); U32(image, 96, 2);
        image[108] = 9; image[109] = 0; image[110] = 1; image[510] = 0x55; image[511] = 0xaa;
        int Address(int c) => (24 + c - 2) * 512;
        U32(image, 16 * 512 + 2 * 4, -1); U32(image, 16 * 512 + 3 * 4, -1); U32(image, 16 * 512 + 8 * 4, 10); U32(image, 16 * 512 + 10 * 4, -1);
        int root = Address(2); image[root] = 0x81; U32(image, root + 20, 3); U64(image, root + 24, 8); image[Address(3)] = 3;
        void Deleted(int at, string name, int first, int length, int initialized, bool contiguous)
        {
            var set = new byte[96]; set[0] = 0x85; set[1] = 2; U16(set, 4, 0x20); set[32] = 0xc0; set[33] = contiguous ? (byte)3 : (byte)1;
            set[35] = (byte)name.Length; U64(set, 40, initialized); U32(set, 52, first); U64(set, 56, length); set[64] = 0xc1; Encoding.Unicode.GetBytes(name).CopyTo(set, 66);
            ushort sum = 0; for (int i = 0; i < set.Length; i++) if (i is not (2 or 3)) sum = (ushort)(((sum << 15) | (sum >> 1)) + set[i]);
            U16(set, 2, sum); for (int i = 0; i < 3; i++) set[i * 32] &= 0x7f;
            if (corrupt) set[70] ^= 1; set.CopyTo(image, at);
        }
        Deleted(root + 32, "holiday.custom", 5, 700, 600, true); Deleted(root + 128, "fragment.bin", 8, 900, 900, false);
        Array.Fill(image, (byte)65, Address(5), 512); Array.Fill(image, (byte)66, Address(6), 188);
        Array.Fill(image, (byte)67, Address(8), 512); Array.Fill(image, (byte)68, Address(10), 388);
        File.WriteAllBytes(path, image);
    }
    private static void U16(byte[] b, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)value);
    private static void U32(byte[] b, int at, int value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)value);
    private static void U64(byte[] b, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), value);
}
