using System.Drawing;
using System.Drawing.Drawing2D;
using KeList.Interop;

namespace KeList.Services;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var panelPath = CreateRoundedRectangle(new RectangleF(2.5f, 2.5f, 27, 27), 6);
        using var panelBrush = new SolidBrush(Color.FromArgb(255, 248, 248, 248));
        using var borderPen = new Pen(Color.FromArgb(220, 45, 45, 45), 1.6f);
        graphics.FillPath(panelBrush, panelPath);
        graphics.DrawPath(borderPen, panelPath);

        using var checkPen = new Pen(Color.FromArgb(245, 30, 30, 30), 2.1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var linePen = new Pen(Color.FromArgb(220, 45, 45, 45), 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        graphics.DrawLines(checkPen, [new PointF(7, 10), new PointF(9, 12), new PointF(12, 8)]);
        graphics.DrawLine(linePen, 15, 10, 24, 10);
        graphics.DrawLines(checkPen, [new PointF(7, 20), new PointF(9, 22), new PointF(12, 18)]);
        graphics.DrawLine(linePen, 15, 20, 24, 20);

        var iconHandle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(iconHandle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
