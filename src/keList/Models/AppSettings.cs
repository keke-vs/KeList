namespace KeList.Models;

public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 390;
    public double Height { get; set; } = 520;
    public double FontSize { get; set; } = 16;
    public double BackgroundOpacity { get; set; } = 0.78;
    public bool IsTopmost { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool StartWithWindows { get; set; }
    public bool HasShownTrayHint { get; set; }
}
