namespace Reclaim.App;

internal static class RecoveryGuide
{
    public const string Text = """
        RECOVER YOUR FILES

        1. Run Reclaim from another drive if possible. Avoid writing new data to the drive that lost files.
        2. Use Run as administrator for live drives. Disk images work without elevation.
        3. Choose a folder, drive, physical disk or raw disk image. You can type an original deleted folder path.
        4. Start Deep scan. Quick scan reads directory records and the Recycle Bin without signature carving.
        5. Choose a category and a format such as JPG, PNG, PDF or MP4. The format dropdown includes counts, custom extensions and No extension. Search/condition filters apply together.
        6. Select visible checks every matching recoverable file; Clear visible unchecks that view. Checked files in other formats stay selected. Recover selected saves them to another drive in category/format folders such as Images/PNG.

        PREVIEW

        Images: common Windows image formats, first frame/page, up to 32 MB and 25 million pixels.
        Text: Unicode and UTF-8 text, up to 256 KB.
        Documents: text from DOCX, XLSX, PPTX, ODT/ODS/ODP and EPUB; full Office layout is not reproduced.
        Archives: ZIP contents. Other binary formats have Details and Hex views.
        PDF / audio / video: Microsoft Edge WebView2 Runtime is required. PDF preview limit is 64 MB; media codec support varies. If the browser cache would be on the source drive, choose a preview workspace on another drive.

        RECOVERY COVERAGE

        NTFS: any extension when deleted records and data extents survive; resident, fragmented and sparse content.
        FAT12/16/32: deleted directory entries. A missing FAT chain requires a contiguous layout inference; those rows need review.
        exFAT: deleted entry sets, allocation bitmap, contiguous and surviving fragmented chains.
        ext2/ext3/ext4: free deleted inodes with surviving block pointers/extent trees. Generated names; cleared mappings and journal reconstruction are unsupported.
        MBR / GPT: all recognized partitions in raw images and exposed Windows physical disks. GPT supports 512- and 4096-byte sectors.
        Deep signatures: JPG, PNG, GIF, BMP, PDF, ZIP / Office ZIP, EPUB, 7Z, RIFF media and common ISO media containers.

        NEW RECOVERY TOOLS

        Built-in results appear during scanning. Check cards and use Recover selected while the scan continues.
        Lost partitions mode searches surviving primary NTFS/FAT/exFAT/ext volume starts without changing the partition table.
        Camera mode reconstructs independent fMP4 moof/mdat clips using compatible surviving or supplied initialization metadata. Ordinary fragmented GoPro/DJI clusters are not guessed.
        Recovery tools > Drive health reads Windows health/SMART counters; unavailable USB/provider counters stay unavailable.
        Recovery tools > Validate & repair checks PNG/ZIP CRCs or mapped readability, repairs decodable photos into new PNG copies and uses an independently supplied FFmpeg engine for video remux + decode checking.
        Chance labels describe metadata/validation evidence, not a probability or guarantee.
        Bad-area scans retry once then skip unreadable 4 KiB units. Export stays strict and fails affected files rather than silently writing zeros.
        Bootable recovery: the Bootable folder contains an independent rescue console and ISO builder. Microsoft ADK + matching WinPE add-on are required. No verified bootable ISO/USB is included. See Bootable/README.md.

        ADVANCED MODE — OPTIONAL PHOTOREC

        Download and extract PhotoRec 7.2 from https://www.cgsecurity.org/wiki/TestDisk_Download.
        Choose Advanced mode. Select photorec_win.exe when prompted, then choose another drive for the output.
        PhotoRec saves files while scanning all bytes. Results are already saved and are grouped by type. Original filenames, folders and deletion status are unknown; existing files and duplicates can appear.
        PhotoRec is an external optional engine. It is not included in this package.

        FILE CONDITIONS

        Stored in record: contents survived in NTFS metadata or the Recycle Bin.
        Clusters free: mapped clusters are free now; this does not prove the original bytes survived.
        Signature found: supported structure found; deletion status and original name/path are unknown.
        Needs review: layout was inferred, data may have been reused, or the allocation state could not be checked.
        Unavailable: required extents are missing or compressed/encrypted NTFS data is unsupported.

        PRACTICAL LIMITS

        Overwritten and SSD-TRIM-erased data cannot be guaranteed recoverable. Locked phone/MTP storage, cloud/network sources, encrypted containers, RAID reconstruction and filesystem repair are not supported. Unlock BitLocker volumes in Windows before scanning them as drive letters.
        Check recovered contents before relying on them. Reclaim opens source storage read-only and never empties the Recycle Bin.

        Recover කරන files වෙනත් drive එකකට save කරන්න. Overwrite හෝ SSD TRIM මගින් ඉවත් කළ දත්ත සම්පූර්ණයෙන් recovery කරන බව guarantee කරන්න බැහැ.
        """;
}
