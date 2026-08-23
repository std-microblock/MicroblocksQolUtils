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
}
