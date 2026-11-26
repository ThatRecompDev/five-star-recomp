// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The render targets <c>--d3d12-watch-target WxH:format[,WxH:format...]</c> follows (the lit HDR is <c>2560x1440:10</c>,
/// the albedo <c>2560x1440:29</c>): every pass that writes one in the frame after a captured present gets it read back,
/// so the first writer that breaks it shows.
/// </summary>
internal readonly record struct TargetShape(IReadOnlyList<(ulong Width, uint Height, int Format)> Shapes)
{
    /// <summary>"WxH:format", or several separated by commas, as shapes; null when any part is not one.</summary>
    public static TargetShape? Parse(string text)
    {
        var shapes = new List<(ulong Width, uint Height, int Format)>();

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var shape = ParseOne(part);

            if (shape is null)
            {
                return null;
            }

            shapes.Add(shape.Value);
        }

        if (shapes.Count == 0)
        {
            return null;
        }

        return new TargetShape(shapes);
    }

    /// <summary>Whether a resource of this width, height and format is one the watch follows.</summary>
    public bool Matches(ulong width, uint height, int format)
    {
        return Shapes.Any(shape => shape.Width == width && shape.Height == height && shape.Format == format);
    }

    private static (ulong Width, uint Height, int Format)? ParseOne(string text)
    {
        const int WidthPart = 0;
        const int HeightPart = 1;
        const int FormatPart = 2;
        const int Parts = 3;
        string[] parts = text.Split(':', 'x');

        if (parts.Length == Parts
            && ulong.TryParse(parts[WidthPart], NumberStyles.None, CultureInfo.InvariantCulture, out ulong width)
            && uint.TryParse(parts[HeightPart], NumberStyles.None, CultureInfo.InvariantCulture, out uint height)
            && int.TryParse(parts[FormatPart], NumberStyles.None, CultureInfo.InvariantCulture, out int format))
        {
            return (width, height, format);
        }

        return null;
    }
}
