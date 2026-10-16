using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Core;

public static class RecycleBin
{
    public static List<RecoveryItem> Scan(ScanSource source, CancellationToken token, List<string> warnings)
    {
        List<RecoveryItem> found = [];
        if (source.IsImage || source.IsPhysicalDevice) return found;
        string recycle = Path.Combine(source.VolumeRoot, "$Recycle.Bin");
        if (!Directory.Exists(recycle)) return found;
        IEnumerable<string> folders;
        try { folders = Directory.GetDirectories(recycle); }
        catch (UnauthorizedAccessException) { warnings.Add("Some Recycle Bin folders require administrator access."); return found; }
        foreach (string sidFolder in folders)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (string info in Directory.EnumerateFiles(sidFolder, "$I*"))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var meta = new FileInfo(info); if (meta.Length > 65536 || meta.Length < 24) continue;
                        var data = File.ReadAllBytes(info);
                        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(data);
                        int offset = version == 2 ? 28 : 24;
                        if (version is not (1 or 2) || data.Length <= offset) continue;
                        string original = Encoding.Unicode.GetString(data, offset, (data.Length - offset) / 2 * 2).TrimEnd('\0');
                        if (!original.StartsWith(source.VolumeRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = original[source.VolumeRoot.Length..];
                        string filter = source.FolderFilter.Trim('\\');
                        if (filter.Length > 0 && !relative.StartsWith(filter + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        string content = Path.Combine(sidFolder, "$R" + Path.GetFileName(info)[2..]);
                        if (!File.Exists(content)) continue;
                        long declared = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
                        long actual = new FileInfo(content).Length;
                        found.Add(new()
                        {
                            Name = Path.GetFileName(original), OriginalPath = relative, Size = actual, InitializedSize = actual,
                            Method = "Recycle Bin", State = actual == declared ? RecoveryState.Resident : RecoveryState.AtRisk,
                            RecyclePath = content, Note = actual == declared ? "File still exists in the Recycle Bin. Recovery copies it without removing the original." : "Recycle Bin file size differs from its deleted-file metadata."
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
            catch (UnauthorizedAccessException) { warnings.Add($"Cannot read Recycle Bin folder {Path.GetFileName(sidFolder)}."); }
            catch (IOException ex) { warnings.Add("Recycle Bin read error: " + ex.Message); }
        }
        return found;
    }
}
