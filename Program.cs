using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZArchiveSharp;
using ZArchiveSharp.Zstd;
using System.IO.Enumeration;

namespace ZarExplorer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new MainForm(args.FirstOrDefault()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "ZAR Explorer startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal sealed record ArchiveItem(
    uint Node, string Name, string Path, bool IsDirectory, ulong Size);

internal sealed record FolderRef(uint Node, string Path);

internal sealed record OpStatus(string Text, int? Percent = null);

internal sealed record SearchOutcome(
    List<ArchiveItem> Items, bool Limited, long Scanned);

internal sealed record ExtractSummary(
    int Extracted, int Skipped, int Failed, List<string> Errors);

internal sealed record PackSourceFile(
    string SourcePath, string ArchivePath, ulong Size);

internal sealed record PackSummary(
    int FilesWritten, ulong InputBytes, ulong ArchiveBytes);

internal sealed class MainForm : Form
{

private static bool MatchesSearchTerm(
    string candidate,
    string query)
{
    if (query.Contains('*') || query.Contains('?'))
    {
        // Normalize Windows separators for path-pattern searches.
        string expression = query.Replace('\\', '/');
        string value = candidate.Replace('\\', '/');

        return FileSystemName.MatchesSimpleExpression(
            expression.AsSpan(),
            value.AsSpan(),
            ignoreCase: true);
    }

    // Preserve existing substring-search behavior.
    return candidate.Contains(
        query,
        StringComparison.OrdinalIgnoreCase);
}






    private const int SearchResultLimit = 100_000;
    private const int PreviewLimit = 64 * 1024;

    private ZArchiveReader? _reader;
    private string? _archivePath;
    private FolderRef _currentFolder =
        new(ZArchiveReader.RootNode, "");

    private List<ArchiveItem> _items = new();

    private bool _busy;
    private bool _sortAscending = true;
    private int _sortColumn;
    private CancellationTokenSource? _operationCts;

    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _fileMenu =
        new("&File");

    private readonly ToolStripMenuItem _overwriteMenu =
        new("Overwrite existing files")
        {
            CheckOnClick = true
        };

    private readonly ToolStripMenuItem _preservePathsMenu =
        new("Preserve archive folder paths")
        {
            CheckOnClick = true,
            Checked = true
        };

    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _openButton =
        new("Open");

    private readonly ToolStripButton _createArchiveButton =
        new("Create archive");

    private readonly ToolStripButton _upButton =
        new("Up");

    private readonly ToolStripButton _extractButton =
        new("Extract selected");

    private readonly ToolStripButton _folderButton =
        new("Extract folder");

    private readonly ToolStripButton _allButton =
        new("Extract all");

    private readonly ToolStripButton _exportButton =
        new("Export CSV");

    private readonly ToolStripButton _previewButton =
        new("Preview");

    private readonly ToolStripButton _infoButton =
        new("Archive info");

    private readonly ToolStripButton _cancelButton =
        new("Cancel");

    private readonly TextBox _searchBox = new();
    private readonly Button _searchButton = new();
    private readonly Button _clearButton = new();
    private readonly Label _pathLabel = new();

    private readonly TreeView _tree = new();
    private readonly DataGridView _grid = new();
    private readonly RichTextBox _preview = new();
    private readonly Label _previewHeader = new();

    private readonly StatusStrip _status = new();

    private readonly ToolStripStatusLabel _statusText =
        new()
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

    private readonly ToolStripStatusLabel _selectionText =
        new();

    private readonly ToolStripProgressBar _progress =
        new()
        {
            Minimum = 0,
            Maximum = 100,
            Width = 180
        };

    public MainForm(string? startupPath)
    {
        Text = "ZAR Explorer";
        Width = 1250;
        Height = 800;
        MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        BuildUi();

        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(startupPath))
            {
                await OpenArchiveAsync(startupPath);
            }
        };

        DragEnter += (_, e) =>
        {
            e.Effect =
                e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
        };

        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop)
                is string[] files && files.Length > 0)
            {
                await OpenArchiveAsync(files[0]);
            }
        };

        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                _operationCts?.Cancel();
                e.Cancel = true;

                _statusText.Text =
                    "Cancelling operation. Close the window when it finishes.";

                return;
            }

            _reader?.Dispose();
            _reader = null;
        };
    }

    private void BuildUi()
    {
        // File menu
        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Open archive...",
                null,
                async (_, _) => await OpenDialogAsync())
            {
                ShortcutKeys = Keys.Control | Keys.O
            });

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Create archive...",
                null,
                async (_, _) => await CreateArchiveAsync())
            {
                ShortcutKeys = Keys.Control | Keys.Shift | Keys.N
            });

        _fileMenu.DropDownItems.Add(
            new ToolStripSeparator());

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Close archive",
                null,
                (_, _) => CloseArchive()));

        _fileMenu.DropDownItems.Add(
            new ToolStripSeparator());

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Extract selected...",
                null,
                async (_, _) => await ExtractSelectedAsync()));

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Extract current folder...",
                null,
                async (_, _) => await ExtractCurrentFolderAsync()));

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Extract entire archive...",
                null,
                async (_, _) => await ExtractAllAsync()));

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Export file inventory to CSV...",
                null,
                async (_, _) => await ExportInventoryAsync()));

        _fileMenu.DropDownItems.Add(
            new ToolStripSeparator());

        _fileMenu.DropDownItems.Add(_overwriteMenu);
        _fileMenu.DropDownItems.Add(_preservePathsMenu);

        _fileMenu.DropDownItems.Add(
            new ToolStripSeparator());

        _fileMenu.DropDownItems.Add(
            new ToolStripMenuItem(
                "Exit",
                null,
                (_, _) => Close()));

        var help = new ToolStripMenuItem("&Help");

        help.DropDownItems.Add(
            new ToolStripMenuItem(
                "About ZAR Explorer",
                null,
                (_, _) =>
                {
                    MessageBox.Show(
                        this,
                        "ZAR Explorer v0.2.0\n\n" +
                        "ZArchive browser, selective extractor, and archive creator.\n" +
                        "Powered by ZArchiveSharp.",
                        "About",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }));

        _menu.Items.Add(_fileMenu);
        _menu.Items.Add(help);
        _menu.Dock = DockStyle.Fill;

        // Toolbar actions
        _openButton.Click +=
            async (_, _) => await OpenDialogAsync();

        _createArchiveButton.Click +=
            async (_, _) => await CreateArchiveAsync();

        _upButton.Click +=
            (_, _) => GoUp();

        _extractButton.Click +=
            async (_, _) => await ExtractSelectedAsync();

        _folderButton.Click +=
            async (_, _) => await ExtractCurrentFolderAsync();

        _allButton.Click +=
            async (_, _) => await ExtractAllAsync();

        _exportButton.Click +=
            async (_, _) => await ExportInventoryAsync();

        _previewButton.Click +=
            (_, _) => PreviewSelected();

        _infoButton.Click +=
            (_, _) => ShowArchiveInfo();

        _cancelButton.Click +=
            (_, _) => _operationCts?.Cancel();

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _openButton,
            _createArchiveButton,
            new ToolStripSeparator(),
            _upButton,
            new ToolStripSeparator(),
            _extractButton,
            _folderButton,
            _allButton,
            new ToolStripSeparator(),
            _exportButton,
            _previewButton,
            _infoButton,
            new ToolStripSeparator(),
            _cancelButton
        });

        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Dock = DockStyle.Fill;

        // Search and location panel
        var searchPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(5, 4, 5, 2)
        };

        _pathLabel.Text = "No archive open";
        _pathLabel.AutoSize = true;
        _pathLabel.MaximumSize = new Size(400, 30);
        _pathLabel.Margin = new Padding(4, 5, 12, 0);

        _searchBox.Width = 260;
        _searchBox.PlaceholderText =
            "Search names or paths...";
        _searchBox.Margin = new Padding(4, 0, 3, 0);

        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchAsync();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _searchBox.Clear();

                if (_reader != null)
                {
                    LoadFolder(_currentFolder);
                }
            }
        };

        _searchButton.Text = "Search all";
        _searchButton.AutoSize = true;
        _searchButton.Click +=
            async (_, _) => await SearchAsync();

        _clearButton.Text = "Clear";
        _clearButton.AutoSize = true;

        _clearButton.Click += (_, _) =>
        {
            _searchBox.Clear();

            if (_reader != null)
            {
                LoadFolder(_currentFolder);
            }
        };

        searchPanel.Controls.Add(new Label
        {
            Text = "Location:",
            AutoSize = true,
            Margin = new Padding(4, 5, 2, 0)
        });

        searchPanel.Controls.Add(_pathLabel);

        searchPanel.Controls.Add(new Label
        {
            Text = "Find:",
            AutoSize = true,
            Margin = new Padding(5, 5, 2, 0)
        });

        searchPanel.Controls.Add(_searchBox);
        searchPanel.Controls.Add(_searchButton);
        searchPanel.Controls.Add(_clearButton);

        // Directory tree
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;

        _tree.BeforeExpand += TreeBeforeExpand;

        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node.Tag is FolderRef folder &&
                _reader != null)
            {
                LoadFolder(folder);
            }
        };

        // Archive file grid
        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToOrderColumns = true;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode =
            DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeRowsMode =
            DataGridViewAutoSizeRowsMode.None;

        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Name",
                HeaderText = "Name",
                Width = 280,
                SortMode =
                    DataGridViewColumnSortMode.Programmatic
            });

        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Type",
                HeaderText = "Type",
                Width = 85,
                SortMode =
                    DataGridViewColumnSortMode.Programmatic
            });

        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Size",
                HeaderText = "Size",
                Width = 110,
                SortMode =
                    DataGridViewColumnSortMode.Programmatic
            });

        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Path",
                HeaderText = "Archive path",
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 180,
                SortMode =
                    DataGridViewColumnSortMode.Programmatic
            });

        _grid.CellValueNeeded += GridCellValueNeeded;
        _grid.CellDoubleClick += GridCellDoubleClick;

        _grid.ColumnHeaderMouseClick +=
            GridColumnHeaderMouseClick;

        _grid.SelectionChanged += (_, _) =>
        {
            _selectionText.Text =
                $"{_grid.SelectedRows.Count:N0} row(s) selected";
        };

        _grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var hit = _grid.HitTest(e.X, e.Y);

            if (hit.RowIndex >= 0 &&
                !_grid.Rows[hit.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[hit.RowIndex].Selected = true;
            }
        };

        var context = new ContextMenuStrip();

        context.Items.Add(
            new ToolStripMenuItem(
                "Preview selected file",
                null,
                (_, _) => PreviewSelected()));

        context.Items.Add(
            new ToolStripMenuItem(
                "Extract selected...",
                null,
                async (_, _) => await ExtractSelectedAsync()));

        context.Items.Add(
            new ToolStripMenuItem(
                "Copy archive path",
                null,
                (_, _) => CopySelectedPath()));

        _grid.ContextMenuStrip = context;

        // Preview panel
        _previewHeader.Text = "File preview";
        _previewHeader.Dock = DockStyle.Top;
        _previewHeader.Height = 25;
        _previewHeader.Padding = new Padding(5, 5, 0, 0);

        _preview.Dock = DockStyle.Fill;
        _preview.ReadOnly = true;
        _preview.WordWrap = false;
        _preview.Font = new Font("Consolas", 9.5f);
        _preview.BackColor = SystemColors.Window;

        var previewPanel = new Panel
        {
            Dock = DockStyle.Fill
        };

        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(_previewHeader);

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };

        rightSplit.Panel1.Controls.Add(_grid);
        rightSplit.Panel2.Controls.Add(previewPanel);

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };

        mainSplit.Panel1.Controls.Add(_tree);
        mainSplit.Panel2.Controls.Add(rightSplit);

        // Status bar
        _statusText.Text = "Ready";

        _status.Items.Add(_statusText);
        _status.Items.Add(_selectionText);
        _status.Items.Add(_progress);
        _status.Dock = DockStyle.Fill;

        // Main layout
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        layout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 38));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_menu, 0, 0);
        layout.Controls.Add(_toolbar, 0, 1);
        layout.Controls.Add(searchPanel, 0, 2);
        layout.Controls.Add(mainSplit, 0, 3);
        layout.Controls.Add(_status, 0, 4);

        Controls.Add(layout);

        UpdateBusyUi();
    }

    private void UpdateBusyUi()
    {
        _fileMenu.Enabled = !_busy;

        _openButton.Enabled = !_busy;
        _createArchiveButton.Enabled = !_busy;
        _upButton.Enabled = !_busy;
        _extractButton.Enabled = !_busy;
        _folderButton.Enabled = !_busy;
        _allButton.Enabled = !_busy;
        _exportButton.Enabled = !_busy;
        _previewButton.Enabled = !_busy;
        _infoButton.Enabled = !_busy;
        _cancelButton.Enabled = _busy;

        _searchBox.Enabled = !_busy;
        _searchButton.Enabled = !_busy;
        _clearButton.Enabled = !_busy;

        _tree.Enabled = !_busy;
        _grid.Enabled = !_busy;
    }

    private async Task RunOperationAsync(
        string initialStatus,
        Func<CancellationToken, IProgress<OpStatus>, Task> action)
    {
        if (_busy)
            return;

        using var cts = new CancellationTokenSource();

        _operationCts = cts;
        _busy = true;
        _progress.Value = 0;
        _statusText.Text = initialStatus;

        UpdateBusyUi();

        var progress = new Progress<OpStatus>(s =>
        {
            _statusText.Text = s.Text;

            if (s.Percent.HasValue)
            {
                _progress.Value =
                    Math.Clamp(s.Percent.Value, 0, 100);
            }
        });

        try
        {
            await action(cts.Token, progress);
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            _statusText.Text = "Operation failed.";

            MessageBox.Show(
                this,
                ex.ToString(),
                "ZAR Explorer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _operationCts = null;
            _busy = false;
            UpdateBusyUi();
        }
    }

    private async Task OpenDialogAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open ZArchive",
            Filter =
                "ZArchive (*.zar;*.wua)|*.zar;*.wua|" +
                "All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await OpenArchiveAsync(dialog.FileName);
        }
    }

    private async Task OpenArchiveAsync(string path)
    {
        if (_busy || !File.Exists(path))
            return;

        await RunOperationAsync(
            "Opening archive...",
            async (ct, p) =>
            {
                var result = await Task.Run(() =>
                {
                    var reader = ZArchiveReader.TryOpen(
                        path, out var failure);

                    return (
                        Reader: reader,
                        Failure: failure
                    );
                }, ct);

                if (ct.IsCancellationRequested)
                {
                    result.Reader?.Dispose();
                    ct.ThrowIfCancellationRequested();
                }

                if (result.Reader == null)
                {
                    throw new InvalidDataException(
                        "The archive could not be opened.\n" +
                        $"Reason: {result.Failure}");
                }

                var oldReader = _reader;

                _reader = result.Reader;
                _archivePath = Path.GetFullPath(path);

                var root = new TreeNode(
                    Path.GetFileName(path))
                {
                    Tag = new FolderRef(
                        ZArchiveReader.RootNode, "")
                };

                if (_reader.GetDirEntryCount(
                    ZArchiveReader.RootNode) > 0)
                {
                    root.Nodes.Add(new TreeNode("..."));
                }

                _tree.Nodes.Clear();
                _tree.Nodes.Add(root);
                _tree.SelectedNode = root;
                root.Expand();

                LoadFolder(
                    new FolderRef(ZArchiveReader.RootNode, ""));

                oldReader?.Dispose();

                Text =
                    $"ZAR Explorer — {Path.GetFileName(path)}";

                p.Report(new OpStatus(
                    $"Opened {Path.GetFileName(path)} — " +
                    $"{_reader.EntryCount:N0} entries, " +
                    $"{FormatSize(_reader.TotalUncompressedSize)} uncompressed"));
            });
    }

    // ------------------------------------------------------------
    // Create archive
    // ------------------------------------------------------------

    private async Task CreateArchiveAsync()
    {
        if (_busy)
            return;

        using var dialog = new CreateArchiveDialog();

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        List<PackSourceFile> files = dialog.Files.ToList();
        string outputPath = dialog.OutputPath;

        if (files.Count == 0)
        {
            MessageBox.Show(
                this,
                "Add at least one file or folder before creating the archive.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        if (_archivePath != null &&
            string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(_archivePath),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "The destination is the archive currently open in ZAR Explorer.\n\n" +
                "Choose a different output path.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        string normalizedOutput = Path.GetFullPath(outputPath);

        if (files.Any(f =>
            string.Equals(
                Path.GetFullPath(f.SourcePath),
                normalizedOutput,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                "The output archive cannot overwrite one of its own source files.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        var duplicatePaths = files
            .GroupBy(
                f => NormalizeArchiveEntryPath(f.ArchivePath),
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(10)
            .ToList();

        if (duplicatePaths.Count > 0)
        {
            MessageBox.Show(
                this,
                "Two or more source files use the same archive path.\n\n" +
                string.Join("\n", duplicatePaths) +
                "\n\nRemove or rename the conflicting entries before continuing.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        if (File.Exists(normalizedOutput))
        {
            var answer = MessageBox.Show(
                this,
                $"The destination file already exists:\n\n{normalizedOutput}\n\n" +
                "Replace it after the new archive has been created successfully?",
                "Confirm overwrite",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return;
        }

        await RunOperationAsync(
            "Creating archive...",
            async (ct, progress) =>
            {
                var summary = await Task.Run(
                    () => PackArchive(
                        files,
                        normalizedOutput,
                        ct,
                        progress),
                    ct);

                progress.Report(new OpStatus(
                    $"Archive created: {summary.FilesWritten:N0} files, " +
                    $"{FormatSize(summary.ArchiveBytes)}",
                    100));

                MessageBox.Show(
                    this,
                    "Archive creation completed.\n\n" +
                    $"Files written: {summary.FilesWritten:N0}\n" +
                    $"Uncompressed input size: {FormatSize(summary.InputBytes)}\n" +
                    $"Archive size: {FormatSize(summary.ArchiveBytes)}\n\n" +
                    $"Output:\n{normalizedOutput}\n\n" +
                    "The archive uses the default ZArchiveSharp compression settings.",
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            });
    }

    private static PackSummary PackArchive(
        IReadOnlyList<PackSourceFile> files,
        string outputPath,
        CancellationToken token,
        IProgress<OpStatus> progress)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "There are no files to add to the archive.");
        }

        string fullOutput = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutput);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new IOException(
                "Could not determine the output directory.");
        }

        Directory.CreateDirectory(outputDirectory);

        ulong totalBytes = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(file.SourcePath))
            {
                throw new FileNotFoundException(
                    "A source file no longer exists.",
                    file.SourcePath);
            }

            ValidateArchiveEntryPath(file.ArchivePath);

            totalBytes = SaturatingAdd(totalBytes, file.Size);
        }

        string tempPath = fullOutput + ".zartmp-" +
            Guid.NewGuid().ToString("N");

        ulong completedBytes = 0;
        int writtenFiles = 0;
        byte[] buffer = new byte[1024 * 1024];

        try
        {
            using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan))
            {
                using (var writer = new ZArchiveWriter(
                    output,
                    maxDegreeOfParallelism: Math.Max(2, Environment.ProcessorCount),
                    compressorFactory: () => new ZstdCompressor()))
                {
                    for (int index = 0; index < files.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();

                        var file = files[index];
                        string archiveEntryPath =
                            NormalizeArchiveEntryPath(file.ArchivePath);

                        ulong copied = 0;

                        progress.Report(new OpStatus(
                            $"Adding {archiveEntryPath}",
                            GetPercent(
                                completedBytes,
                                totalBytes,
                                index,
                                files.Count)));

                        using (var input = new FileStream(
                            file.SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 1024 * 1024,
                            FileOptions.SequentialScan))
                        {
                            if ((ulong)input.Length != file.Size)
                            {
                                throw new IOException(
                                    $"The source file changed after it was queued:\n" +
                                    $"{file.SourcePath}");
                            }

                            // Create the complete parent directory tree before adding the file.
int lastSlash = archiveEntryPath.LastIndexOf('/');

if (lastSlash > 0)
{
    string parentDirectory = archiveEntryPath[..lastSlash];

    if (!writer.MakeDir(parentDirectory, recursive: true))
    {
        throw new IOException(
            $"Could not create archive directory: {parentDirectory}");
    }
}

// Never continue writing if the file entry could not be created.
if (!writer.StartNewFile(archiveEntryPath))
{
    throw new IOException(
        $"Could not create archive file entry: {archiveEntryPath}");
}

                            int read;

                            while ((read = input.Read(
                                buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();

                                writer.AppendData(
                                    buffer.AsSpan(0, read));

                                copied = SaturatingAdd(
                                    copied, (ulong)read);

                                if (copied % (16UL * 1024 * 1024) <
                                    (ulong)read)
                                {
                                    progress.Report(new OpStatus(
                                        $"Writing {archiveEntryPath}",
                                        GetPercent(
                                            SaturatingAdd(
                                                completedBytes, copied),
                                            totalBytes,
                                            index,
                                            files.Count)));
                                }
                            }
                        }

                        if (copied != file.Size)
                        {
                            throw new IOException(
                                $"The source file size changed while reading:\n" +
                                $"{file.SourcePath}\n\n" +
                                $"Expected {file.Size:N0} bytes; read {copied:N0} bytes.");
                        }

                        completedBytes = SaturatingAdd(
                            completedBytes, copied);

                        writtenFiles++;

                        progress.Report(new OpStatus(
                            $"Added {writtenFiles:N0}/{files.Count:N0}: " +
                            archiveEntryPath,
                            GetPercent(
                                completedBytes,
                                totalBytes,
                                writtenFiles,
                                files.Count)));
                    }

                    token.ThrowIfCancellationRequested();

                    writer.Finalize();

                    // Persist the finished archive before moving it to
                    // the destination path.
                    output.Flush(true);
                }
            }

            token.ThrowIfCancellationRequested();

            // Same-directory temporary output keeps final replacement
            // separate from archive construction. A failed or cancelled
            // pack will not replace an existing destination.
            File.Move(tempPath, fullOutput, overwrite: true);
            tempPath = "";

            ulong archiveBytes = (ulong)new FileInfo(fullOutput).Length;

            progress.Report(new OpStatus(
                $"Archive complete: {fullOutput}",
                100));

            return new PackSummary(
                writtenFiles,
                totalBytes,
                archiveBytes);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static ulong SaturatingAdd(ulong left, ulong right)
    {
        return left > ulong.MaxValue - right
            ? ulong.MaxValue
            : left + right;
    }

    private static string NormalizeArchiveEntryPath(string path)
    {
        return path.Replace('\\', '/');
    }

    internal static void ValidateArchiveEntryPath(string archivePath)
    {
        string normalized = NormalizeArchiveEntryPath(archivePath);

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized))
        {
            throw new IOException(
                $"Unsafe or empty archive path: {archivePath}");
        }

        string[] parts = normalized.Split('/');

        if (parts.Length == 0 ||
            parts.Any(part =>
                string.IsNullOrEmpty(part) ||
                part == "." ||
                part == ".." ||
                part.Contains(':') ||
                part.Any(char.IsControl) ||
                part.IndexOfAny(
                    Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new IOException(
                $"Archive path contains unsafe components: {archivePath}");
        }
    }

    private void CloseArchive()
    {
        if (_busy)
            return;

        _reader?.Dispose();
        _reader = null;
        _archivePath = null;
        _items.Clear();

        _tree.Nodes.Clear();
        _grid.RowCount = 0;

        _preview.Clear();
        _previewHeader.Text = "File preview";
        _pathLabel.Text = "No archive open";

        Text = "ZAR Explorer";
        _statusText.Text = "Archive closed";
    }

    private void TreeBeforeExpand(
        object? sender,
        TreeViewCancelEventArgs e)
    {
        if (_reader == null ||
            e.Node.Tag is not FolderRef folder)
        {
            return;
        }

        if (e.Node.Nodes.Count == 1 &&
            e.Node.Nodes[0].Tag == null)
        {
            PopulateTreeNode(e.Node, folder);
        }
    }

    private void PopulateTreeNode(
        TreeNode parent,
        FolderRef folder)
    {
        if (_reader == null)
            return;

        parent.Nodes.Clear();

        uint count = _reader.GetDirEntryCount(folder.Node);

        for (uint i = 0; i < count; i++)
        {
            if (!_reader.TryGetDirEntry(
                folder.Node,
                i,
                out uint child,
                out var entry))
            {
                continue;
            }

            if (!entry.IsDirectory)
                continue;

            string path =
                JoinArchivePath(folder.Path, entry.Name);

            var childFolder = new FolderRef(child, path);

            var node = new TreeNode(entry.Name)
            {
                Tag = childFolder
            };

            if (_reader.GetDirEntryCount(child) > 0)
            {
                node.Nodes.Add(new TreeNode("..."));
            }

            parent.Nodes.Add(node);
        }
    }

    private void LoadFolder(FolderRef folder)
    {
        if (_reader == null)
            return;

        _currentFolder = folder;
        _searchBox.Text = "";

        var entries = new List<ArchiveItem>();

        uint count =
            _reader.GetDirEntryCount(folder.Node);

        for (uint i = 0; i < count; i++)
        {
            if (!_reader.TryGetDirEntry(
                folder.Node,
                i,
                out uint child,
                out var entry))
            {
                continue;
            }

            entries.Add(new ArchiveItem(
                child,
                entry.Name,
                JoinArchivePath(folder.Path, entry.Name),
                entry.IsDirectory,
                entry.Size));
        }

        _pathLabel.Text =
            string.IsNullOrEmpty(folder.Path)
                ? "(archive root)"
                : "/" + folder.Path;

        ShowItems(entries);

        _statusText.Text = $"{entries.Count:N0} entries";
        _preview.Clear();
        _previewHeader.Text = "File preview";
    }

    private static string JoinArchivePath(
        string parent,
        string name)
    {
        return string.IsNullOrEmpty(parent)
            ? name
            : parent + "/" + name;
    }

    private void ShowItems(List<ArchiveItem> items)
    {
        _items = items;

        _sortColumn = 0;
        _sortAscending = true;

        SortItems();

        _grid.RowCount = 0;
        _grid.RowCount = _items.Count;

        _grid.ClearSelection();
        _grid.Invalidate();
    }

    private void SortItems()
    {
        IEnumerable<ArchiveItem> SortGroup(
            IEnumerable<ArchiveItem> source)
        {
            return _sortColumn switch
            {
                1 => _sortAscending
                    ? source.OrderBy(
                        x => x.IsDirectory ? "Folder" : "File")
                    : source.OrderByDescending(
                        x => x.IsDirectory ? "Folder" : "File"),

                2 => _sortAscending
                    ? source.OrderBy(x => x.Size)
                    : source.OrderByDescending(x => x.Size),

                3 => _sortAscending
                    ? source.OrderBy(
                        x => x.Path,
                        StringComparer.OrdinalIgnoreCase)
                    : source.OrderByDescending(
                        x => x.Path,
                        StringComparer.OrdinalIgnoreCase),

                _ => _sortAscending
                    ? source.OrderBy(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase)
                    : source.OrderByDescending(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase)
            };
        }

        _items = SortGroup(
                _items.Where(x => x.IsDirectory))
            .Concat(
                SortGroup(_items.Where(x => !x.IsDirectory)))
            .ToList();

        for (int i = 0; i < _grid.Columns.Count; i++)
        {
            _grid.Columns[i].HeaderCell.SortGlyphDirection =
                i != _sortColumn
                    ? SortOrder.None
                    : _sortAscending
                        ? SortOrder.Ascending
                        : SortOrder.Descending;
        }
    }

    private void GridCellValueNeeded(
        object? sender,
        DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.RowIndex >= _items.Count)
        {
            return;
        }

        var item = _items[e.RowIndex];

        e.Value = e.ColumnIndex switch
        {
            0 => item.Name,
            1 => item.IsDirectory ? "Folder" : "File",
            2 => item.IsDirectory
                ? ""
                : FormatSize(item.Size),
            3 => item.Path,
            _ => ""
        };
    }

    private void GridColumnHeaderMouseClick(
        object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0)
            return;

        if (_sortColumn == e.ColumnIndex)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = e.ColumnIndex;
            _sortAscending = true;
        }

        SortItems();
        _grid.Invalidate();
    }

    private void GridCellDoubleClick(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.RowIndex >= _items.Count)
        {
            return;
        }

        var item = _items[e.RowIndex];

        if (item.IsDirectory)
        {
            LoadFolder(
                new FolderRef(item.Node, item.Path));
        }
        else
        {
            ShowPreview(item);
        }
    }

    private async Task SearchAsync()
    {
        if (_reader == null)
            return;

        string query = _searchBox.Text.Trim();

        if (query.Length == 0)
        {
            LoadFolder(_currentFolder);
            return;
        }

        var reader = _reader;

        await RunOperationAsync(
            "Searching archive...",
            async (ct, p) =>
            {
                var result = await Task.Run(
                    () => SearchArchive(
                        reader, query, ct, p),
                    ct);

                ct.ThrowIfCancellationRequested();

                ShowItems(result.Items);

                _pathLabel.Text = $"Search: {query}";

                _statusText.Text =
                    $"{result.Items.Count:N0} matches from " +
                    $"{result.Scanned:N0} entries searched" +
                    (result.Limited
                        ? " — result limit reached"
                        : "");
            });
    }

    private static SearchOutcome SearchArchive(
        ZArchiveReader reader,
        string query,
        CancellationToken token,
        IProgress<OpStatus> progress)
    {
        var matches = new List<ArchiveItem>();
        long scanned = 0;

        bool limited = SearchDirectory(
            reader,
            ZArchiveReader.RootNode,
            "",
            query,
            matches,
            token,
            progress,
            ref scanned);

        return new SearchOutcome(
            matches, limited, scanned);
    }

    private static bool SearchDirectory(
        ZArchiveReader reader,
        uint directoryNode,
        string directoryPath,
        string query,
        List<ArchiveItem> matches,
        CancellationToken token,
        IProgress<OpStatus> progress,
        ref long scanned)
    {
        uint count =
            reader.GetDirEntryCount(directoryNode);

        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();

            if (!reader.TryGetDirEntry(
                directoryNode,
                i,
                out uint child,
                out var entry))
            {
                continue;
            }

            string path =
                JoinArchivePath(directoryPath, entry.Name);

            scanned++;

            if (MatchesSearchTerm(entry.Name, query) ||
                MatchesSearchTerm(path, query))
            {
                matches.Add(new ArchiveItem(
                    child,
                    entry.Name,
                    path,
                    entry.IsDirectory,
                    entry.Size));

                if (matches.Count >= SearchResultLimit)
                    return true;
            }


            if (scanned % 5000 == 0)
            {
                progress.Report(new OpStatus(
                    $"Searching... {scanned:N0} entries inspected"));
            }

            if (entry.IsDirectory &&
                SearchDirectory(
                    reader,
                    child,
                    path,
                    query,
                    matches,
                    token,
                    progress,
                    ref scanned))
            {
                return true;
            }
        }

        return false;
    }

    private List<ArchiveItem> GetSelectedItems()
    {
        return _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Index)
            .Where(i => i >= 0 && i < _items.Count)
            .Distinct()
            .Select(i => _items[i])
            .ToList();
    }

    private async Task ExtractSelectedAsync()
    {
        var selected = GetSelectedItems();

        if (_reader == null || selected.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select one or more files or folders first.",
                "Extract selected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        await ExtractItemsAsync(
            selected,
            "Extract selected items");
    }

    private async Task ExtractCurrentFolderAsync()
    {
        if (_reader == null)
            return;

        var folder = new ArchiveItem(
            _currentFolder.Node,
            string.IsNullOrEmpty(_currentFolder.Path)
                ? "(root)"
                : Path.GetFileName(_currentFolder.Path),
            _currentFolder.Path,
            true,
            0);

        await ExtractItemsAsync(
            new List<ArchiveItem> { folder },
            "Extract current folder");
    }

    private async Task ExtractAllAsync()
    {
        if (_reader == null)
            return;

        var reader = _reader;

        var answer = MessageBox.Show(
            this,
            $"Extract the entire archive?\n\n" +
            $"Entries: {reader.EntryCount:N0}\n" +
            $"Uncompressed size: " +
            $"{FormatSize(reader.TotalUncompressedSize)}\n\n" +
            "Extraction may require substantial disk space.",
            "Confirm full extraction",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
            return;

        var root = new ArchiveItem(
            ZArchiveReader.RootNode,
            "",
            "",
            true,
            0);

        await ExtractItemsAsync(
            new List<ArchiveItem> { root },
            "Extract entire archive");
    }

    private async Task ExtractItemsAsync(
        List<ArchiveItem> roots,
        string operationName)
    {
        if (_reader == null)
            return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the destination directory",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var reader = _reader;
        string target = dialog.SelectedPath;

        bool overwrite = _overwriteMenu.Checked;
        bool preservePaths = _preservePathsMenu.Checked;

        await RunOperationAsync(
            "Preparing extraction...",
            async (ct, p) =>
            {
                var files = await Task.Run(
                    () => BuildSelectedFiles(
                        reader, roots, ct, p),
                    ct);

                if (files.Count == 0)
                {
                    p.Report(new OpStatus(
                        "No files found to extract.", 0));
                    return;
                }

                var summary = await Task.Run(
                    () => ExtractFiles(
                        reader,
                        files,
                        target,
                        preservePaths,
                        overwrite,
                        ct,
                        p),
                    ct);

                p.Report(new OpStatus(
                    $"Finished: {summary.Extracted:N0} extracted, " +
                    $"{summary.Skipped:N0} skipped, " +
                    $"{summary.Failed:N0} failed.",
                    100));

                var message =
                    $"Extraction results\n\n" +
                    $"Extracted: {summary.Extracted:N0}\n" +
                    $"Skipped: {summary.Skipped:N0}\n" +
                    $"Failed: {summary.Failed:N0}\n\n" +
                    $"Destination: {target}";

                if (summary.Errors.Count > 0)
                {
                    message += "\n\nFirst errors:\n" +
                        string.Join(
                            "\n",
                            summary.Errors.Take(15));

                    if (summary.Failed > summary.Errors.Count)
                    {
                        message +=
                            "\n... additional errors omitted.";
                    }
                }

                MessageBox.Show(
                    this,
                    message,
                    operationName,
                    MessageBoxButtons.OK,
                    summary.Failed > 0
                        ? MessageBoxIcon.Warning
                        : MessageBoxIcon.Information);
            });
    }

    private static List<ArchiveItem> BuildSelectedFiles(
        ZArchiveReader reader,
        List<ArchiveItem> roots,
        CancellationToken token,
        IProgress<OpStatus> progress)
    {
        var files = new List<ArchiveItem>();

        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        long visited = 0;

        foreach (var item in roots)
        {
            token.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                CollectFiles(
                    reader,
                    item.Node,
                    item.Path,
                    files,
                    seen,
                    token,
                    progress,
                    ref visited);
            }
            else if (seen.Add(item.Path))
            {
                files.Add(item);
            }
        }

        return files;
    }

    private static void CollectFiles(
        ZArchiveReader reader,
        uint directoryNode,
        string directoryPath,
        List<ArchiveItem> files,
        HashSet<string> seen,
        CancellationToken token,
        IProgress<OpStatus> progress,
        ref long visited)
    {
        uint count =
            reader.GetDirEntryCount(directoryNode);

        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();

            if (!reader.TryGetDirEntry(
                directoryNode,
                i,
                out uint child,
                out var entry))
            {
                continue;
            }

            string path =
                JoinArchivePath(directoryPath, entry.Name);

            visited++;

            if (entry.IsDirectory)
            {
                CollectFiles(
                    reader,
                    child,
                    path,
                    files,
                    seen,
                    token,
                    progress,
                    ref visited);
            }
            else if (seen.Add(path))
            {
                files.Add(new ArchiveItem(
                    child,
                    entry.Name,
                    path,
                    false,
                    entry.Size));
            }

            if (visited % 5000 == 0)
            {
                progress.Report(new OpStatus(
                    $"Preparing file list: {visited:N0} entries scanned"));
            }
        }
    }

    private static ExtractSummary ExtractFiles(
        ZArchiveReader reader,
        List<ArchiveItem> files,
        string destinationRoot,
        bool preservePaths,
        bool overwrite,
        CancellationToken token,
        IProgress<OpStatus> progress)
    {
        int extracted = 0;
        int skipped = 0;
        int failed = 0;

        var errors = new List<string>();

        ulong totalBytes = 0;

        foreach (var file in files)
        {
            totalBytes =
                totalBytes > ulong.MaxValue - file.Size
                    ? ulong.MaxValue
                    : totalBytes + file.Size;
        }

        ulong completedBytes = 0;

        byte[] buffer = new byte[1024 * 1024];

        for (int index = 0; index < files.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            var file = files[index];

            string? tempPath = null;
            ulong copied = 0;

            try
            {
                string relative = preservePaths
                    ? file.Path
                    : file.Name;

                string fullPath = SafeDestinationPath(
                    destinationRoot,
                    relative);

                if (File.Exists(fullPath))
                {
                    if (!overwrite)
                    {
                        skipped++;
                        completedBytes += file.Size;

                        progress.Report(new OpStatus(
                            $"Skipped existing file: {file.Path}",
                            GetPercent(
                                completedBytes,
                                totalBytes,
                                index + 1,
                                files.Count)));

                        continue;
                    }

                    if ((File.GetAttributes(fullPath) &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException(
                            "Refusing to overwrite a reparse-point file.");
                    }
                }
                else if (Directory.Exists(fullPath))
                {
                    throw new IOException(
                        "A directory already exists at the target file path.");
                }

                tempPath = fullPath + ".zartmp-" +
                    Guid.NewGuid().ToString("N");

                using (var source = reader.OpenRead(file.Node))
                using (var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan))
                {
                    int read;

                    while ((read = source.Read(
                        buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();

                        output.Write(buffer, 0, read);
                        copied += (uint)read;

                        if (totalBytes > 0 &&
                            copied % (16UL * 1024 * 1024) <
                                (ulong)read)
                        {
                            ulong now = completedBytes + copied;

                            progress.Report(new OpStatus(
                                $"Extracting {file.Path}",
                                GetPercent(
                                    now,
                                    totalBytes,
                                    index,
                                    files.Count)));
                        }
                    }
                }

                if (copied != file.Size)
                {
                    throw new IOException(
                        $"Short read: expected {file.Size:N0} bytes, " +
                        $"received {copied:N0}.");
                }

                File.Move(tempPath, fullPath, overwrite);
                tempPath = null;

                extracted++;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                failed++;

                if (errors.Count < 20)
                {
                    errors.Add($"{file.Path}: {ex.Message}");
                }
            }
            finally
            {
                TryDelete(tempPath);
            }

            completedBytes =
                completedBytes > ulong.MaxValue - file.Size
                    ? ulong.MaxValue
                    : completedBytes + file.Size;

            progress.Report(new OpStatus(
                $"Processed {index + 1:N0}/{files.Count:N0}: {file.Path}",
                GetPercent(
                    completedBytes,
                    totalBytes,
                    index + 1,
                    files.Count)));
        }

        return new ExtractSummary(
            extracted, skipped, failed, errors);
    }

    private static int GetPercent(
        ulong completed,
        ulong total,
        int completedFiles,
        int totalFiles)
    {
        if (total > 0)
        {
            return (int)Math.Clamp(
                (double)completed / total * 100.0,
                0.0,
                100.0);
        }

        if (totalFiles <= 0)
            return 100;

        return (int)Math.Clamp(
            (double)completedFiles / totalFiles * 100.0,
            0.0,
            100.0);
    }

    private static string SafeDestinationPath(
        string root,
        string archivePath)
    {
        string normalized =
            archivePath.Replace('\\', '/');

        if (string.IsNullOrEmpty(normalized) ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized))
        {
            throw new IOException(
                "Unsafe or empty archive path.");
        }

        string[] parts = normalized.Split('/');

        if (parts.Length == 0 ||
            parts.Any(p =>
                string.IsNullOrEmpty(p) ||
                p == "." ||
                p == ".." ||
                p.Contains(':') ||
                p.Any(char.IsControl) ||
                p.IndexOfAny(
                    Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new IOException(
                "Archive path contains unsafe components.");
        }

        string fullRoot = Path.GetFullPath(root);

        Directory.CreateDirectory(fullRoot);

        string fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, Path.Combine(parts)));

        string prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "Target path escapes the chosen directory.");
        }

        string current = fullRoot;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = Path.Combine(current, parts[i]);

            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Refusing to extract through a junction or symbolic link.");
                }
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }

        return fullPath;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    private async Task ExportInventoryAsync()
    {
        if (_reader == null || _archivePath == null)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Export archive inventory",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = "csv",
            AddExtension = true,
            FileName =
                Path.GetFileNameWithoutExtension(_archivePath)
                + "_contents.csv"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var reader = _reader;
        string output = dialog.FileName;

        await RunOperationAsync(
            "Exporting archive inventory...",
            async (ct, p) =>
            {
                await Task.Run(() =>
                {
                    using var writer = new StreamWriter(
                        output,
                        false,
                        new UTF8Encoding(true));

                    writer.WriteLine("Path,Type,Size");

                    long visited = 0;

                    WriteManifestDirectory(
                        reader,
                        ZArchiveReader.RootNode,
                        "",
                        writer,
                        ct,
                        p,
                        ref visited);
                }, ct);

                p.Report(new OpStatus(
                    $"Inventory exported: {output}",
                    100));
            });
    }

    private static void WriteManifestDirectory(
        ZArchiveReader reader,
        uint directoryNode,
        string directoryPath,
        StreamWriter writer,
        CancellationToken token,
        IProgress<OpStatus> progress,
        ref long visited)
    {
        uint count =
            reader.GetDirEntryCount(directoryNode);

        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();

            if (!reader.TryGetDirEntry(
                directoryNode,
                i,
                out uint child,
                out var entry))
            {
                continue;
            }

            string path =
                JoinArchivePath(directoryPath, entry.Name);

            writer.Write(Csv(path));
            writer.Write(',');

            writer.Write(
                entry.IsDirectory ? "Directory" : "File");

            writer.Write(',');

            if (entry.IsFile)
            {
                writer.Write(
                    entry.Size.ToString(
                        CultureInfo.InvariantCulture));
            }

            writer.WriteLine();

            visited++;

            if (entry.IsDirectory)
            {
                WriteManifestDirectory(
                    reader,
                    child,
                    path,
                    writer,
                    token,
                    progress,
                    ref visited);
            }

            if (visited % 5000 == 0)
            {
                progress.Report(new OpStatus(
                    $"Exporting inventory: {visited:N0} entries"));
            }
        }
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void PreviewSelected()
    {
        var selected = GetSelectedItems();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select a file first.",
                "Preview",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        ShowPreview(selected[0]);
    }

    private void ShowPreview(ArchiveItem item)
    {
        if (_reader == null)
            return;

        if (item.IsDirectory)
        {
            _previewHeader.Text =
                "Folder — no preview available";

            _preview.Clear();
            return;
        }

        try
        {
            using var source =
                _reader.OpenRead(item.Node);

            int wanted = (int)Math.Min(
                (ulong)PreviewLimit,
                (ulong)source.Length);

            byte[] bytes = new byte[wanted];

            int readTotal = 0;

            while (readTotal < wanted)
            {
                int read = source.Read(
                    bytes,
                    readTotal,
                    wanted - readTotal);

                if (read <= 0)
                    break;

                readTotal += read;
            }

            _previewHeader.Text =
                $"{item.Path} — {FormatSize(item.Size)}" +
                (item.Size > (ulong)readTotal
                    ? $" — first {FormatSize((ulong)readTotal)} shown"
                    : "");

            bool binary =
                bytes.Take(readTotal).Contains((byte)0);

            if (!binary && readTotal > 0)
            {
                int printable = bytes
                    .Take(readTotal)
                    .Count(b =>
                        b == 9 ||
                        b == 10 ||
                        b == 13 ||
                        b >= 32);

                binary = printable < readTotal * 0.85;
            }

            if (!binary)
            {
                _preview.Text =
                    Encoding.UTF8.GetString(
                        bytes,
                        0,
                        readTotal);
            }
            else
            {
                var text = new StringBuilder();

                for (int offset = 0;
                     offset < readTotal;
                     offset += 16)
                {
                    int lineLength =
                        Math.Min(16, readTotal - offset);

                    text.Append($"{offset:X8}  ");

                    for (int i = 0; i < 16; i++)
                    {
                        if (i < lineLength)
                        {
                            text.Append(
                                $"{bytes[offset + i]:X2} ");
                        }
                        else
                        {
                            text.Append("   ");
                        }

                        if (i == 7)
                            text.Append(' ');
                    }

                    text.Append(" |");

                    for (int i = 0; i < lineLength; i++)
                    {
                        byte b = bytes[offset + i];

                        text.Append(
                            b >= 32 && b < 127
                                ? (char)b
                                : '.');
                    }

                    text.AppendLine("|");
                }

                _preview.Text = text.ToString();
            }

            if (item.Size > (ulong)readTotal)
            {
                _preview.AppendText(
                    "\r\n[Preview truncated. Use Extract to save the full file.]");
            }
        }
        catch (Exception ex)
        {
            _previewHeader.Text = "Preview failed";
            _preview.Text = ex.ToString();
        }
    }

    private void CopySelectedPath()
    {
        var selected = GetSelectedItems();

        if (selected.Count == 0)
            return;

        Clipboard.SetText(selected[0].Path);

        _statusText.Text =
            "Copied archive path to clipboard";
    }

    private void GoUp()
    {
        if (_reader == null ||
            string.IsNullOrEmpty(_currentFolder.Path))
        {
            return;
        }

        int slash =
            _currentFolder.Path.LastIndexOf('/');

        string parent = slash < 0
            ? ""
            : _currentFolder.Path[..slash];

        uint node = _reader.LookUp(parent);

        if (node != ZArchiveReader.InvalidNode &&
            _reader.IsDirectory(node))
        {
            LoadFolder(new FolderRef(node, parent));
        }
    }

    private void ShowArchiveInfo()
    {
        if (_reader == null || _archivePath == null)
            return;

        var info = new FileInfo(_archivePath);

        MessageBox.Show(
            this,
            $"Archive: {_archivePath}\n" +
            $"On-disk size: {FormatSize((ulong)info.Length)}\n" +
            $"Entry count: {_reader.EntryCount:N0}\n" +
            $"Total uncompressed size: " +
            $"{FormatSize(_reader.TotalUncompressedSize)}\n\n" +
            "The original archive is opened read-only.",
            "Archive information",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static string FormatSize(ulong bytes)
    {
        string[] units =
        {
            "B", "KB", "MB", "GB", "TB", "PB"
        };

        double value = bytes;
        int unit = 0;

        while (value >= 1024 &&
               unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : $"{value:0.##} {units[unit]}";
    }
}

// =====================================================================
// Create Archive dialog
// =====================================================================

internal sealed class CreateArchiveDialog : Form
{
    private readonly List<PackSourceFile> _files = new();

    private readonly ListView _list = new();
    private readonly TextBox _outputBox = new();

    private readonly Label _summaryLabel = new();

    public IReadOnlyList<PackSourceFile> Files => _files;

    public string OutputPath { get; private set; } = "";

    public CreateArchiveDialog()
    {
        Text = "Create ZArchive";
        Width = 1000;
        Height = 650;
        MinimumSize = new Size(760, 450);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        BuildUi();
        UpdateSummary();
    }

    private void BuildUi()
    {
        var addFilesButton = new Button
        {
            Text = "Add files...",
            AutoSize = true
        };

        var addFolderContentsButton = new Button
        {
            Text = "Add folder contents...",
            AutoSize = true
        };

        var addFolderRootButton = new Button
        {
            Text = "Add folder with root name...",
            AutoSize = true
        };

        var removeButton = new Button
        {
            Text = "Remove selected",
            AutoSize = true
        };

        var clearButton = new Button
        {
            Text = "Clear list",
            AutoSize = true
        };

        addFilesButton.Click += (_, _) => AddFiles();
        addFolderContentsButton.Click +=
            (_, _) => AddFolder(includeFolderName: false);
        addFolderRootButton.Click +=
            (_, _) => AddFolder(includeFolderName: true);
        removeButton.Click += (_, _) => RemoveSelected();
        clearButton.Click += (_, _) => ClearList();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(5)
        };

        actions.Controls.Add(addFilesButton);
        actions.Controls.Add(addFolderContentsButton);
        actions.Controls.Add(addFolderRootButton);
        actions.Controls.Add(removeButton);
        actions.Controls.Add(clearButton);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;

        _list.Columns.Add("Archive path", 260);
        _list.Columns.Add("Source file", 500);
        _list.Columns.Add("Size", 100);

        _list.ShowItemToolTips = true;

        var outputLabel = new Label
        {
            Text = "Output archive:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(5, 8, 5, 5)
        };

        _outputBox.Dock = DockStyle.Fill;
        _outputBox.Text = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory),
            "newarchive.zar");

        var browseOutputButton = new Button
        {
            Text = "Browse...",
            AutoSize = true
        };

        browseOutputButton.Click += (_, _) => BrowseOutput();

        var outputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(5),
            Margin = Padding.Empty
        };

        outputPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        outputPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        outputPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        outputPanel.Controls.Add(outputLabel, 0, 0);
        outputPanel.Controls.Add(_outputBox, 1, 0);
        outputPanel.Controls.Add(browseOutputButton, 2, 0);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Padding = new Padding(8, 0, 0, 0);

        var createButton = new Button
        {
            Text = "Create archive",
            AutoSize = true
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };

        createButton.Click += (_, _) => ConfirmCreate();

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(5),
            Margin = Padding.Empty
        };

        footer.Controls.Add(cancelButton);
        footer.Controls.Add(createButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };

        layout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 42));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 42));

        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(outputPanel, 0, 2);

        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        bottomPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        bottomPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        bottomPanel.Controls.Add(_summaryLabel, 0, 0);
        bottomPanel.Controls.Add(footer, 1, 0);

        layout.Controls.Add(bottomPanel, 0, 3);

        Controls.Add(layout);

        AcceptButton = createButton;
        CancelButton = cancelButton;
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select files to add",
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var additions = new List<PackSourceFile>();

        try
        {
            foreach (string filePath in dialog.FileNames)
            {
                var info = new FileInfo(filePath);

                if (!info.Exists)
                    continue;

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                additions.Add(new PackSourceFile(
                    Path.GetFullPath(info.FullName),
                    info.Name,
                    (ulong)info.Length));
            }

            AddEntries(additions);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Could not add files",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void AddFolder(bool includeFolderName)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = includeFolderName
                ? "Select a folder to add under its own root folder name"
                : "Select a folder whose contents should become the archive root",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            string root = Path.GetFullPath(dialog.SelectedPath);

            if (!Directory.Exists(root))
            {
                MessageBox.Show(
                    this,
                    "The selected directory no longer exists.",
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            if ((File.GetAttributes(root) &
                 FileAttributes.ReparsePoint) != 0)
            {
                MessageBox.Show(
                    this,
                    "The selected folder is a junction or symbolic link.\n\n" +
                    "Select its real target directory instead.",
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            List<PackSourceFile> additions =
                ScanFolder(root, includeFolderName);

            if (additions.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No regular files were found in the selected folder.",
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            AddEntries(additions);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.ToString(),
                "Could not scan folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static List<PackSourceFile> ScanFolder(
        string root,
        bool includeFolderName)
    {
        var result = new List<PackSourceFile>();
        var pending = new Stack<string>();

        pending.Push(root);

        string rootName = Path.GetFileName(
            root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            foreach (string filePath in
                     Directory.GetFiles(directory))
            {
                FileAttributes attributes =
                    File.GetAttributes(filePath);

                // Do not follow symbolic links or other reparse points.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var info = new FileInfo(filePath);

                string relative = Path.GetRelativePath(
                    root, info.FullName);

                relative = relative
                    .Replace('\\', '/');

                string archivePath = includeFolderName
                    ? rootName + "/" + relative
                    : relative;

                result.Add(new PackSourceFile(
                    Path.GetFullPath(info.FullName),
                    archivePath,
                    (ulong)info.Length));
            }

            foreach (string childDirectory in
                     Directory.GetDirectories(directory))
            {
                FileAttributes attributes =
                    File.GetAttributes(childDirectory);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                pending.Push(childDirectory);
            }
        }

        return result;
    }

    private void AddEntries(List<PackSourceFile> additions)
    {
        if (additions.Count == 0)
            return;

        var seen = new HashSet<string>(
            _files.Select(f => f.ArchivePath),
            StringComparer.OrdinalIgnoreCase);

        var collisions = new List<string>();

        foreach (var file in additions)
        {
            try
            {
                MainForm.ValidateArchiveEntryPath(file.ArchivePath);
            }
            catch (Exception ex)
            {
                collisions.Add(
                    $"{file.ArchivePath}: {ex.Message}");

                if (collisions.Count >= 10)
                    break;

                continue;
            }

            if (!seen.Add(file.ArchivePath))
            {
                collisions.Add(file.ArchivePath);

                if (collisions.Count >= 10)
                    break;
            }
        }

        if (collisions.Count > 0)
        {
            MessageBox.Show(
                this,
                "The selected items contain an invalid or duplicate archive path.\n\n" +
                string.Join("\n", collisions) +
                "\n\nNo items from this batch were added.",
                "Archive path conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        foreach (var file in additions)
        {
            _files.Add(file);

            var item = new ListViewItem(file.ArchivePath);

            item.SubItems.Add(file.SourcePath);
            item.SubItems.Add(FormatSize(file.Size));
            item.ToolTipText = file.SourcePath;

            _list.Items.Add(item);
        }

        UpdateSummary();
    }

    private void RemoveSelected()
    {
        int[] selectedIndices = _list.SelectedIndices
            .Cast<int>()
            .OrderByDescending(i => i)
            .ToArray();

        foreach (int index in selectedIndices)
        {
            if (index >= 0 && index < _files.Count)
            {
                _files.RemoveAt(index);
                _list.Items.RemoveAt(index);
            }
        }

        UpdateSummary();
    }

    private void ClearList()
    {
        if (_files.Count == 0)
            return;

        _files.Clear();
        _list.Items.Clear();

        UpdateSummary();
    }

    private void BrowseOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Choose archive output path",
            Filter =
                "ZArchive (*.zar)|*.zar|" +
                "ZArchive/WUA (*.wua)|*.wua|" +
                "All files (*.*)|*.*",
            DefaultExt = "zar",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = Path.GetFileName(_outputBox.Text)
        };

        string? currentDirectory =
            Path.GetDirectoryName(_outputBox.Text);

        if (!string.IsNullOrWhiteSpace(currentDirectory) &&
            Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputBox.Text = dialog.FileName;
        }
    }

    private void ConfirmCreate()
    {
        if (_files.Count == 0)
        {
            MessageBox.Show(
                this,
                "Add at least one file or folder.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        string rawOutput = _outputBox.Text.Trim();

        if (rawOutput.Length == 0)
        {
            MessageBox.Show(
                this,
                "Choose an output path.",
                "Create archive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        try
        {
            if (Path.GetExtension(rawOutput).Length == 0)
            {
                rawOutput += ".zar";
            }

            string extension = Path.GetExtension(rawOutput);

            if (!extension.Equals(
                    ".zar",
                    StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(
                    ".wua",
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "Choose a .zar or .wua output extension.",
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            OutputPath = Path.GetFullPath(rawOutput);
            _outputBox.Text = OutputPath;

            var duplicates = _files
                .GroupBy(
                    f => f.ArchivePath,
                    StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .Take(10)
                .ToList();

            if (duplicates.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "Duplicate archive paths were found:\n\n" +
                    string.Join("\n", duplicates),
                    "Create archive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (var file in _files)
            {
                if (!File.Exists(file.SourcePath))
                {
                    MessageBox.Show(
                        this,
                        $"A queued source file no longer exists:\n\n" +
                        $"{file.SourcePath}",
                        "Create archive",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    return;
                }

                MainForm.ValidateArchiveEntryPath(file.ArchivePath);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Invalid archive settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void UpdateSummary()
    {
        ulong totalBytes = 0;

        foreach (var file in _files)
        {
            totalBytes = totalBytes > ulong.MaxValue - file.Size
                ? ulong.MaxValue
                : totalBytes + file.Size;
        }

        _summaryLabel.Text =
            $"{_files.Count:N0} files queued — " +
            $"{FormatSize(totalBytes)} uncompressed";
    }

    private static string FormatSize(ulong bytes)
    {
        string[] units =
        {
            "B", "KB", "MB", "GB", "TB", "PB"
        };

        double value = bytes;
        int unit = 0;

        while (value >= 1024 &&
               unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : $"{value:0.##} {units[unit]}";
    }
}