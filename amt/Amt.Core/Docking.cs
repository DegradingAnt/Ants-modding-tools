namespace Amt.Core;

/// The three sizes a docked side panel can take. There is no "destroyed" — a panel always leaves at least a
/// slim handle on its window face so it can be brought back (the dock engine's core promise to the user).
public enum DockPanelState
{
    Expanded,   // full panel at the user's chosen width
    IconRail,   // narrow icon-only strip (the splitter snaps here when dragged very narrow)
    Hidden,     // just the slim ⟩ edge handle
}

/// The GUI-agnostic geometry rules of the dock engine (AMT-03). The App layer owns the controls and the drag
/// mechanics; every NUMBER and RULE lives here so the same engine drives future panels and pop-out windows
/// (AMT-04) and can be unit-tested without a UI. All widths are logical (DPI-independent) pixels.
public static class DockRules
{
    public const double RailWidth = 44;          // the icon-only rail
    public const double HandleWidth = 24;        // the hidden-state ⟩ edge handle
    public const double MinExpandedWidth = 170;  // an expanded panel is never narrower — below this it's the rail
    public const double DefaultSidebarWidth = 283;
    public const double AutoHideBelow = 900;     // window narrower than this auto-hides side panels (author spec)

    /// Clamp a panel width to its legal expanded range: at least MinExpandedWidth, at most HALF the window
    /// (the hard cap — a side panel may never squeeze the content below its own share, author spec).
    public static double ClampWidth(double desired, double windowWidth) =>
        Math.Min(Math.Max(desired, MinExpandedWidth), Math.Max(MinExpandedWidth, windowWidth * 0.5));

    /// What a live splitter drag at this raw width MEANS: below the midpoint between the rail and the minimum
    /// expanded width the user is asking for the rail; anywhere above, an expanded panel. (The midpoint gives
    /// the snap a little hysteresis-free dead zone instead of flickering right at MinExpandedWidth.)
    public static DockPanelState StateForDrag(double rawWidth) =>
        rawWidth < (RailWidth + MinExpandedWidth) / 2 ? DockPanelState.IconRail : DockPanelState.Expanded;

    /// The faces-independent corner rule (author, 2026-07-04): a panel face that is DOCKED — flush against a
    /// window face or another panel — is a square seam; a corner may round only when BOTH faces meeting at it
    /// are free. Returns (topLeft, topRight, bottomRight, bottomLeft) radii.
    public static (double TL, double TR, double BR, double BL) Corners(
        bool leftDocked, bool topDocked, bool rightDocked, bool bottomDocked, double radius) => (
        leftDocked || topDocked ? 0 : radius,
        topDocked || rightDocked ? 0 : radius,
        rightDocked || bottomDocked ? 0 : radius,
        bottomDocked || leftDocked ? 0 : radius);

    /// Compact monogram for icon-rail chips: initials of the first two real words ("Mobs & Animals" → "MA"),
    /// or the first three letters of a single word ("Performance" → "Per").
    public static string Monogram(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != "&").ToArray();
        if (words.Length == 0) return "";
        return words.Length >= 2
            ? string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])))
            : words[0][..Math.Min(3, words[0].Length)];
    }
}
