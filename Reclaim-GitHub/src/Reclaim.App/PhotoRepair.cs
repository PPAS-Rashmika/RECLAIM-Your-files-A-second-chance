using System.Drawing.Imaging;

namespace Reclaim.App;

public static class PhotoRepair
{
    public static string Repair(string input, string output)
    {
        if (File.Exists(output) || Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a new repair output file.");
        var info = new FileInfo(input); if (info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Photo repair is limited to 64 MB encoded images.");
        byte[] bytes = File.ReadAllBytes(input); bool marker = false;
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && !(bytes[^2] == 0xff && bytes[^1] == 0xd9))
        { bytes = bytes.Concat(new byte[] {0xff, 0xd9}).ToArray(); marker = true; }
        try
        {
            using var stream = new MemoryStream(bytes, false); using var decoded = Image.FromStream(stream, true, true);
            if ((long)decoded.Width * decoded.Height > 25_000_000) throw new InvalidDataException("Photo exceeds the 25-million-pixel repair limit.");
            using var bitmap = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(decoded, 0, 0);
            using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) bitmap.Save(target, ImageFormat.Png);
            using var verified = Image.FromFile(output);
            if (verified.Width != bitmap.Width || verified.Height != bitmap.Height) throw new InvalidDataException("Repaired image validation failed.");
            return (marker ? "Added a missing JPEG end marker and " : "") + "re-encoded decodable pixels as PNG. Missing/overwritten pixels and original metadata are not reconstructed.";
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
}
