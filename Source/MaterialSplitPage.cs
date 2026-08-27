using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Shared geometry for material pages with a tab rail on the left and content on the right.
/// The dimensions intentionally match the Mod Options page, which is the visual baseline.
/// </summary>
internal readonly record struct MaterialSplitPageLayout(
    MaterialRect Frame,
    MaterialRect Header,
    MaterialRect Navigation,
    MaterialRect Content
) {
    public const float SurfaceRadius = 28f;
    public const float TabHeight = 60f;
    public const float TabGap = 4f;
    public const float TabRadius = 20f;

    public static MaterialSplitPageLayout Create(float transition) {
        float rise = transition * 32f;
        MaterialRect frame = new(28f, 24f + rise, 1864f, 1030f);
        MaterialRect inner = frame.Inset(38f, 28f, 38f, 26f);
        MaterialRect[] vertical = MaterialLayout.Split(
            inner,
            MaterialAxis.Vertical,
            14f,
            MaterialTrack.Fixed(72f),
            MaterialTrack.Flex()
        );
        MaterialRect[] body = MaterialLayout.Split(
            vertical[1],
            MaterialAxis.Horizontal,
            24f,
            MaterialTrack.Fixed(330f),
            MaterialTrack.Flex()
        );
        return new MaterialSplitPageLayout(frame, vertical[0], body[0], body[1]);
    }

    public MaterialRect NavigationItems(float topInset, float bottomInset = 14f) => new(
        Navigation.X + 12f,
        Navigation.Y + topInset,
        Navigation.Width - 24f,
        Navigation.Height - topInset - bottomInset
    );

    public MaterialRect ContentHeader => new(
        Content.X + 24f,
        Content.Y + 12f,
        Content.Width - 48f,
        58f
    );

    public MaterialRect ContentBody => new(
        Content.X + 24f,
        Content.Y + 82f,
        Content.Width - 56f,
        Content.Height - 100f
    );

    public static MaterialRect Tab(MaterialRect navigationItems, int index, float scrollOffset = 0f) => new(
        navigationItems.X,
        navigationItems.Y + index * (TabHeight + TabGap) - scrollOffset,
        navigationItems.Width,
        TabHeight
    );
}

/// <summary>
/// Shared visual chrome for split material pages. Pages own their data and content controls,
/// while this class keeps headers, surfaces and tab rails visually identical.
/// </summary>
internal static class MaterialSplitPageChrome {
    public static void RenderHeader(
        MaterialRect header,
        string icon,
        string title,
        MaterialPalette palette,
        float alpha
    ) {
        MaterialUiKit.Icon(icon, new Vector2(header.X + 20f, header.Center.Y),
            34f, palette.Primary, alpha, filled: true);
        MaterialUiKit.Text(title, new Vector2(header.X + 52f, header.Center.Y),
            new Vector2(0f, 0.5f), MaterialTextRole.Display, palette.OnSurface, alpha,
            scaleOverride: 0.72f);
    }

    public static void RenderNavigationSurface(
        MaterialRect navigation,
        MaterialPalette palette,
        float alpha
    ) => MaterialUi.RoundedRect(navigation.X, navigation.Y, navigation.Width, navigation.Height,
        MaterialSplitPageLayout.SurfaceRadius, palette.Surface * (0.42f * alpha));

    public static void RenderContentSurface(
        MaterialRect content,
        MaterialPalette palette,
        float alpha
    ) => MaterialUi.RoundedRect(content.X, content.Y, content.Width, content.Height,
        MaterialSplitPageLayout.SurfaceRadius, palette.Surface * (0.38f * alpha));

    public static void RenderTabLead(
        MaterialMotionController motion,
        string key,
        MaterialRect rect,
        string icon,
        bool selected,
        Color accent,
        MaterialPalette palette,
        float alpha
    ) {
        if (selected) {
            MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height,
                MaterialSplitPageLayout.TabRadius,
                Color.Lerp(palette.SurfaceHighest, accent, 0.18f) * (0.98f * alpha));
        }
        motion.RenderStateLayer(key, rect, MaterialSplitPageLayout.TabRadius, accent, alpha);
        MaterialUiKit.Icon(icon, new Vector2(rect.X + 30f, rect.Center.Y), 23f,
            selected ? accent : palette.OnSurfaceVariant, alpha, filled: selected);
    }

    public static void RenderSimpleTab(
        MaterialMotionController motion,
        string key,
        MaterialRect rect,
        string icon,
        string title,
        bool selected,
        Color accent,
        MaterialPalette palette,
        float alpha
    ) {
        RenderTabLead(motion, key, rect, icon, selected, accent, palette, alpha);
        string shownTitle = MaterialTextUtil.Ellipsize(title, rect.Width - 78f,
            0.29f, UiFontWeight.Bold);
        MaterialUiKit.Text(shownTitle, new Vector2(rect.X + 58f, rect.Center.Y),
            new Vector2(0f, 0.5f), MaterialTextRole.Label,
            selected ? palette.OnSurface : palette.OnSurfaceVariant, alpha,
            scaleOverride: 0.29f);
    }

    public static void RenderContentHeading(
        MaterialRect header,
        string icon,
        string title,
        float titleWidth,
        Color accent,
        MaterialPalette palette,
        float alpha
    ) {
        MaterialRect iconTile = new(header.X, header.Center.Y - 21f, 42f, 42f);
        MaterialUi.RoundedRect(iconTile.X, iconTile.Y, iconTile.Width, iconTile.Height, 15f,
            Color.Lerp(palette.SurfaceHighest, accent, 0.26f) * alpha);
        MaterialUiKit.Icon(icon, iconTile.Center, 24f, accent, alpha, filled: true);
        string shownTitle = MaterialTextUtil.Ellipsize(title, titleWidth, 0.47f, UiFontWeight.Bold);
        MaterialUiKit.Text(shownTitle, new Vector2(header.X + 56f, header.Center.Y),
            new Vector2(0f, 0.5f), MaterialTextRole.Title, palette.OnSurface, alpha,
            scaleOverride: 0.47f);
    }
}
