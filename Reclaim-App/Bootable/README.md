# Reclaim Rescue 2.2.0

This is an independent x64 .NET Framework console for Windows PE. The full .NET 10 desktop application is not assumed to run in WinPE.

## Build the ISO

Install Microsoft's **Windows ADK Deployment Tools** and its matching **Windows PE add-on**. Open PowerShell as administrator in this folder:

    powershell -ExecutionPolicy Bypass -File .\Build-RescueISO.ps1 -OutputDirectory D:\Reclaim-Rescue-Media

Choose a **new** output folder. The builder adds WinPE-WMI and WinPE-NetFX to Microsoft's base x64 image, includes the rescue console and creates **Reclaim-Rescue-x64.iso**. It never formats or writes a USB device. Mounts are released if servicing fails. Windows PE components retain Microsoft's license terms and are obtained from your ADK installation, not redistributed with Reclaim.

The ADK/WinPE add-on was not installed on the development computer, so ISO creation and VM/hardware boot have **not** been verified. Test the resulting ISO in a VM before using a USB-writing tool. Keep the USB separate from the source disk. USB-writing tools can erase their selected destination.

Official prerequisites: https://learn.microsoft.com/windows-hardware/get-started/adk-install and https://learn.microsoft.com/windows-hardware/manufacture/desktop/winpe-add-packages--optional-components-reference

## Use the rescue console

Boot the ISO/USB, enter the physical disk number shown by the console and an output folder on **another physical disk**. Choose `all`, file numbers separated by commas, or `q` after scanning. A report records exported/failed candidates. The source handle has read access only. Output on the source physical disk and folder junctions are blocked.

For a disk image on a running Windows computer:

    .\ReclaimRescue.exe --image D:\disk.img --out E:\Recovered --all

Or select a raw physical disk (administrator):

    .\ReclaimRescue.exe --disk \\.\PhysicalDrive1 --out E:\Recovered

Rescue scanning supports **contiguous JPEG, checksum-validated PNG and PDF ending at its first EOF**, up to 512 MB per candidate. Results may include existing/embedded files, have generated names and require review. It does not include the desktop app's NTFS/FAT/ext metadata, gallery, SMART, camera reconstruction or media repair. Failed source reads are retried once and skipped in 4 KiB units for scanning; strict export reads fail an affected file rather than silently inserting zeros. Ctrl+C stops the console. Restarting scans again.

The console was tested on synthetic disk images under Windows. A successful build is not a verified bootable ISO, and a file-structure match is not proof that original data survived.
