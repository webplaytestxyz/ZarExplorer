# ZAR Explorer

**ZAR Explorer 0.2.0** is a Windows desktop application for browsing, searching, previewing, creating, and selectively extracting ZArchive files. It is designed to inspect large archives without needing to unpack everything first.

Built with C# and Windows Forms on .NET 10, it uses [ZArchiveSharp](https://github.com/purelogiccode/ZArchiveSharp).

## Features

### Browse and inspect
- Open `.zar` and `.wua` archives.
- Navigate through directory trees and inspect filenames, archive paths, and file sizes.
- Search archive contents recursively.
- Use wildcard patterns such as `*.exe`, `*.dll`, and `*.json`, or search with ordinary text such as `eboot`.
- Preview text files and inspect binary files in hexadecimal format. Previews are limited to the first 64 KiB.
- View archive information, including entry count, on-disk size, and total uncompressed size.

### Extract files
- Extract selected files without unpacking unrelated archive entries.
- Extract selected directories or the complete archive.
- Preserve archive folder paths or extract selected files by name.
- Configure how existing files are handled.
- Monitor progress and cancel an active operation.
- Export an archive inventory to CSV.

### Create archives
- Create a new archive from selected files or folder contents.
- Add a folder's contents at the archive root, or preserve the selected folder name as a top-level directory.
- Preserve nested directory paths.
- Monitor compression progress and cancel an active operation.
- Use parallel block compression provided by ZArchiveSharp.

Archive creation produces a new archive; it does not modify an existing archive in place. The source files are read from disk and are not intentionally modified.

## Download

Download the Windows x64 release ZIP from the project's GitHub Releases page.

Extract the ZIP to a folder and launch `ZarExplorer.exe`. The release is packaged as a self-contained application, so a separate .NET runtime installation should not be necessary.

## Usage

### Browse an archive

1. Open a `.zar` or `.wua` archive.
2. Navigate using the directory tree or enter a search term.
3. Double-click a directory to browse it, or select a file to preview it.
4. Select files or folders and choose an extraction operation.

### Search

Ordinary text searches match names and paths. Wildcards are also supported:

- `*.exe` — executable filenames
- `*.dll` — DLL filenames
- `*.json` — JSON filenames
- `sce_sys/*.json` — JSON paths matching that pattern

Searches are recursive across the archive.

### Create an archive

1. Choose **Create archive**.
2. Add individual files or select a folder.
3. Choose **Add folder contents** to put the folder's contents at the archive root, or **Add folder with root name** to retain the selected folder as the top-level directory.
4. Choose an output path and create the archive.
5. Open the resulting archive to verify its contents.

For PS5-related workflows, verify the internal layout required by the target tool before using a newly created archive. In particular, check the positions of `eboot.bin`, `sce_sys`, and any other required directories.

### Extract files

1. Select one or more files or folders.
2. Choose **Extract selected**, or use the folder/full-archive extraction options.
3. Choose a destination directory.
4. Configure whether archive paths should be preserved and how existing files should be handled.

Extracting individual files does not require unpacking unrelated archive entries.

## Build from source

Requirements:

- Windows x64
- .NET 10 SDK
- Internet access for the initial NuGet restore

Clone the repository, open a terminal in its root directory, and run:

```powershell
dotnet restore .\ZarExplorer.csproj -r win-x64

dotnet build .\ZarExplorer.csproj `
    -c Release `
    -r win-x64 `
    --no-restore

dotnet publish .\ZarExplorer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --no-restore `
    -o .\publish
```

The published application will be in `.\publish\`.

## Automated builds and releases

GitHub Actions builds the application and publishes a Windows x64 release when a version tag such as `v0.2.0` is pushed. Releases include the portable application ZIP and a SHA-256 checksum file.

## Limitations

- Archive creation and extraction have been tested with representative files and nested folders.
- Empty directories that contain no files are not guaranteed to be represented in a newly created archive.
- Preview is limited to the first 64 KiB of a file.
- Searches and inventory exports may take time on archives containing many entries.
- Drag-and-drop into the Create Archive dialog is not currently implemented.
- The application creates new archives; it does not edit an existing archive in place.
- Compatibility with every ZArchive variant and every external tool has not been established.

## License

ZAR Explorer is distributed under the MIT License. See [LICENSE](LICENSE).

Third-party dependency licensing information is available in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).