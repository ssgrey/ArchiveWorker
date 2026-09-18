// 诊断脚本：检查路径匹配问题
//
// 问题点：AssemblePdfOutputsAsync 第833行
// var records = pages.Select(page => report.Pages.FirstOrDefault(record =>
//     record.SourcePath.Equals(page.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
//
// BatchProcessor.ProcessOneAsync 第116行设置：
// SourcePath = Path.GetFullPath(sourcePath)
//
// 但是 page.FilePath 可能不是完整路径格式
//
// 修复：应该统一使用 Path.GetFullPath 进行比较
