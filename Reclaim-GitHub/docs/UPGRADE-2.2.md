# Reclaim 2.2.0 upgrade

| Requested function | Delivered behavior | Boundary / dependency |
| --- | --- | --- |
| Results/recovery during scanning | Built-in scans publish files to the gallery in batches; selection, preview and export work before scan completion. Scan/export cancellation are tracked separately. | Source choices remain locked; scans are not saved/resumable across restarts. |
| Drive health / SMART | Read-only Windows Storage Management query: model, health/operational state, temperature, wear, power-on hours and uncorrected errors. | Administrator access may be needed. Unsupported drive/USB/provider counters show Unavailable; no raw ATA/NVMe attribute decoder is claimed. |
| Bad-sector handling | One retry, 4 KiB fallback, recorded unreadable ranges, skip known failures while scanning. | Export is strict and fails unreadable files. This is not a multi-pass imaging tool and cannot fix physical damage. |
| Lost partition search | Finds surviving primary NTFS/FAT/exFAT/ext volume structures sector-by-sector, validates geometry and scans recognized candidates. | Source partition tables are not rewritten. Missing primary structures/unsupported layouts can prevent detection. |
| Additional file systems | ext2/ext3/ext4 deleted inode recovery with surviving block pointers, indirect mappings or extent trees. | Generated names; deleted directory paths/journal replay absent. Cleared mappings cannot be invented. No APFS/HFS+/ReFS metadata parser. |
| Camera video recovery | Reconstructs individual fragmented ISO-BMFF (fMP4) `moof`/`mdat` clips using surviving or supplied compatible initialization. | Conventional GoPro/DJI arbitrary cluster fragmentation is outside this implementation. Reference must contain `mvex/trex`; ordinary MP4 reference clips are rejected clearly. Camera ownership cannot be proven by a matching track ID, so reconstructed clips require playback review. |
| Photo repair | Adds a missing JPEG end marker when applicable; decodes surviving raster data and writes/validates a new PNG. | 64 MB encoded / 25 million pixel limit. No missing-pixel generation, preservation of original metadata, or repair of entirely undecodable pictures. |
| Video repair | Copies surviving input to a separate workspace, uses FFmpeg to remux video/audio and decode-checks the result. | Supply FFmpeg separately. Missing `moov`/codec headers or lost media may prevent repair. Corrupt packets can be dropped, so a decodable output is not proof of complete original content. |
| Recovery chance | Evidence labels on cards/details; explicit selected-file checks for PNG/ZIP checksums, JPEG markers and mapped readability. | No invented success percentages. Structural checks do not establish historical contents; bounded checks say when incomplete. |
| Bootable recovery | Independent .NET Framework rescue console and WinPE ISO builder in **Bootable/**. Console scans JPEG/PNG/PDF signatures and exports to a different disk. | Windows ADK Deployment Tools + matching WinPE add-on needed. No ready-to-boot ISO/USB supplied; ISO creation and VM/hardware boot were not verified on this development host. |

## Where to find it

- **Deep / Quick scan:** live results and recovery, ext support, bad-area handling and chance labels.
- **Lost partitions · deep:** read-only lost-volume discovery followed by metadata/signature recovery.
- **Camera video · deep:** optional compatible fMP4 reference, deep scan plus camera reconstruction.
- **Recovery tools:** Drive health, Validate & repair, Bootable recovery and Guide tabs. **Create rescue ISO** runs the bundled ADK media builder after checking administrator access and dependencies; choose an output parent folder. It does not format a USB.

## Optional FFmpeg engine

Obtain a Windows build via [FFmpeg's download page](https://ffmpeg.org/download.html), verify the distributor's checksum, extract it and use **Recovery tools → Validate & repair → Choose FFmpeg**. Alternatively place its executable and required dependencies under **Reclaim/Engines/FFmpeg/**. FFmpeg is not redistributed in the Reclaim runtime package; keep the engine's license/source notices and comply with its distribution terms if you distribute it yourself.

For this local installation, an independently downloaded, checksum-verified FFmpeg engine is retained in **Reclaim-App/Engines/FFmpeg/**. The app discovers it automatically. Keep its original LICENSE and README. This local engine is outside the GitHub source and is not included in the distributable ZIP produced by the source build script.

## Source protection

Raw source handles remain read-only. Repairs create input/output copies in a new workspace; source-volume/physical-disk destination checks remain active. A JSON recovery report includes chance/validation evidence, unreadable ranges and discovered partitions. The independent rescue console writes a TSV report and rejects output on its source physical disk.

## Verification

Synthetic fixtures verify live callbacks, recoverable previews before completion, bad-sector fallback/strict export, lost NTFS/ext partitions, ext block and extent mappings, allocation reuse, fMP4 byte reconstruction, PNG/ZIP integrity and unavailable SMART counters. Windows UI tests exercise live selection and JPEG-to-PNG repair. Optional real-media tests generate clips using FFmpeg, damage an AVI index, remux it and decode-check reconstructed fMP4 fragments. These tests do not establish recovery rates on all real devices.

Verified locally on 2026-10-21:

- 37 core checks, 3 real FFmpeg/media checks and 1 actual read-only Windows health-provider check passed. The one reported drive exposed no temperature counter; it remained unavailable.
- 15 gallery workflow, 5 virtual-gallery, 9 live-UI/photo-repair, 12 DPI/alignment, 12 native-window/button and 4 WebView2 PDF/audio checks passed.
- The independent rescue console exported exact PNG/PDF candidate bytes and an actual encoded JPEG; the source images remained unchanged.
- A connected NuGet dependency audit reported no known vulnerable package for the app. Initial offline audit warnings were resolved by that connected check.
- Windows ADK/WinPE add-on was absent. Builder syntax was checked, but ISO creation and VM/hardware boot remain unverified.
