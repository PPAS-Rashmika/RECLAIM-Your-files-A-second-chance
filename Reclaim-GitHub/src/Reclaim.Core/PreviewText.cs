using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reclaim.Core;

public static class PreviewText
{
    private const int TextLimit = 256 * 1024;
    public static byte[] Head(ScanResult scan, RecoveryItem item, int count = 4096)
    {
        using var stream = RecoveryContent.Open(scan, item);
        var bytes = new byte[(int)Math.Min(item.Size, count)]; stream.ReadExactly(bytes); return bytes;
    }
    public static string Hex(byte[] bytes)
    {
        var result = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            var line = bytes.AsSpan(i, Math.Min(16, bytes.Length - i));
            result.Append($"{i:X8}  "); foreach (byte b in line) result.Append($"{b:X2} ");
            result.Append(new string(' ', (16 - line.Length) * 3)).Append("  ");
            foreach (byte b in line) result.Append(b is >= 32 and < 127 ? (char)b : '.'); result.AppendLine();
        }
        return result.ToString();
    }
    public static bool IsText(string extension) => new[] { ".txt", ".csv", ".tsv", ".md", ".json", ".xml", ".html", ".htm", ".log", ".sql", ".py", ".cs", ".js", ".ts", ".css", ".yaml", ".yml", ".ini", ".conf", ".svg", ".rtf" }.Contains(extension.ToLowerInvariant());
    public static string Read(ScanResult scan, RecoveryItem item)
    {
        string ext = Path.GetExtension(item.Name).ToLowerInvariant();
        using var stream = RecoveryContent.Open(scan, item);
        if (ext is ".zip" or ".docx" or ".xlsx" or ".pptx" or ".epub" or ".odt" or ".ods" or ".odp")
        {
            if (item.Size > 256 * 1024 * 1024) return "This archive exceeds the 256 MB preview limit. Use Details or Hex; the size limit affects only preview, not recovery.";
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > 50000) throw new InvalidDataException("Archive contains too many entries for a preview.");
            if (ext == ".zip") return "ARCHIVE CONTENTS\r\n\r\n" + string.Join("\r\n", zip.Entries.Take(500).Select(x => $"{Categories.Size(x.Length),10}   {x.FullName}")) + (zip.Entries.Count > 500 ? "\r\n… remaining entries omitted" : "");
            string[] targets = ext switch
            {
                ".docx" => ["word/document.xml"], ".xlsx" => ["xl/sharedStrings.xml", "xl/worksheets/sheet1.xml"],
                ".pptx" => zip.Entries.Where(x => x.FullName.StartsWith("ppt/slides/slide") && x.FullName.EndsWith(".xml")).Take(20).Select(x => x.FullName).ToArray(),
                ".epub" => zip.Entries.Where(x => x.FullName.EndsWith(".xhtml")).Take(10).Select(x => x.FullName).ToArray(),
                _ => ["content.xml"]
            };
            var result = new StringBuilder("DOCUMENT TEXT\r\nLayout, images and formatting are not reproduced. Spreadsheet numeric cells may be shared-string indexes.\r\n\r\n");
            foreach (string target in targets)
            {
                var entry = zip.GetEntry(target); if (entry is null) continue;
                if (entry.Length > 4 * 1024 * 1024) { result.AppendLine("Large document section omitted."); continue; }
                using var content = entry.Open(); using var xml = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var doc = XDocument.Load(xml);
                string text = string.Join(" ", doc.DescendantNodes().OfType<XText>().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)));
                result.AppendLine(text[..Math.Min(text.Length, TextLimit - Math.Min(TextLimit, result.Length))]).AppendLine();
                if (result.Length >= TextLimit) break;
            }
            return result.ToString();
        }
        if (!IsText(ext)) return "No built-in visual decoder for this format. Use Details or Hex to inspect it; recovery still supports its bytes when directory metadata survives.";
        var bytes = new byte[(int)Math.Min(stream.Length, TextLimit)]; stream.ReadExactly(bytes);
        Encoding encoding = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? Encoding.Unicode : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode : Encoding.UTF8;
        string decoded = encoding.GetString(bytes).TrimStart('\ufeff').Replace("\0", "·");
        return decoded + (stream.Length > TextLimit ? "\r\n\r\n… preview limited to the first 256 KB" : "");
    }
}
