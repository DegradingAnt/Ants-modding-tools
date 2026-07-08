using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Amt.App;

/// The Explorer-style tab silhouette (matched to design-ref/win11-explorer-reference.png): slight-rounded
/// top corners, then at each bottom corner the tab flares WIDER at its base via a CONCAVE quarter-ellipse
/// foot that sweeps out and blends smoothly into the toolbar panel below — exactly the Win11 "This PC" tab.
/// (Author corrected a convex attempt: "the corners are both the wrong way" — concave is the right sweep.)
/// A border-radius can't make that outline, so it's drawn directly. Fill = null draws nothing (an idle tab is
/// just its label); the styles set Fill on hover/active.
public sealed class TabShape : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TabShape, IBrush?>(nameof(Fill));

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    static TabShape() => AffectsRender<TabShape>(FillProperty);

    public override void Render(DrawingContext ctx)
    {
        if (Fill is not { } fill) return;

        double w = Bounds.Width, h = Bounds.Height;
        const double rt = 7;    // top-corner radius (rounded, ~Explorer)
        const double fw = 5;    // foot width  — SMALL flare past the body at the baseline (author: smaller diameter)
        const double fh = 4;    // foot height — short concave sweep, just enough to merge into the toolbar field
        const double k = 0.5523; // quarter-ellipse bezier constant

        // The body occupies x = [fw, w-fw]; the base flares WIDER, out to x = [0, w] at the baseline. Each foot is
        // a concave quarter-ellipse: tangent-vertical where it leaves the body side, tangent-horizontal where it
        // meets the baseline, so the tab widens and melts smoothly into the toolbar — the Win11 "This PC" sweep.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(fw, rt), true);
            g.LineTo(new Point(fw, h - fh));                                                             // down the left side
            g.CubicBezierTo(new Point(fw, h - fh + fh * k), new Point(fw * k, h), new Point(0, h));      // left foot (concave flare)
            g.LineTo(new Point(w, h));                                                                   // baseline (full cell width)
            g.CubicBezierTo(new Point(w - fw * k, h), new Point(w - fw, h - fh + fh * k), new Point(w - fw, h - fh)); // right foot
            g.LineTo(new Point(w - fw, rt));                                                             // up the right side
            // The figure is traced counter-clockwise, so BOTH top corners need a CounterClockwise sweep to
            // round OUTWARD (convex). Clockwise bit a concave notch into whichever corner used it.
            g.ArcTo(new Point(w - fw - rt, 0), new Size(rt, rt), 0, false, SweepDirection.CounterClockwise); // top-right
            g.LineTo(new Point(fw + rt, 0));                                                             // top edge
            g.ArcTo(new Point(fw, rt), new Size(rt, rt), 0, false, SweepDirection.CounterClockwise);     // top-left
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, geo);
    }
}
