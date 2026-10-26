

\# ZAR Explorer



A Windows GUI for browsing ZArchive files and selectively extracting their contents without unpacking an entire archive.



ZAR Explorer is built with C# / Windows Forms on .NET 10 and uses \[ZArchiveSharp](https://github.com/purelogiccode/ZArchiveSharp) to read ZArchive files.



\## Features



\- Open `.zar` and `.wua` archives.

\- Browse directories and inspect file names, paths, and sizes.

\- Search archive paths recursively.

\- Preview small text files and binary files in hexadecimal format.

\- Extract selected files without unpacking the complete archive.

\- Extract selected directories while preserving their folder structure.

\- Monitor extraction progress and cancel an active extraction.

\- Configure overwrite behavior for existing files.

\- Export archive inventories to CSV.

\- Read-only access to the source archive.



\## Download



Download the Windows x64 ZIP from the project's GitHub Releases page.



Extract the ZIP to a folder and launch `ZarExplorer.exe`.



The release is published as a self-contained application, so a separate .NET runtime installation should not be necessary.



\## Usage



1\. Open a `.zar` or `.wua` archive.

2\. Navigate through the directory tree, or search for a filename or path.

3\. Select one or more files or directories.

4\. Choose \*\*Extract selected\*\* and select an output directory.

5\. Enable \*\*Preserve archive folder paths\*\* to retain the selected items' internal paths.



The application reads the archive directly. Extracting individual files does not require unpacking unrelated archive entries.



\## Build from source



Requirements:



\- Windows x64

\- .NET 10 SDK

\- Internet access for the initial NuGet restore



Clone the repository and run the following commands from its root directory:



```powershell

dotnet restore .\\ZarExplorer.csproj -r win-x64



dotnet build .\\ZarExplorer.csproj `

&#x20;   -c Release `

&#x20;   -r win-x64 `

&#x20;   --no-restore



dotnet publish .\\ZarExplorer.csproj `

&#x20;   -c Release `

&#x20;   -r win-x64 `

&#x20;   --self-contained true `

&#x20;   -p:PublishSingleFile=true `

&#x20;   -p:IncludeNativeLibrariesForSelfExtract=true `

&#x20;   --no-restore `

&#x20;   -o .\\publish

```



The published application will be in `.\\publish\\`.



\## Automated builds and releases



GitHub Actions builds the project on pushes, pull requests, and manual workflow runs.



Pushing a version tag such as `v0.1.0` creates a GitHub Release containing:



\- A portable Windows x64 ZIP.

\- A SHA-256 checksum file.

\- Automatically generated release notes.



\## Limitations



\- The application is an archive browser and extractor, not an archive editor.

\- Preview is limited to the first 64 KiB of a file.

\- Searching and exporting an inventory may require time for archives containing many entries.

\- Compatibility beyond the archives tested by the maintainer has not yet been established.



\## License



ZAR Explorer is distributed under the MIT License. See \[LICENSE](LICENSE).



Third-party dependency licensing information is available in \[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).



