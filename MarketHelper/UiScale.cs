using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;

namespace MarketHelper;

/// <summary>
/// High-DPI / 4K QOL helpers, ported from the conventions used across FCTracker,
/// VenueHelper and HousingLottoTracker. Every fixed pixel dimension in a window
/// should pass through these so nothing clips or overflows at scales like 4K @ 230%.
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

    // Shared palette matching the VenueHelper convention.
    public static readonly Vector4 Gold = new(1.0f, 0.84f, 0.10f, 1.0f);
    public static readonly Vector4 Green = new(0.40f, 0.95f, 0.40f, 1.0f);
    public static readonly Vector4 Red = new(0.95f, 0.40f, 0.40f, 1.0f);
    public static readonly Vector4 Blue = new(0.50f, 0.75f, 1.0f, 1.0f);
    public static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1.0f);
}
