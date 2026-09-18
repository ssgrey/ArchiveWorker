# 测试路径匹配问题
$pdfPath = "D:\test\sample.pdf"
$cachedPath = "C:\Users\Admin\AppData\Local\Temp\ArchiveCleaner\pdf-pages\ABC123-page1.png"

Write-Host "PDF源路径: $pdfPath"
Write-Host "缓存PNG路径: $cachedPath"
Write-Host ""
Write-Host "在ProcessBatchAsync中:"
Write-Host "  Dictionary键 (item.FilePath): $cachedPath"
Write-Host ""
Write-Host "在BatchProcessor.ProcessOneAsync中:"
Write-Host "  查找键 (sourcePath): $cachedPath"
Write-Host "  GetFullPath后: " + [System.IO.Path]::GetFullPath($cachedPath)
