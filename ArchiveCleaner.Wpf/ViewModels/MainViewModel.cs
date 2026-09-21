using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Input;
using ArchiveCleaner.Core.Audit;
using ArchiveCleaner.Core.Batch;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;
using ArchiveCleaner.Wpf.Views;

namespace ArchiveCleaner.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ImageCatalogService _imageCatalog = new();
    private readonly SettingsManager _settingsManager = new();
    private AppSettings _currentSettings;
    private ArchiveImageItem? _selectedImage;
    private BitmapSource? _selectedPreview;
    private readonly Dictionary<BitmapSource, BitmapSource> _rotatedPreviews = new();
    private ReviewRegionItem? _selectedReviewRegion;
    private string _batchName = "新建批次";
    private string _engineStatus = "OpenCV 离线清理引擎就绪";
    private string _riskMessage = "尚未分析当前批次。点击“分析当前批次”后，程序会在此提示需要人工确认的区域。";
    private string _outputDirectory = string.Empty;
    private double _progressPercent;
    private int _processedCount;
    private int _reviewCount;
    private bool _isBusy;
    private bool _isLoading;
    private string _loadingMessage = string.Empty;
    private int _loadVersion;
    private BitmapSource? _preparedSelectionPreview;
    private bool _canCancelOperation;
    private ManualEditMode _manualEditMode;
    private Color _protectToolColor = Color.FromRgb(41, 135, 92);
    private Color _removeToolColor = Color.FromRgb(212, 71, 71);
    private Color _eraserToolColor = Color.FromRgb(89, 101, 107);

    public MainViewModel(bool loadSamples = true)
    {
        _currentSettings = _settingsManager.Load();
        Settings.PropertyChanged += Settings_PropertyChanged;
#if DEBUG
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        if (loadSamples && Directory.Exists(sampleDirectory)) LoadDirectory(sampleDirectory, replaceExisting: true);
#endif
    }

    public ObservableCollection<ArchiveImageItem> Images { get; } = [];
    public ObservableCollection<CatalogTreeNode> TreeRoots { get; } = [];
    public CleanupSettings Settings { get; } = new();

    public ICommand OpenSettingsCommand => new RelayCommand(OpenSettings);
    public Key HorizontalAssistKey => _currentSettings.HorizontalAssistKey;
    public Key VerticalAssistKey => _currentSettings.VerticalAssistKey;

    public ArchiveImageItem? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (!SetProperty(ref _selectedImage, value)) return;
            SelectedPreview = value is null ? null : _preparedSelectionPreview ?? _imageCatalog.LoadPreview(value.FilePath);
            SelectedReviewRegion = value?.ReviewRegions.FirstOrDefault(region => !value.Decisions.ContainsKey(region.Id)) ?? value?.ReviewRegions.FirstOrDefault();
            NotifySelectedPreviewChanged();
            UpdateRiskForSelected();
            OnPropertyChanged(nameof(SelectedIndexText));
            OnPropertyChanged(nameof(DirectoryPositionText));
            OnPropertyChanged(nameof(ReviewRegions));
            OnPropertyChanged(nameof(CanUseManualTools));
            OnPropertyChanged(nameof(CanRestoreOriginal));
            OnPropertyChanged(nameof(CanRotateSelected));
            OnPropertyChanged(nameof(ManualCorrectionSummary));
            OnPropertyChanged(nameof(CanUndoManualEdit));
            OnPropertyChanged(nameof(CanRedoManualEdit));
        }
    }

    public BitmapSource? SelectedPreview { get => RotatePreview(_selectedPreview); private set => SetProperty(ref _selectedPreview, value); }
    public BitmapSource? SelectedAnalysisPreview => RotatePreview(SelectedImage?.AnalysisPreview ?? _selectedPreview);
    public BitmapSource? SelectedRepairedPreview => RotatePreview(SelectedImage?.RepairedPreview ?? _selectedPreview);
    public BitmapSource? SelectedMaskPreview => RotatePreview(SelectedImage?.MaskPreview);
    public bool CanRotateSelected => SelectedImage is not null && !IsBusy;

    private BitmapSource? RotatePreview(BitmapSource? source)
    {
        var turns = SelectedImage?.RotationQuarterTurns ?? 0;
        if (source is null || turns == 0) return source;
        if (_rotatedPreviews.TryGetValue(source, out var cached)) return cached;
        var rotated = new TransformedBitmap(source, new RotateTransform(turns * 90));
        rotated.Freeze();
        _rotatedPreviews[source] = rotated;
        return rotated;
    }

    public bool RotateSelectedClockwise()
    {
        if (!CanRotateSelected) return false;
        if (!SelectedImage!.TryRotateClockwise())
        {
            EngineStatus = "PDF图片节点不支持旋转";
            return false;
        }
        NotifySelectedPreviewChanged();
        EngineStatus = $"当前图片已旋转至 {SelectedImage.RotationQuarterTurns * 90}°，导出将保持此方向";
        return true;
    }
    public ObservableCollection<ReviewRegionItem> ReviewRegions => SelectedImage?.ReviewRegions ?? [];

    public ReviewRegionItem? SelectedReviewRegion
    {
        get => _selectedReviewRegion;
        set
        {
            if (SetProperty(ref _selectedReviewRegion, value)) OnPropertyChanged(nameof(SelectedReviewRegionText));
        }
    }

    public string SelectedReviewRegionText => SelectedReviewRegion is null ? "当前页没有待复核区域" : $"区域 {ReviewRegions.IndexOf(SelectedReviewRegion) + 1}/{ReviewRegions.Count} · {SelectedReviewRegion.DecisionText}";
    public string BatchName { get => _batchName; set => SetProperty(ref _batchName, value); }
    public string EngineStatus { get => _engineStatus; set => SetProperty(ref _engineStatus, value); }
    public string RiskMessage { get => _riskMessage; set => SetProperty(ref _riskMessage, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetProperty(ref _outputDirectory, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public int ProcessedCount { get => _processedCount; private set => SetProperty(ref _processedCount, value); }
    public int ReviewCount { get => _reviewCount; private set => SetProperty(ref _reviewCount, value); }
    public int UnresolvedReviewRegionCount => Images.Sum(item =>
        IsSelectedForBatch(item) && !item.ManualOnly ? item.ReviewRegions.Count(region => !item.Decisions.ContainsKey(region.Id)) : 0);
    public bool CanRemoveAllReviewRegions => !IsBusy && UnresolvedReviewRegionCount > 0;
    public int SelectedCount => GetSelectedImages().Count;
    public string SelectionSummary => $"已选择 {SelectedCount} / 共 {TotalCount} 张";
    public string DirectoryPositionText => $"{(SelectedImage is null ? 0 : Images.IndexOf(SelectedImage) + 1)}/{Images.Count}";
    public bool CanRunSelectedBatch => !IsBusy && SelectedCount > 0;
    public bool CanRestoreOriginal => !IsBusy && SelectedImage?.Analysis is not null;
    public bool CanCancelOperation { get => _canCancelOperation; private set => SetProperty(ref _canCancelOperation, value); }
    public bool CanLoadFiles => !IsBusy;
    public bool CanInteractWithContent => !IsLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(CanInteractWithContent));
        }
    }
    public string LoadingMessage { get => _loadingMessage; private set => SetProperty(ref _loadingMessage, value); }

    public void ShowLoadingCancellation()
    {
        if (IsLoading) LoadingMessage = "正在取消加载，等待当前文件处理结束…";
    }

    public void SetOperationCancelable(bool canCancel) => CanCancelOperation = canCancel;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanUseManualTools));
                OnPropertyChanged(nameof(CanRemoveAllReviewRegions));
                OnPropertyChanged(nameof(CanUndoManualEdit));
                OnPropertyChanged(nameof(CanRedoManualEdit));
                OnPropertyChanged(nameof(CanRunSelectedBatch));
                OnPropertyChanged(nameof(CanRestoreOriginal));
                OnPropertyChanged(nameof(CanRotateSelected));
                OnPropertyChanged(nameof(CanLoadFiles));
            }
        }
    }
    public ManualEditMode ManualEditMode
    {
        get => _manualEditMode;
        set
        {
            if (!SetProperty(ref _manualEditMode, value)) return;
            OnPropertyChanged(nameof(ManualToolText));
            OnPropertyChanged(nameof(BrushDiameterPixels));
            OnPropertyChanged(nameof(CurrentManualToolBrush));
        }
    }
    public bool CanUseManualTools => SelectedImage?.Analysis is not null && !IsBusy;
    public bool CanUndoManualEdit => !IsBusy && SelectedImage?.CanUndoManualEdit == true;
    public bool CanRedoManualEdit => !IsBusy && SelectedImage?.CanRedoManualEdit == true;
    public double BrushDiameterPixels
    {
        get => ManualEditMode switch
        {
            ManualEditMode.RemoveBrush => Settings.RemoveBrushDiameterPixels,
            ManualEditMode.CloneStamp => Settings.CloneStampDiameterPixels,
            ManualEditMode.Eraser => Settings.EraserDiameterPixels,
            _ => Settings.ProtectBrushDiameterPixels
        };
        set
        {
            var diameter = Math.Clamp(value, 10, 300);
            switch (ManualEditMode)
            {
                case ManualEditMode.RemoveBrush:
                    Settings.RemoveBrushDiameterPixels = diameter;
                    break;
                case ManualEditMode.CloneStamp:
                    Settings.CloneStampDiameterPixels = diameter;
                    break;
                case ManualEditMode.Eraser:
                    Settings.EraserDiameterPixels = diameter;
                    break;
                default:
                    Settings.ProtectBrushDiameterPixels = diameter;
                    break;
            }
        }
    }
    public Brush ProtectToolBrush => FrozenBrush(_protectToolColor);
    public Brush RemoveToolBrush => FrozenBrush(_removeToolColor);
    public Brush EraserToolBrush => FrozenBrush(_eraserToolColor);
    public Brush CurrentManualToolBrush => ManualEditMode switch
    {
        ManualEditMode.RemoveBrush => RemoveToolBrush,
        ManualEditMode.Eraser => EraserToolBrush,
        _ => ProtectToolBrush
    };
    public string ManualToolText => ManualEditMode switch
    {
        ManualEditMode.ProtectBrush => "保护笔刷：在左侧图像上拖动",
        ManualEditMode.RemoveBrush => "去除笔刷：在左侧图像上拖动",
        ManualEditMode.CloneStamp => "仿制图章：按住 Alt 点击取样，再拖动复制",
        ManualEditMode.Eraser => "恢复橡皮擦：拖动恢复自动去除、人工去除或仿制图章覆盖的原图内容",
        _ => "请选择人工修正工具"
    };
    public string ManualCorrectionSummary => SelectedImage is null
        ? "未选择图片"
        : $"人工保护 {CountActive(SelectedImage.ManualProtectionPixels):N0} px · 人工去除 {CountActive(SelectedImage.ManualRemovalPixels):N0} px";
    public int TotalCount => Images.Count;
    public string SelectedIndexText => SelectedImage is null ? "未选择" : $"第 {Images.IndexOf(SelectedImage) + 1} / {Images.Count} 张";

    public void AddFiles(IEnumerable<string> filePaths)
    {
        var result = _imageCatalog.LoadFiles(filePaths, ExistingSources(), UsedOutputPaths());
        AddCatalogResult(result);
        SelectedImage ??= Images.FirstOrDefault();
        NotifyCollectionSummary();
        EngineStatus = $"当前已加载 {Images.Count} 张图片 / PDF 页面";
        if (result.Errors.Count > 0) throw new AggregateException("部分图片或 PDF 无法读取", result.Errors);
    }

    private HashSet<string> ExistingSources() => Images.Select(item => item.PdfSourcePath ?? item.FilePath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> UsedOutputPaths() => Images.Select(item => item.IsPdfPage
        ? Path.GetDirectoryName(item.RelativeOutputPath)! : item.RelativeOutputPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task AddFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var paths = filePaths.ToArray();
        var existing = ExistingSources();
        var used = UsedOutputPaths();
        return LoadCatalogAsync((progress, token) =>
            [_imageCatalog.LoadFiles(paths, existing, used, progress, token)],
            replaceExisting: false, batchName: null, "正在加载文件…", cancellationToken);
    }

    public Task LoadDirectoriesAsync(IEnumerable<string> directories, bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        var roots = directories.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) return Task.CompletedTask;
        var batchName = roots.Length == 1 ? new DirectoryInfo(roots[0]).Name
            : $"{new DirectoryInfo(roots[0]).Name} 等 {roots.Length} 个文件夹";
        return LoadCatalogAsync((progress, token) =>
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<CatalogLoadResult>();
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                var prefix = roots.Length > 1 ? CreateUniqueRootPrefix(new DirectoryInfo(root).Name, prefixes) : null;
                results.Add(_imageCatalog.LoadDirectoryTree(root, prefix, progress, token));
            }
            return results;
        }, replaceExisting, batchName, "正在扫描文件夹…", cancellationToken);
    }

    private async Task LoadCatalogAsync(
        Func<IProgress<CatalogLoadProgress>, CancellationToken, IReadOnlyList<CatalogLoadResult>> prepare,
        bool replaceExisting, string? batchName, string initialMessage, CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        LoadingMessage = initialMessage;
        IsLoading = true;
        var version = ++_loadVersion;
        // All collection changes below resume on the caller's UI context. Only detached data is built on the worker.
        var progress = new ThrottledCatalogProgress(new Progress<CatalogLoadProgress>(value =>
        {
            if (IsLoading && version == _loadVersion && !cancellationToken.IsCancellationRequested)
                LoadingMessage = value.Message;
        }));
        var existingSelection = replaceExisting ? null : SelectedImage ?? Images.FirstOrDefault();
        try
        {
            var prepared = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var results = prepare(progress, cancellationToken);
                var first = existingSelection ?? results.SelectMany(result => result.Images).FirstOrDefault();
                BitmapSource? preview = null;
                if (first is not null && (replaceExisting || existingSelection is null))
                {
                    progress.Report(new("正在准备图片预览…"));
                    preview = _imageCatalog.LoadPreview(first.FilePath);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return (Results: results, Preview: preview);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Stop queued progress reports from overwriting completion/cancellation messages.
            _loadVersion++;
            LoadingMessage = "正在更新图片列表…";
            if (replaceExisting)
            {
                Images.Clear();
                TreeRoots.Clear();
                SelectedImage = null;
            }
            foreach (var result in prepared.Results) AddCatalogResult(result);
            _preparedSelectionPreview = prepared.Preview;
            SelectedImage ??= Images.FirstOrDefault();
            if (batchName is not null) BatchName = batchName;
            NotifyCollectionSummary();
            var skippedDirectories = prepared.Results.Sum(result => result.SkippedDirectoryCount);
            var skippedFiles = prepared.Results.Sum(result => result.SkippedFileCount);
            EngineStatus = $"已加载 {Images.Count} 张图片 / PDF 页面" +
                (skippedDirectories + skippedFiles > 0 ? $"；跳过 {skippedDirectories} 个不可访问目录和 {skippedFiles} 个无法读取文件" : string.Empty);
            var errors = prepared.Results.SelectMany(result => result.Errors).ToArray();
            if (errors.Length > 0) throw new AggregateException("部分图片或 PDF 无法读取", errors);
        }
        catch (OperationCanceledException)
        {
            EngineStatus = "已取消加载，保留原来的图片列表";
            throw;
        }
        finally
        {
            _preparedSelectionPreview = null;
            IsLoading = false;
            IsBusy = false;
        }
    }

    private sealed class ThrottledCatalogProgress(IProgress<CatalogLoadProgress> target) : IProgress<CatalogLoadProgress>
    {
        private long _lastReport;
        public void Report(CatalogLoadProgress value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastReport != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 50) return;
            _lastReport = now;
            target.Report(value);
        }
    }

    public void LoadDirectory(string directory, bool replaceExisting)
    {
        if (replaceExisting)
        {
            Images.Clear();
            TreeRoots.Clear();
            SelectedImage = null;
        }
        var result = _imageCatalog.LoadDirectoryTree(directory);
        AddCatalogResult(result);
        SelectedImage ??= Images.FirstOrDefault();
        BatchName = new DirectoryInfo(directory).Name;
        EngineStatus = $"已递归加载 {Images.Count} 张图片" +
            (result.SkippedDirectoryCount + result.SkippedFileCount > 0
                ? $"；跳过 {result.SkippedDirectoryCount} 个不可访问目录和 {result.SkippedFileCount} 个无法读取文件"
                : string.Empty);
        NotifyCollectionSummary();
    }

    public void LoadDirectories(IEnumerable<string> directories, bool replaceExisting)
    {
        var roots = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return;
        if (replaceExisting)
        {
            Images.Clear();
            TreeRoots.Clear();
            SelectedImage = null;
        }

        var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedDirectories = 0;
        var skippedFiles = 0;
        foreach (var rootPath in roots)
        {
            var prefix = roots.Length > 1
                ? CreateUniqueRootPrefix(new DirectoryInfo(rootPath).Name, usedPrefixes)
                : null;
            var result = _imageCatalog.LoadDirectoryTree(rootPath, prefix);
            AddCatalogResult(result);
            skippedDirectories += result.SkippedDirectoryCount;
            skippedFiles += result.SkippedFileCount;
        }

        SelectedImage ??= Images.FirstOrDefault();
        BatchName = roots.Length == 1
            ? new DirectoryInfo(roots[0]).Name
            : $"{new DirectoryInfo(roots[0]).Name} 等 {roots.Length} 个文件夹";
        EngineStatus = $"已递归加载 {Images.Count} 张图片" +
            (skippedDirectories + skippedFiles > 0
                ? $"；跳过 {skippedDirectories} 个不可访问目录和 {skippedFiles} 个无法读取文件"
                : string.Empty);
        NotifyCollectionSummary();
    }

    private void AddCatalogResult(CatalogLoadResult result)
    {
        if (result.Root is null) return;
        // PDF identity belongs to its source and page number, not the rendered cache path.
        static string Identity(ArchiveImageItem item) => item.IsPdfPage
            ? $"{item.PdfSourcePath}|{item.PdfPageNumber}"
            : item.FilePath;
        var existingPaths = Images.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicates = result.Images.Where(item => !existingPaths.Add(Identity(item))).ToHashSet();
        RemoveImagesRecursive(result.Root, duplicates);
        if (result.Root.ImageCount == 0) return;
        foreach (var image in result.Images.Where(item => !duplicates.Contains(item)))
        {
            image.CopyBoundaryFrom(Settings);
            Images.Add(image);
        }
        var standalone = result.Root.FullPath == "standalone://images"
            ? TreeRoots.OfType<FolderTreeNode>().FirstOrDefault(root => root.FullPath == result.Root.FullPath)
            : null;
        if (standalone is not null)
        {
            foreach (var child in result.Root.Children.ToArray()) standalone.AddChild(child);
            standalone.RaiseContentSummaryChanged();
        }
        else
        {
            TreeRoots.Add(result.Root);
            SubscribeToRoot(result.Root);
        }
    }

    public void InitializeImageBoundariesFromDefaults()
    {
        foreach (var item in Images) item.CopyBoundaryFrom(Settings);
        if (SelectedImage is not null) OnPropertyChanged(nameof(SelectedImage));
    }

    public bool ApplySelectedBoundaryToChecked()
    {
        var source = SelectedImage;
        var checkedImages = GetSelectedImages();
        if (source is null || checkedImages.Count == 0) return false;
        source.CopyBoundaryTo(Settings);
        foreach (var item in checkedImages) item.CopyBoundaryFrom(source);
        OnPropertyChanged(nameof(SelectedImage));
        return true;
    }

    public void SelectTreeNode(CatalogTreeNode? node)
    {
        if (node is ImageTreeNode imageNode) SelectedImage = imageNode.Image;
    }

    public void SetAllImagesChecked(bool isChecked)
    {
        foreach (var root in TreeRoots) root.IsChecked = isChecked;
        NotifySelectionSummary();
    }

    public void SetAllFoldersExpanded(bool isExpanded)
    {
        foreach (var folder in TreeRoots.OfType<FolderTreeNode>()) SetExpandedRecursive(folder, isExpanded);
    }

    public int RemoveCheckedImages()
    {
        var removed = GetSelectedImages();
        if (removed.Count == 0) return 0;
        var removedSet = removed.ToHashSet();
        foreach (var root in TreeRoots.OfType<FolderTreeNode>().ToArray())
        {
            RemoveImagesRecursive(root, removedSet);
            if (root.Children.Count == 0) TreeRoots.Remove(root);
        }
        foreach (var item in removed) Images.Remove(item);
        if (SelectedImage is not null && removedSet.Contains(SelectedImage)) SelectedImage = Images.FirstOrDefault();
        NotifyCollectionSummary();
        UpdateReviewSummary();
        EngineStatus = $"已从任务队列移除 {removed.Count} 张图片；源文件未删除";
        return removed.Count;
    }

    public int RemoveTreeNode(CatalogTreeNode node)
    {
        if (IsBusy) return 0;
        var removed = EnumerateImageNodes(node).Select(item => item.Image).Distinct().ToArray();
        if (node.Parent is { } parent)
        {
            parent.Children.Remove(node);
            while (parent.Children.Count == 0)
            {
                var emptyFolder = parent;
                parent = emptyFolder.Parent;
                if (parent is null)
                {
                    TreeRoots.Remove(emptyFolder);
                    break;
                }
                parent.Children.Remove(emptyFolder);
            }
            if (parent is not null)
            {
                parent.RefreshCheckedStateFromChildren();
                parent.RaiseContentSummaryChanged();
            }
        }
        else
        {
            TreeRoots.Remove(node);
        }

        foreach (var item in removed) Images.Remove(item);
        if (SelectedImage is not null && removed.Contains(SelectedImage)) SelectedImage = Images.FirstOrDefault();
        NotifyCollectionSummary();
        UpdateReviewSummary();
        EngineStatus = node is FolderTreeNode folder
            ? $"已从任务队列移除文件夹“{folder.Name}”及其中 {removed.Length} 张图片；源文件未删除"
            : $"已从任务队列移除文件“{node.Name}”；源文件未删除";
        return removed.Length;
    }

    public int ClearImageQueue()
    {
        var count = Images.Count;
        Images.Clear();
        TreeRoots.Clear();
        SelectedImage = null;
        ProcessedCount = 0;
        ProgressPercent = 0;
        ReviewCount = 0;
        NotifyCollectionSummary();
        UpdateReviewSummary();
        EngineStatus = $"已清空任务队列中的 {count} 张图片；源文件未删除";
        return count;
    }

    public async Task AnalyzeBatchAsync(CancellationToken cancellationToken = default)
    {
        var selectedItems = GetSelectedImages();
        if (selectedItems.Count == 0 || IsBusy) return;
        var settings = Settings.CreateSnapshot();
        IsBusy = true;
        ProcessedCount = 0;
        ReviewCount = 0;
        ProgressPercent = 0;
        EngineStatus = "正在运行真实页面分析";
        try
        {
            foreach (var item in selectedItems)
            {
                if (cancellationToken.IsCancellationRequested) return;
                item.ManualOnly = false;
                item.ClearManualCorrections();
                item.StatusKind = ImageStatusKind.Analyzing;
                item.StatusText = "正在分析";
                try
                {
                    await AnalyzeItemAsync(item, item.ApplyBoundary(settings), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    item.StatusKind = ImageStatusKind.Failed;
                    item.StatusText = "分析失败";
                    EngineStatus = $"{item.FileName} 分析失败：{exception.Message}";
                }
                ProcessedCount++;
                ProgressPercent = ProcessedCount * 60d / selectedItems.Count;
            }
            if (settings.DetectRepeatedDefects)
            {
                var analyzedItems = selectedItems.Where(item => item.Analysis is not null).ToArray();
                if (analyzedItems.Length > 1)
                {
                    EngineStatus = "正在关联批次重复痕迹";
                    ProgressPercent = Math.Max(ProgressPercent, 62d);
                    var enhanced = await Task.Run(
                        () => RepeatedDefectAnalyzer.Enhance(analyzedItems.Select(item => item.Analysis!).ToArray()),
                        cancellationToken);
                    for (var index = 0; index < analyzedItems.Length; index++)
                    {
                        await ApplyAnalysisAsync(analyzedItems[index], enhanced[index], cancellationToken);
                        ProgressPercent = 60d + (index + 1) * 10d / analyzedItems.Length;
                    }
                }
            }
            ProgressPercent = Math.Max(ProgressPercent, 70d);
        ReviewCount = selectedItems.Count(item => !item.ManualOnly && item.ReviewRegions.Any(region => !item.Decisions.ContainsKey(region.Id)));
            RiskMessage = ReviewCount == 0
                ? "真实分析完成，未发现未决内容冲突。批量处理前仍建议抽查红色区域。"
                : $"有 {ReviewCount} 页包含未确认的黄色冲突区域。请选择具体区域后逐一保留或确认去除。";
            EngineStatus = $"批次分析完成：{ProcessedCount} 张，{ReviewCount} 张需要人工确认";
            UpdateReviewSummary();
        }
        finally { IsBusy = false; }
    }

    public async Task<int> AnalyzeAndRemoveAllReviewRegionsAsync(CancellationToken cancellationToken = default)
    {
        var selectedItems = GetSelectedImages();
        try
        {
            await AnalyzeBatchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EngineStatus = "分析已取消；未执行自动去除";
            return 0;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            EngineStatus = "分析已取消；未执行自动去除";
            return 0;
        }

        var failedCount = selectedItems.Count(item => item.StatusKind == ImageStatusKind.Failed);
        if (failedCount > 0)
        {
            EngineStatus = $"有 {failedCount} 张图片分析失败；未执行自动去除";
            return 0;
        }

        int removedCount;
        try
        {
            removedCount = await RemoveAllUnresolvedReviewRegionsAsync(selectedItems, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EngineStatus = "自动去除已取消；已完成的区域决定会保留";
            return 0;
        }
        if (!cancellationToken.IsCancellationRequested) ProgressPercent = 100;
        if (removedCount == 0 && !cancellationToken.IsCancellationRequested)
            EngineStatus = $"批次分析完成：{ProcessedCount} 张，未发现需要批准去除的黄色区域";
        return removedCount;
    }

    public async Task RefreshSelectedAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        if (item is null || IsBusy) return;
        IsBusy = true;
        try
        {
            item.StatusKind = ImageStatusKind.Analyzing;
            item.StatusText = "正在重新分析";
            item.ManualOnly = false;
            item.ClearManualCorrections();
            await AnalyzeItemAsync(item, item.ApplyBoundary(Settings.CreateSnapshot()), cancellationToken);
            var unresolved = item.ReviewRegions.Where(region => !item.Decisions.ContainsKey(region.Id)).ToArray();
            foreach (var region in unresolved)
            {
                item.Decisions[region.Id] = ReviewDecision.Remove;
                region.DecisionText = "已批准去除";
            }
            if (item.Analysis is not null)
            {
                var result = await new DocumentCleanupEngine().RepairAsync(item.Analysis,
                    CreateDecisionSet(item), cancellationToken);
                foreach (var stamp in item.ActiveCloneStamps)
                    ManualPixelEditor.ApplyChange(result.RepairedImage.Pixels, stamp, useAfter: true);
                item.RepairedPreview = _imageCatalog.CreateBitmapSource(result.RepairedImage);
                UpdateItemStatus(item);
                NotifySelectedPreviewChanged();
            }
            EngineStatus = $"已按当前设置重新分析 {item.FileName}";
            UpdateRiskForSelected();
            UpdateReviewSummary();
        }
        finally { IsBusy = false; }
    }

    public async Task DecideSelectedRegionAsync(ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        var selected = SelectedReviewRegion;
        if (item?.Analysis is null || selected is null || IsBusy) return;
        item.Decisions[selected.Id] = decision;
        selected.DecisionText = decision == ReviewDecision.Keep ? "已保留" : "已批准去除";
        IsBusy = true;
        try
        {
            var result = await new DocumentCleanupEngine().RepairAsync(item.Analysis, CreateDecisionSet(item), cancellationToken);
            foreach (var stamp in item.ActiveCloneStamps)
                ManualPixelEditor.ApplyChange(result.RepairedImage.Pixels, stamp, useAfter: true);
            item.RepairedPreview = _imageCatalog.CreateBitmapSource(result.RepairedImage);
            UpdateItemStatus(item);
            NotifySelectedPreviewChanged();
            SelectedReviewRegion = item.ReviewRegions.FirstOrDefault(region => !item.Decisions.ContainsKey(region.Id)) ?? selected;
            EngineStatus = decision == ReviewDecision.Keep ? "已保留当前冲突区域" : "已确认去除当前冲突区域并刷新修复预览";
            UpdateRiskForSelected();
            UpdateReviewSummary();
        }
        finally { IsBusy = false; }
    }

    public async Task<int> RemoveAllUnresolvedReviewRegionsAsync(CancellationToken cancellationToken = default)
        => await RemoveAllUnresolvedReviewRegionsAsync(GetSelectedImages(), cancellationToken);

    private async Task<int> RemoveAllUnresolvedReviewRegionsAsync(
        IReadOnlyCollection<ArchiveImageItem> selectedItems, CancellationToken cancellationToken)
    {
        if (IsBusy) return 0;
        var pendingItems = selectedItems
            .Where(item => item.Analysis is not null && !item.ManualOnly && item.ReviewRegions.Any(region => !item.Decisions.ContainsKey(region.Id)))
            .ToArray();
        if (pendingItems.Length == 0)
        {
            if (!cancellationToken.IsCancellationRequested) ProgressPercent = 100;
            return 0;
        }

        var removedCount = 0;
        var progressStart = Math.Clamp(ProgressPercent, 0, 99);
        IsBusy = true;
        try
        {
            for (var index = 0; index < pendingItems.Length; index++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var item = pendingItems[index];
                var unresolved = item.ReviewRegions.Where(region => !item.Decisions.ContainsKey(region.Id)).ToArray();
                foreach (var region in unresolved)
                {
                    item.Decisions[region.Id] = ReviewDecision.Remove;
                    region.DecisionText = "已批准去除";
                }

                var result = await new DocumentCleanupEngine().RepairAsync(item.Analysis!, CreateDecisionSet(item), cancellationToken);
                foreach (var stamp in item.ActiveCloneStamps)
                    ManualPixelEditor.ApplyChange(result.RepairedImage.Pixels, stamp, useAfter: true);
                item.RepairedPreview = _imageCatalog.CreateBitmapSource(result.RepairedImage);
                removedCount += unresolved.Length;
                UpdateItemStatus(item);
                ProgressPercent = progressStart + (index + 1) * (100d - progressStart) / pendingItems.Length;
                EngineStatus = $"正在批准全部黄色区域 {index + 1}/{pendingItems.Length}：{item.FileName}";
            }

            SelectedReviewRegion = SelectedImage?.ReviewRegions.FirstOrDefault();
            NotifySelectedPreviewChanged();
            UpdateRiskForSelected();
            UpdateReviewSummary();
            EngineStatus = cancellationToken.IsCancellationRequested
                ? "自动去除已取消；已完成的区域决定会保留"
                : $"已批准去除全部未确认区域：{pendingItems.Length} 张，{removedCount} 处";
            if (!cancellationToken.IsCancellationRequested) ProgressPercent = 100;
            return removedCount;
        }
        finally { IsBusy = false; }
    }

    public Task RestoreSelectedToOriginalAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        if (item?.Analysis is null || IsBusy) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        IsBusy = true;
        try
        {
            var original = ImageFileCodec.Load(item.FilePath, item.DpiX, item.DpiY);
            cancellationToken.ThrowIfCancellationRequested();
            item.ManualOnly = true;
            item.Decisions.Clear();
            item.ManualProtectionPixels = null;
            item.ManualRemovalPixels = null;
            item.ClearManualEditHistory();
            foreach (var region in item.ReviewRegions) region.DecisionText = "未确认";
            item.AnalysisPreview = _imageCatalog.CreateBitmapSource(CreateManualOverlay(item));
            item.RepairedPreview = _imageCatalog.CreateBitmapSource(original);
            item.MaskPreview = _imageCatalog.CreateBitmapSource(CreateMaskPreview(item));
            UpdateItemStatus(item);
            SelectedReviewRegion = item.ReviewRegions.FirstOrDefault();
            NotifySelectedPreviewChanged();
            UpdateRiskForSelected();
            UpdateReviewSummary();
            EngineStatus = "已恢复原图，当前图片切换为仅手工处理";
            OnPropertyChanged(nameof(ManualCorrectionSummary));
            return Task.CompletedTask;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreOriginal));
        }
    }

    public async Task ApplyManualEditsAsync(ManualEditMode mode, IReadOnlyList<PixelPoint> points, int brushDiameter,
        PixelPoint? cloneSamplePoint = null, CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        if (item?.Analysis is null || IsBusy || mode == ManualEditMode.None || points.Count == 0) return;
        if (mode == ManualEditMode.CloneStamp)
        {
            if (cloneSamplePoint is null) return;
            var working = await CreateCurrentWorkingImageAsync(item, cancellationToken);
            var cloneChange = ManualPixelEditor.ApplyCloneStamp(working.Pixels, item.PixelWidth, item.PixelHeight,
                cloneSamplePoint.Value, points, brushDiameter);
            if (cloneChange.PixelCount == 0) return;
            item.RecordCloneStamp(cloneChange);
            await RefreshManualEditPreviewAsync(item, "已应用仿制图章", cancellationToken);
            return;
        }

        item.ManualProtectionPixels ??= new byte[item.PixelWidth * item.PixelHeight];
        item.ManualRemovalPixels ??= new byte[item.PixelWidth * item.PixelHeight];

        var kind = mode switch
        {
            ManualEditMode.RemoveBrush => ManualMaskEditKind.Remove,
            _ => ManualMaskEditKind.Protect
        };
        IManualEditChange change;
        if (mode == ManualEditMode.Eraser)
        {
            var underlyingRemovalMask = DocumentCleanupEngine.CreateRepairMask(item.Analysis,
                new ReviewDecisionSet(item.Decisions, disableAutomaticRepair: item.ManualOnly), cancellationToken);
            var beforeStamps = item.ActiveCloneStamps.ToArray();
            var afterStamps = ManualPixelEditor.EraseCloneStamps(beforeStamps,
                item.PixelWidth, item.PixelHeight, points, brushDiameter);
            var maskChange = ManualMaskEditor.ApplyRestoreBrush(item.ManualProtectionPixels, item.ManualRemovalPixels,
                item.PixelWidth, item.PixelHeight, points, brushDiameter, underlyingRemovalMask);
            change = new ManualRestoreChange(maskChange, beforeStamps, afterStamps);
            item.ReplaceCloneStamps(afterStamps);
        }
        else
        {
            change = ManualMaskEditor.ApplyBrush(item.ManualProtectionPixels, item.ManualRemovalPixels,
                item.PixelWidth, item.PixelHeight, kind, points, brushDiameter);
        }
        if (change.PixelCount == 0) return;
        item.RecordManualEdit(change);
        await RefreshManualEditPreviewAsync(item, mode == ManualEditMode.Eraser ? "已恢复原图内容" : "已应用人工修正", cancellationToken);
    }

    public async Task UndoManualEditAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        if (item?.Analysis is null || IsBusy) return;
        var change = item.TakeUndoManualEdit();
        if (change is null) return;
        switch (change)
        {
            case ManualRestoreChange restoreChange:
                ManualMaskEditor.Undo(item.ManualProtectionPixels!, item.ManualRemovalPixels!,
                    item.PixelWidth, item.PixelHeight, restoreChange.MaskChange);
                item.ReplaceCloneStamps(restoreChange.BeforeStamps);
                break;
            case ManualMaskChange maskChange:
                ManualMaskEditor.Undo(item.ManualProtectionPixels!, item.ManualRemovalPixels!, item.PixelWidth, item.PixelHeight, maskChange);
                break;
            case ManualPixelChange pixelChange:
                item.RemoveCloneStamp(pixelChange);
                break;
        }
        await RefreshManualEditPreviewAsync(item, "已撤销人工修正", cancellationToken);
    }

    public async Task RedoManualEditAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedImage;
        if (item?.Analysis is null || IsBusy) return;
        var change = item.TakeRedoManualEdit();
        if (change is null) return;
        switch (change)
        {
            case ManualRestoreChange restoreChange:
                ManualMaskEditor.Redo(item.ManualProtectionPixels!, item.ManualRemovalPixels!,
                    item.PixelWidth, item.PixelHeight, restoreChange.MaskChange);
                item.ReplaceCloneStamps(restoreChange.AfterStamps);
                break;
            case ManualMaskChange maskChange:
                ManualMaskEditor.Redo(item.ManualProtectionPixels!, item.ManualRemovalPixels!, item.PixelWidth, item.PixelHeight, maskChange);
                break;
            case ManualPixelChange pixelChange:
                item.RestoreCloneStamp(pixelChange);
                break;
        }
        await RefreshManualEditPreviewAsync(item, "已重做人工修正", cancellationToken);
    }

    public void SetCurrentToolColor(Color color)
    {
        switch (ManualEditMode)
        {
            case ManualEditMode.RemoveBrush:
                _removeToolColor = color;
                OnPropertyChanged(nameof(RemoveToolBrush));
                break;
            case ManualEditMode.Eraser:
                _eraserToolColor = color;
                OnPropertyChanged(nameof(EraserToolBrush));
                break;
            default:
                _protectToolColor = color;
                OnPropertyChanged(nameof(ProtectToolBrush));
                break;
        }
        OnPropertyChanged(nameof(CurrentManualToolBrush));
        RefreshSelectedDisplayLayers();
    }

    private async Task RefreshManualEditPreviewAsync(ArchiveImageItem item, string status, CancellationToken cancellationToken)
    {
        IsBusy = true;
        OnPropertyChanged(nameof(CanUseManualTools));
        try
        {
            var result = await new DocumentCleanupEngine().RepairAsync(item.Analysis!, CreateDecisionSet(item), cancellationToken);
            foreach (var stamp in item.ActiveCloneStamps)
                ManualPixelEditor.ApplyChange(result.RepairedImage.Pixels, stamp, useAfter: true);
            item.AnalysisPreview = _imageCatalog.CreateBitmapSource(CreateManualOverlay(item));
            item.RepairedPreview = _imageCatalog.CreateBitmapSource(result.RepairedImage);
            item.MaskPreview = _imageCatalog.CreateBitmapSource(CreateMaskPreview(item));
            NotifySelectedPreviewChanged();
            OnPropertyChanged(nameof(ManualCorrectionSummary));
            EngineStatus = $"{status}：{ManualCorrectionSummary}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUseManualTools));
            OnPropertyChanged(nameof(CanUndoManualEdit));
            OnPropertyChanged(nameof(CanRedoManualEdit));
        }
    }

    public async Task<BatchAuditReport> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var selectedItems = GetSelectedImages();
        if (selectedItems.Count == 0) throw new CleanupValidationException("请先勾选至少一张需要导出的图片。");
        if (IsBusy) throw new InvalidOperationException("当前已有任务正在运行。");
        var settings = Settings.CreateSnapshot();
        IsBusy = true;
        ProcessedCount = 0;
        ProgressPercent = 0;
        try
        {
            var decisions = selectedItems.ToDictionary(item => item.FilePath, CreateDecisionSet, StringComparer.OrdinalIgnoreCase);
            var cloneStamps = selectedItems.ToDictionary(item => item.FilePath,
                item => (IReadOnlyList<ManualPixelChange>)item.ActiveCloneStamps.ToArray(), StringComparer.OrdinalIgnoreCase);
            var relativeOutputPaths = selectedItems.ToDictionary(item => item.FilePath,
                item => item.RelativeOutputPath, StringComparer.OrdinalIgnoreCase);
            var settingsByFile = selectedItems.ToDictionary(item => item.FilePath,
                item => item.ApplyBoundary(settings), StringComparer.OrdinalIgnoreCase);
            var progress = new Progress<BatchProgress>(value =>
            {
                ProcessedCount = Math.Min(value.Completed, selectedItems.Count);
                ProgressPercent = Math.Max(ProgressPercent, value.Percent);
                EngineStatus = $"批量处理 {value.Completed}/{value.Total}：{value.FileName} · {value.Message}";
            });
            var processor = new BatchProcessor(() => new DocumentCleanupEngine());
            var report = await processor.ProcessAsync(new BatchRequest
            {
                SourceFiles = selectedItems.Select(item => item.FilePath).ToArray(), OutputDirectory = OutputDirectory,
                Settings = settings, DecisionsByFile = decisions, CloneStampsByFile = cloneStamps,
                RelativeOutputPathsByFile = relativeOutputPaths, SettingsByFile = settingsByFile,
                RotationByFile = selectedItems.ToDictionary(item => item.FilePath,
                    item => item.IsPdfPage ? 0 : item.RotationQuarterTurns, StringComparer.OrdinalIgnoreCase)
            }, progress, cancellationToken);
            EngineStatus = $"批量处理完成：成功 {report.SuccessCount}，失败 {report.FailedCount}";
            ProgressPercent = 100;
            return report;
        }
        finally { IsBusy = false; }
    }

    public IReadOnlyList<string> GetPlannedOutputPaths(string outputDirectory)
    {
        var settings = Settings.CreateSnapshot();
        return GetSelectedImages()
            .Select(item => SafeImageWriter.GetDestinationPath(
                item.FilePath, outputDirectory, settings, item.RelativeOutputPath))
            .ToArray();
    }

    private async Task AnalyzeItemAsync(ArchiveImageItem item, CleanupSettingsSnapshot settings, CancellationToken cancellationToken)
    {
        var lastReportedPercent = -1d;
        var lastReportedStage = string.Empty;
        var progress = new Progress<ProcessingProgress>(value =>
        {
            if (value.Stage == lastReportedStage && value.Percent - lastReportedPercent < 5)
                return;
            lastReportedStage = value.Stage;
            lastReportedPercent = value.Percent;
            EngineStatus = $"{item.FileName} · {value.Message}";
        });
        var engine = new DocumentCleanupEngine();
        var analysis = await engine.AnalyzeAsync(new CleanupRequest(item.FilePath, item.DpiX, item.DpiY, settings), progress, cancellationToken);
        await ApplyAnalysisAsync(item, analysis, cancellationToken);
    }

    private async Task ApplyAnalysisAsync(ArchiveImageItem item, CleanupAnalysisResult analysis, CancellationToken cancellationToken)
    {
        item.Analysis = analysis;
        var currentIds = analysis.Regions.Where(region => region.Status == RegionStatus.NeedsReview).Select(region => region.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in item.Decisions.Keys.Where(key => !currentIds.Contains(key)).ToArray()) item.Decisions.Remove(stale);
        item.ReviewRegions.Clear();
        foreach (var region in analysis.Regions.Where(region => region.Status == RegionStatus.NeedsReview))
        {
            var view = new ReviewRegionItem { Region = region };
            if (item.Decisions.TryGetValue(region.Id, out var decision)) view.DecisionText = decision == ReviewDecision.Keep ? "已保留" : "已批准去除";
            item.ReviewRegions.Add(view);
        }
        var engine = new DocumentCleanupEngine();
        var repair = await engine.RepairAsync(analysis, CreateDecisionSet(item), cancellationToken);
        var previews = await Task.Run(() =>
        {
            foreach (var stamp in item.ActiveCloneStamps)
                ManualPixelEditor.ApplyChange(repair.RepairedImage.Pixels, stamp, useAfter: true);
            return (
                Analysis: _imageCatalog.CreateBitmapSource(CreateManualOverlay(item)),
                Repaired: _imageCatalog.CreateBitmapSource(repair.RepairedImage),
                Mask: _imageCatalog.CreateBitmapSource(CreateMaskPreview(item)));
        }, cancellationToken);
        item.AnalysisPreview = previews.Analysis;
        item.RepairedPreview = previews.Repaired;
        item.MaskPreview = previews.Mask;
        UpdateItemStatus(item);
        if (ReferenceEquals(item, SelectedImage))
        {
            SelectedReviewRegion = item.ReviewRegions.FirstOrDefault(region => !item.Decisions.ContainsKey(region.Id)) ?? item.ReviewRegions.FirstOrDefault();
            OnPropertyChanged(nameof(ReviewRegions));
            NotifySelectedPreviewChanged();
            OnPropertyChanged(nameof(CanUseManualTools));
            OnPropertyChanged(nameof(ManualCorrectionSummary));
            OnPropertyChanged(nameof(CanRestoreOriginal));
        }
    }

    private static void UpdateItemStatus(ArchiveImageItem item)
    {
        if (item.ManualOnly)
        {
            item.StatusKind = ImageStatusKind.Completed;
            item.StatusText = "仅手工处理";
            return;
        }
        var unresolved = item.ReviewRegions.Count(region => !item.Decisions.ContainsKey(region.Id));
        item.StatusKind = unresolved > 0 ? ImageStatusKind.Review : ImageStatusKind.Completed;
        item.StatusText = unresolved > 0 ? $"{unresolved} 处需要确认" : item.Analysis is null ? "等待处理" : $"检测完成 · {item.Analysis.AutoRemoveMask.ActivePixelCount:N0} px";
    }

    private void UpdateRiskForSelected()
    {
        if (SelectedImage?.Analysis is null) return;
        if (SelectedImage.ManualOnly)
        {
            RiskMessage = "当前页已切换为仅手工处理，自动去除和待确认区域处理均已停用。";
            return;
        }
        var unresolved = SelectedImage.ReviewRegions.Count(region => !SelectedImage.Decisions.ContainsKey(region.Id));
        RiskMessage = unresolved == 0
            ? $"当前页有 {SelectedImage.Analysis.Regions.Count(region => region.Status == RegionStatus.AutoRemove)} 个自动清理区域，没有未决冲突。"
            : $"当前页有 {unresolved} 个未决冲突。黄色区域不会自动修复，请在下方选择具体区域后作出决定。";
    }

    private void UpdateReviewSummary()
    {
        ReviewCount = GetSelectedImages().Count(item => !item.ManualOnly && item.ReviewRegions.Any(region => !item.Decisions.ContainsKey(region.Id)));
        OnPropertyChanged(nameof(UnresolvedReviewRegionCount));
        OnPropertyChanged(nameof(CanRemoveAllReviewRegions));
    }

    private void NotifySelectedPreviewChanged()
    {
        _rotatedPreviews.Clear();
        OnPropertyChanged(nameof(SelectedPreview));
        OnPropertyChanged(nameof(SelectedAnalysisPreview));
        OnPropertyChanged(nameof(SelectedRepairedPreview));
        OnPropertyChanged(nameof(SelectedMaskPreview));
        OnPropertyChanged(nameof(SelectedReviewRegionText));
    }

    private static ReviewDecisionSet CreateDecisionSet(ArchiveImageItem item) =>
        new(item.Decisions, item.CreateManualProtectionMask(), item.CreateManualRemovalMask(), item.ManualOnly);

    private static async Task<ImageBuffer> CreateCurrentWorkingImageAsync(ArchiveImageItem item, CancellationToken cancellationToken)
    {
        var result = await new DocumentCleanupEngine().RepairAsync(item.Analysis!, CreateDecisionSet(item), cancellationToken);
        foreach (var stamp in item.ActiveCloneStamps)
            ManualPixelEditor.ApplyChange(result.RepairedImage.Pixels, stamp, useAfter: true);
        return result.RepairedImage;
    }

    private ImageBuffer CreateManualOverlay(ArchiveImageItem item)
    {
        var analysis = item.Analysis ?? throw new InvalidOperationException("当前图片尚未分析。");
        var baseImage = item.ManualOnly ? ImageFileCodec.Load(item.FilePath, item.DpiX, item.DpiY) : analysis.OverlayPreview;
        if (item.ManualProtectionPixels is null && item.ManualRemovalPixels is null) return baseImage;
        var pixels = (byte[])baseImage.Pixels.Clone();
        for (var index = 0; index < analysis.Width * analysis.Height; index++)
        {
            if (item.ManualRemovalPixels is { } manualRemoval && manualRemoval[index] != 0)
                BlendOverlay(pixels, index, _removeToolColor, 0.58);
            if (item.ManualProtectionPixels is { } manualProtection && manualProtection[index] != 0)
                BlendOverlay(pixels, index, _protectToolColor, 0.58);
        }
        return new ImageBuffer(baseImage.Width, baseImage.Height, pixels, baseImage.DpiX, baseImage.DpiY, baseImage.Extension);
    }

    private ImageBuffer CreateMaskPreview(ArchiveImageItem item)
    {
        var analysis = item.Analysis ?? throw new InvalidOperationException("当前图片尚未分析。");
        var pixels = new byte[analysis.Width * analysis.Height * 3];
        for (var index = 0; index < analysis.Width * analysis.Height; index++)
        {
            SetPixel(pixels, index, Color.FromRgb(31, 37, 40));
            if (analysis.ProtectionMask.Pixels[index] != 0) SetPixel(pixels, index, Color.FromRgb(41, 135, 92));
            if (analysis.AutoRemoveMask.Pixels[index] != 0) SetPixel(pixels, index, Color.FromRgb(212, 71, 71));
            if (analysis.ReviewMask.Pixels[index] != 0) SetPixel(pixels, index, Color.FromRgb(213, 154, 36));
            if (item.ManualRemovalPixels is { } manualRemoval && manualRemoval[index] != 0)
                SetPixel(pixels, index, _removeToolColor);
            if (item.ManualProtectionPixels is { } manualProtection && manualProtection[index] != 0)
                SetPixel(pixels, index, _protectToolColor);
        }
        return new ImageBuffer(analysis.Width, analysis.Height, pixels, analysis.DpiX, analysis.DpiY, analysis.Extension);
    }

    private static void BlendOverlay(byte[] pixels, int pixelIndex, byte blue, byte green, byte red, double alpha)
    {
        var offset = pixelIndex * 3;
        pixels[offset] = (byte)(pixels[offset] * (1 - alpha) + blue * alpha);
        pixels[offset + 1] = (byte)(pixels[offset + 1] * (1 - alpha) + green * alpha);
        pixels[offset + 2] = (byte)(pixels[offset + 2] * (1 - alpha) + red * alpha);
    }

    private static void BlendOverlay(byte[] pixels, int pixelIndex, Color color, double alpha) =>
        BlendOverlay(pixels, pixelIndex, color.B, color.G, color.R, alpha);

    private static void SetPixel(byte[] pixels, int pixelIndex, Color color)
    {
        var offset = pixelIndex * 3;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
    }

    private void RefreshSelectedDisplayLayers()
    {
        var item = SelectedImage;
        if (item?.Analysis is null) return;
        item.AnalysisPreview = _imageCatalog.CreateBitmapSource(CreateManualOverlay(item));
        item.MaskPreview = _imageCatalog.CreateBitmapSource(CreateMaskPreview(item));
        NotifySelectedPreviewChanged();
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static long CountActive(byte[]? pixels) => pixels?.LongCount(value => value != 0) ?? 0;

    private void SubscribeToRoot(CatalogTreeNode root) => root.CheckedStateChanged += TreeRoot_CheckedStateChanged;

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupSettings.ProtectBrushDiameterPixels)
            or nameof(CleanupSettings.RemoveBrushDiameterPixels)
            or nameof(CleanupSettings.CloneStampDiameterPixels)
            or nameof(CleanupSettings.EraserDiameterPixels)
            or nameof(CleanupSettings.ManualBrushDiameterPixels))
            OnPropertyChanged(nameof(BrushDiameterPixels));
    }

    private void TreeRoot_CheckedStateChanged(object? sender, EventArgs e)
    {
        NotifySelectionSummary();
        UpdateReviewSummary();
    }

    private List<ArchiveImageItem> GetSelectedImages() => TreeRoots
        .SelectMany(EnumerateImageNodes)
        .Where(node => node.IsChecked == true)
        .Select(node => node.Image)
        .ToList();

    private bool IsSelectedForBatch(ArchiveImageItem item) => TreeRoots
        .SelectMany(EnumerateImageNodes)
        .Any(node => ReferenceEquals(node.Image, item) && node.IsChecked == true);

    private static IEnumerable<ImageTreeNode> EnumerateImageNodes(CatalogTreeNode node)
    {
        if (node is ImageTreeNode imageNode)
        {
            yield return imageNode;
            yield break;
        }
        foreach (var child in ((FolderTreeNode)node).Children)
            foreach (var image in EnumerateImageNodes(child)) yield return image;
    }

    private static void SetExpandedRecursive(FolderTreeNode folder, bool isExpanded)
    {
        folder.IsExpanded = isExpanded;
        foreach (var child in folder.Children.OfType<FolderTreeNode>()) SetExpandedRecursive(child, isExpanded);
    }

    private static void RemoveImagesRecursive(FolderTreeNode folder, HashSet<ArchiveImageItem> removed)
    {
        foreach (var child in folder.Children.ToArray())
        {
            if (child is ImageTreeNode imageNode && removed.Contains(imageNode.Image)) folder.Children.Remove(child);
            else if (child is FolderTreeNode childFolder)
            {
                RemoveImagesRecursive(childFolder, removed);
                if (childFolder.Children.Count == 0) folder.Children.Remove(childFolder);
            }
        }
        folder.RefreshCheckedStateFromChildren();
        OnFolderContentsChanged(folder);
    }

    private static void OnFolderContentsChanged(FolderTreeNode folder)
    {
        folder.RaiseContentSummaryChanged();
    }

    private static string CreateUniqueRootPrefix(string rootName, HashSet<string> usedPrefixes)
    {
        var prefix = string.IsNullOrWhiteSpace(rootName) ? "文件夹" : rootName;
        if (usedPrefixes.Add(prefix)) return prefix;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{prefix}_{suffix}";
            if (usedPrefixes.Add(candidate)) return candidate;
        }
    }

    private void NotifyCollectionSummary()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedIndexText));
        OnPropertyChanged(nameof(DirectoryPositionText));
        NotifySelectionSummary();
    }

    private void NotifySelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanRunSelectedBatch));
        OnPropertyChanged(nameof(UnresolvedReviewRegionCount));
        OnPropertyChanged(nameof(CanRemoveAllReviewRegions));
    }

    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        settingsWindow.DataContext = new SettingsViewModel(_settingsManager, settingsWindow);

        if (settingsWindow.ShowDialog() == true)
        {
            _currentSettings = _settingsManager.Load();
            OnPropertyChanged(nameof(HorizontalAssistKey));
            OnPropertyChanged(nameof(VerticalAssistKey));
        }
    }
}
