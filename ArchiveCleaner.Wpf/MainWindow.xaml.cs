using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using Shape = System.Windows.Shapes.Shape;
using Polyline = System.Windows.Shapes.Polyline;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;
using ArchiveCleaner.Wpf.ViewModels;
using Microsoft.Win32;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Wpf;

public partial class MainWindow : Window
{
    private enum PreviewMode { SideBySide, Compare, MaskOnly }
    private enum ManualConstraint { None, Horizontal, Vertical }

    private readonly MainViewModel _viewModel = new();
    private readonly SettingsPersistenceService _settingsPersistence = new();
    private CancellationTokenSource? _operationCancellation;
    private readonly List<System.Windows.Point> _manualPoints = [];
    private System.Windows.Point _manualStart;
    private Shape? _manualGuide;
    private bool _isManualDrawing;
    private ManualConstraint _manualConstraint;
    private Line? _constraintGuide;
    private System.Windows.Point? _lastCrosshairPoint;
    private PixelPoint? _cloneSamplePoint;
    private readonly Ellipse _cloneSampleCursorEllipse = new()
    {
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        Fill = Brushes.Transparent,
        Stroke = Brushes.DodgerBlue,
        StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 3, 2 }
    };
    private PreviewMode _previewMode = PreviewMode.SideBySide;
    private double _previewZoomPercent = 100;
    private double _compareDividerRatio = 0.5;
    private bool _isDraggingCompareDivider;
    private bool _isSynchronizingScroll;
    private EdgeSide? _draggingCandidateSide;
    private Canvas? _candidateDragCanvas;
    private UIElement? _candidateDragElement;
    private ArchiveImageItem? _observedSelectedImage;
    private readonly Dictionary<(Canvas Canvas, EdgeSide Side), Line> _candidateLines = [];
    private readonly Dictionary<(Canvas Canvas, EdgeSide Side), ShapeRectangle> _candidateHandles = [];

    public MainWindow()
    {
        InitializeComponent();
        Panel.SetZIndex(_cloneSampleCursorEllipse, 10);
        ManualDrawingCanvas.Children.Add(_cloneSampleCursorEllipse);
        DataContext = _viewModel;
        if (!_settingsPersistence.TryLoad(_viewModel.Settings) && File.Exists(SettingsPersistenceService.DefaultPath))
            _viewModel.EngineStatus = "上次保存的参数无效，已使用系统默认值";
        _viewModel.InitializeImageBoundariesFromDefaults();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Settings.PropertyChanged += Settings_PropertyChanged;
        _observedSelectedImage = _viewModel.SelectedImage;
        if (_observedSelectedImage is not null) _observedSelectedImage.PropertyChanged += SelectedImage_PropertyChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedImage))
        {
            if (_observedSelectedImage is not null) _observedSelectedImage.PropertyChanged -= SelectedImage_PropertyChanged;
            _observedSelectedImage = _viewModel.SelectedImage;
            if (_observedSelectedImage is not null) _observedSelectedImage.PropertyChanged += SelectedImage_PropertyChanged;
            _cloneSamplePoint = null;
            _cloneSampleCursorEllipse.Visibility = Visibility.Collapsed;
            ResetManualDrawingState();
        }
        if (e.PropertyName is nameof(MainViewModel.SelectedImage) or nameof(MainViewModel.SelectedAnalysisPreview) or
            nameof(MainViewModel.SelectedRepairedPreview) or nameof(MainViewModel.SelectedMaskPreview))
            Dispatcher.BeginInvoke(UpdatePreviewLayout);
    }

    private void SelectedImage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ArchiveImageItem && IsBoundaryProperty(e.PropertyName) && _draggingCandidateSide is null)
            Dispatcher.BeginInvoke(RedrawPreviewOverlays, DispatcherPriority.Render);
    }

    private static bool IsBoundaryProperty(string? propertyName) => propertyName is nameof(ArchiveImageItem.CleanLeft)
        or nameof(ArchiveImageItem.CleanRight) or nameof(ArchiveImageItem.CleanTop) or nameof(ArchiveImageItem.CleanBottom)
        or nameof(ArchiveImageItem.LeftMarginMm) or nameof(ArchiveImageItem.RightMarginMm)
        or nameof(ArchiveImageItem.TopMarginMm) or nameof(ArchiveImageItem.BottomMarginMm);

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupSettings.ShowCandidateGuides))
        {
            if (!_viewModel.Settings.ShowCandidateGuides)
                StopCandidateDrag();
            SetCandidateGuideVisibility();
        }
        if (e.PropertyName is nameof(CleanupSettings.CleanLeft) or nameof(CleanupSettings.CleanRight)
            or nameof(CleanupSettings.CleanTop) or nameof(CleanupSettings.CleanBottom)
            or nameof(CleanupSettings.LeftMarginMm) or nameof(CleanupSettings.RightMarginMm)
            or nameof(CleanupSettings.TopMarginMm) or nameof(CleanupSettings.BottomMarginMm)
            or nameof(CleanupSettings.ShowRulers) or nameof(CleanupSettings.ShowCrosshair)
            or nameof(CleanupSettings.ShowCandidateGuides))
            if (_draggingCandidateSide is null)
                Dispatcher.BeginInvoke(RedrawPreviewOverlays, DispatcherPriority.Render);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        CancelCurrentOperation();
        try
        {
            _settingsPersistence.Save(_viewModel.Settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.EngineStatus = $"自动保存参数失败：{exception.Message}";
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择档案扫描图片",
            Filter = "支持的图片|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp|JPEG 图片|*.jpg;*.jpeg|TIFF 图片|*.tif;*.tiff|PNG 图片|*.png|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewModel.AddFiles(dialog.FileNames);
        }
        catch (Exception exception)
        {
            ShowError("部分图片无法读取", exception);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择档案扫描图片文件夹",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewModel.LoadDirectories(dialog.FolderNames, replaceExisting: true);
        }
        catch (Exception exception)
        {
            ShowError("文件夹中的图片无法读取", exception);
        }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存清理方案",
            Filter = "档案净页方案|*.json",
            FileName = "四边保守清理.json",
            AddExtension = true,
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(_viewModel.Settings, options);
            await File.WriteAllTextAsync(dialog.FileName, json);
            _viewModel.EngineStatus = $"方案已保存：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            ShowError("清理方案保存失败", exception);
        }
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载清理方案",
            Filter = "档案净页方案|*.json|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var loaded = JsonSerializer.Deserialize<CleanupSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("方案文件内容为空。");
            loaded.CreateSnapshot();
            _viewModel.Settings.CopyFrom(loaded);
            _viewModel.EngineStatus = $"方案已加载：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            ShowError("清理方案加载失败", exception);
        }
    }

    private void DetectionPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string preset }) _viewModel.Settings.ApplyDetectionPreset(preset);
    }

    private void ResetDetection_Click(object sender, RoutedEventArgs e) => _viewModel.Settings.ResetDetectionDefaults();

    private void RestoreAllDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "恢复全部系统默认参数？\n\n四边范围、检测灵敏度、内容保护、修复方式和输出设置都会重置。图片队列和人工修正不会改变。",
            "恢复默认参数", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        _viewModel.Settings.ResetAllDefaults();
        _viewModel.InitializeImageBoundariesFromDefaults();
        _viewModel.EngineStatus = "已恢复全部系统默认参数；请重新生成预览";
    }

    private void ApplyBoundaryToAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ApplySelectedBoundaryToAll())
        {
            MessageBox.Show(this, "请先选择一张图片。", "没有选中的图片", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _settingsPersistence.Save(_viewModel.Settings);
            _viewModel.EngineStatus = $"已将 {_viewModel.SelectedImage?.FileName} 的四边边界应用到全部 {_viewModel.Images.Count} 张图片，并保存为默认值；请重新分析";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.EngineStatus = $"边界已应用到全部图片，但默认值保存失败：{exception.Message}";
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCount == 0)
        {
            MessageBox.Show(this, "请先在左侧目录中勾选至少一张图片。", "没有已选图片", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var operation = BeginCancelableOperation();
        try
        {
            await _viewModel.AnalyzeAndRemoveAllReviewRegionsAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.EngineStatus = "分析已取消；没有写出或修改源图片";
        }
        catch (CleanupValidationException exception)
        {
            MessageBox.Show(this, exception.Message, "设置值无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            ShowError("批次分析失败", exception);
        }
        finally { EndCancelableOperation(operation); }
    }

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanRunSelectedBatch || !TrySelectOutputDirectory()) return;
        IReadOnlyList<string> plannedOutputs;
        try
        {
            plannedOutputs = _viewModel.GetPlannedOutputPaths(_viewModel.OutputDirectory);
        }
        catch (CleanupValidationException exception)
        {
            MessageBox.Show(this, exception.Message, "无法开始处理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var duplicateOutputs = plannedOutputs
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => Path.GetFileName(group.Key))
            .ToArray();
        if (duplicateOutputs.Length > 0)
        {
            MessageBox.Show(this,
                $"当前输出格式会让多个源文件生成同名结果，无法安全导出：\n\n{string.Join("\n", duplicateOutputs.Take(8))}",
                "输出文件名冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existingOutputs = plannedOutputs.Where(File.Exists).ToArray();
        if (existingOutputs.Length > 0)
        {
            var preview = string.Join("\n", existingOutputs.Take(8).Select(Path.GetFileName));
            var suffix = existingOutputs.Length > 8 ? $"\n……及其他 {existingOutputs.Length - 8} 个文件" : string.Empty;
            var overwrite = MessageBox.Show(this,
                $"选定输出文件夹中已有 {existingOutputs.Length} 个同名结果文件。\n\n{preview}{suffix}\n\n继续将覆盖这些文件，是否继续？",
                "确认覆盖输出文件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (overwrite != MessageBoxResult.Yes) return;
        }

        var operation = BeginCancelableOperation();
        try
        {
            var report = await _viewModel.ProcessBatchAsync(operation.Token);
            MessageBox.Show(this,
                $"结果导出完成。\n\n成功：{report.SuccessCount}\n失败：{report.FailedCount}\n输出目录：{report.OutputDirectory}",
                "导出完成", MessageBoxButton.OK, report.FailedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            _viewModel.EngineStatus = "批量处理已取消；完整写出的结果已保留，临时文件已清理";
        }
        catch (CleanupValidationException exception)
        {
            MessageBox.Show(this, exception.Message, "无法开始处理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            ShowError("结果导出失败", exception);
        }
        finally { EndCancelableOperation(operation); }
    }

    private async void KeepRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedImage is null)
        {
            return;
        }

        await RunCancelableAsync(
            token => _viewModel.DecideSelectedRegionAsync(ReviewDecision.Keep, token),
            "保留区域操作已取消", "保存区域决定失败");
    }

    private async void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedImage is null)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "请确认该区域只包含污渍、装订孔或扫描阴影，不包含手写批注、印章和正文。\n\n确定允许后续处理引擎修复该区域吗？",
            "确认去除区域",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCancelableAsync(
            token => _viewModel.DecideSelectedRegionAsync(ReviewDecision.Remove, token),
            "确认去除操作已取消", "应用区域决定失败");
    }

    private async void RemoveAllReviewRegions_Click(object sender, RoutedEventArgs e)
    {
        var regionCount = _viewModel.UnresolvedReviewRegionCount;
        if (regionCount == 0) return;
        var result = MessageBox.Show(
            this,
            $"将批准去除已勾选图片中全部 {regionCount} 个未确认黄色区域。\n\n已经明确标记为“保留”的区域不会改变。确认后将立即重新生成这些图片的修复预览。是否继续？",
            "批准去除全部黄色区域",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        var operation = BeginCancelableOperation();
        try
        {
            await _viewModel.RemoveAllUnresolvedReviewRegionsAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.EngineStatus = "批量批准去除已取消；已完成的区域决定会保留";
        }
        catch (Exception exception)
        {
            ShowError("批量批准去除失败", exception);
        }
        finally { EndCancelableOperation(operation); }
    }

    private async void RefreshPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedImage is null)
        {
            MessageBox.Show(this, "请先在左侧文件队列中选择一张图片。", "没有选中图片", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var operation = BeginCancelableOperation();
        try
        {
            await _viewModel.RefreshSelectedAsync(operation.Token);
        }
        catch (OperationCanceledException) { _viewModel.EngineStatus = "重新预览已取消"; }
        catch (CleanupValidationException exception) { MessageBox.Show(this, exception.Message, "设置值无效", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception exception) { ShowError("重新生成预览失败", exception); }
        finally { EndCancelableOperation(operation); }
    }

    private bool TrySelectOutputDirectory()
    {
        var configuredOutput = string.IsNullOrWhiteSpace(_viewModel.OutputDirectory)
            ? null
            : Path.GetFullPath(_viewModel.OutputDirectory);
        var currentOutput = configuredOutput is not null && Directory.Exists(configuredOutput)
            ? configuredOutput
            : configuredOutput is not null
                ? Directory.GetParent(configuredOutput)?.FullName ?? AppContext.BaseDirectory
                : GetSourceParentDirectory();
        if (!Directory.Exists(currentOutput)) currentOutput = AppContext.BaseDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "选择批量输出文件夹",
            Multiselect = false,
            InitialDirectory = currentOutput,
            DefaultDirectory = currentOutput
        };
        if (dialog.ShowDialog(this) != true) return false;
        _viewModel.OutputDirectory = dialog.FolderName;
        return true;
    }

    private string GetSourceParentDirectory()
    {
        var sourceDirectory = _viewModel.SelectedImage is null
            ? null
            : Path.GetDirectoryName(_viewModel.SelectedImage.FilePath);
        return sourceDirectory is null
            ? AppContext.BaseDirectory
            : Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory;
    }

    private void ProtectBrush_Click(object sender, RoutedEventArgs e) => SetManualMode(ManualEditMode.ProtectBrush, (ToggleButton)sender);
    private void RemoveBrush_Click(object sender, RoutedEventArgs e) => SetManualMode(ManualEditMode.RemoveBrush, (ToggleButton)sender);
    private void CloneStamp_Click(object sender, RoutedEventArgs e) => SetManualMode(ManualEditMode.CloneStamp, (ToggleButton)sender);
    private void Eraser_Click(object sender, RoutedEventArgs e) => SetManualMode(ManualEditMode.Eraser, (ToggleButton)sender);

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedImage is null) return;
        await RunCancelableAsync(_viewModel.RestoreSelectedToOriginalAsync,
            "恢复原图操作已取消", "恢复原图失败");
    }

    private void SetManualMode(ManualEditMode mode, ToggleButton selected)
    {
        ResetManualDrawingState();
        var enabled = selected.IsChecked == true;
        ProtectBrushButton.IsChecked = ReferenceEquals(selected, ProtectBrushButton) && enabled;
        RemoveBrushButton.IsChecked = ReferenceEquals(selected, RemoveBrushButton) && enabled;
        CloneStampButton.IsChecked = ReferenceEquals(selected, CloneStampButton) && enabled;
        EraserButton.IsChecked = ReferenceEquals(selected, EraserButton) && enabled;
        if (!enabled || mode != ManualEditMode.CloneStamp) _cloneSamplePoint = null;
        _viewModel.ManualEditMode = enabled ? mode : ManualEditMode.None;
        if (!enabled) BrushCursorEllipse.Visibility = Visibility.Collapsed;
        UpdateCloneSampleCursor();
    }

    private void ResetManualDrawingState()
    {
        _isManualDrawing = false;
        _manualConstraint = ManualConstraint.None;
        _manualPoints.Clear();
        _manualGuide = null;
        RemoveConstraintGuide();
        if (ManualDrawingCanvas.IsMouseCaptured) ManualDrawingCanvas.ReleaseMouseCapture();
        for (var index = ManualDrawingCanvas.Children.Count - 1; index >= 0; index--)
            if (ManualDrawingCanvas.Children[index] is Shape shape &&
                !ReferenceEquals(shape, BrushCursorEllipse) && !ReferenceEquals(shape, _cloneSampleCursorEllipse))
                ManualDrawingCanvas.Children.RemoveAt(index);
    }

    private void ToolColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        var color = (Color)ColorConverter.ConvertFromString(value);
        _viewModel.SetCurrentToolColor(color);
        if (BrushCursorEllipse.Visibility == Visibility.Visible)
        {
            var left = Canvas.GetLeft(BrushCursorEllipse);
            var top = Canvas.GetTop(BrushCursorEllipse);
            if (!double.IsNaN(left) && !double.IsNaN(top))
                UpdateBrushCursor(new System.Windows.Point(left + BrushCursorEllipse.Width / 2, top + BrushCursorEllipse.Height / 2));
        }
    }

    private void PreviewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender == SideBySideModeButton) _previewMode = PreviewMode.SideBySide;
        else if (sender == CompareModeButton) _previewMode = PreviewMode.Compare;
        else _previewMode = PreviewMode.MaskOnly;

        SideBySideModeButton.IsChecked = _previewMode == PreviewMode.SideBySide;
        CompareModeButton.IsChecked = _previewMode == PreviewMode.Compare;
        MaskOnlyModeButton.IsChecked = _previewMode == PreviewMode.MaskOnly;
        SideBySidePreviewPanel.Visibility = _previewMode == PreviewMode.SideBySide ? Visibility.Visible : Visibility.Collapsed;
        SliderComparePreviewPanel.Visibility = _previewMode == PreviewMode.Compare ? Visibility.Visible : Visibility.Collapsed;
        MaskOnlyPreviewPanel.Visibility = _previewMode == PreviewMode.MaskOnly ? Visibility.Visible : Visibility.Collapsed;
        ManualDrawingCanvas.IsHitTestVisible = _previewMode == PreviewMode.SideBySide;
        BrushCursorEllipse.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(UpdatePreviewLayout, DispatcherPriority.Loaded);
    }

    private void PreviewZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _previewZoomPercent = e.NewValue;
        if (PreviewZoomText is not null) PreviewZoomText.Text = $"{_previewZoomPercent:0}%";
        UpdatePreviewLayout();
    }

    private void FitPreview_Click(object sender, RoutedEventArgs e)
    {
        PreviewZoomSlider.Value = 100;
        UpdatePreviewLayout();
    }
    private void ZoomInPreview_Click(object sender, RoutedEventArgs e) => PreviewZoomSlider.Value = Math.Min(400, PreviewZoomSlider.Value + 25);

    private void PreviewContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewLayout();
        Dispatcher.BeginInvoke(RedrawPreviewOverlays, DispatcherPriority.Render);
    }

    private void PreviewFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RedrawPreviewOverlays();
            RedrawCloneStampGuides();
            UpdateCloneSampleCursor();
        }, DispatcherPriority.Render);
    }

    private void PreviewContentHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var step = e.Delta > 0 ? 25 : -25;
        PreviewZoomSlider.Value = Math.Clamp(PreviewZoomSlider.Value + step, PreviewZoomSlider.Minimum, PreviewZoomSlider.Maximum);
        e.Handled = true;
    }

    private void UpdatePreviewLayout()
    {
        var item = _viewModel.SelectedImage;
        if (item is null || item.PixelWidth <= 0 || item.PixelHeight <= 0 || !IsLoaded) return;
        switch (_previewMode)
        {
            case PreviewMode.SideBySide:
                SetFrameSize(AnalysisImageFrame, SideAnalysisScroll, item.PixelWidth, item.PixelHeight);
                SetFrameSize(RepairedImageFrame, SideRepairedScroll, item.PixelWidth, item.PixelHeight);
                break;
            case PreviewMode.Compare:
                SetFrameSize(CompareImageFrame, CompareScroll, item.PixelWidth, item.PixelHeight);
                UpdateCompareClip();
                break;
            case PreviewMode.MaskOnly:
                SetFrameSize(MaskImageFrame, MaskScroll, item.PixelWidth, item.PixelHeight);
                break;
        }
        RedrawPreviewOverlays();
        RedrawCloneStampGuides();
        UpdateCloneSampleCursor();
    }

    private void SetFrameSize(FrameworkElement frame, FrameworkElement viewport, int pixelWidth, int pixelHeight)
    {
        var availableWidth = Math.Max(80, viewport.ActualWidth - 42);
        var availableHeight = Math.Max(80, viewport.ActualHeight - 42);
        var fitScale = Math.Min(availableWidth / pixelWidth, availableHeight / pixelHeight);
        var scale = fitScale * _previewZoomPercent / 100d;
        frame.Width = Math.Max(1, pixelWidth * scale);
        frame.Height = Math.Max(1, pixelHeight * scale);
    }

    private void RedrawPreviewOverlays()
    {
        var item = _viewModel.SelectedImage;
        if (item is null) return;
        SetCandidateGuideVisibility();
        DrawRuler(AnalysisRulerCanvas, item);
        DrawRuler(RepairedRulerCanvas, item);
        SetRulerVisualVisibility(_viewModel.Settings.ShowRulers ? Visibility.Visible : Visibility.Collapsed);
        if (_viewModel.Settings.ShowCrosshair && _lastCrosshairPoint is { } point)
            UpdateSynchronizedCrosshair(point);
        else
            SetCrosshairVisibility(Visibility.Collapsed);
        if (_viewModel.Settings.ShowCandidateGuides)
        {
            DrawCandidateGuides(AnalysisCandidateGuideCanvas, item);
            DrawCandidateGuides(RepairedCandidateGuideCanvas, item);
        }
        else
        {
            DrawCandidateGuides(AnalysisCandidateGuideCanvas, item, clearOnly: true);
            DrawCandidateGuides(RepairedCandidateGuideCanvas, item, clearOnly: true);
        }
    }

    private void SetCandidateGuideVisibility()
    {
        var visibility = _viewModel.Settings.ShowCandidateGuides ? Visibility.Visible : Visibility.Collapsed;
        if (AnalysisCandidateGuideCanvas is not null) AnalysisCandidateGuideCanvas.Visibility = visibility;
        if (RepairedCandidateGuideCanvas is not null) RepairedCandidateGuideCanvas.Visibility = visibility;
    }

    private static void DrawRuler(Canvas canvas, ArchiveImageItem item)
    {
        canvas.Children.Clear();
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var dpiX = ImageGeometry.IsValidDpi(item.DpiX) ? item.DpiX : ImageGeometry.DefaultDpi;
        var dpiY = ImageGeometry.IsValidDpi(item.DpiY) ? item.DpiY : ImageGeometry.DefaultDpi;
        var scaleX = canvas.ActualWidth / item.PixelWidth;
        var scaleY = canvas.ActualHeight / item.PixelHeight;
        var stripBrush = new SolidColorBrush(Color.FromArgb(205, 248, 250, 250));
        var tickBrush = new SolidColorBrush(Color.FromArgb(220, 61, 76, 81));
        const double horizontalStrip = 24;
        const double verticalStrip = 30;
        canvas.Children.Add(new ShapeRectangle { Tag = "RulerVisual", Width = canvas.ActualWidth, Height = horizontalStrip, Fill = stripBrush, IsHitTestVisible = false });
        var bottomStrip = new ShapeRectangle { Tag = "RulerVisual", Width = canvas.ActualWidth, Height = horizontalStrip, Fill = stripBrush, IsHitTestVisible = false };
        Canvas.SetTop(bottomStrip, Math.Max(0, canvas.ActualHeight - horizontalStrip));
        canvas.Children.Add(bottomStrip);
        canvas.Children.Add(new ShapeRectangle { Tag = "RulerVisual", Width = verticalStrip, Height = canvas.ActualHeight, Fill = stripBrush, IsHitTestVisible = false });
        var rightStrip = new ShapeRectangle { Tag = "RulerVisual", Width = verticalStrip, Height = canvas.ActualHeight, Fill = stripBrush, IsHitTestVisible = false };
        Canvas.SetLeft(rightStrip, Math.Max(0, canvas.ActualWidth - verticalStrip));
        canvas.Children.Add(rightStrip);

        for (var mm = 0d; ; mm += 10)
        {
            var x = mm / 25.4 * dpiX * scaleX;
            if (x > canvas.ActualWidth) break;
            AddRulerTick(canvas, x, horizontalStrip, true, tickBrush, $"{mm:0}");
            AddRulerTick(canvas, x, horizontalStrip, true, tickBrush, $"{mm:0}", opposite: true);
        }
        for (var mm = 0d; ; mm += 10)
        {
            var y = mm / 25.4 * dpiY * scaleY;
            if (y > canvas.ActualHeight) break;
            AddRulerTick(canvas, y, verticalStrip, false, tickBrush, $"{mm:0}");
            AddRulerTick(canvas, y, verticalStrip, false, tickBrush, $"{mm:0}", opposite: true);
        }
        var crosshairBrush = new SolidColorBrush(Color.FromArgb(210, 213, 71, 71));
        canvas.Children.Add(new Line
        {
            Tag = "CrosshairX", Stroke = crosshairBrush, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }, Stretch = Stretch.None,
            Width = 1, Height = canvas.ActualHeight, X1 = 0, Y1 = 0, X2 = 0, Y2 = canvas.ActualHeight,
            Visibility = Visibility.Collapsed
        });
        canvas.Children.Add(new Line
        {
            Tag = "CrosshairY", Stroke = crosshairBrush, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }, Stretch = Stretch.None,
            Width = canvas.ActualWidth, Height = 1, X1 = 0, Y1 = 0, X2 = canvas.ActualWidth, Y2 = 0,
            Visibility = Visibility.Collapsed
        });
    }

    private static void AddRulerTick(Canvas canvas, double position, double stripSize, bool horizontal,
        Brush brush, string label, bool opposite = false)
    {
        var tick = new ShapeRectangle
        {
            Width = horizontal ? 1 : 7,
            Height = horizontal ? 7 : 1,
            Fill = brush,
            Tag = "RulerVisual",
            IsHitTestVisible = false
        };
        Canvas.SetLeft(tick, horizontal
            ? position
            : opposite ? canvas.ActualWidth - stripSize : stripSize - 7);
        Canvas.SetTop(tick, horizontal
            ? opposite ? canvas.ActualHeight - stripSize : stripSize - 7
            : position);
        canvas.Children.Add(tick);
        var text = new TextBlock { Text = label, Foreground = brush, FontSize = 9, Tag = "RulerVisual" };
        if (horizontal)
        {
            Canvas.SetLeft(text, Math.Min(position + 2, Math.Max(0, canvas.ActualWidth - 22)));
            Canvas.SetTop(text, opposite ? canvas.ActualHeight - 17 : 1);
        }
        else
        {
            text.Width = Math.Max(1, stripSize - 2);
            text.TextAlignment = opposite ? TextAlignment.Left : TextAlignment.Right;
            Canvas.SetLeft(text, opposite ? canvas.ActualWidth - stripSize + 1 : 1);
            Canvas.SetTop(text, Math.Min(position + 1, Math.Max(0, canvas.ActualHeight - 16)));
        }
        canvas.Children.Add(text);
    }

    private void DrawCandidateGuides(Canvas canvas, ArchiveImageItem item, bool clearOnly = false)
    {
        canvas.Children.Clear();
        foreach (var key in _candidateLines.Keys.Where(key => ReferenceEquals(key.Canvas, canvas)).ToArray())
            _candidateLines.Remove(key);
        foreach (var key in _candidateHandles.Keys.Where(key => ReferenceEquals(key.Canvas, canvas)).ToArray())
            _candidateHandles.Remove(key);
        if (clearOnly || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

        var lineBrush = new SolidColorBrush(Color.FromArgb(220, 31, 133, 119));
        foreach (var side in new[] { EdgeSide.Left, EdgeSide.Right, EdgeSide.Top, EdgeSide.Bottom })
        {
            if (!IsCandidateSideEnabled(side)) continue;
            var line = new Line { Stroke = lineBrush, StrokeThickness = 2, IsHitTestVisible = false };
            var handle = new ShapeRectangle
            {
                Width = 12, Height = 12, Fill = lineBrush, Stroke = Brushes.White, StrokeThickness = 1,
                Cursor = side is EdgeSide.Left or EdgeSide.Right ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.SizeNS,
                Tag = side
            };
            handle.MouseLeftButtonDown += CandidateHandle_MouseLeftButtonDown;
            handle.MouseMove += CandidateHandle_MouseMove;
            handle.MouseLeftButtonUp += CandidateHandle_MouseLeftButtonUp;
            handle.LostMouseCapture += CandidateHandle_LostMouseCapture;
            canvas.Children.Add(line);
            canvas.Children.Add(handle);
            _candidateLines[(canvas, side)] = line;
            _candidateHandles[(canvas, side)] = handle;
        }
        UpdateCandidateGuideGeometry(item);
    }

    private bool IsCandidateSideEnabled(EdgeSide side) => side switch
    {
        EdgeSide.Left => _viewModel.SelectedImage?.CleanLeft == true,
        EdgeSide.Right => _viewModel.SelectedImage?.CleanRight == true,
        EdgeSide.Top => _viewModel.SelectedImage?.CleanTop == true,
        EdgeSide.Bottom => _viewModel.SelectedImage?.CleanBottom == true,
        _ => false
    };

    private void UpdateCandidateGuideGeometry(ArchiveImageItem item)
    {
        var dpiX = ImageGeometry.IsValidDpi(item.DpiX) ? item.DpiX : ImageGeometry.DefaultDpi;
        var dpiY = ImageGeometry.IsValidDpi(item.DpiY) ? item.DpiY : ImageGeometry.DefaultDpi;
        foreach (var canvas in new[] { AnalysisCandidateGuideCanvas, RepairedCandidateGuideCanvas })
        {
            var sx = canvas.ActualWidth / item.PixelWidth;
            var sy = canvas.ActualHeight / item.PixelHeight;
            foreach (var side in new[] { EdgeSide.Left, EdgeSide.Right, EdgeSide.Top, EdgeSide.Bottom })
            {
                if (!_candidateLines.TryGetValue((canvas, side), out var line) || !_candidateHandles.TryGetValue((canvas, side), out var handle)) continue;
                var depth = side switch
                {
                    EdgeSide.Left => ImageGeometry.MillimetersToPixels(item.LeftMarginMm, dpiX, item.PixelWidth),
                    EdgeSide.Right => ImageGeometry.MillimetersToPixels(item.RightMarginMm, dpiX, item.PixelWidth),
                    EdgeSide.Top => ImageGeometry.MillimetersToPixels(item.TopMarginMm, dpiY, item.PixelHeight),
                    _ => ImageGeometry.MillimetersToPixels(item.BottomMarginMm, dpiY, item.PixelHeight)
                };
                depth = Math.Min(depth, side is EdgeSide.Left or EdgeSide.Right ? item.PixelWidth / 2 : item.PixelHeight / 2);
                var x = side == EdgeSide.Left ? depth * sx : side == EdgeSide.Right ? canvas.ActualWidth - depth * sx : 0;
                var y = side == EdgeSide.Top ? depth * sy : side == EdgeSide.Bottom ? canvas.ActualHeight - depth * sy : 0;
                if (side is EdgeSide.Left or EdgeSide.Right)
                {
                    line.X1 = line.X2 = x; line.Y1 = 0; line.Y2 = canvas.ActualHeight;
                    Canvas.SetLeft(handle, x - handle.Width / 2); Canvas.SetTop(handle, 6);
                }
                else
                {
                    line.X1 = 0; line.X2 = canvas.ActualWidth; line.Y1 = line.Y2 = y;
                    Canvas.SetLeft(handle, 6); Canvas.SetTop(handle, y - handle.Height / 2);
                }
            }
        }
    }

    private void RedrawCloneStampGuides()
    {
        if (ManualDrawingCanvas is null) return;
        for (var index = ManualDrawingCanvas.Children.Count - 1; index >= 0; index--)
            if (ManualDrawingCanvas.Children[index] is Shape shape &&
                !ReferenceEquals(shape, BrushCursorEllipse) && !ReferenceEquals(shape, _cloneSampleCursorEllipse))
                ManualDrawingCanvas.Children.RemoveAt(index);

        var item = _viewModel.SelectedImage;
        if (item is null || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0) return;
        var scaleX = ManualDrawingCanvas.ActualWidth / item.PixelWidth;
        var scaleY = ManualDrawingCanvas.ActualHeight / item.PixelHeight;
        foreach (var stamp in item.ActiveCloneStamps)
        {
            if (stamp.TargetPoints.Count == 0) continue;
            var points = stamp.TargetPoints.Select(point => new System.Windows.Point(point.X * scaleX, point.Y * scaleY)).ToArray();
            var diameter = Math.Max(2, stamp.Diameter * ((scaleX + scaleY) / 2d));
            if (points.Length == 1)
            {
                var marker = new Ellipse
                {
                    Width = diameter, Height = diameter, Stroke = Brushes.DodgerBlue,
                    StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(24, 30, 111, 237)), IsHitTestVisible = false
                };
                Canvas.SetLeft(marker, points[0].X - diameter / 2d);
                Canvas.SetTop(marker, points[0].Y - diameter / 2d);
                ManualDrawingCanvas.Children.Add(marker);
            }
            else
            {
                var guide = new Polyline
                {
                    Stroke = Brushes.DodgerBlue, StrokeThickness = Math.Max(2, diameter),
                    Opacity = 0.28, StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false, Points = new PointCollection(points)
                };
                ManualDrawingCanvas.Children.Add(guide);
            }
        }
    }

    private void SidePreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isSynchronizingScroll || _previewMode != PreviewMode.SideBySide) return;
        _isSynchronizingScroll = true;
        try
        {
            var source = (ScrollViewer)sender;
            var target = ReferenceEquals(source, SideAnalysisScroll) ? SideRepairedScroll : SideAnalysisScroll;
            var horizontalRatio = source.ScrollableWidth <= 0 ? 0 : source.HorizontalOffset / source.ScrollableWidth;
            var verticalRatio = source.ScrollableHeight <= 0 ? 0 : source.VerticalOffset / source.ScrollableHeight;
            target.ScrollToHorizontalOffset(horizontalRatio * target.ScrollableWidth);
            target.ScrollToVerticalOffset(verticalRatio * target.ScrollableHeight);
        }
        finally { _isSynchronizingScroll = false; }
    }

    private void CandidateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.Settings.ShowCandidateGuides) return;
        if (sender is not ShapeRectangle { Tag: EdgeSide side } handle) return;
        var canvas = FindParentCanvas(handle);
        if (canvas is null || _viewModel.SelectedImage is null) return;
        _draggingCandidateSide = side;
        _candidateDragCanvas = canvas;
        _candidateDragElement = handle;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void CandidateHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingCandidateSide is null || _candidateDragCanvas is null || _viewModel.SelectedImage is null
            || e.LeftButton != MouseButtonState.Pressed) return;
        var item = _viewModel.SelectedImage;
        var point = e.GetPosition(_candidateDragCanvas);
        var x = Math.Clamp(point.X, 0, _candidateDragCanvas.ActualWidth);
        var y = Math.Clamp(point.Y, 0, _candidateDragCanvas.ActualHeight);
        var dpiX = ImageGeometry.IsValidDpi(item.DpiX) ? item.DpiX : ImageGeometry.DefaultDpi;
        var dpiY = ImageGeometry.IsValidDpi(item.DpiY) ? item.DpiY : ImageGeometry.DefaultDpi;
        var side = _draggingCandidateSide.Value;
        var mm = side switch
        {
            EdgeSide.Left => x / _candidateDragCanvas.ActualWidth * item.PixelWidth / dpiX * 25.4,
            EdgeSide.Right => (_candidateDragCanvas.ActualWidth - x) / _candidateDragCanvas.ActualWidth * item.PixelWidth / dpiX * 25.4,
            EdgeSide.Top => y / _candidateDragCanvas.ActualHeight * item.PixelHeight / dpiY * 25.4,
            _ => (_candidateDragCanvas.ActualHeight - y) / _candidateDragCanvas.ActualHeight * item.PixelHeight / dpiY * 25.4
        };
        var maximumMm = side is EdgeSide.Left or EdgeSide.Right
            ? item.PixelWidth / dpiX * 25.4 * 0.5
            : item.PixelHeight / dpiY * 25.4 * 0.5;
        var value = Math.Clamp(mm, 0, maximumMm);
        switch (side)
        {
            case EdgeSide.Left: item.LeftMarginMm = value; break;
            case EdgeSide.Right: item.RightMarginMm = value; break;
            case EdgeSide.Top: item.TopMarginMm = value; break;
            case EdgeSide.Bottom: item.BottomMarginMm = value; break;
        }
        UpdateCandidateGuideGeometry(item);
        e.Handled = true;
    }

    private void CandidateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopCandidateDrag();
        RedrawPreviewOverlays();
        e.Handled = true;
    }

    private void CandidateHandle_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _candidateDragElement))
            StopCandidateDrag();
    }

    private void StopCandidateDrag()
    {
        if (_candidateDragElement is not null && _candidateDragElement.IsMouseCaptured)
            _candidateDragElement.ReleaseMouseCapture();
        _draggingCandidateSide = null;
        _candidateDragCanvas = null;
        _candidateDragElement = null;
    }

    private static Canvas? FindParentCanvas(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Canvas canvas) return canvas;
        return null;
    }

    private void CompareImageFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCompareDivider = true;
        CompareImageFrame.CaptureMouse();
        SetCompareDivider(e.GetPosition(CompareImageFrame).X);
    }

    private void CompareImageFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingCompareDivider && e.LeftButton == MouseButtonState.Pressed)
            SetCompareDivider(e.GetPosition(CompareImageFrame).X);
    }

    private void CompareImageFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingCompareDivider) return;
        SetCompareDivider(e.GetPosition(CompareImageFrame).X);
        _isDraggingCompareDivider = false;
        CompareImageFrame.ReleaseMouseCapture();
    }

    private void SetCompareDivider(double x)
    {
        if (CompareImageFrame.ActualWidth <= 0) return;
        _compareDividerRatio = Math.Clamp(x / CompareImageFrame.ActualWidth, 0, 1);
        UpdateCompareClip();
    }

    private void UpdateCompareClip()
    {
        if (CompareImageFrame is null || CompareProcessedClip is null) return;
        var width = CompareImageFrame.ActualWidth > 0 ? CompareImageFrame.ActualWidth : CompareImageFrame.Width;
        var height = CompareImageFrame.ActualHeight > 0 ? CompareImageFrame.ActualHeight : CompareImageFrame.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;
        var x = width * _compareDividerRatio;
        CompareProcessedClip.Rect = new Rect(x, 0, Math.Max(0, width - x), height);
        Canvas.SetLeft(CompareDividerLine, x);
        CompareDividerLine.Height = height;
        Canvas.SetLeft(CompareDividerThumb, x - CompareDividerThumb.Width / 2);
        Canvas.SetTop(CompareDividerThumb, Math.Max(4, height / 2 - CompareDividerThumb.Height / 2));
    }

    private async void UndoManualEdit_Click(object sender, RoutedEventArgs e)
    {
        await RunCancelableAsync(_viewModel.UndoManualEditAsync, "撤销操作已取消", "撤销人工修正失败");
    }

    private async void RedoManualEdit_Click(object sender, RoutedEventArgs e)
    {
        await RunCancelableAsync(_viewModel.RedoManualEditAsync, "重做操作已取消", "重做人工修正失败");
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Keyboard.FocusedElement is TextBoxBase) return;
        if (e.Key == Key.Z && _viewModel.CanUndoManualEdit)
        {
            e.Handled = true;
            await RunCancelableAsync(_viewModel.UndoManualEditAsync, "撤销操作已取消", "人工修正历史操作失败");
        }
        else if (e.Key == Key.Y && _viewModel.CanRedoManualEdit)
        {
            e.Handled = true;
            await RunCancelableAsync(_viewModel.RedoManualEditAsync, "重做操作已取消", "人工修正历史操作失败");
        }
    }

    private void ManualCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ManualEditMode == ManualEditMode.None || !_viewModel.CanUseManualTools || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0)
            return;

        var start = ClampToCanvas(e.GetPosition(ManualDrawingCanvas));
        if (_viewModel.ManualEditMode == ManualEditMode.CloneStamp &&
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            _cloneSamplePoint = ToPixelPoint(start);
            _viewModel.EngineStatus = $"已设置仿制图章取样点：{_cloneSamplePoint.Value.X}，{_cloneSamplePoint.Value.Y}";
            UpdateCloneSampleCursor();
            e.Handled = true;
            return;
        }
        if (_viewModel.ManualEditMode == ManualEditMode.CloneStamp && _cloneSamplePoint is null)
        {
            _viewModel.EngineStatus = "请先按住 Alt 点击图像设置仿制图章取样点";
            e.Handled = true;
            return;
        }

        _manualStart = start;
        _manualConstraint = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            ? ManualConstraint.Horizontal
            : (Keyboard.Modifiers & ModifierKeys.Control) != 0
                ? ManualConstraint.Vertical
                : ManualConstraint.None;
        _manualPoints.Clear();
        _manualPoints.Add(_manualStart);
        _isManualDrawing = true;
        RemoveConstraintGuide();
        ManualDrawingCanvas.CaptureMouse();
        UpdateCloneSampleCursor(_manualStart);

        var color = _viewModel.ManualEditMode switch
        {
            ManualEditMode.ProtectBrush => _viewModel.ProtectToolBrush,
            ManualEditMode.RemoveBrush => _viewModel.RemoveToolBrush,
            ManualEditMode.CloneStamp => _viewModel.ProtectToolBrush,
            _ => _viewModel.EraserToolBrush
        };
        var polyline = new Polyline
        {
            Stroke = color, StrokeThickness = GetBrushDiameterOnCanvas(),
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, StrokeMiterLimit = 1,
            Opacity = 0.78, Points = new PointCollection { _manualStart }
        };
        _manualGuide = polyline;
        ManualDrawingCanvas.Children.Add(_manualGuide);
        UpdateConstraintGuide(start);
        e.Handled = true;
    }

    private void ManualCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var hoverPoint = ClampToCanvas(e.GetPosition(ManualDrawingCanvas));
        UpdateSynchronizedCrosshair(hoverPoint);
        var point = _isManualDrawing ? GetConstrainedPoint(hoverPoint) : hoverPoint;
        UpdateBrushCursor(point);
        UpdateCloneSampleCursor(_isManualDrawing ? point : null);
        if (!_isManualDrawing || e.LeftButton != MouseButtonState.Pressed || _manualGuide is null) return;
        UpdateConstraintGuide(hoverPoint);
        if (_manualGuide is Polyline polyline)
            AppendManualPoint(polyline, point);
        e.Handled = true;
    }

    private async void ManualCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isManualDrawing) return;
        var end = GetConstrainedPoint(ClampToCanvas(e.GetPosition(ManualDrawingCanvas)));
        _isManualDrawing = false;
        ManualDrawingCanvas.ReleaseMouseCapture();
        UpdateCloneSampleCursor();
        if (_manualGuide is Polyline polyline)
            AppendManualPoint(polyline, end);
        var guide = _manualGuide;
        _manualGuide = null;
        _manualConstraint = ManualConstraint.None;
        try
        {
            var marks = BuildManualMarks(end);
            await RunCancelableAsync(
                token => _viewModel.ApplyManualEditsAsync(_viewModel.ManualEditMode, marks,
                    (int)Math.Round(_viewModel.BrushDiameterPixels), _cloneSamplePoint, token),
                "人工修正已取消", "人工修正失败");
        }
        finally
        {
            if (guide is not null) ManualDrawingCanvas.Children.Remove(guide);
            RemoveConstraintGuide();
            _manualPoints.Clear();
        }
        e.Handled = true;
    }

    private IReadOnlyList<PixelPoint> BuildManualMarks(System.Windows.Point end)
    {
        var item = _viewModel.SelectedImage;
        if (item is null || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0) return [];
        var scaleX = item.PixelWidth / ManualDrawingCanvas.ActualWidth;
        var scaleY = item.PixelHeight / ManualDrawingCanvas.ActualHeight;
        return _manualPoints.Select(point => new PixelPoint(
            Math.Clamp((int)Math.Round(point.X * scaleX), 0, item.PixelWidth - 1),
            Math.Clamp((int)Math.Round(point.Y * scaleY), 0, item.PixelHeight - 1))).ToArray();
    }

    private PixelPoint ToPixelPoint(System.Windows.Point point)
    {
        var item = _viewModel.SelectedImage!;
        return new PixelPoint(
            Math.Clamp((int)Math.Round(point.X * item.PixelWidth / ManualDrawingCanvas.ActualWidth), 0, item.PixelWidth - 1),
            Math.Clamp((int)Math.Round(point.Y * item.PixelHeight / ManualDrawingCanvas.ActualHeight), 0, item.PixelHeight - 1));
    }

    private double GetBrushDiameterOnCanvas()
    {
        var item = _viewModel.SelectedImage;
        if (item is null || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0) return 10;
        var scaleX = item.PixelWidth / ManualDrawingCanvas.ActualWidth;
        var scaleY = item.PixelHeight / ManualDrawingCanvas.ActualHeight;
        return Math.Max(2, _viewModel.BrushDiameterPixels / ((scaleX + scaleY) / 2d));
    }

    private void UpdateBrushCursor(System.Windows.Point point)
    {
        if (!_viewModel.CanUseManualTools || _viewModel.ManualEditMode is ManualEditMode.None)
        {
            BrushCursorEllipse.Visibility = Visibility.Collapsed;
            return;
        }
        var diameter = GetBrushDiameterOnCanvas();
        BrushCursorEllipse.Width = diameter;
        BrushCursorEllipse.Height = diameter;
        BrushCursorEllipse.Stroke = _viewModel.ManualEditMode switch
        {
            ManualEditMode.ProtectBrush => _viewModel.ProtectToolBrush,
            ManualEditMode.RemoveBrush => _viewModel.RemoveToolBrush,
            ManualEditMode.CloneStamp => _viewModel.ProtectToolBrush,
            _ => _viewModel.EraserToolBrush
        };
        Canvas.SetLeft(BrushCursorEllipse, point.X - diameter / 2d);
        Canvas.SetTop(BrushCursorEllipse, point.Y - diameter / 2d);
        BrushCursorEllipse.Visibility = Visibility.Visible;
    }

    private void UpdateCloneSampleCursor(System.Windows.Point? activeTargetPoint = null)
    {
        var item = _viewModel.SelectedImage;
        if (_viewModel.ManualEditMode != ManualEditMode.CloneStamp || _cloneSamplePoint is null ||
            item is null || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0)
        {
            _cloneSampleCursorEllipse.Visibility = Visibility.Collapsed;
            return;
        }

        var center = new System.Windows.Point(
            _cloneSamplePoint.Value.X * ManualDrawingCanvas.ActualWidth / item.PixelWidth,
            _cloneSamplePoint.Value.Y * ManualDrawingCanvas.ActualHeight / item.PixelHeight);
        if (_isManualDrawing && activeTargetPoint is { } target)
            center += target - _manualStart;

        var diameter = GetBrushDiameterOnCanvas();
        _cloneSampleCursorEllipse.Width = diameter;
        _cloneSampleCursorEllipse.Height = diameter;
        Canvas.SetLeft(_cloneSampleCursorEllipse, center.X - diameter / 2d);
        Canvas.SetTop(_cloneSampleCursorEllipse, center.Y - diameter / 2d);
        _cloneSampleCursorEllipse.Visibility = Visibility.Visible;
    }

    private void ManualCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        _lastCrosshairPoint = null;
        SetCrosshairVisibility(Visibility.Collapsed);
        if (!_isManualDrawing) BrushCursorEllipse.Visibility = Visibility.Collapsed;
    }

    private void UpdateSynchronizedCrosshair(System.Windows.Point point)
    {
        _lastCrosshairPoint = point;
        if (!_viewModel.Settings.ShowCrosshair)
        {
            SetCrosshairVisibility(Visibility.Collapsed);
            return;
        }
        var item = _viewModel.SelectedImage;
        if (item is null || ManualDrawingCanvas.ActualWidth <= 0 || ManualDrawingCanvas.ActualHeight <= 0) return;
        var ratioX = Math.Clamp(point.X / ManualDrawingCanvas.ActualWidth, 0, 1);
        var ratioY = Math.Clamp(point.Y / ManualDrawingCanvas.ActualHeight, 0, 1);
        SetCrosshair(AnalysisRulerCanvas, ratioX, ratioY);
        SetCrosshair(RepairedRulerCanvas, ratioX, ratioY);
    }

    private static void SetCrosshair(Canvas canvas, double ratioX, double ratioY)
    {
        var vertical = canvas.Children.OfType<Line>().FirstOrDefault(line => Equals(line.Tag, "CrosshairX"));
        var horizontal = canvas.Children.OfType<Line>().FirstOrDefault(line => Equals(line.Tag, "CrosshairY"));
        if (vertical is null || horizontal is null) return;
        vertical.Width = 1;
        vertical.Height = canvas.ActualHeight;
        vertical.X1 = vertical.X2 = 0;
        vertical.Y1 = 0; vertical.Y2 = canvas.ActualHeight;
        Canvas.SetLeft(vertical, ratioX * canvas.ActualWidth);
        horizontal.Width = canvas.ActualWidth;
        horizontal.Height = 1;
        horizontal.X1 = 0; horizontal.X2 = canvas.ActualWidth;
        horizontal.Y1 = horizontal.Y2 = 0;
        Canvas.SetTop(horizontal, ratioY * canvas.ActualHeight);
        vertical.Visibility = horizontal.Visibility = Visibility.Visible;
    }

    private void SetCrosshairVisibility(Visibility visibility)
    {
        foreach (var canvas in new[] { AnalysisRulerCanvas, RepairedRulerCanvas })
        foreach (var line in canvas.Children.OfType<Line>().Where(line => line.Tag is "CrosshairX" or "CrosshairY"))
            line.Visibility = visibility;
    }

    private void ManualCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isManualDrawing) return;
        _isManualDrawing = false;
        if (_manualGuide is not null) ManualDrawingCanvas.Children.Remove(_manualGuide);
        _manualGuide = null;
        _manualConstraint = ManualConstraint.None;
        RemoveConstraintGuide();
        _manualPoints.Clear();
        UpdateCloneSampleCursor();
    }

    private System.Windows.Point GetConstrainedPoint(System.Windows.Point point)
    {
        if (!_isManualDrawing) return point;
        if (_manualConstraint == ManualConstraint.Horizontal)
            return new System.Windows.Point(point.X, _manualStart.Y);
        if (_manualConstraint == ManualConstraint.Vertical)
            return new System.Windows.Point(_manualStart.X, point.Y);
        return point;
    }

    private void UpdateConstraintGuide(System.Windows.Point point)
    {
        if (!_isManualDrawing) return;
        var horizontal = _manualConstraint == ManualConstraint.Horizontal;
        var vertical = _manualConstraint == ManualConstraint.Vertical;
        if (!horizontal && !vertical)
        {
            RemoveConstraintGuide();
            return;
        }

        _constraintGuide ??= new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(210, 61, 76, 81)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            Tag = "ManualConstraintGuide"
        };
        if (!ManualDrawingCanvas.Children.Contains(_constraintGuide))
            ManualDrawingCanvas.Children.Add(_constraintGuide);
        _constraintGuide.Visibility = Visibility.Visible;
        if (horizontal)
        {
            _constraintGuide.X1 = 0; _constraintGuide.X2 = ManualDrawingCanvas.ActualWidth;
            _constraintGuide.Y1 = _manualStart.Y; _constraintGuide.Y2 = _manualStart.Y;
        }
        else
        {
            _constraintGuide.X1 = _manualStart.X; _constraintGuide.X2 = _manualStart.X;
            _constraintGuide.Y1 = 0; _constraintGuide.Y2 = ManualDrawingCanvas.ActualHeight;
        }
    }

    private void RemoveConstraintGuide()
    {
        if (_constraintGuide is null) return;
        ManualDrawingCanvas.Children.Remove(_constraintGuide);
        _constraintGuide.Visibility = Visibility.Collapsed;
    }

    private void SetRulerVisualVisibility(Visibility visibility)
    {
        foreach (var canvas in new[] { AnalysisRulerCanvas, RepairedRulerCanvas })
        foreach (var element in canvas.Children.OfType<FrameworkElement>().Where(element => Equals(element.Tag, "RulerVisual")))
            element.Visibility = visibility;
    }

    private void AppendManualPoint(Polyline polyline, System.Windows.Point point)
    {
        if (_manualPoints.Count == 0)
        {
            _manualPoints.Add(point);
            polyline.Points.Add(point);
            return;
        }

        var previous = _manualPoints[^1];
        var distance = (point - previous).Length;
        if (distance < 0.5) return;
        // Mouse capture can occasionally report a point far outside the previous
        // sample. Limit one event's contribution so an outlier cannot create a
        // full-width stroke; subsequent events continue the path normally.
        const double maxEventStep = 48;
        var limitedPoint = point;
        if (distance > maxEventStep)
        {
            var ratio = maxEventStep / distance;
            limitedPoint = new System.Windows.Point(
                previous.X + (point.X - previous.X) * ratio,
                previous.Y + (point.Y - previous.Y) * ratio);
        }
        var limitedDistance = (limitedPoint - previous).Length;
        var steps = Math.Max(1, (int)Math.Ceiling(limitedDistance / maxEventStep));
        for (var step = 1; step <= steps; step++)
        {
            var ratio = step / (double)steps;
            var next = new System.Windows.Point(
                previous.X + (limitedPoint.X - previous.X) * ratio,
                previous.Y + (limitedPoint.Y - previous.Y) * ratio);
            _manualPoints.Add(next);
            polyline.Points.Add(next);
        }
    }

    private void CatalogTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectTreeNode(e.NewValue as CatalogTreeNode);

    private void RemoveTreeNode_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || sender is not MenuItem { DataContext: CatalogTreeNode node }) return;
        var kind = node is FolderTreeNode ? "文件夹" : "文件";
        var result = MessageBox.Show(this,
            $"从当前任务队列移除{kind}“{node.Name}”？\n\n此操作不会删除或修改磁盘上的源文件。",
            $"移除{kind}", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _viewModel.RemoveTreeNode(node);
    }

    private void CatalogMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogMenuButton.ContextMenu is null) return;
        SelectAllMenuItem.IsEnabled = !_viewModel.IsBusy && _viewModel.SelectedCount < _viewModel.TotalCount;
        SelectNoneMenuItem.IsEnabled = !_viewModel.IsBusy && _viewModel.SelectedCount > 0;
        RemoveCheckedMenuItem.IsEnabled = !_viewModel.IsBusy && _viewModel.SelectedCount > 0;
        ClearQueueMenuItem.IsEnabled = !_viewModel.IsBusy && _viewModel.TotalCount > 0;
        CatalogMenuButton.ContextMenu.PlacementTarget = CatalogMenuButton;
        CatalogMenuButton.ContextMenu.Placement = PlacementMode.Bottom;
        CatalogMenuButton.ContextMenu.IsOpen = true;
    }

    private void SelectAllImages_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsBusy) _viewModel.SetAllImagesChecked(true);
    }

    private void SelectNoImages_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsBusy) _viewModel.SetAllImagesChecked(false);
    }

    private void ExpandAllFolders_Click(object sender, RoutedEventArgs e) => _viewModel.SetAllFoldersExpanded(true);

    private void CollapseAllFolders_Click(object sender, RoutedEventArgs e) => _viewModel.SetAllFoldersExpanded(false);

    private void RemoveCheckedImages_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.SelectedCount == 0) return;
        var count = _viewModel.SelectedCount;
        var result = MessageBox.Show(this,
            $"从当前任务队列移除已勾选的 {count} 张图片？\n\n此操作不会删除或修改磁盘上的源文件。",
            "移除已勾选项", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _viewModel.RemoveCheckedImages();
    }

    private void ClearImageQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.Images.Count == 0) return;
        var result = MessageBox.Show(this,
            $"清空当前任务队列中的 {_viewModel.Images.Count} 张图片？\n\n此操作不会删除或修改磁盘上的源文件。",
            "清空任务队列", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _viewModel.ClearImageQueue();
    }

    private System.Windows.Point ClampToCanvas(System.Windows.Point point) => new(
        Math.Clamp(double.IsFinite(point.X) ? point.X : 0, 0, ManualDrawingCanvas.ActualWidth),
        Math.Clamp(double.IsFinite(point.Y) ? point.Y : 0, 0, ManualDrawingCanvas.ActualHeight));

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!CancelCurrentOperation()) return;
        _viewModel.EngineStatus = "正在取消当前任务…";
    }

    private CancellationTokenSource BeginCancelableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _viewModel.SetOperationCancelable(true);
        return _operationCancellation;
    }

    private async Task RunCancelableAsync(Func<CancellationToken, Task> operationBody,
        string canceledStatus, string errorTitle)
    {
        if (_viewModel.IsBusy) return;
        var operation = BeginCancelableOperation();
        try
        {
            await operationBody(operation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.EngineStatus = canceledStatus;
        }
        catch (Exception exception)
        {
            ShowError(errorTitle, exception);
        }
        finally
        {
            EndCancelableOperation(operation);
        }
    }

    private void EndCancelableOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_operationCancellation, operation)) return;
        _viewModel.SetOperationCancelable(false);
        _operationCancellation = null;
        operation.Dispose();
    }

    private bool CancelCurrentOperation()
    {
        var operation = _operationCancellation;
        if (operation is null || operation.IsCancellationRequested)
        {
            _viewModel.SetOperationCancelable(false);
            return false;
        }
        _viewModel.SetOperationCancelable(false);
        try
        {
            operation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WindowMessageHook);
    }

    private static IntPtr WindowMessageHook(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int getMinMaxInfoMessage = 0x0024;
        if (message != getMinMaxInfoMessage)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(windowHandle, 2);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = Math.Abs(monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left);
        minMaxInfo.MaxPosition.Y = Math.Abs(monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top);
        minMaxInfo.MaxSize.X = Math.Abs(monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left);
        minMaxInfo.MaxSize.Y = Math.Abs(monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowError(string title, Exception exception)
    {
        MessageBox.Show(this, $"{exception.Message}\n\n请确认文件存在、格式受支持且当前用户有读取权限。", title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle MonitorArea;
        public Rectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
