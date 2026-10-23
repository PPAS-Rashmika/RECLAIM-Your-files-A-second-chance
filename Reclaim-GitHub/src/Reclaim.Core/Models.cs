namespace Reclaim.Core;

public enum FileCategory { Images, Videos, Audio, Documents, Archives, Other }
public enum ScanMode { Quick, Deep, LostPartitions, Camera }
public enum RecoveryState { Metadata, Resident, Signature, AtRisk, Unsupported }
public sealed record DataRun(long VirtualCluster, long ClusterCount, long? PhysicalCluster);
public sealed record ByteRange(long Offset, long Length);
public sealed record ContentPiece(long Length, long? Offset = null, byte[]? Bytes = null);
public sealed record VolumeLayout(long Offset, long Length, int ClusterSize, string FileSystem, byte[] BootHash);

public sealed class RecoveryItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string OriginalPath { get; init; }
    public required long Size { get; init; }
    public required string Method { get; init; }
    public FileCategory Category => Categories.Of(Name);
    public string Format => Categories.FormatOf(Name);
    public RecoveryState State { get; set; }
    public string Note { get; set; } = "";
    public DateTime? Modified { get; init; }
    public long? RecordNumber { get; init; }
    public ushort RecordSequence { get; init; }
    public byte[]? ResidentData { get; init; }
    public List<DataRun>? Runs { get; init; }
    public long InitializedSize { get; init; }
    public long? CarvedOffset { get; init; }
    public string? RecyclePath { get; init; }
    public string? RecoveredPath { get; init; }
    public DateTime? RecoveredLastWriteUtc { get; init; }
    public List<ByteRange>? Extents { get; init; }
    public List<ContentPiece>? Pieces { get; init; }
    public string Validation { get; set; } = "Not checked";
    public bool ValidationFailed { get; set; }
    public string RecoveryChance => !CanRecover ? "Unavailable" : ValidationFailed ? "Low · validation failed" : State switch
    {
        RecoveryState.Resident => "Good · retained content",
        RecoveryState.Metadata => "Good · allocation clear",
        RecoveryState.Signature => "Possible · structure found",
        _ => "Low · review required"
    };
    public long? EntryOffset { get; init; }
    public byte[]? EntryFingerprint { get; init; }
    public VolumeLayout? Volume { get; set; }
    public bool CanRecover => State != RecoveryState.Unsupported;
    public string Condition => State switch
    {
        RecoveryState.Resident => "Stored in record",
        RecoveryState.Metadata => "Clusters free",
        RecoveryState.Signature => "Signature found",
        RecoveryState.AtRisk => "Needs review",
        _ => "Unavailable"
    };
}

public sealed record ScanSource(string Path, bool IsImage, string FolderFilter = "", bool IsPhysicalDevice = false)
{
    public string VolumeRoot => IsImage || IsPhysicalDevice ? "" : System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path))!;
    public string DevicePath => IsPhysicalDevice ? ValidateDevice(Path) : IsImage ? System.IO.Path.GetFullPath(Path) : @"\\.\" + VolumeRoot.TrimEnd('\\');
    public string Description => IsImage ? System.IO.Path.GetFileName(Path) : Path;
    private static string ValidateDevice(string path) => System.Text.RegularExpressions.Regex.IsMatch(path, @"^\\\\\.\\PhysicalDrive[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        ? path : throw new ArgumentException("Select a Windows physical disk.");
}

public sealed record ScanProgress(string Phase, long Done, long Total, int Found, string Detail = "")
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}
public sealed class ScanResult
{
    public required ScanSource Source { get; init; }
    public List<RecoveryItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<VolumeLayout> Volumes { get; } = [];
    public List<ByteRange> BadRanges { get; } = [];
    public List<Partition> LostPartitions { get; } = [];
    public long VolumeOffset { get; set; }
    public long VolumeLength { get; set; }
    public long SourceLength { get; set; }
    public DateTime? ImageLastWriteUtc { get; set; }
    public string? SourceVolumeIdentity { get; set; }
    public byte[]? BootHash { get; set; }
    public int ClusterSize { get; set; }
    public string FileSystem { get; set; } = "Unknown";
    public bool Cancelled { get; set; }
    public bool InProgress { get; init; }
}
public sealed record RecoveryOutcome(int Saved, int Failed, bool Cancelled, string Folder, List<string> Errors);

public static class Categories
{
    private static readonly Dictionary<string, FileCategory> Types = new(StringComparer.OrdinalIgnoreCase);
    static Categories()
    {
        Add(FileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic heif avif raw cr2 cr3 nef arw dng svg ico psd eps");
        Add(FileCategory.Videos, "mp4 mov m4v mkv avi webm wmv flv mpg mpeg 3gp mts m2ts vob ts");
        Add(FileCategory.Audio, "mp3 wav flac aac m4a ogg opus wma aiff aif midi mid amr");
        Add(FileCategory.Documents, "pdf doc docx xls xlsx ppt pptx txt rtf csv tsv odt ods odp md json xml html htm log epub pages numbers key sql");
        Add(FileCategory.Archives, "zip rar 7z gz tar bz2 xz iso cab zst tgz");
    }
    private static void Add(FileCategory type, string extensions)
    {
        foreach (var ext in extensions.Split(' ')) Types["." + ext] = type;
    }
    public static FileCategory Of(string name) => Types.GetValueOrDefault(System.IO.Path.GetExtension(name), FileCategory.Other);
    public static string ExtensionOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant();
    public static string FormatOf(string name)
    {
        string extension = ExtensionOf(name);
        return extension.Length == 0 ? "No extension" : new string(extension[1..].Select(c => char.IsControl(c) ? '_' : c).ToArray()).ToUpperInvariant();
    }
    public static string Size(long value)
    {
        double size = value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:0.#} {units[i]}";
    }
}
