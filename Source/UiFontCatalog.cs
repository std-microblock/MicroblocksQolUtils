namespace Celeste.Mod.MicroblocksQolUtils;

internal static class UiFontCatalog {
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private static readonly Lazy<IReadOnlyList<string>> installedFamilies = new(LoadInstalledFamilies);

    public static IReadOnlyList<string> InstalledFamilies => installedFamilies.Value;

    private static IReadOnlyList<string> LoadInstalledFamilies() {
        try {
            string[] names = PortableRasterizer.FontFamilies()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return names.Length == 0 ? [DefaultFontFamily] : names;
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils", $"Unable to enumerate installed fonts: {exception}");
            return [DefaultFontFamily];
        }
    }
}
