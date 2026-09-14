using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Wpf.Models;

namespace ArchiveCleaner.Core.Tests;

public sealed class DefaultSettingsTests
{
    [Fact]
    public void NewSettings_EnableAllFourCandidateEdges()
    {
        var settings = new CleanupSettings();
        Assert.True(settings.CleanLeft);
        Assert.True(settings.CleanRight);
        Assert.True(settings.CleanTop);
        Assert.True(settings.CleanBottom);

        var snapshot = new CleanupSettingsSnapshot();
        Assert.True(snapshot.CleanLeft);
        Assert.True(snapshot.CleanRight);
        Assert.True(snapshot.CleanTop);
        Assert.True(snapshot.CleanBottom);

        settings.RightMarginMm = 37;
        settings.DetectionSensitivity = 88;
        settings.ProtectHandwriting = false;
        settings.RepairMethod = "纯色填充";
        settings.OutputFormat = "PNG";
        settings.ResetAllDefaults();
        Assert.Equal(new CleanupSettings().CreateSnapshot(), settings.CreateSnapshot());
    }

    [Fact]
    public void CancelAvailability_IsExplicitAndDefaultsToDisabled()
    {
        var viewModel = new ArchiveCleaner.Wpf.ViewModels.MainViewModel();
        Assert.False(viewModel.CanCancelOperation);
        viewModel.SetOperationCancelable(true);
        Assert.True(viewModel.CanCancelOperation);
        viewModel.SetOperationCancelable(false);
        Assert.False(viewModel.CanCancelOperation);
    }
}
