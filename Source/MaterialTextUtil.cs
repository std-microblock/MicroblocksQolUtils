using System.Globalization;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MaterialTextUtil {
    private const string Ellipsis = "…";

    public static string Ellipsize(
        string value,
        float maxWidth,
        float scale,
        UiFontWeight weight = UiFontWeight.Regular
    ) {
        if (string.IsNullOrEmpty(value) || maxWidth <= 0f) return maxWidth > 0f ? value : "";
        if (SystemTtfFont.MeasureVisible(value, scale, weight).X <= maxWidth) return value;
        if (SystemTtfFont.MeasureVisible(Ellipsis, scale, weight).X > maxWidth) return "";

        int[] textElements = StringInfo.ParseCombiningCharacters(value);
        int low = 0;
        int high = textElements.Length;
        while (low < high) {
            int middle = (low + high + 1) / 2;
            int end = middle < textElements.Length ? textElements[middle] : value.Length;
            string candidate = value[..end].TrimEnd() + Ellipsis;
            if (SystemTtfFont.MeasureVisible(candidate, scale, weight).X <= maxWidth) low = middle;
            else high = middle - 1;
        }

        int prefixEnd = low < textElements.Length ? textElements[low] : value.Length;
        return value[..prefixEnd].TrimEnd() + Ellipsis;
    }

    public static List<string> WrapLines(
        string value,
        float maxWidth,
        float scale,
        int maxLines,
        UiFontWeight weight = UiFontWeight.Regular
    ) {
        List<string> lines = [];
        if (string.IsNullOrWhiteSpace(value) || maxWidth <= 0f || maxLines <= 0) return lines;
        bool truncated = false;
        foreach (string paragraph in value.Replace("\r", "").Split('\n')) {
            string remaining = paragraph.Trim();
            if (remaining.Length == 0) continue;
            while (remaining.Length > 0) {
                if (lines.Count >= maxLines) {
                    truncated = true;
                    break;
                }
                if (SystemTtfFont.MeasureVisible(remaining, scale, weight).X <= maxWidth) {
                    lines.Add(remaining);
                    remaining = "";
                    continue;
                }
                int[] elements = StringInfo.ParseCombiningCharacters(remaining);
                int low = 1;
                int high = elements.Length;
                while (low < high) {
                    int middle = (low + high + 1) / 2;
                    int end = middle < elements.Length ? elements[middle] : remaining.Length;
                    if (SystemTtfFont.MeasureVisible(remaining[..end], scale, weight).X <= maxWidth) low = middle;
                    else high = middle - 1;
                }
                int take = low < elements.Length ? elements[low] : remaining.Length;
                int whitespace = remaining.LastIndexOf(' ', Math.Max(0, take - 1), take);
                if (whitespace > 0) take = whitespace;
                lines.Add(remaining[..take].TrimEnd());
                remaining = remaining[take..].TrimStart();
            }
            if (truncated) break;
        }
        if (truncated && lines.Count > 0) {
            lines[^1] = Ellipsize(lines[^1] + Ellipsis, maxWidth, scale, weight);
        }
        return lines;
    }
}
