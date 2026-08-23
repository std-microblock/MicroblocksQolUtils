using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class QolSettingsOverlay : Entity, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const float RowHeight = 58f;
    private const float RowGap = 8f;

    private static QolSettingsOverlay? activePage;

    private readonly Level level;
    private readonly int returnIndex;
    private readonly bool minimal;
    private readonly bool oldAllowHudHide;
    private readonly List<SettingsTab> tabs;
    private readonly MaterialScrollController rowScroll = new();
    private readonly MaterialScrollViewport rowViewport = new("mqol-settings-rows");
    private int selectedTab;
    private int selectedRow;
    private float inputDelay = 0.18f;
    private bool capturingKey;
    private SettingRow? keyRow;
    private bool closing;

    public static QolSettingsOverlay? ActivePage => activePage is { Scene: not null, Visible: true }
        ? activePage
        : null;

    public bool SuppressNormalRender { get; set; }

    public QolSettingsOverlay(Level level, int returnIndex, bool minimal, bool oldAllowHudHide) {
        this.level = level;
        this.returnIndex = returnIndex;
        this.minimal = minimal;
        this.oldAllowHudHide = oldAllowHudHide;
        tabs = BuildTabs();
        Tag = Tags.HUD | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -2_000_000;
    }

    public override void Added(Scene scene) {
        base.Added(scene);
        activePage = this;
    }

    public override void Removed(Scene scene) {
        if (activePage == this) activePage = null;
        rowViewport.Dispose();
        base.Removed(scene);
    }

    public override void Update() {
        base.Update();
        if (closing) return;
        OverlayLayout layout = OverlayLayout.Create();
        rowScroll.Update(MaxRowScroll(layout));
        inputDelay -= Engine.DeltaTime;
        if (capturingKey) {
            UpdateKeyCapture();
            return;
        }
        if (inputDelay > 0f) return;

        if (Input.Pause.Pressed) {
            CloseToGame();
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            CloseToPauseMenu();
            return;
        }
        if (MInput.Keyboard.Pressed(Keys.Tab)
            || MInput.Keyboard.Pressed(Keys.PageDown)
            || MInput.Keyboard.Pressed(Keys.PageUp)) {
            bool backwards = MInput.Keyboard.Pressed(Keys.PageUp)
                || MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift);
            SelectTab(selectedTab + (backwards ? -1 : 1));
        }

        List<SettingRow> rows = CurrentRows;
        if (rows.Count > 0) {
            if (Input.MenuUp.Pressed) SelectRow(selectedRow - 1);
            else if (Input.MenuDown.Pressed) SelectRow(selectedRow + 1);
            else if (Input.MenuLeft.Pressed) ActivateRow(-1);
            else if (Input.MenuRight.Pressed) ActivateRow(1);
            else if (Input.MenuConfirm.Pressed || MInput.Keyboard.Pressed(Keys.Enter)) ActivateRow(1);
        }
        UpdateMouse();
    }

    public override void Render() {
        base.Render();
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        OverlayLayout layout = OverlayLayout.Create();
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * 0.92f);
        MaterialUiKit.Surface(
            layout.Panel,
            42f,
            palette with { SurfaceHigh = palette.Surface * (acrylicActive ? 0.80f : 0.97f) }
        );

        MaterialUiKit.Text("Microblock 的 QOL 工具", new Vector2(layout.Header.X, layout.Header.Y),
            Vector2.Zero, MaterialTextRole.Display, palette.OnSurface);
        string subtitle = $"{tabs[selectedTab].Title}  ·  键盘、鼠标均可操作";
        MaterialUiKit.Text(subtitle, new Vector2(layout.Header.X + 2f, layout.Header.Y + 52f),
            Vector2.Zero, MaterialTextRole.Body, palette.OnSurfaceVariant);

        for (int index = 0; index < tabs.Count; index++) {
            MaterialRect tab = layout.Tab(index, tabs.Count);
            bool selected = index == selectedTab;
            MaterialUiKit.NavigationPill(tab, palette, selected);
            MaterialUiKit.Text(tabs[index].Title, tab.Center, new Vector2(0.5f), MaterialTextRole.Label,
                selected ? palette.OnPrimary : palette.OnSurfaceVariant);
        }

        MaterialUiKit.Surface(layout.Body, 28f,
            palette with { SurfaceHigh = palette.SurfaceHigh * 0.78f }, elevated: false);
        RenderRows(layout, palette);

        string footer = capturingKey
            ? "按下新的按键；Esc 取消"
            : "↑↓ 选择   ←→ 修改   Enter 确认   Tab 切换分页   Esc 返回";
        MaterialUiKit.Text(footer, new Vector2(layout.Footer.X, layout.Footer.Y), Vector2.Zero,
            MaterialTextRole.Caption, palette.OnSurfaceVariant);
        string compatibility = MotionSmoothingBridge.Available ? "MotionSmoothing 已连接" : "MotionSmoothing 未安装";
        MaterialUiKit.Text(compatibility, new Vector2(layout.Footer.Right, layout.Footer.Y),
            new Vector2(1f, 0f), MaterialTextRole.Caption, palette.OnSurfaceVariant);

        if (capturingKey) RenderKeyCaptureModal(palette);
        MaterialUiKit.Cursor(MInput.Mouse.Position, palette, 1f);
    }

    private void RenderRows(OverlayLayout layout, MaterialPalette palette) {
        List<SettingRow> rows = CurrentRows;
        rowViewport.Render(layout.Rows, () => {
            for (int index = 0; index < rows.Count; index++) {
                SettingRow row = rows[index];
                MaterialRect rect = layout.Row(index, rowScroll.Offset);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                bool selected = index == selectedRow;
                bool enabled = row.Enabled();
                if (selected) {
                    MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 22f,
                        palette.Primary * (enabled ? 0.20f : 0.09f));
                    MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 22f, 2f,
                        palette.Primary * (enabled ? 0.90f : 0.35f));
                }
                Color labelColor = enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.45f;
                Color valueColor = selected ? palette.Primary : palette.OnSurfaceVariant;
                MaterialUiKit.Text(row.Label, new Vector2(rect.X + 20f, rect.Y + 12f), Vector2.Zero,
                    MaterialTextRole.Body, labelColor, scaleOverride: 0.37f);
                MaterialUiKit.Text(Trim(row.Value(), 56), new Vector2(rect.Right - 20f, rect.Y + 13f),
                    new Vector2(1f, 0f), MaterialTextRole.Caption,
                    enabled ? valueColor : palette.OnSurfaceVariant * 0.38f, scaleOverride: 0.34f);
                if (index + 1 < rows.Count)
                    MaterialUi.Line(new Vector2(rect.X + 18f, rect.Bottom + RowGap / 2f),
                        new Vector2(rect.Right - 18f, rect.Bottom + RowGap / 2f), 1f,
                        palette.Outline * 0.32f);
            }
        });
    }

    private void RenderKeyCaptureModal(MaterialPalette palette) {
        MaterialRect modal = new(560f, 420f, 800f, 240f);
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, Color.Black * 0.48f);
        MaterialUiKit.Surface(modal, 36f, palette);
        MaterialUiKit.Text("设置快捷键", new Vector2(modal.Center.X, modal.Y + 42f),
            new Vector2(0.5f, 0f), MaterialTextRole.Title, palette.OnSurface);
        MaterialUiKit.Text($"{keyRow?.Label}：请按下新的按键", modal.Center + new Vector2(0f, 4f),
            new Vector2(0.5f), MaterialTextRole.Body, palette.OnSurfaceVariant);
    }

    private void UpdateMouse() {
        OverlayLayout layout = OverlayLayout.Create();
        Vector2 mouse = MInput.Mouse.Position;
        if (MInput.Mouse.WheelDelta != 0 && layout.Body.Contains(mouse)) {
            rowScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * 180f, MaxRowScroll(layout));
        }
        if (!MInput.Mouse.WasMoved && !MInput.Mouse.PressedLeftButton) return;
        for (int index = 0; index < tabs.Count; index++) {
            if (!layout.Tab(index, tabs.Count).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton) SelectTab(index);
            return;
        }
        if (!layout.Rows.Contains(mouse)) return;
        for (int index = 0; index < CurrentRows.Count; index++) {
            if (!layout.Row(index, rowScroll.Offset).Contains(mouse)) continue;
            selectedRow = index;
            if (MInput.Mouse.PressedLeftButton) ActivateRow(1);
            return;
        }
    }

    private void UpdateKeyCapture() {
        if (Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            capturingKey = false;
            keyRow = null;
            return;
        }
        Keys[] pressed = MInput.Keyboard.CurrentState.GetPressedKeys();
        foreach (Keys key in pressed) {
            if (!MInput.Keyboard.Pressed(key) || key is Keys.None or Keys.Escape) continue;
            keyRow?.AssignKey?.Invoke(key);
            capturingKey = false;
            keyRow = null;
            Audio.Play("event:/ui/main/button_select");
            return;
        }
    }

    private void ActivateRow(int direction) {
        if (CurrentRows.Count == 0) return;
        SettingRow row = CurrentRows[Math.Clamp(selectedRow, 0, CurrentRows.Count - 1)];
        if (!row.Enabled()) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        if (row.AssignKey is not null) {
            capturingKey = true;
            keyRow = row;
            Audio.Play("event:/ui/main/button_select");
            return;
        }
        row.Change?.Invoke(direction);
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void SelectTab(int index) {
        selectedTab = (index % tabs.Count + tabs.Count) % tabs.Count;
        selectedRow = 0;
        rowScroll.Reset();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void SelectRow(int index) {
        int count = CurrentRows.Count;
        if (count == 0) return;
        selectedRow = (index % count + count) % count;
        EnsureRowVisible();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void EnsureRowVisible() {
        OverlayLayout layout = OverlayLayout.Create();
        float top = selectedRow * (RowHeight + RowGap);
        rowScroll.EnsureVisible(top, top + RowHeight, layout.Rows.Height, MaxRowScroll(layout));
    }

    private float MaxRowScroll(OverlayLayout layout) {
        float contentHeight = CurrentRows.Count == 0
            ? 0f
            : CurrentRows.Count * RowHeight + (CurrentRows.Count - 1) * RowGap;
        return Math.Max(0f, contentHeight - layout.Rows.Height);
    }

    private void CloseToPauseMenu() {
        if (closing) return;
        closing = true;
        Audio.Play("event:/ui/main/button_back");
        level.AllowHudHide = oldAllowHudHide;
        RemoveSelf();
        level.Pause(returnIndex, minimal);
        MicroblocksQolUtilsModule.Instance.SaveSettings();
    }

    private void CloseToGame() {
        if (closing) return;
        closing = true;
        Audio.Play("event:/ui/main/button_back");
        level.AllowHudHide = oldAllowHudHide;
        level.Paused = false;
        RemoveSelf();
        MicroblocksQolUtilsModule.Instance.SaveSettings();
    }

    private List<SettingRow> CurrentRows => tabs[selectedTab].Rows;

    private List<SettingsTab> BuildTabs() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        return [
            new SettingsTab("HUD", [
                Toggle("启用 QOL 工具", () => settings.Enabled, value => settings.Enabled = value),
                Toggle("显示帧率", () => settings.ShowFps, value => settings.ShowFps = value),
                Toggle("显示 CPU 帧耗时", () => settings.ShowFrameTime, value => settings.ShowFrameTime = value),
                Toggle("同时显示物理与渲染帧率", () => settings.ShowPhysicalAndRenderFps,
                    value => settings.ShowPhysicalAndRenderFps = value),
                Toggle("显示帧率分析", () => settings.EnableFrameProfiler, value => settings.EnableFrameProfiler = value),
                Range("卡顿采样阈值", () => settings.FrameSpikeThresholdMs,
                    value => settings.FrameSpikeThresholdMs = value, 20, 250, 5, value => $"{value} ms"),
                Toggle("显示还剩多少面", () => settings.ShowRoomsRemaining, value => settings.ShowRoomsRemaining = value),
                Toggle("显示地图人数", () => settings.ShowMapPlayerCount, value => settings.ShowMapPlayerCount = value),
                Toggle("显示当前时间", () => settings.ShowClock, value => settings.ShowClock = value)
            ]),
            new SettingsTab("小地图", [
                Toggle("启用小地图", () => settings.MiniMapEnabled, value => settings.MiniMapEnabled = value),
                EnumRow("裁剪形状", () => settings.MiniMapShape, value => settings.MiniMapShape = value),
                Range("地图尺寸", () => settings.MiniMapSize, value => settings.MiniMapSize = value,
                    96, 384, 16, value => $"{value} px"),
                Range("缩放档位", () => settings.MiniMapZoom, value => settings.MiniMapZoom = value,
                    0, 12, 1, value => value == 0 ? "当前房间" : value.ToString()),
                Key("放大快捷键", () => settings.MiniMapZoomInKey, value => settings.MiniMapZoomInKey = value),
                Key("缩小快捷键", () => settings.MiniMapZoomOutKey, value => settings.MiniMapZoomOutKey = value),
                Toggle("显示背景", () => settings.MiniMapBackground, value => settings.MiniMapBackground = value),
                Range("背景不透明度", () => settings.MiniMapBackgroundOpacity,
                    value => settings.MiniMapBackgroundOpacity = value, 0, 10, 1, value => $"{value * 10}%"),
                Toggle("显示地图边框", () => settings.MiniMapBorder, value => settings.MiniMapBorder = value),
                Toggle("显示房间边缘线", () => settings.MiniMapRoomBounds, value => settings.MiniMapRoomBounds = value),
                Toggle("自适应地图颜色", () => settings.MiniMapAdaptiveColors, value => settings.MiniMapAdaptiveColors = value),
                Toggle("显示 MiaoNet 玩家", () => settings.ShowMiaoNetPlayers, value => settings.ShowMiaoNetPlayers = value),
                Toggle("边框显示越界玩家", () => settings.MiniMapShowOffscreenPlayers,
                    value => settings.MiniMapShowOffscreenPlayers = value),
                EnumRow("玩家名字", () => settings.MiniMapNames, value => settings.MiniMapNames = value),
                Toggle("隐藏 MiaoNet 原生越界名字", () => settings.HideMiaoNetOffscreenNames,
                    value => settings.HideMiaoNetOffscreenNames = value)
            ]),
            new SettingsTab("录制", [
                Status("当前状态", RecordingStatus),
                Status("当前片段", () => AutoRecorder.IsRecording ? $"{AutoRecorder.CurrentSeconds:0.0} 秒" : "—"),
                Status("当前文件", () => ShortPath(AutoRecorder.CurrentPath)),
                Status("最后输出", () => ShortPath(AutoRecorder.LastOutput)),
                Action("开始手动录制", () => AutoRecorder.StartManual(), () => !AutoRecorder.ManualMode),
                Action("停止并保存", () => AutoRecorder.StopManual(level, save: true),
                    () => AutoRecorder.ManualMode || AutoRecorder.IsRecording),
                Action("停止并丢弃", () => AutoRecorder.StopManual(level, save: false),
                    () => AutoRecorder.ManualMode || AutoRecorder.IsRecording),
                Toggle("自动录制", () => settings.AutoRecorderEnabled, value => settings.AutoRecorderEnabled = value),
                EnumRow("自动录制策略", () => settings.RecordingPolicy, value => settings.RecordingPolicy = value),
                EnumRow("BGM 处理", () => settings.BgmMode, value => settings.BgmMode = value),
                Toggle("录制 UI 音效", () => settings.RecordingIncludeUiSfx, value => settings.RecordingIncludeUiSfx = value),
                Range("录制帧率", () => settings.RecordingFrameRate, value => settings.RecordingFrameRate = value,
                    30, 120, 30, value => $"{value} FPS"),
                Range("录制码率", () => settings.RecordingBitrateKbps, value => settings.RecordingBitrateKbps = value,
                    2000, 50000, 1000, value => $"{value / 1000f:0.#} Mbps")
            ]),
            new SettingsTab("界面与系统", [
                Toggle("Material You 界面", () => settings.MaterialYouInterface,
                    value => settings.MaterialYouInterface = value),
                Toggle("亚克力模糊背景", () => settings.MaterialAcrylicBackground,
                    value => settings.MaterialAcrylicBackground = value),
                Range("模糊强度", () => settings.MaterialAcrylicBlurStrength,
                    value => settings.MaterialAcrylicBlurStrength = value, 1, 12, 1, value => value.ToString()),
                Toggle("取代原版选关页", () => settings.ReplaceChapterSelect, value => settings.ReplaceChapterSelect = value),
                Toggle("选关页显示 Collab 地图", () => settings.ChapterSelectShowCollabMaps,
                    value => settings.ChapterSelectShowCollabMaps = value),
                Toggle("完全移除场景过渡", () => settings.RemoveRoomTransitions,
                    value => settings.RemoveRoomTransitions = value),
                Toggle("关心玩家过面通知", () => settings.WatchedPlayerNotifications,
                    value => settings.WatchedPlayerNotifications = value)
            ])
        ];
    }

    private static SettingRow Toggle(string label, Func<bool> get, Action<bool> set) => new(
        label,
        () => get() ? "开" : "关",
        _ => set(!get())
    );

    private static SettingRow Range(
        string label,
        Func<int> get,
        Action<int> set,
        int min,
        int max,
        int step,
        Func<int, string> format
    ) => new(label, () => format(get()), direction =>
        set(Math.Clamp(get() + Math.Sign(direction) * step, min, max)));

    private static SettingRow EnumRow<T>(string label, Func<T> get, Action<T> set) where T : struct, Enum {
        T[] values = Enum.GetValues<T>();
        return new SettingRow(label, () => FormatEnum(get()), direction => {
            int index = Array.IndexOf(values, get());
            index = (index + Math.Sign(direction) + values.Length) % values.Length;
            set(values[index]);
        });
    }

    private static SettingRow Key(string label, Func<Keys> get, Action<Keys> set) => new(
        label,
        () => get().ToString(),
        null,
        set
    );

    private static SettingRow Action(string label, System.Action action, Func<bool>? enabled = null) => new(
        label,
        () => "执行",
        _ => action(),
        null,
        enabled
    );

    private static SettingRow Status(string label, Func<string> value) => new(
        label,
        value,
        null,
        null,
        () => false
    );

    private static string RecordingStatus() {
        if (AutoRecorder.IsRecording) return AutoRecorder.ManualMode ? "手动录制中" : "自动录制中";
        if (AutoRecorder.IsFinalizing) return "正在生成视频";
        if (AutoRecorder.ManualMode) return "已开启，等待游戏画面";
        return "空闲";
    }

    private static string ShortPath(string path) => string.IsNullOrWhiteSpace(path)
        ? "—"
        : Path.GetFileName(path);

    private static string FormatEnum<T>(T value) where T : struct, Enum => value switch {
        MiniMapShape.Circle => "圆形",
        MiniMapShape.Square => "方形",
        MiniMapNameMode.None => "不显示",
        MiniMapNameMode.WatchedOnly => "仅关心的人",
        MiniMapNameMode.Everyone => "所有人",
        RecordingPolicy.EveryRoom => "每一面",
        RecordingPolicy.GoldenRunsOnly => "仅金草莓",
        BgmRecordingMode.CaptureGameMix => "直接录制混音",
        BgmRecordingMode.SfxOnlyWithPostMix => "仅音效，后期对齐 BGM",
        _ => value.ToString()
    };

    private static string Trim(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : value[..Math.Max(1, maxCharacters - 1)] + "…";

    private sealed record SettingsTab(string Title, List<SettingRow> Rows);

    private sealed record SettingRow(
        string Label,
        Func<string> Value,
        Action<int>? Change,
        Action<Keys>? AssignKey = null,
        Func<bool>? IsEnabled = null
    ) {
        public bool Enabled() => IsEnabled?.Invoke() ?? true;
    }

    private readonly record struct OverlayLayout(
        MaterialRect Panel,
        MaterialRect Header,
        MaterialRect Tabs,
        MaterialRect Body,
        MaterialRect Rows,
        MaterialRect Footer
    ) {
        public static OverlayLayout Create() {
            MaterialRect panel = new(130f, 50f, 1660f, 980f);
            MaterialRect inner = panel.Inset(MaterialSpacing.Xxl, 30f);
            MaterialRect[] tracks = MaterialLayout.Split(
                inner,
                MaterialAxis.Vertical,
                MaterialSpacing.Md,
                MaterialTrack.Fixed(82f),
                MaterialTrack.Fixed(54f),
                MaterialTrack.Flex(),
                MaterialTrack.Fixed(34f)
            );
            MaterialRect rows = tracks[2].Inset(18f, 14f);
            return new OverlayLayout(panel, tracks[0], tracks[1], tracks[2], rows, tracks[3]);
        }

        public MaterialRect Tab(int index, int count) => MaterialLayout.GridCell(
            Tabs,
            Math.Max(1, count),
            1,
            MaterialSpacing.Sm,
            0f,
            index
        );

        public MaterialRect Row(int index, float scrollOffset) => new(
            Rows.X,
            Rows.Y + index * (RowHeight + RowGap) - scrollOffset,
            Rows.Width,
            RowHeight
        );
    }
}
