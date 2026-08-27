namespace Celeste.Mod.MicroblocksQolUtils;

internal static class UiFontCatalog {
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private static readonly Lazy<FontCatalog> catalog = new(LoadCatalog);

    public static IReadOnlyList<string> InstalledFamilies => catalog.Value.InstalledFamilies;

    public static string ResolveFamily(string? configuredFamily) {
        string requested = string.IsNullOrWhiteSpace(configuredFamily)
            ? DefaultFontFamily
            : configuredFamily.Trim();
        string? installed = InstalledFamilies.FirstOrDefault(name =>
            string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (installed is not null) return installed;

        return InstalledFamilies.FirstOrDefault(name =>
                   string.Equals(name, DefaultFontFamily, StringComparison.OrdinalIgnoreCase))
               ?? catalog.Value.RecommendedFamily
               ?? InstalledFamilies.FirstOrDefault()
               ?? DefaultFontFamily;
    }

    private static FontCatalog LoadCatalog() {
        try {
            string[] nativeNames = PortableRasterizer.FontFamilies()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Non-Windows native enumeration puts a CJK-capable platform fallback first.
            string? recommended = OperatingSystem.IsWindows() ? null : nativeNames.FirstOrDefault();
            string[] names = nativeNames
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (names.Length == 0) names = [DefaultFontFamily];
            return new FontCatalog(names, recommended);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils", $"Unable to enumerate installed fonts: {exception}");
            return new FontCatalog([DefaultFontFamily], null);
        }
    }

    private sealed record FontCatalog(IReadOnlyList<string> InstalledFamilies, string? RecommendedFamily);
}
