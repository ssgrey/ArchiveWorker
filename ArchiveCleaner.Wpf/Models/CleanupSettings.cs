using ArchiveCleaner.Wpf.ViewModels;
using ArchiveCleaner.Core.Contracts;
using System.Text.Json.Serialization;

namespace ArchiveCleaner.Wpf.Models;

public sealed class CleanupSettings : ObservableObject
{
    private bool _cleanLeft = true;
    private bool _cleanRight = true;
    private bool _cleanTop = true;
    private bool _cleanBottom = true;
    private double _leftMarginMm = 20;
    private double _rightMarginMm = 15;
    private double _topMarginMm = 12;
    private double _bottomMarginMm = 15;
    private double _detectionSensitivity = 55;
    private bool _useAdvancedDetectionSettings;
    private double _relativeContrastSensitivity = 55;
    private double _absoluteDarknessSensitivity = 55;
    private double _smallDefectSensitivity = 50;
    private double _traceConnectionPixels = 2;
    private double _edgeShadowTracePixels = 8;
    private double _autoRemoveConfidencePercent = 75;
    private bool _detectRepeatedDefects = true;
    private bool _detectPageEdgeShadow = true;
    private double _maskExpansionPixels = 6;
    private bool _protectPrintedText = true;
    private bool _protectHandwriting = true;
    private bool _protectStamps = true;
    private bool _protectTablesAndPageNumbers = true;
    private double _contentProtectionStrength = 60;
    private double _protectionPaddingPixels = 8;
    private bool _blockOnConflict = true;
    private string _repairMethod = "仿制图章修复";
    private double _colorFeathering = 58;
    private bool _preserveDimensionsAndDpi = true;
    private string _outputFormat = "与原图一致";
    private double _jpegQuality = 95;
    private double _protectBrushDiameterPixels = 80;
    private double _removeBrushDiameterPixels = 80;
    private double _cloneStampDiameterPixels = 80;
    private double _eraserDiameterPixels = 80;
    private bool _showRulers = true;
    private bool _showCrosshair = true;
    private bool _showCandidateGuides = true;

    public bool CleanLeft { get => _cleanLeft; set => SetProperty(ref _cleanLeft, value); }
    public bool CleanRight { get => _cleanRight; set => SetProperty(ref _cleanRight, value); }
    public bool CleanTop { get => _cleanTop; set => SetProperty(ref _cleanTop, value); }
    public bool CleanBottom { get => _cleanBottom; set => SetProperty(ref _cleanBottom, value); }
    public double LeftMarginMm { get => _leftMarginMm; set => SetProperty(ref _leftMarginMm, value); }
    public double RightMarginMm { get => _rightMarginMm; set => SetProperty(ref _rightMarginMm, value); }
    public double TopMarginMm { get => _topMarginMm; set => SetProperty(ref _topMarginMm, value); }
    public double BottomMarginMm { get => _bottomMarginMm; set => SetProperty(ref _bottomMarginMm, value); }
    public double DetectionSensitivity
    {
        get => _detectionSensitivity;
        set { if (SetProperty(ref _detectionSensitivity, value)) OnPropertyChanged(nameof(DetectionSensitivityLabel)); }
    }
    public bool UseAdvancedDetectionSettings
    {
        get => _useAdvancedDetectionSettings;
        set { if (SetProperty(ref _useAdvancedDetectionSettings, value)) OnPropertyChanged(nameof(DetectionSensitivityLabel)); }
    }
    public double RelativeContrastSensitivity { get => _relativeContrastSensitivity; set => SetProperty(ref _relativeContrastSensitivity, value); }
    public double AbsoluteDarknessSensitivity { get => _absoluteDarknessSensitivity; set => SetProperty(ref _absoluteDarknessSensitivity, value); }
    public double SmallDefectSensitivity { get => _smallDefectSensitivity; set => SetProperty(ref _smallDefectSensitivity, value); }
    public double TraceConnectionPixels { get => _traceConnectionPixels; set => SetProperty(ref _traceConnectionPixels, value); }
    public double EdgeShadowTracePixels { get => _edgeShadowTracePixels; set => SetProperty(ref _edgeShadowTracePixels, value); }
    public double AutoRemoveConfidencePercent { get => _autoRemoveConfidencePercent; set => SetProperty(ref _autoRemoveConfidencePercent, value); }
    public bool DetectRepeatedDefects { get => _detectRepeatedDefects; set => SetProperty(ref _detectRepeatedDefects, value); }
    public bool DetectPageEdgeShadow { get => _detectPageEdgeShadow; set => SetProperty(ref _detectPageEdgeShadow, value); }
    public double MaskExpansionPixels { get => _maskExpansionPixels; set => SetProperty(ref _maskExpansionPixels, value); }
    public bool ProtectPrintedText { get => _protectPrintedText; set => SetProperty(ref _protectPrintedText, value); }
    public bool ProtectHandwriting { get => _protectHandwriting; set => SetProperty(ref _protectHandwriting, value); }
    public bool ProtectStamps { get => _protectStamps; set => SetProperty(ref _protectStamps, value); }
    public bool ProtectTablesAndPageNumbers { get => _protectTablesAndPageNumbers; set => SetProperty(ref _protectTablesAndPageNumbers, value); }
    public double ContentProtectionStrength { get => _contentProtectionStrength; set => SetProperty(ref _contentProtectionStrength, value); }
    public double ProtectionPaddingPixels { get => _protectionPaddingPixels; set => SetProperty(ref _protectionPaddingPixels, value); }
    public bool BlockOnConflict { get => _blockOnConflict; set => SetProperty(ref _blockOnConflict, value); }
    public string RepairMethod { get => _repairMethod; set => SetProperty(ref _repairMethod, value); }
    public double ColorFeathering { get => _colorFeathering; set => SetProperty(ref _colorFeathering, value); }
    public bool PreserveDimensionsAndDpi { get => _preserveDimensionsAndDpi; set => SetProperty(ref _preserveDimensionsAndDpi, value); }
    public string OutputFormat { get => _outputFormat; set => SetProperty(ref _outputFormat, value); }
    public double JpegQuality { get => _jpegQuality; set => SetProperty(ref _jpegQuality, value); }
    public bool ShowRulers { get => _showRulers; set => SetProperty(ref _showRulers, value); }
    public bool ShowCrosshair { get => _showCrosshair; set => SetProperty(ref _showCrosshair, value); }
    public bool ShowCandidateGuides { get => _showCandidateGuides; set => SetProperty(ref _showCandidateGuides, value); }
    public double ProtectBrushDiameterPixels
    {
        get => _protectBrushDiameterPixels;
        set => SetProperty(ref _protectBrushDiameterPixels, Math.Clamp(value, 10, 300));
    }

    public double RemoveBrushDiameterPixels
    {
        get => _removeBrushDiameterPixels;
        set => SetProperty(ref _removeBrushDiameterPixels, Math.Clamp(value, 10, 300));
    }

    public double CloneStampDiameterPixels
    {
        get => _cloneStampDiameterPixels;
        set => SetProperty(ref _cloneStampDiameterPixels, Math.Clamp(value, 10, 300));
    }

    public double EraserDiameterPixels
    {
        get => _eraserDiameterPixels;
        set => SetProperty(ref _eraserDiameterPixels, Math.Clamp(value, 10, 300));
    }

    // Keeps settings files written by older versions usable. New files store the four tool values above.
    [JsonIgnore]
    public double ManualBrushDiameterPixels
    {
        get => ProtectBrushDiameterPixels;
        set
        {
            ProtectBrushDiameterPixels = value;
            RemoveBrushDiameterPixels = value;
            CloneStampDiameterPixels = value;
            EraserDiameterPixels = value;
        }
    }

    [JsonIgnore]
    public string DetectionSensitivityLabel => UseAdvancedDetectionSettings ? "自定义" : DetectionSensitivity switch
    {
        < 34 => "较低",
        < 67 => "中等",
        _ => "较高"
    };

    public CleanupSettingsSnapshot CreateSnapshot()
    {
        var snapshot = new CleanupSettingsSnapshot
        {
            CleanLeft = CleanLeft, CleanRight = CleanRight, CleanTop = CleanTop, CleanBottom = CleanBottom,
            LeftMarginMm = LeftMarginMm, RightMarginMm = RightMarginMm, TopMarginMm = TopMarginMm, BottomMarginMm = BottomMarginMm,
            DetectionSensitivity = DetectionSensitivity,
            UseAdvancedDetectionSettings = UseAdvancedDetectionSettings,
            RelativeContrastSensitivity = RelativeContrastSensitivity, AbsoluteDarknessSensitivity = AbsoluteDarknessSensitivity,
            SmallDefectSensitivity = SmallDefectSensitivity, TraceConnectionPixels = (int)Math.Round(TraceConnectionPixels),
            EdgeShadowTracePixels = (int)Math.Round(EdgeShadowTracePixels), AutoRemoveConfidencePercent = AutoRemoveConfidencePercent,
            DetectRepeatedDefects = DetectRepeatedDefects,
            DetectPageEdgeShadow = DetectPageEdgeShadow, MaskExpansionPixels = (int)Math.Round(MaskExpansionPixels),
            ProtectPrintedText = ProtectPrintedText, ProtectHandwriting = ProtectHandwriting, ProtectStamps = ProtectStamps,
            ProtectTablesAndPageNumbers = ProtectTablesAndPageNumbers, ContentProtectionStrength = ContentProtectionStrength,
            ProtectionPaddingPixels = (int)Math.Round(ProtectionPaddingPixels),
            BlockOnConflict = BlockOnConflict, RepairMethod = RepairMethod switch
            {
                "快速局部修复" => RepairMode.FastInpaint,
                "纯色填充" => RepairMode.SolidFill,
                "仿制图章修复" => RepairMode.CloneStamp,
                "污点修复" => RepairMode.SpotHealing,
                _ => RepairMode.PaperTexture
            },
            ColorFeathering = ColorFeathering, PreserveDimensionsAndDpi = PreserveDimensionsAndDpi,
            OutputFormat = OutputFormat switch
            {
                "JPEG" => ImageOutputFormat.Jpeg,
                "PNG" => ImageOutputFormat.Png,
                "TIFF" => ImageOutputFormat.Tiff,
                "BMP" => ImageOutputFormat.Bmp,
                _ => ImageOutputFormat.SameAsSource
            },
            JpegQuality = (int)Math.Round(JpegQuality)
        };
        snapshot.Validate();
        return snapshot;
    }

    public void ApplyDetectionPreset(string preset)
    {
        UseAdvancedDetectionSettings = true;
        switch (preset)
        {
            case "Conservative":
                RelativeContrastSensitivity = 45; AbsoluteDarknessSensitivity = 45; SmallDefectSensitivity = 35;
                TraceConnectionPixels = 2; EdgeShadowTracePixels = 6; AutoRemoveConfidencePercent = 85; ContentProtectionStrength = 75;
                break;
            case "HighRecall":
                RelativeContrastSensitivity = 78; AbsoluteDarknessSensitivity = 72; SmallDefectSensitivity = 80;
                TraceConnectionPixels = 4; EdgeShadowTracePixels = 16; AutoRemoveConfidencePercent = 80; ContentProtectionStrength = 70;
                break;
            default:
                RelativeContrastSensitivity = 55; AbsoluteDarknessSensitivity = 55; SmallDefectSensitivity = 50;
                TraceConnectionPixels = 2; EdgeShadowTracePixels = 8; AutoRemoveConfidencePercent = 75; ContentProtectionStrength = 60;
                break;
        }
    }

    public void ResetDetectionDefaults()
    {
        DetectionSensitivity = 55;
        ApplyDetectionPreset("Balanced");
        UseAdvancedDetectionSettings = false;
    }

    public void ResetAllDefaults() => CopyFrom(new CleanupSettings());

    public void CopyFrom(CleanupSettings source)
    {
        CleanLeft = source.CleanLeft; CleanRight = source.CleanRight; CleanTop = source.CleanTop; CleanBottom = source.CleanBottom;
        LeftMarginMm = source.LeftMarginMm; RightMarginMm = source.RightMarginMm; TopMarginMm = source.TopMarginMm; BottomMarginMm = source.BottomMarginMm;
        DetectionSensitivity = source.DetectionSensitivity;
        UseAdvancedDetectionSettings = source.UseAdvancedDetectionSettings;
        RelativeContrastSensitivity = source.RelativeContrastSensitivity; AbsoluteDarknessSensitivity = source.AbsoluteDarknessSensitivity;
        SmallDefectSensitivity = source.SmallDefectSensitivity; TraceConnectionPixels = source.TraceConnectionPixels;
        EdgeShadowTracePixels = source.EdgeShadowTracePixels; AutoRemoveConfidencePercent = source.AutoRemoveConfidencePercent;
        DetectRepeatedDefects = source.DetectRepeatedDefects; DetectPageEdgeShadow = source.DetectPageEdgeShadow;
        MaskExpansionPixels = source.MaskExpansionPixels; ProtectPrintedText = source.ProtectPrintedText;
        ProtectHandwriting = source.ProtectHandwriting; ProtectStamps = source.ProtectStamps;
        ProtectTablesAndPageNumbers = source.ProtectTablesAndPageNumbers; ContentProtectionStrength = source.ContentProtectionStrength;
        ProtectionPaddingPixels = source.ProtectionPaddingPixels; BlockOnConflict = source.BlockOnConflict;
        RepairMethod = source.RepairMethod; ColorFeathering = source.ColorFeathering;
        PreserveDimensionsAndDpi = source.PreserveDimensionsAndDpi; OutputFormat = source.OutputFormat; JpegQuality = source.JpegQuality;
        ShowRulers = source.ShowRulers; ShowCrosshair = source.ShowCrosshair; ShowCandidateGuides = source.ShowCandidateGuides;
        ProtectBrushDiameterPixels = source.ProtectBrushDiameterPixels;
        RemoveBrushDiameterPixels = source.RemoveBrushDiameterPixels;
        CloneStampDiameterPixels = source.CloneStampDiameterPixels;
        EraserDiameterPixels = source.EraserDiameterPixels;
    }
}
