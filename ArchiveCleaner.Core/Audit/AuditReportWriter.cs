using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ArchiveCleaner.Core.Audit;

public static class AuditReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task WriteAsync(BatchAuditReport report, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        var jsonPath = Path.Combine(report.OutputDirectory, "batch-report.json");
        var csvPath = Path.Combine(report.OutputDirectory, "batch-report.csv");
        var jsonTemp = jsonPath + $".{Guid.NewGuid():N}.tmp";
        var csvTemp = csvPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(jsonTemp, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(csvTemp, BuildCsv(report.Pages), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            File.Move(jsonTemp, jsonPath, overwrite: true);
            File.Move(csvTemp, csvPath, overwrite: true);
        }
        finally
        {
            TryDelete(jsonTemp);
            TryDelete(csvTemp);
        }
    }

    public static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string BuildCsv(IEnumerable<PageAuditRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("源文件,输出文件,源SHA-256,输出SHA-256,源尺寸,输出尺寸,源DPI,输出DPI,源格式,输出格式,自动清理区域,自动清理像素,保护区域,待复核区域,人工决定,人工保护像素,人工去除像素,开始时间,结束时间,耗时毫秒,状态,错误,警告");
        foreach (var record in records)
        {
            var fields = new[]
            {
                record.SourcePath, record.OutputPath, record.SourceSha256, record.OutputSha256,
                $"{record.SourceWidth}x{record.SourceHeight}", $"{record.OutputWidth}x{record.OutputHeight}",
                $"{record.SourceDpiX:0.##}x{record.SourceDpiY:0.##}", $"{record.OutputDpiX:0.##}x{record.OutputDpiY:0.##}",
                record.SourceFormat, record.OutputFormat, record.AutoRemoveRegionCount.ToString(), record.AutoRemovePixelArea.ToString(),
                record.ProtectedRegionCount.ToString(), record.NeedsReviewRegionCount.ToString(),
                string.Join(";", record.ManualDecisions.Select(item => $"{item.RegionId}:{item.Decision}")),
                record.ManualProtectionPixelArea.ToString(), record.ManualRemovalPixelArea.ToString(),
                record.StartedAt.ToString("O"), record.FinishedAt.ToString("O"), record.ElapsedMilliseconds.ToString(),
                record.Status.ToString(), record.Error, string.Join("；", record.Warnings)
            };
            builder.AppendLine(string.Join(',', fields.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
