using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KeList.Services;

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer()
        : base(new TrayMenuColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Color.FromArgb(250, 250, 250));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(34, 0, 0, 0), 1);
        var bounds = new Rectangle(
            e.AffectedBounds.X,
            e.AffectedBounds.Y,
            Math.Max(0, e.AffectedBounds.Width - 1),
            Math.Max(0, e.AffectedBounds.Height - 1));
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(5, 2, e.Item.Width - 10, e.Item.Height - 4);
        using var path = CreateRoundedRectangle(bounds, 7);
        using var brush = new SolidBrush(Color.FromArgb(235, 235, 235));
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(24, 0, 0, 0), 1);
        e.Graphics.DrawLine(pen, 40, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(230, 24, 24, 24), 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var centerX = e.ImageRectangle.Left + (e.ImageRectangle.Width / 2f);
        var centerY = e.ImageRectangle.Top + (e.ImageRectangle.Height / 2f);
        e.Graphics.DrawLines(
            pen,
            [
                new PointF(centerX - 5, centerY),
                new PointF(centerX - 1.5f, centerY + 3.5f),
                new PointF(centerX + 5.5f, centerY - 4)
            ]);
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

    private sealed class TrayMenuColorTable : ProfessionalColorTable
    {
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.Transparent;
        public override Color MenuBorder => Color.Transparent;
        public override Color ToolStripDropDownBackground => Color.FromArgb(250, 250, 250);
        public override Color ImageMarginGradientBegin => Color.FromArgb(250, 250, 250);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(250, 250, 250);
        public override Color ImageMarginGradientEnd => Color.FromArgb(250, 250, 250);
        public override Color SeparatorDark => Color.Transparent;
        public override Color SeparatorLight => Color.Transparent;
    }
}
