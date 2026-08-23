using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum MaterialTextRole {
    Display,
    Title,
    Section,
    Body,
    Label,
    Caption
}

internal readonly record struct MaterialRect(float X, float Y, float Width, float Height) {
    public Vector2 Center => new(X + Width / 2f, Y + Height / 2f);
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(Vector2 point) => MaterialUi.Contains(point, X, Y, Width, Height);

    public MaterialRect Inset(float all) => Inset(all, all, all, all);

    public MaterialRect Inset(float horizontal, float vertical) =>
        Inset(horizontal, vertical, horizontal, vertical);

    public MaterialRect Inset(float left, float top, float right, float bottom) => new(
        X + left,
        Y + top,
        Math.Max(0f, Width - left - right),
        Math.Max(0f, Height - top - bottom)
    );

    public MaterialRect Offset(float x, float y) => new(X + x, Y + y, Width, Height);
}

internal enum MaterialAxis {
    Horizontal,
    Vertical
}

internal readonly record struct MaterialTrack(float Value, bool Flexible) {
    public static MaterialTrack Fixed(float pixels) => new(Math.Max(0f, pixels), false);
    public static MaterialTrack Flex(float weight = 1f) => new(Math.Max(0.001f, weight), true);
}

internal static class MaterialSpacing {
    public const float Xs = 8f;
    public const float Sm = 12f;
    public const float Md = 16f;
    public const float Lg = 24f;
    public const float Xl = 32f;
    public const float Xxl = 40f;
}

internal static class MaterialLayout {
    public static MaterialRect[] Split(
        MaterialRect bounds,
        MaterialAxis axis,
        float gap,
        params MaterialTrack[] tracks
    ) {
        if (tracks.Length == 0) return [];
        float available = (axis == MaterialAxis.Horizontal ? bounds.Width : bounds.Height)
            - gap * Math.Max(0, tracks.Length - 1);
        float fixedSize = tracks.Where(track => !track.Flexible).Sum(track => track.Value);
        float totalWeight = tracks.Where(track => track.Flexible).Sum(track => track.Value);
        float flexibleSize = Math.Max(0f, available - fixedSize);
        MaterialRect[] result = new MaterialRect[tracks.Length];
        float cursor = axis == MaterialAxis.Horizontal ? bounds.X : bounds.Y;
        for (int index = 0; index < tracks.Length; index++) {
            MaterialTrack track = tracks[index];
            float size = track.Flexible ? flexibleSize * track.Value / totalWeight : track.Value;
            result[index] = axis == MaterialAxis.Horizontal
                ? new MaterialRect(cursor, bounds.Y, size, bounds.Height)
                : new MaterialRect(bounds.X, cursor, bounds.Width, size);
            cursor += size + gap;
        }
        return result;
    }

    public static MaterialRect GridCell(
        MaterialRect bounds,
        int columns,
        int rows,
        float columnGap,
        float rowGap,
        int index
    ) {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        int column = Math.Clamp(index % columns, 0, columns - 1);
        int row = Math.Clamp(index / columns, 0, rows - 1);
        float width = Math.Max(0f, (bounds.Width - columnGap * (columns - 1)) / columns);
        float height = Math.Max(0f, (bounds.Height - rowGap * (rows - 1)) / rows);
        return new MaterialRect(
            bounds.X + column * (width + columnGap),
            bounds.Y + row * (height + rowGap),
            width,
            height
        );
    }
}

internal sealed class MaterialScrollController {
    public float Offset { get; private set; }
    public float Target { get; private set; }

    public void Update(float maximum) {
        maximum = Math.Max(0f, maximum);
        Target = Math.Clamp(Target, 0f, maximum);
        float speed = Math.Max(420f, Math.Abs(Target - Offset) * 10f);
        Offset = Calc.Approach(Offset, Target, speed * Engine.RawDeltaTime);
        Offset = Math.Clamp(Offset, 0f, maximum);
    }

    public void Scroll(float pixels, float maximum) {
        Target = Math.Clamp(Target + pixels, 0f, Math.Max(0f, maximum));
    }

    public void EnsureVisible(float top, float bottom, float viewportHeight, float maximum) {
        if (top < Target) Target = top;
        else if (bottom > Target + viewportHeight) Target = bottom - viewportHeight;
        Target = Math.Clamp(Target, 0f, Math.Max(0f, maximum));
    }

    public void Reset() {
        Offset = 0f;
        Target = 0f;
    }
}

internal sealed class MaterialScrollViewport : IDisposable {
    // Changing render targets while drawing a page can discard everything already batched on
    // the backbuffer. Scissoring keeps the existing page intact while still clipping scrolling
    // content to its viewport. Capture the active SpriteBatch state instead of guessing from
    // HiresRenderer.DrawToBuffer: fullscreen/letterboxed HUD passes can use a different transform.
    private static readonly RasterizerState ScissorRasterizer = new() {
        CullMode = CullMode.None,
        ScissorTestEnable = true
    };

    public MaterialScrollViewport(string name) {
        _ = name;
    }

    public void Render(MaterialRect bounds, System.Action drawContents) {
        GraphicsDevice graphics = Engine.Graphics.GraphicsDevice;
        SpriteBatchState state = SpriteBatchState.Capture(Draw.SpriteBatch);
        Rectangle previousScissor = graphics.ScissorRectangle;
        Rectangle scissor = ScreenScissor(bounds, graphics.Viewport, state.TransformMatrix);
        if (scissor.Width <= 0 || scissor.Height <= 0) return;

        Draw.SpriteBatch.End();
        try {
            graphics.ScissorRectangle = scissor;
            state.Begin(Draw.SpriteBatch, ScissorRasterizer);
            try {
                drawContents();
            } finally {
                Draw.SpriteBatch.End();
            }
        } finally {
            graphics.ScissorRectangle = previousScissor;
            state.Begin(Draw.SpriteBatch, state.RasterizerState);
        }
    }

    public void Dispose() { }

    private static Rectangle ScreenScissor(MaterialRect bounds, Viewport viewport, Matrix renderMatrix) {
        Vector2 viewportOrigin = new(viewport.X, viewport.Y);
        Vector2 first = Vector2.Transform(new Vector2(bounds.X, bounds.Y), renderMatrix)
            + viewportOrigin;
        Vector2 second = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), renderMatrix)
            + viewportOrigin;
        int viewportRight = viewport.X + viewport.Width;
        int viewportBottom = viewport.Y + viewport.Height;
        int left = Math.Clamp((int)MathF.Floor(Math.Min(first.X, second.X)), viewport.X, viewportRight);
        int top = Math.Clamp((int)MathF.Floor(Math.Min(first.Y, second.Y)), viewport.Y, viewportBottom);
        int right = Math.Clamp((int)MathF.Ceiling(Math.Max(first.X, second.X)), viewport.X, viewportRight);
        int bottom = Math.Clamp((int)MathF.Ceiling(Math.Max(first.Y, second.Y)), viewport.Y, viewportBottom);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private readonly record struct SpriteBatchState(
        SpriteSortMode SortMode,
        BlendState BlendState,
        SamplerState SamplerState,
        DepthStencilState DepthStencilState,
        RasterizerState RasterizerState,
        Effect? Effect,
        Matrix TransformMatrix
    ) {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo BeginCalledField = RequiredField("beginCalled", "_beginCalled");
        private static readonly FieldInfo SortModeField = RequiredField("sortMode", "_sortMode");
        private static readonly FieldInfo BlendStateField = RequiredField("blendState", "_blendState");
        private static readonly FieldInfo SamplerStateField = RequiredField("samplerState", "_samplerState");
        private static readonly FieldInfo DepthStencilStateField = RequiredField(
            "depthStencilState", "_depthStencilState");
        private static readonly FieldInfo RasterizerStateField = RequiredField(
            "rasterizerState", "_rasterizerState");
        private static readonly FieldInfo EffectField = RequiredField("customEffect", "_customEffect");
        private static readonly FieldInfo TransformMatrixField = RequiredField(
            "transformMatrix", "_transformMatrix");

        public static SpriteBatchState Capture(SpriteBatch spriteBatch) {
            if (!Read<bool>(spriteBatch, BeginCalledField))
                throw new InvalidOperationException("Material scroll viewport requires an active SpriteBatch.");
            return new SpriteBatchState(
                Read<SpriteSortMode>(spriteBatch, SortModeField),
                Read<BlendState>(spriteBatch, BlendStateField),
                Read<SamplerState>(spriteBatch, SamplerStateField),
                Read<DepthStencilState>(spriteBatch, DepthStencilStateField),
                Read<RasterizerState>(spriteBatch, RasterizerStateField),
                (Effect?)EffectField.GetValue(spriteBatch),
                Read<Matrix>(spriteBatch, TransformMatrixField)
            );
        }

        public void Begin(SpriteBatch spriteBatch, RasterizerState rasterizerState) => spriteBatch.Begin(
            SortMode,
            BlendState,
            SamplerState,
            DepthStencilState,
            rasterizerState,
            Effect,
            TransformMatrix
        );

        private static FieldInfo RequiredField(params string[] names) {
            Type type = typeof(SpriteBatch);
            foreach (string name in names) {
                FieldInfo? field = type.GetField(name, InstanceFields);
                if (field is not null) return field;
            }
            throw new MissingFieldException(type.FullName, string.Join(" or ", names));
        }

        private static T Read<T>(SpriteBatch spriteBatch, FieldInfo field) =>
            (T)(field.GetValue(spriteBatch)
                ?? throw new InvalidOperationException($"SpriteBatch.{field.Name} was null."));
    }
}

/// <summary>
/// Shared Material You widget primitives. Chapter select and the in-level settings
/// overlay use the same typography hierarchy, surfaces, focus state and AA geometry.
/// </summary>
internal static class MaterialUiKit {
    public static void Surface(
        MaterialRect rect,
        float radius,
        MaterialPalette palette,
        float alpha = 1f,
        bool elevated = true
    ) {
        if (elevated) {
            MaterialUi.AcrylicSurface(
                rect.X, rect.Y, rect.Width, rect.Height, radius,
                palette.SurfaceHigh * alpha, palette.Outline * alpha
            );
        } else {
            MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius,
                palette.Surface * alpha);
        }
    }

    public static void Card(
        MaterialRect rect,
        MaterialPalette palette,
        bool selected,
        float alpha = 1f
    ) {
        Color fill = selected ? palette.SurfaceHighest : palette.SurfaceHigh * 0.90f;
        Color outline = selected ? palette.Primary : palette.Outline;
        MaterialUi.RoundedRect(rect.X, rect.Y + 6f, rect.Width, rect.Height, 30f,
            Color.Black * 0.18f * alpha);
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 30f, fill * alpha);
        MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 30f,
            selected ? 3f : 1f, outline * alpha);
    }

    public static void NavigationPill(
        MaterialRect rect,
        MaterialPalette palette,
        bool selected,
        float alpha = 1f
    ) {
        if (!selected) return;
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f,
            palette.Primary * 0.92f * alpha);
    }

    public static void Chip(
        string text,
        Vector2 rightTop,
        MaterialPalette palette,
        bool selected,
        float alpha = 1f
    ) {
        const float scale = 0.27f;
        float width = Math.Max(82f, SystemTtfFont.Measure(text, scale, UiFontWeight.Bold).X + 30f);
        MaterialUi.RoundedRect(rightTop.X - width, rightTop.Y, width, 32f, 16f,
            (selected ? palette.Primary : palette.SurfaceHighest) * alpha);
        Text(text, new Vector2(rightTop.X - width / 2f, rightTop.Y + 16f),
            new Vector2(0.5f), MaterialTextRole.Label,
            selected ? palette.OnPrimary : palette.OnSurfaceVariant, alpha, scaleOverride: scale);
    }

    public static void Text(
        string text,
        Vector2 position,
        Vector2 justify,
        MaterialTextRole role,
        Color color,
        float alpha = 1f,
        float? scaleOverride = null
    ) {
        (float scale, UiFontWeight weight) = role switch {
            MaterialTextRole.Display => (0.88f, UiFontWeight.Bold),
            MaterialTextRole.Title => (0.48f, UiFontWeight.Bold),
            MaterialTextRole.Section => (0.46f, UiFontWeight.Bold),
            MaterialTextRole.Body => (0.38f, UiFontWeight.Regular),
            MaterialTextRole.Label => (0.31f, UiFontWeight.Bold),
            _ => (0.31f, UiFontWeight.Regular)
        };
        SystemTtfFont.Draw(text, position, justify, scaleOverride ?? scale, color * alpha, weight: weight);
    }

    public static void Cursor(Vector2 position, MaterialPalette palette, float alpha) {
        MaterialUi.Circle(position, 10f, Color.Black * 0.55f * alpha);
        MaterialUi.Circle(position, 7f, palette.Primary * alpha);
    }
}
