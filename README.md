# Reclaim 2.1.0 — Windows File Recovery

Extract the complete ZIP into a folder and double-click **Reclaim.exe**. Keep the Licenses folder with the application. Windows x64 is required. The .NET runtime is included. No separate .NET installation is required.

## Recover files

1. For a live drive, use **Run as administrator**. Disk images do not require elevation.
2. Choose a folder, drive, physical disk or **Disk image**.
3. Use **Quick scan** for deleted directory records and Recycle Bin files. Folder selection filters results whose original paths survive.
4. **Deep scan** also searches the selected volume's free space using file signatures. Signature results can come from other folders; their original folders and deletion status are unknown. If allocation data cannot be read, the whole volume is searched. Selecting a physical disk scans its recognized partitions. Other disks are not automatically scanned.
5. View large image thumbnails and supported text/document/archive snippets directly in the file list. Each card shows the filename, size and condition. Use categories, search and condition filters to narrow results. Some formats show a format card instead of a visual thumbnail.
6. Check the required cards and choose **Recover selected**. Save them to a different drive. **Open saved folder** opens the output. Arrow keys move between cards and Space checks the current card.
7. Double-click a card, press Enter or choose **Open file** for the full viewer, including PDF/media playback, **Details** and **Hex**.

Source storage is opened read-only. Recovery creates category folders and a JSON report in the chosen destination. Avoid further writes to the storage that lost files, and run Reclaim from another drive when possible.

## Previews and optional engine

Images, text, document text and ZIP contents can be previewed. PDF/audio/video use the Microsoft Edge WebView2 Runtime and codecs available on the computer. Install that runtime if these previews are unavailable. The app may ask for a preview cache location on another drive. Other formats can still be inspected in Details or Hex.

**Advanced · PhotoRec** requires a separately extracted PhotoRec 7.2 engine. It is not bundled. Select photorec_win.exe when prompted. This mode saves files during the scan to a separate drive and cannot restrict results to the original folder. Its external engine integration has not been verified with the actual executable.

## Limits

Recovery is not guaranteed. Overwritten bytes, SSD TRIM, encryption and lost metadata can make deleted data unrecoverable. Check previews and recovered contents. Signature reconstruction requires supported contiguous structures; fragmented signatures, NTFS compression/EFS and RAID reconstruction are not supported. Built-in signature candidates are limited to 2 GB. Phones/MTP and cloud/network folders do not expose the raw storage needed for this recovery method.

Third-party license and notice files are in **Licenses/**.

සිංහලෙන්: **Run as administrator → location තෝරන්න → Quick/Deep scan → Start scan**. Files තෝරාගෙන **Recover selected** මගින් වෙනත් drive එකකට save කරන්න. Folder එකට අදාළ results සඳහා Quick scan භාවිත කරන්න; Deep scan එකෙන් ඒ drive එකේ වෙනත් තැන්වල filesත් හමුවිය හැක.
