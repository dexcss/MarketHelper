using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;

namespace MarketHelper;

/// <summary>
/// High-DPI / 4K helpers (SW/SV/Dummy/WrapText) and the legacy colour names used across the
/// plugin's windows. As of the Fancy UI reskin the palette VALUES come from
/// <see cref="FancyUI.Theme"/> so every existing TextColored call renders in the theme:
/// Gold→accent violet, Green→good teal, Red→danger, Blue/Grey→muted.
///
/// Every fixed pixel dimension in a window should pass through <see cref="SW"/> so nothing
/// clips or overflows at scales like 4K @ 230%.
/// </summary>
public static class UiScale
{
    /// <summary>Dalamud's current global UI scale factor.</summary>
    public static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary>
    /// Scale a fixed pixel measurement by the global UI scale. Use for widths, child
    /// heights, table column widths, SameLine spacing, etc. Named SW to match VenueHelper.
    /// </summary>
    public static float SW(float px) => px * ImGuiHelpers.GlobalScale;

    /// <summary>Scale a Vector2 of fixed pixel sizes (e.g. window MinimumSize).</summary>
    public static Vector2 SV(float x, float y) => new Vector2(x, y) * ImGuiHelpers.GlobalScale;

    /// <summary>Vertical spacing that respects UI scale (wraps ImGuiHelpers.ScaledDummy).</summary>
    public static void Dummy(float px) => ImGuiHelpers.ScaledDummy(px);

    /// <summary>Colored text that wraps at the window's right edge instead of overflowing.</summary>
    public static void WrapText(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>Plain wrapped text.</summary>
    public static void WrapText(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    // ---- Legacy palette names, now themed ------------------------------------------------
    // Gold/Green/Red/Blue/Grey predate the Fancy UI and are used across every tab partial;
    // they now resolve to the FancyUI.Theme tokens so the whole plugin shares one palette.

    /// <summary>Accent / attention (was gold, now the violet accent).</summary>
    public static readonly Vector4 Gold = FancyUI.Theme.Accent;

    /// <summary>Success / running (was green, now the theme's good teal).</summary>
    public static readonly Vector4 Green = FancyUI.Theme.Good;

    /// <summary>Danger (was red, now the theme's danger red).</summary>
    public static readonly Vector4 Red = FancyUI.Theme.Danger;

    /// <summary>Informational (was blue, now muted).</summary>
    public static readonly Vector4 Blue = FancyUI.Theme.Muted;

    /// <summary>Dim / idle (was grey, now muted).</summary>
    public static readonly Vector4 Grey = FancyUI.Theme.Muted;

    /// <summary>A scaled separator with padding above and below.</summary>
    public static void Divider()
    {
        Dummy(4f);
        ImGui.Separator();
        Dummy(4f);
    }
}
