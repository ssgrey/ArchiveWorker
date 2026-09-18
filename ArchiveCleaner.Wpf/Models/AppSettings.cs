using System.Windows.Input;

namespace ArchiveCleaner.Wpf.Models;

public class AppSettings
{
    /// <summary>
    /// 水平辅助绘制按键
    /// </summary>
    public Key HorizontalAssistKey { get; set; } = Key.LeftCtrl;

    /// <summary>
    /// 垂直辅助绘制按键
    /// </summary>
    public Key VerticalAssistKey { get; set; } = Key.LeftShift;
}
