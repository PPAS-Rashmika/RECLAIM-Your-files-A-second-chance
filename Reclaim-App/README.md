# Reclaim 2.3.0 — Windows File Recovery

Extract the complete ZIP into a folder and double-click **Reclaim.exe**. Keep the Licenses folder with the application. Windows x64 is required. The .NET runtime is included. No separate .NET installation is required.

## Recover files

1. For a live drive, use **Run as administrator**. Disk images do not require elevation.
2. Choose a folder, drive, physical disk or **Disk image**.
3. Use **Quick scan** for deleted directory records and Recycle Bin files. Folder selection filters results whose original paths survive.
4. **Deep scan** also searches the selected volume's free space using file signatures. Signature results can come from other folders; their original folders and deletion status are unknown. If allocation data cannot be read, the whole volume is searched. Selecting a physical disk scans its recognized partitions. Other disks are not automatically scanned.
5. Results appear while built-in scanning continues. Choose a category, then a specific format such as **JPG, PNG, PDF or MP4** in the **All formats** dropdown. Each format shows a count. Custom extensions and **No extension** are included. Search and condition filters work together with the format filter.
6. Use **Select visible** to check all recoverable files matching the current filters, or check individual preview cards. **Clear visible** unchecks that view; selections in other formats remain checked. The Selected total includes all checked formats. Choose **Recover selected** and save to another drive. Output is organized into folders such as **Images/PNG/** and **Documents/PDF/**. Arrow keys move between cards and Space checks the current card.
7. Double-click a card, press Enter or choose **Open file** for the full viewer, including PDF/media playback, **Details** and **Hex**.
8. **Lost partitions · deep** searches surviving NTFS/FAT/exFAT/ext volume starts without changing the partition table. **Camera video · deep** reconstructs independent fMP4 clips with compatible surviving/reference initialization; conventional arbitrary camera fragmentation is not supported.

## Recovery tools

Open **Recovery tools** in the sidebar:

- **Drive health:** selected disk or All drives; health/SMART counters exposed by Windows. Run as administrator if required. Missing counters show Unavailable.
- **Validate & repair:** check PNG/ZIP integrity or surviving mapped bytes. Repair selected or saved photos into a new PNG. Video remux/repair requires a separately obtained **ffmpeg.exe**, selectable with **Choose FFmpeg**. The output is decode-checked. Missing pixels/video/codec metadata are not recreated.
- **Bootable recovery:** check requirements, then use **Create rescue ISO** and choose an output folder. The builder needs administrator access, Windows ADK Deployment Tools and its matching WinPE add-on. It creates a new media folder and never formats a USB. This release supplies a rescue console and builder, **not a verified bootable ISO/USB**. See Bootable/README.md.

ext2/ext3/ext4 deleted files are supported when their inode mappings survive; generated names replace lost filenames. APFS/HFS+/ReFS metadata recovery is not included. Scan reads retry once and skip recorded unreadable 4 KiB areas; strict recovery fails unreadable files instead of silently filling them with zeros.

Source storage is opened read-only. Recovery creates category/format folders and a JSON report in the chosen destination. Avoid further writes to the storage that lost files, and run Reclaim from another drive when possible.

## Previews and optional engine

Images, text, document text and ZIP contents can be previewed. PDF/audio/video use the Microsoft Edge WebView2 Runtime and codecs available on the computer. Install that runtime if these previews are unavailable. The app may ask for a preview cache location on another drive. Other formats can still be inspected in Details or Hex.

**Advanced · PhotoRec** requires a separately extracted PhotoRec 7.2 engine. It is not bundled. Select photorec_win.exe when prompted. This mode saves files during the scan to a separate drive and cannot restrict results to the original folder. Its external engine integration has not been verified with the actual executable.

## Limits

Recovery is not guaranteed. Overwritten bytes, SSD TRIM, encryption and lost metadata can make deleted data unrecoverable. Chance labels describe evidence, not statistical probabilities. General signatures require supported contiguous structures; fMP4 mode is a specific limited exception. NTFS compression/EFS and RAID reconstruction are unsupported. Built-in general signature candidates are limited to 2 GB. Phones/MTP and cloud/network folders do not expose the raw storage needed here. Scans cannot yet be saved/resumed after closing the app.

Third-party license and notice files are in **Licenses/**.

සිංහලෙන්: **Run as administrator → location තෝරන්න → Quick/Deep scan → Start scan**. Files තෝරාගෙන **Recover selected** මගින් වෙනත් drive එකකට save කරන්න. Folder එකට අදාළ results සඳහා Quick scan භාවිත කරන්න; Deep scan එකෙන් ඒ drive එකේ වෙනත් තැන්වල filesත් හමුවිය හැක.
