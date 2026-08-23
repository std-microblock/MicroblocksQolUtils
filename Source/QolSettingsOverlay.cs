using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class QolSettingsOverlay : Entity, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const int Columns = 2;
    private const float RowHeight = 92f;
    private const float RowGap = 12f;

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
    private float inputDelay = 0.16f;
    private float ease;
    private float contentEase = 1f;
    private float tabIndicatorY;
    private bool capturingKey;
    private SettingRow? keyRow;
    private SettingRow? editingRow;
    private string editBuffer = "";
    private string imeText = "";
    private bool textInputSubscribed;
    private float editError;
    private SettingRow? draggedSlider;
    private CloseDestination closeDestination;

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
        foreach (SettingRow row in tabs.SelectMany(tab => tab.Rows)) row.InitializeVisuals();
        tabIndicatorY = OverlayLayout.Create(1f).Tab(0, tabs.Count).Y;
        Tag = Tags.HUD | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -2_000_000;
    }

    public override void Added(Scene scene) {
        base.Added(scene);
        activePage = this;
    }

    public override void Removed(Scene scene) {
        if (activePage == this) activePage = null;
        ReleaseFocusedInput();
        rowViewport.Dispose();
        base.Removed(scene);
    }

    public override void Update() {
        base.Update();
        ease = Calc.Approach(ease, closeDestination == CloseDestination.None ? 1f : 0f,
            Engine.RawDeltaTime * 7.5f);
        contentEase = Calc.Approach(contentEase, 1f, Engine.RawDeltaTime * 9f);
        editError = Calc.Approach(editError, 0f, Engine.RawDeltaTime * 4f);

        OverlayLayout layout = OverlayLayout.Create(ease);
        rowScroll.Update(MaxRowScroll(layout));
        tabIndicatorY = Calc.Approach(tabIndicatorY, layout.Tab(selectedTab, tabs.Count).Y,
            Math.Max(520f, Math.Abs(tabIndicatorY - layout.Tab(selectedTab, tabs.Count).Y) * 12f)
                * Engine.RawDeltaTime);
        UpdateRowAnimations(layout);

        if (closeDestination != CloseDestination.None) {
            if (ease <= 0.001f) FinishClose();
            return;
        }

        inputDelay -= Engine.DeltaTime;
        if (capturingKey) {
            UpdateKeyCapture();
            return;
        }
        if (editingRow is not null) {
            UpdateTextEdit(layout);
            return;
        }
        if (draggedSlider is not null) {
            UpdateSliderDrag(layout);
            if (draggedSlider is not null) return;
        }
        if (inputDelay > 0f) return;

        if (Input.Pause.Pressed) {
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            BeginClose(CloseDestination.PauseMenu);
            return;
        }
        if (MInput.Keyboard.Pressed(Keys.Tab)
            || MInput.Keyboard.Pressed(Keys.PageDown)
            || MInput.Keyboard.Pressed(Keys.PageUp)) {
            bool backwards = MInput.Keyboard.Pressed(Keys.PageUp)
                || MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift);
            SelectTab(selectedTab + (backwards ? -1 : 1));
        }

        if (ProfilerTabSelected) {
            if (Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space)) StartProfilerSampling();
        } else if (CurrentRows.Count > 0) {
            if (Input.MenuUp.Pressed) SelectRow(selectedRow - 1);
            else if (Input.MenuDown.Pressed) SelectRow(selectedRow + 1);
            else if (Input.MenuLeft.Pressed) AdjustSelected(-1);
            else if (Input.MenuRight.Pressed) AdjustSelected(1);
            else if (Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space)) ActivateSelected();
        }
        UpdateMouse(layout);
    }

    public override void Render() {
        base.Render();
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        if (ease <= 0f) return;
        OverlayLayout layout = OverlayLayout.Create(ease);
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * (0.92f * ease));
        MaterialUiKit.Surface(
            layout.Panel,
            38f,
            palette with { SurfaceHigh = palette.Surface * (acrylicActive ? 0.80f : 0.97f) },
            ease
        );

        MaterialUiKit.Text("Microblock 的 QOL 工具", new Vector2(layout.Header.X, layout.Header.Y),
            Vector2.Zero, MaterialTextRole.Display, palette.OnSurface, ease, scaleOverride: 0.76f);
        MaterialUiKit.Text("设置会即时生效并在离开时保存", new Vector2(layout.Header.X + 2f, layout.Header.Y + 50f),
            Vector2.Zero, MaterialTextRole.Body, palette.OnSurfaceVariant, ease, scaleOverride: 0.32f);

        RenderNavigation(layout, palette);
        RenderContent(layout, palette);

        string footer = ProfilerTabSelected
            ? "点击开始后会自动返回游戏，采样 10 秒；完成后回到这里查看报告"
            : editingRow is not null
            ? "输入后按 Enter 保存，Esc 取消"
            : capturingKey
                ? "按下新的按键，Esc 取消"
                : "↑↓ 选择  ·  ←→ 调整  ·  Enter 编辑  ·  Tab 分页  ·  鼠标可拖动滑杆";
        MaterialUiKit.Text(footer, new Vector2(layout.Footer.X, layout.Footer.Y), Vector2.Zero,
            MaterialTextRole.Caption, palette.OnSurfaceVariant, ease, scaleOverride: 0.28f);
        string compatibility = MotionSmoothingBridge.Available ? "MotionSmoothing 已连接" : "MotionSmoothing 未安装";
        MaterialUiKit.Text(compatibility, new Vector2(layout.Footer.Right, layout.Footer.Y),
            new Vector2(1f, 0f), MaterialTextRole.Caption, palette.OnSurfaceVariant, ease,
            scaleOverride: 0.28f);

        if (capturingKey) RenderKeyCaptureModal(palette);
        MaterialUiKit.Cursor(MInput.Mouse.Position, palette, ease);
    }

    private void RenderNavigation(OverlayLayout layout, MaterialPalette palette) {
        MaterialUi.RoundedRect(layout.Navigation.X, layout.Navigation.Y, layout.Navigation.Width,
            layout.Navigation.Height, 28f, palette.Surface * (0.64f * ease));
        MaterialUi.RoundedRect(layout.Navigation.X + 10f, tabIndicatorY,
            layout.Navigation.Width - 20f, OverlayLayout.TabHeight, 22f,
            palette.Primary * (0.90f * ease));

        for (int index = 0; index < tabs.Count; index++) {
            MaterialRect tab = layout.Tab(index, tabs.Count);
            bool selected = index == selectedTab;
            MaterialUiKit.Text(tabs[index].Title, new Vector2(tab.X + 24f, tab.Center.Y - 9f),
                Vector2.Zero, MaterialTextRole.Label,
                selected ? palette.OnPrimary : palette.OnSurfaceVariant, ease, scaleOverride: 0.34f);
            MaterialUiKit.Text(tabs[index].Summary, new Vector2(tab.X + 24f, tab.Center.Y + 16f),
                Vector2.Zero, MaterialTextRole.Caption,
                selected ? palette.OnPrimary * 0.72f : palette.OnSurfaceVariant * 0.62f,
                ease, scaleOverride: 0.23f);
        }

        MaterialUiKit.Text("操作方式", new Vector2(layout.Navigation.X + 24f, layout.Navigation.Bottom - 126f),
            Vector2.Zero, MaterialTextRole.Label, palette.OnSurfaceVariant, ease, scaleOverride: 0.28f);
        MaterialUiKit.Text("开关可直接点击\n数值可拖动或输入\n文字栏支持中文输入",
            new Vector2(layout.Navigation.X + 24f, layout.Navigation.Bottom - 92f), Vector2.Zero,
            MaterialTextRole.Caption, palette.OnSurfaceVariant, ease, scaleOverride: 0.25f);
    }

    private void RenderContent(OverlayLayout layout, MaterialPalette palette) {
        MaterialUiKit.Text(tabs[selectedTab].Title, new Vector2(layout.ContentHeader.X, layout.ContentHeader.Y),
            Vector2.Zero, MaterialTextRole.Title, palette.OnSurface, ease * contentEase,
            scaleOverride: 0.48f);
        if (ProfilerTabSelected) {
            RenderProfilerContent(layout, palette);
            return;
        }
        MaterialUiKit.Text($"{CurrentRows.Count} 项", new Vector2(layout.ContentHeader.Right, layout.ContentHeader.Y + 8f),
            new Vector2(1f, 0f), MaterialTextRole.Caption, palette.OnSurfaceVariant,
            ease * contentEase, scaleOverride: 0.28f);
        RenderRows(layout, palette);

        float maximum = MaxRowScroll(layout);
        if (maximum <= 0f) return;
        float ratio = layout.Rows.Height / (layout.Rows.Height + maximum);
        float thumbHeight = Math.Max(52f, layout.Rows.Height * ratio);
        float travel = layout.Rows.Height - thumbHeight;
        float y = layout.Rows.Y + (maximum <= 0f ? 0f : rowScroll.Offset / maximum * travel);
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, layout.Rows.Y, 5f, layout.Rows.Height, 2.5f,
            palette.Outline * (0.18f * ease));
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, y, 5f, thumbHeight, 2.5f,
            palette.Primary * (0.68f * ease));
    }

    private void RenderRows(OverlayLayout layout, MaterialPalette palette) {
        List<SettingRow> rows = CurrentRows;
        rowViewport.Render(layout.Rows, () => {
            for (int index = 0; index < rows.Count; index++) {
                SettingRow row = rows[index];
                MaterialRect rect = layout.Row(index, rowScroll.Offset)
                    .Offset(0f, (1f - contentEase) * 14f);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                RenderRow(row, rect, palette, ease * contentEase);
            }
        });
    }

    private void RenderRow(SettingRow row, MaterialRect rect, MaterialPalette palette, float alpha) {
        bool enabled = row.Enabled();
        float emphasis = Math.Max(row.Pulse * 0.42f,
            Math.Max(row.FocusAnimation, row.HoverAnimation * 0.72f));
        Color fill = Color.Lerp(palette.SurfaceHigh * 0.72f, palette.SurfaceHighest, emphasis);
        MaterialUi.RoundedRect(rect.X, rect.Y + 3f, rect.Width, rect.Height, 23f,
            Color.Black * (0.10f * alpha));
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 23f,
            fill * (alpha * (enabled ? 1f : 0.48f)));
        if (emphasis > 0.01f) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 23f,
                1f + emphasis, palette.Primary * (alpha * emphasis));
        }

        Color labelColor = enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.55f;
        MaterialUiKit.Text(row.Label, new Vector2(rect.X + 18f, rect.Y + 12f), Vector2.Zero,
            MaterialTextRole.Label, labelColor, alpha, scaleOverride: 0.31f);

        switch (row.Kind) {
            case SettingKind.Toggle:
                RenderToggle(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Range:
                RenderRange(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Enum:
                RenderEnum(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Text:
                RenderTextField(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Key:
                RenderKey(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Action:
                RenderAction(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Status:
                RenderStatus(row, rect, palette, alpha);
                break;
        }
    }

    private static void RenderToggle(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = ToggleRect(rect);
        float state = row.ToggleAnimation;
        Color track = Color.Lerp(palette.Outline * 0.45f, palette.Primary, state);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, control.Height / 2f,
            track * (alpha * (enabled ? 1f : 0.45f)));
        float knobX = MathHelper.Lerp(control.X + 14f, control.Right - 14f, state);
        MaterialUi.Circle(new Vector2(knobX, control.Center.Y), 10f,
            Color.Lerp(palette.OnSurfaceVariant, palette.OnPrimary, state) * alpha);
        MaterialUiKit.Text(row.ToggleValue?.Invoke() == true ? "开" : "关",
            new Vector2(control.X - 14f, control.Center.Y - 8f), new Vector2(1f, 0f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
    }

    private void RenderRange(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect track = SliderRect(rect);
        float state = row.SliderAnimation;
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            palette.Outline * (0.32f * alpha));
        float fillWidth = Math.Max(track.Height, track.Width * state);
        MaterialUi.RoundedRect(track.X, track.Y, fillWidth, track.Height, track.Height / 2f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialUi.Circle(new Vector2(track.X + track.Width * state, track.Center.Y), 10f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialRect valueRect = RangeValueRect(rect);
        bool editing = editingRow == row;
        Color outline = editError > 0f && editing ? Color.OrangeRed : editing ? palette.Primary : palette.Outline;
        MaterialUi.RoundedRect(valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height, 15f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height, 15f,
            editing ? 2f : 1f, outline * (alpha * (editing ? 1f : 0.48f)));
        string shown = editing ? editBuffer + imeText : row.Value();
        MaterialUiKit.Text(Trim(shown, 13), valueRect.Center + new Vector2(0f, -7f),
            new Vector2(0.5f), MaterialTextRole.Label,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.26f);
        if (editing && Scene.BetweenInterval(0.5f)) {
            float caretX = Math.Min(valueRect.Right - 8f,
                valueRect.Center.X + SystemTtfFont.Measure(Trim(shown, 13), 0.26f).X / 2f + 2f);
            MaterialUi.Line(new Vector2(caretX, valueRect.Y + 7f),
                new Vector2(caretX, valueRect.Bottom - 7f), 2f, palette.Primary * alpha);
            SetTextInputRectangle(valueRect);
        }
    }

    private static void RenderEnum(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Surface * (0.72f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 16f, 1f,
            palette.Outline * (0.48f * alpha));
        MaterialUiKit.Text("<", new Vector2(control.X + 18f, control.Center.Y - 8f), Vector2.Zero,
            MaterialTextRole.Label, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
        MaterialUiKit.Text(Trim(row.Value(), 28), control.Center + new Vector2(0f, -7f),
            new Vector2(0.5f), MaterialTextRole.Label,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f, alpha, scaleOverride: 0.28f);
        MaterialUiKit.Text(">", new Vector2(control.Right - 18f, control.Center.Y - 8f),
            new Vector2(1f, 0f), MaterialTextRole.Label, palette.OnSurfaceVariant, alpha,
            scaleOverride: 0.27f);
    }

    private void RenderTextField(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        bool editing = editingRow == row;
        Color outline = editError > 0f && editing ? Color.OrangeRed : editing ? palette.Primary : palette.Outline;
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 15f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 15f,
            editing ? 2f : 1f, outline * (alpha * (editing ? 1f : 0.48f)));
        string shown = editing ? editBuffer + imeText : row.Value();
        string placeholder = row.Placeholder ?? "点击输入";
        string text = shown.Length == 0 ? placeholder : shown;
        Color color = shown.Length == 0 ? palette.OnSurfaceVariant * 0.55f : palette.OnSurface;
        MaterialUiKit.Text(TrimFromLeft(text, 46), new Vector2(control.X + 14f, control.Y + 8f),
            Vector2.Zero, MaterialTextRole.Caption, enabled ? color : color * 0.45f, alpha,
            scaleOverride: 0.27f);
        if (editing && Scene.BetweenInterval(0.5f)) {
            string visible = TrimFromLeft(shown, 46);
            float caretX = Math.Min(control.Right - 12f,
                control.X + 14f + SystemTtfFont.Measure(visible, 0.27f).X + 2f);
            MaterialUi.Line(new Vector2(caretX, control.Y + 7f),
                new Vector2(caretX, control.Bottom - 7f), 2f, palette.Primary * alpha);
        }
        if (editing) SetTextInputRectangle(control);
    }

    private static void RenderKey(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Surface * (0.74f * alpha));
        MaterialUiKit.Text(row.Value(), control.Center + new Vector2(0f, -7f), new Vector2(0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.28f);
    }

    private static void RenderAction(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            (enabled ? palette.Primary : palette.Outline) * (alpha * (enabled ? 0.88f : 0.25f)));
        MaterialUiKit.Text(row.Value(), control.Center + new Vector2(0f, -7f), new Vector2(0.5f),
            MaterialTextRole.Label, enabled ? palette.OnPrimary : palette.OnSurfaceVariant * 0.55f,
            alpha, scaleOverride: 0.28f);
    }

    private static void RenderStatus(SettingRow row, MaterialRect rect, MaterialPalette palette, float alpha) {
        MaterialUiKit.Text(Trim(row.Value(), 42), new Vector2(rect.X + 18f, rect.Bottom - 31f),
            Vector2.Zero, MaterialTextRole.Body, palette.Primary, alpha, scaleOverride: 0.30f);
    }

    private void RenderKeyCaptureModal(MaterialPalette palette) {
        MaterialRect modal = new(610f, 410f, 700f, 260f);
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, Color.Black * (0.52f * ease));
        MaterialUiKit.Surface(modal, 34f, palette, ease);
        MaterialUiKit.Text("设置快捷键", new Vector2(modal.Center.X, modal.Y + 46f),
            new Vector2(0.5f, 0f), MaterialTextRole.Title, palette.OnSurface, ease);
        MaterialUiKit.Text(keyRow?.Label ?? "", modal.Center + new Vector2(0f, -4f),
            new Vector2(0.5f), MaterialTextRole.Body, palette.OnSurfaceVariant, ease);
        MaterialUiKit.Text("请按下新的按键  ·  Esc 取消", new Vector2(modal.Center.X, modal.Bottom - 58f),
            new Vector2(0.5f, 0f), MaterialTextRole.Caption, palette.Primary, ease,
            scaleOverride: 0.29f);
    }

    private void UpdateMouse(OverlayLayout layout) {
        Vector2 mouse = MInput.Mouse.Position;
        if (MInput.Mouse.WheelDelta != 0 && layout.Body.Contains(mouse)) {
            rowScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * 178f, MaxRowScroll(layout));
        }
        if (!MInput.Mouse.WasMoved && !MInput.Mouse.PressedLeftButton) return;

        for (int index = 0; index < tabs.Count; index++) {
            if (!layout.Tab(index, tabs.Count).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton) SelectTab(index);
            return;
        }
        if (ProfilerTabSelected) {
            if (MInput.Mouse.PressedLeftButton && ProfilerStartRect(layout).Contains(mouse))
                StartProfilerSampling();
            return;
        }
        if (!layout.Rows.Contains(mouse)) return;
        for (int index = 0; index < CurrentRows.Count; index++) {
            MaterialRect rect = layout.Row(index, rowScroll.Offset);
            if (!rect.Contains(mouse)) continue;
            selectedRow = index;
            SettingRow row = CurrentRows[index];
            if (MInput.Mouse.PressedLeftButton) ActivateMouse(row, rect, mouse);
            return;
        }
    }

    private void ActivateMouse(SettingRow row, MaterialRect rect, Vector2 mouse) {
        if (!row.Enabled() || row.Kind == SettingKind.Status) {
            if (!row.Enabled()) Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        if (row.Kind == SettingKind.Range) {
            if (RangeValueRect(rect).Contains(mouse)) {
                StartEdit(row);
                return;
            }
            draggedSlider = row;
            ApplySliderMouse(row, SliderRect(rect), mouse.X);
            Audio.Play("event:/ui/main/button_select");
            return;
        }
        if (row.Kind == SettingKind.Text) {
            StartEdit(row);
            return;
        }
        Activate(row);
    }

    private void UpdateSliderDrag(OverlayLayout layout) {
        SettingRow row = draggedSlider!;
        int index = CurrentRows.IndexOf(row);
        if (index < 0 || !MInput.Mouse.CheckLeftButton) {
            draggedSlider = null;
            return;
        }
        MaterialRect rect = layout.Row(index, rowScroll.Offset);
        ApplySliderMouse(row, SliderRect(rect), MInput.Mouse.Position.X);
        if (MInput.Mouse.ReleasedLeftButton) draggedSlider = null;
    }

    private static void ApplySliderMouse(SettingRow row, MaterialRect track, float mouseX) {
        float normalized = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        row.SetNormalized?.Invoke(normalized);
        row.Pulse = 1f;
    }

    private void UpdateTextEdit(OverlayLayout layout) {
        if (MaterialTextInputFocus.Pressed(Keys.Escape) || Input.MenuCancel.Pressed) {
            CancelEdit();
            return;
        }
        if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
            CommitEdit();
            return;
        }
        if (MInput.Mouse.PressedLeftButton) {
            int index = CurrentRows.IndexOf(editingRow!);
            MaterialRect control = index < 0
                ? default
                : editingRow!.Kind == SettingKind.Range
                    ? RangeValueRect(layout.Row(index, rowScroll.Offset))
                    : WideControlRect(layout.Row(index, rowScroll.Offset));
            if (index < 0 || !control.Contains(MInput.Mouse.Position)) CommitEdit();
        }
    }

    private void StartEdit(SettingRow row) {
        if (row.EditValue is null || row.CommitEdit is null) return;
        editingRow = row;
        editBuffer = row.EditValue();
        imeText = "";
        editError = 0f;
        SubscribeTextInput();
        MaterialTextInputFocus.Focus(this);
        Audio.Play("event:/ui/main/button_select");
    }

    private void CommitEdit() {
        SettingRow? row = editingRow;
        if (row?.CommitEdit is null) {
            CancelEdit();
            return;
        }
        if (!row.CommitEdit(editBuffer)) {
            editError = 1f;
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        row.Pulse = 1f;
        editingRow = null;
        editBuffer = "";
        imeText = "";
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_select");
    }

    private void CancelEdit() {
        editingRow = null;
        editBuffer = "";
        imeText = "";
        editError = 0f;
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_back");
    }

    private void OnTextInput(char character) {
        if (editingRow is null) return;
        if (character == '\b') {
            if (editBuffer.Length > 0) editBuffer = editBuffer[..^1];
        } else if (!char.IsControl(character) && editBuffer.Length < editingRow.MaxInputLength) {
            if (editingRow.NumericInput && !char.IsDigit(character)) return;
            editBuffer += character;
        } else {
            return;
        }
        imeText = "";
        editError = 0f;
    }

    private void OnTextEditing(string? text, int start, int length) {
        _ = start;
        _ = length;
        if (editingRow is not null) imeText = text ?? "";
    }

    private void UpdateKeyCapture() {
        if (MaterialTextInputFocus.Pressed(Keys.Escape)) {
            capturingKey = false;
            keyRow = null;
            ReleaseFocusedInput();
            Audio.Play("event:/ui/main/button_back");
            return;
        }
        foreach (Keys key in MaterialTextInputFocus.GetPressedKeys()) {
            if (!MaterialTextInputFocus.Pressed(key) || key is Keys.None or Keys.Escape) continue;
            keyRow?.AssignKey?.Invoke(key);
            if (keyRow is not null) keyRow.Pulse = 1f;
            capturingKey = false;
            keyRow = null;
            ReleaseFocusedInput();
            Audio.Play("event:/ui/main/button_select");
            return;
        }
    }

    private void ActivateSelected() {
        if (CurrentRows.Count == 0) return;
        Activate(CurrentRows[Math.Clamp(selectedRow, 0, CurrentRows.Count - 1)]);
    }

    private void Activate(SettingRow row) {
        if (!row.Enabled()) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        switch (row.Kind) {
            case SettingKind.Toggle:
                row.Change?.Invoke(row.ToggleValue?.Invoke() == true ? -1 : 1);
                break;
            case SettingKind.Range:
            case SettingKind.Text:
                StartEdit(row);
                return;
            case SettingKind.Enum:
            case SettingKind.Action:
                row.Change?.Invoke(1);
                break;
            case SettingKind.Key:
                capturingKey = true;
                keyRow = row;
                MaterialTextInputFocus.Focus(this);
                Audio.Play("event:/ui/main/button_select");
                return;
            case SettingKind.Status:
                return;
        }
        row.Pulse = 1f;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void AdjustSelected(int direction) {
        if (CurrentRows.Count == 0) return;
        SettingRow row = CurrentRows[Math.Clamp(selectedRow, 0, CurrentRows.Count - 1)];
        if (!row.Enabled() || row.Change is null
            || row.Kind is SettingKind.Action or SettingKind.Status or SettingKind.Key or SettingKind.Text) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        row.Change(direction);
        row.Pulse = 1f;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void SelectTab(int index) {
        int next = (index % tabs.Count + tabs.Count) % tabs.Count;
        if (next == selectedTab) return;
        selectedTab = next;
        selectedRow = 0;
        rowScroll.Reset();
        contentEase = 0f;
        draggedSlider = null;
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void SelectRow(int index) {
        int count = CurrentRows.Count;
        if (count == 0) return;
        selectedRow = (index % count + count) % count;
        EnsureRowVisible();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void UpdateRowAnimations(OverlayLayout layout) {
        Vector2 mouse = MInput.Mouse.Position;
        for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++) {
            for (int index = 0; index < tabs[tabIndex].Rows.Count; index++) {
                SettingRow row = tabs[tabIndex].Rows[index];
                bool current = tabIndex == selectedTab;
                bool selected = current && index == selectedRow;
                bool hovered = current && layout.Rows.Contains(mouse)
                    && layout.Row(index, rowScroll.Offset).Contains(mouse);
                row.FocusAnimation = Calc.Approach(row.FocusAnimation, selected ? 1f : 0f,
                    Engine.RawDeltaTime * 8f);
                row.HoverAnimation = Calc.Approach(row.HoverAnimation, hovered ? 1f : 0f,
                    Engine.RawDeltaTime * 10f);
                if (row.ToggleValue is not null) {
                    row.ToggleAnimation = Calc.Approach(row.ToggleAnimation,
                        row.ToggleValue() ? 1f : 0f, Engine.RawDeltaTime * 10f);
                }
                if (row.Normalized is not null) {
                    row.SliderAnimation = Calc.Approach(row.SliderAnimation,
                        row.Normalized(), Engine.RawDeltaTime * 8f);
                }
                row.Pulse = Calc.Approach(row.Pulse, 0f, Engine.RawDeltaTime * 3.5f);
            }
        }
    }

    private void EnsureRowVisible() {
        OverlayLayout layout = OverlayLayout.Create(ease);
        int band = selectedRow / Columns;
        float top = band * (RowHeight + RowGap);
        rowScroll.EnsureVisible(top, top + RowHeight, layout.Rows.Height, MaxRowScroll(layout));
    }

    private float MaxRowScroll(OverlayLayout layout) {
        int bands = (CurrentRows.Count + Columns - 1) / Columns;
        float contentHeight = bands == 0 ? 0f : bands * RowHeight + (bands - 1) * RowGap;
        return Math.Max(0f, contentHeight - layout.Rows.Height);
    }

    private void BeginClose(CloseDestination destination) {
        if (closeDestination != CloseDestination.None) return;
        if (editingRow is not null) editingRow.CommitEdit!(editBuffer);
        editingRow = null;
        closeDestination = destination;
        draggedSlider = null;
        capturingKey = false;
        keyRow = null;
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_back");
    }

    private void SubscribeTextInput() {
        if (textInputSubscribed) return;
        textInputSubscribed = true;
        TextInput.OnInput += OnTextInput;
        TextInputEXT.TextEditing += OnTextEditing;
    }

    private void ReleaseFocusedInput() {
        if (textInputSubscribed) {
            textInputSubscribed = false;
            TextInput.OnInput -= OnTextInput;
            TextInputEXT.TextEditing -= OnTextEditing;
        }
        MaterialTextInputFocus.Blur(this);
    }

    private void FinishClose() {
        CloseDestination destination = closeDestination;
        closeDestination = CloseDestination.None;
        level.AllowHudHide = oldAllowHudHide;
        RemoveSelf();
        if (destination == CloseDestination.PauseMenu) level.Pause(returnIndex, minimal);
        else level.Paused = false;
        MicroblocksQolUtilsModule.Instance.SaveSettings();
    }

    private List<SettingRow> CurrentRows => tabs[selectedTab].Rows;

    private bool ProfilerTabSelected => tabs[selectedTab].IsProfiler;

    private List<SettingsTab> BuildTabs() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        return [
            new SettingsTab("HUD", "帧率与状态信息", [
                Toggle("启用 QOL 工具", () => settings.Enabled, value => settings.Enabled = value),
                Toggle("HUD 信息卡阴影、背景与边框", () => settings.HudMaterialSurfaces,
                    value => settings.HudMaterialSurfaces = value),
                Toggle("显示帧率", () => settings.ShowFps, value => settings.ShowFps = value),
                Toggle("显示 CPU 帧耗时", () => settings.ShowFrameTime, value => settings.ShowFrameTime = value),
                Toggle("物理与渲染帧率", () => settings.ShowPhysicalAndRenderFps,
                    value => settings.ShowPhysicalAndRenderFps = value),
                Toggle("显示帧率分析", () => settings.EnableFrameProfiler,
                    value => settings.EnableFrameProfiler = value),
                Range("卡顿采样阈值", () => settings.FrameSpikeThresholdMs,
                    value => settings.FrameSpikeThresholdMs = value, 20, 250, 5, value => $"{value} ms"),
                Toggle("显示还剩多少面", () => settings.ShowRoomsRemaining, value => settings.ShowRoomsRemaining = value),
                Toggle("显示地图人数", () => settings.ShowMapPlayerCount, value => settings.ShowMapPlayerCount = value),
                Toggle("显示当前时间", () => settings.ShowClock, value => settings.ShowClock = value)
            ]),
            new SettingsTab("Profiler", "10 秒托管 CPU 采样", [], IsProfiler: true),
            new SettingsTab("小地图", "尺寸、玩家与外观", [
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
                Toggle("高亮正路房间", () => settings.MiniMapHighlightRoute,
                    value => settings.MiniMapHighlightRoute = value),
                Toggle("标注收集品", () => settings.MiniMapCollectibles,
                    value => settings.MiniMapCollectibles = value),
                Toggle("自适应地图颜色", () => settings.MiniMapAdaptiveColors,
                    value => settings.MiniMapAdaptiveColors = value),
                Toggle("显示 MiaoNet 玩家", () => settings.ShowMiaoNetPlayers,
                    value => settings.ShowMiaoNetPlayers = value),
                Toggle("边框显示越界玩家", () => settings.MiniMapShowOffscreenPlayers,
                    value => settings.MiniMapShowOffscreenPlayers = value),
                EnumRow("玩家名字", () => settings.MiniMapNames, value => settings.MiniMapNames = value),
                Toggle("隐藏原生越界名字", () => settings.HideMiaoNetOffscreenNames,
                    value => settings.HideMiaoNetOffscreenNames = value)
            ]),
            new SettingsTab("录制", "录制状态与编码参数", [
                Status("当前状态", RecordingStatus),
                Status("当前片段", () => AutoRecorder.IsRecording ? $"{AutoRecorder.CurrentSeconds:0.0} 秒" : "—"),
                Status("当前文件", () => ShortPath(AutoRecorder.CurrentPath)),
                Status("最后输出", () => ShortPath(AutoRecorder.LastOutput)),
                Status("清理状态", () => AutoRecorder.IsCleaning ? "清理中" : AutoRecorder.LastCleanupStatus),
                Action("开始手动录制", "开始", () => AutoRecorder.StartManual(), () => !AutoRecorder.ManualMode),
                Action("停止并保存", "保存视频", () => AutoRecorder.StopManual(level, save: true),
                    () => AutoRecorder.ManualMode || AutoRecorder.IsRecording),
                Action("停止并丢弃", "丢弃片段", () => AutoRecorder.StopManual(level, save: false),
                    () => AutoRecorder.ManualMode || AutoRecorder.IsRecording),
                Toggle("自动录制", () => settings.AutoRecorderEnabled, value => settings.AutoRecorderEnabled = value),
                Toggle("显示录制红点", () => settings.ShowRecordingIndicator,
                    value => settings.ShowRecordingIndicator = value),
                Toggle("显示录制时长", () => settings.ShowRecordingDuration,
                    value => settings.ShowRecordingDuration = value),
                EnumRow("自动录制策略", () => settings.RecordingPolicy, value => settings.RecordingPolicy = value),
                EnumRow("BGM 处理", () => settings.BgmMode, value => settings.BgmMode = value),
                Toggle("录制 UI 音效", () => settings.RecordingIncludeUiSfx,
                    value => settings.RecordingIncludeUiSfx = value),
                Range("录制帧率", () => settings.RecordingFrameRate, value => settings.RecordingFrameRate = value,
                    30, 120, 30, value => $"{value} FPS"),
                Range("录制码率", () => settings.RecordingBitrateKbps,
                    value => settings.RecordingBitrateKbps = value,
                    2000, 50000, 1000, value => $"{value / 1000f:0.#} Mbps"),
                Range("最多保留录像", () => settings.RecordingRetentionCount,
                    value => settings.RecordingRetentionCount = value,
                    0, 500, 10, value => value == 0 ? "不限" : $"{value} 个"),
                Action("立即清理旧录像", "清理", AutoRecorder.CleanupRecordings,
                    () => settings.RecordingRetentionCount > 0 && !AutoRecorder.IsCleaning),
                Text("输出目录", () => settings.RecordingDirectory, value => settings.RecordingDirectory = value,
                    "留空使用默认目录", 240),
                Text("编码器", () => settings.RecordingEncoder, value => settings.RecordingEncoder = value,
                    "auto / nvenc / qsv / amf…", 48),
                Text("BGM 映射文件", () => settings.BgmEventMapFile, value => settings.BgmEventMapFile = value,
                    "JSON 文件路径", 240)
            ]),
            new SettingsTab("界面与系统", "外观、字体与兼容项", [
                Toggle("亚克力模糊背景", () => settings.MaterialAcrylicBackground,
                    value => settings.MaterialAcrylicBackground = value),
                Range("模糊强度", () => settings.MaterialAcrylicBlurStrength,
                    value => settings.MaterialAcrylicBlurStrength = value, 1, 12, 1, value => value.ToString()),
                Choice("界面字体", UiFontCatalog.InstalledFamilies, () => settings.FontFamily, value => {
                    settings.FontFamily = value;
                    settings.FontFile = "";
                }),
                Toggle("取代原版选关页", () => settings.ReplaceChapterSelect,
                    value => settings.ReplaceChapterSelect = value),
                Toggle("选关页显示 Collab 地图", () => settings.ChapterSelectShowCollabMaps,
                    value => settings.ChapterSelectShowCollabMaps = value),
                Toggle("完全移除场景过渡", () => settings.RemoveRoomTransitions,
                    value => settings.RemoveRoomTransitions = value),
                Toggle("完全移除死亡动画", () => settings.RemoveDeathAnimation,
                    value => settings.RemoveDeathAnimation = value),
                Toggle("关心玩家过面通知", () => settings.WatchedPlayerNotifications,
                    value => settings.WatchedPlayerNotifications = value)
            ])
        ];
    }

    private static SettingRow Toggle(string label, Func<bool> get, Action<bool> set) => new(
        label,
        SettingKind.Toggle,
        () => get() ? "开" : "关",
        direction => set(direction > 0),
        toggleValue: get
    );

    private static SettingRow Range(
        string label,
        Func<int> get,
        Action<int> set,
        int min,
        int max,
        int step,
        Func<int, string> format
    ) => new(
        label,
        SettingKind.Range,
        () => format(get()),
        direction => set(Math.Clamp(get() + Math.Sign(direction) * step, min, max)),
        normalized: () => (get() - min) / (float)Math.Max(1, max - min),
        setNormalized: normalized => {
            int raw = min + (int)MathF.Round((max - min) * normalized / step) * step;
            set(Math.Clamp(raw, min, max));
        },
        editValue: () => get().ToString(),
        commitEdit: text => {
            if (!int.TryParse(text, out int value) || value < min || value > max) return false;
            int snapped = min + (int)MathF.Round((value - min) / (float)step) * step;
            set(Math.Clamp(snapped, min, max));
            return true;
        },
        numericInput: true,
        maxInputLength: 10
    );

    private static SettingRow EnumRow<T>(string label, Func<T> get, Action<T> set) where T : struct, Enum {
        T[] values = Enum.GetValues<T>();
        return new SettingRow(label, SettingKind.Enum, () => FormatEnum(get()), direction => {
            int index = Array.IndexOf(values, get());
            index = (index + Math.Sign(direction) + values.Length) % values.Length;
            set(values[index]);
        });
    }

    private static SettingRow Text(
        string label,
        Func<string> get,
        Action<string> set,
        string placeholder,
        int maxLength
    ) => new(
        label,
        SettingKind.Text,
        get,
        editValue: get,
        commitEdit: value => {
            set(value.Trim());
            return true;
        },
        placeholder: placeholder,
        maxInputLength: maxLength
    );

    private static SettingRow Choice(
        string label,
        IReadOnlyList<string> values,
        Func<string> get,
        Action<string> set
    ) => new(label, SettingKind.Enum, get, direction => {
        if (values.Count == 0) return;
        int index = -1;
        for (int candidate = 0; candidate < values.Count; candidate++) {
            if (!string.Equals(values[candidate], get(), StringComparison.OrdinalIgnoreCase)) continue;
            index = candidate;
            break;
        }
        index = (index + Math.Sign(direction) + values.Count) % values.Count;
        set(values[index]);
    });

    private static SettingRow Key(string label, Func<Keys> get, Action<Keys> set) => new(
        label,
        SettingKind.Key,
        () => get().ToString(),
        assignKey: set
    );

    private static SettingRow Action(string label, string buttonText, System.Action action,
        Func<bool>? enabled = null) => new(
        label,
        SettingKind.Action,
        () => buttonText,
        _ => action(),
        isEnabled: enabled
    );

    private static SettingRow Status(string label, Func<string> value) => new(
        label,
        SettingKind.Status,
        value
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

    private void StartProfilerSampling() {
        if (!ManagedCpuSampler.Start(10d)) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        Audio.Play("event:/ui/main/button_select");
        BeginClose(CloseDestination.Game);
    }

    private void RenderProfilerContent(OverlayLayout layout, MaterialPalette palette) {
        float alpha = ease * contentEase;
        ManagedSamplingStage stage = ManagedCpuSampler.Stage;
        ManagedProfileReport? report = ManagedCpuSampler.LatestReport;
        string status = stage switch {
            ManagedSamplingStage.WarmingUp => "准备返回游戏…",
            ManagedSamplingStage.Sampling => $"采样中 · 剩余 {ManagedCpuSampler.RemainingSeconds:0.0} 秒",
            ManagedSamplingStage.Analyzing => "正在解析托管调用栈…",
            ManagedSamplingStage.Complete => "报告已生成",
            ManagedSamplingStage.Failed => "采样失败",
            _ => report is null ? "尚未采样" : "上次报告"
        };
        MaterialUiKit.Text(status, new Vector2(layout.ContentHeader.Right, layout.ContentHeader.Y + 8f),
            new Vector2(1f, 0f), MaterialTextRole.Caption,
            stage == ManagedSamplingStage.Failed ? Color.OrangeRed : palette.Primary,
            alpha, scaleOverride: 0.29f);

        MaterialRect summary = ProfilerSummaryRect(layout);
        MaterialUi.RoundedRect(summary.X, summary.Y, summary.Width, summary.Height, 25f,
            palette.SurfaceHigh * (0.82f * alpha));
        MaterialUi.RoundedOutline(summary.X, summary.Y, summary.Width, summary.Height, 25f, 1f,
            palette.Outline * (0.42f * alpha));

        string headline;
        string detail;
        if (stage == ManagedSamplingStage.Failed) {
            headline = "无法启动 .NET 采样器";
            detail = Trim(ManagedCpuSampler.Failure, 96);
        } else if (stage is ManagedSamplingStage.WarmingUp or ManagedSamplingStage.Sampling) {
            headline = "正在记录主线程 Update / Render 调用栈";
            detail = "采样期间正常游玩并复现卡顿；不要停在暂停菜单。";
        } else if (stage == ManagedSamplingStage.Analyzing) {
            headline = "采样完成，正在生成报告";
            detail = "正在解析方法、程序集以及 MonoMod hook 目标。";
        } else if (report is not null) {
            headline = $"{report.DurationSeconds:0} 秒 · {report.StackSamples} 个主循环样本 · 最慢帧 {report.MaximumFrameMilliseconds:0.0} ms";
            detail = $"平均帧 {report.AverageFrameMilliseconds:0.0} ms · Update 峰值 {report.MaximumUpdateMilliseconds:0.0} ms · Render 峰值 {report.MaximumRenderMilliseconds:0.0} ms";
        } else {
            headline = "采样真实托管调用栈，包括其他 Mod 安装的 hook";
            detail = "点击后自动返回游戏，等待约 1 秒再开始连续采样 10 秒。";
        }
        MaterialUiKit.Text(headline, new Vector2(summary.X + 24f, summary.Y + 24f), Vector2.Zero,
            MaterialTextRole.Label, palette.OnSurface, alpha, scaleOverride: 0.34f);
        MaterialUiKit.Text(detail, new Vector2(summary.X + 24f, summary.Y + 62f), Vector2.Zero,
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);

        MaterialRect button = ProfilerStartRect(layout);
        bool busy = ManagedCpuSampler.IsBusy;
        MaterialUi.RoundedRect(button.X, button.Y, button.Width, button.Height, 18f,
            (busy ? palette.Outline : palette.Primary) * (alpha * (busy ? 0.26f : 0.92f)));
        MaterialUiKit.Text(busy ? "正在采样" : "开始 10 秒采样", button.Center + new Vector2(0f, -8f),
            new Vector2(0.5f), MaterialTextRole.Label,
            busy ? palette.OnSurfaceVariant : palette.OnPrimary, alpha, scaleOverride: 0.29f);

        if (busy) {
            float progress = ManagedCpuSampler.Progress;
            MaterialUi.RoundedRect(summary.X + 24f, summary.Bottom - 16f, summary.Width - 48f, 5f, 2.5f,
                palette.Outline * (0.32f * alpha));
            MaterialUi.RoundedRect(summary.X + 24f, summary.Bottom - 16f,
                (summary.Width - 48f) * progress, 5f, 2.5f, palette.Primary * alpha);
        }

        if (report is null) {
            MaterialUiKit.Text("报告会分别列出 Update 与 Render 中最常占用 CPU 的方法。\n若方法是 MonoMod detour，下面会额外显示 hook 的目标方法。",
                new Vector2(layout.Rows.X + 18f, summary.Bottom + 40f), Vector2.Zero,
                MaterialTextRole.Body, palette.OnSurfaceVariant, alpha, scaleOverride: 0.31f);
            return;
        }

        float gap = 16f;
        float top = summary.Bottom + 18f;
        float columnWidth = (layout.Rows.Width - gap) / 2f;
        MaterialRect updateColumn = new(layout.Rows.X, top, columnWidth, layout.Rows.Bottom - top);
        MaterialRect renderColumn = new(updateColumn.Right + gap, top, columnWidth, layout.Rows.Bottom - top);
        RenderProfileColumn(updateColumn, "UPDATE", report.UpdateCpuMilliseconds, report.Update, palette, alpha);
        RenderProfileColumn(renderColumn, "RENDER", report.RenderCpuMilliseconds, report.Render, palette, alpha);

        MaterialUiKit.Text($"原始 trace: {Path.GetFileName(report.TracePath)}",
            new Vector2(layout.Rows.X + 4f, layout.Rows.Bottom - 22f), Vector2.Zero,
            MaterialTextRole.Caption, palette.OnSurfaceVariant * 0.72f, alpha, scaleOverride: 0.22f);
    }

    private static void RenderProfileColumn(
        MaterialRect bounds,
        string title,
        double totalMilliseconds,
        IReadOnlyList<ManagedProfileEntry> entries,
        MaterialPalette palette,
        float alpha
    ) {
        MaterialUiKit.Text(title, new Vector2(bounds.X + 4f, bounds.Y + 2f), Vector2.Zero,
            MaterialTextRole.Label, palette.Primary, alpha, scaleOverride: 0.30f);
        MaterialUiKit.Text($"采样 CPU {totalMilliseconds:0} ms", new Vector2(bounds.Right - 4f, bounds.Y + 4f),
            new Vector2(1f, 0f), MaterialTextRole.Caption, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.23f);
        float y = bounds.Y + 34f;
        int count = Math.Min(8, entries.Count);
        for (int index = 0; index < count; index++) {
            ManagedProfileEntry entry = entries[index];
            MaterialRect row = new(bounds.X, y + index * 66f, bounds.Width, 58f);
            MaterialUi.RoundedRect(row.X, row.Y, row.Width, row.Height, 16f,
                palette.SurfaceHigh * (0.68f * alpha));
            float bar = Math.Clamp((float)entry.Percent / 100f, 0f, 1f);
            MaterialUi.RoundedRect(row.X, row.Bottom - 4f, row.Width * bar, 4f, 2f,
                palette.Primary * (0.82f * alpha));
            string owner = entry.HookTarget is null ? entry.Owner : entry.Owner + "  ·  HOOK";
            MaterialUiKit.Text(Trim(owner, 32), new Vector2(row.X + 12f, row.Y + 7f), Vector2.Zero,
                MaterialTextRole.Caption, entry.HookTarget is null ? palette.OnSurfaceVariant : Color.Orange,
                alpha, scaleOverride: 0.22f);
            MaterialUiKit.Text(Trim(entry.Method, 54), new Vector2(row.X + 12f, row.Y + 28f), Vector2.Zero,
                MaterialTextRole.Label, palette.OnSurface, alpha, scaleOverride: 0.24f);
            MaterialUiKit.Text($"{entry.Percent:0.0}%", new Vector2(row.Right - 12f, row.Y + 7f),
                new Vector2(1f, 0f), MaterialTextRole.Caption, palette.Primary,
                alpha, scaleOverride: 0.22f);
            if (entry.HookTarget is not null) {
                MaterialUiKit.Text(Trim("→ " + entry.HookTarget, 58), new Vector2(row.X + 12f, row.Bottom - 18f),
                    Vector2.Zero, MaterialTextRole.Caption, palette.OnSurfaceVariant,
                    alpha, scaleOverride: 0.18f);
            }
        }
        if (count == 0) {
            MaterialUiKit.Text("没有捕获到该阶段的主线程样本", new Vector2(bounds.X + 4f, y + 14f),
                Vector2.Zero, MaterialTextRole.Caption, palette.OnSurfaceVariant,
                alpha, scaleOverride: 0.25f);
        }
    }

    private static MaterialRect ProfilerSummaryRect(OverlayLayout layout) =>
        new(layout.Rows.X, layout.Rows.Y, layout.Rows.Width, 112f);

    private static MaterialRect ProfilerStartRect(OverlayLayout layout) {
        MaterialRect summary = ProfilerSummaryRect(layout);
        return new MaterialRect(summary.Right - 232f, summary.Y + 26f, 204f, 54f);
    }

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

    private static MaterialRect ToggleRect(MaterialRect row) => new(row.Right - 78f, row.Y + 45f, 58f, 28f);

    private static MaterialRect SliderRect(MaterialRect row) => new(row.X + 20f, row.Y + 61f,
        Math.Max(40f, row.Width - 154f), 7f);

    private static MaterialRect RangeValueRect(MaterialRect row) => new(row.Right - 118f, row.Y + 46f, 98f, 34f);

    private static MaterialRect WideControlRect(MaterialRect row) => new(row.X + 18f, row.Y + 45f,
        row.Width - 36f, 35f);

    private static void SetTextInputRectangle(MaterialRect rect) {
        float xScale = Engine.ViewWidth / ScreenWidth;
        float yScale = Engine.ViewHeight / ScreenHeight;
        TextInputEXT.SetInputRectangle(new Rectangle(
            (int)(rect.X * xScale),
            (int)(rect.Y * yScale),
            Math.Max(1, (int)(rect.Width * xScale)),
            Math.Max(1, (int)(rect.Height * yScale))
        ));
    }

    private static string Trim(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : value[..Math.Max(1, maxCharacters - 1)] + "…";

    private static string TrimFromLeft(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : "…" + value[^Math.Max(1, maxCharacters - 1)..];

    private enum SettingKind {
        Toggle,
        Range,
        Enum,
        Text,
        Key,
        Action,
        Status
    }

    private enum CloseDestination {
        None,
        PauseMenu,
        Game
    }

    private sealed record SettingsTab(
        string Title,
        string Summary,
        List<SettingRow> Rows,
        bool IsProfiler = false
    );

    private sealed class SettingRow {
        public string Label { get; }
        public SettingKind Kind { get; }
        public Func<string> Value { get; }
        public Action<int>? Change { get; }
        public Action<Keys>? AssignKey { get; }
        public Func<bool>? IsEnabled { get; }
        public Func<bool>? ToggleValue { get; }
        public Func<float>? Normalized { get; }
        public Action<float>? SetNormalized { get; }
        public Func<string>? EditValue { get; }
        public Func<string, bool>? CommitEdit { get; }
        public bool NumericInput { get; }
        public string? Placeholder { get; }
        public int MaxInputLength { get; }
        public float FocusAnimation { get; set; }
        public float HoverAnimation { get; set; }
        public float ToggleAnimation { get; set; }
        public float SliderAnimation { get; set; }
        public float Pulse { get; set; }

        public SettingRow(
            string label,
            SettingKind kind,
            Func<string> value,
            Action<int>? change = null,
            Action<Keys>? assignKey = null,
            Func<bool>? isEnabled = null,
            Func<bool>? toggleValue = null,
            Func<float>? normalized = null,
            Action<float>? setNormalized = null,
            Func<string>? editValue = null,
            Func<string, bool>? commitEdit = null,
            bool numericInput = false,
            string? placeholder = null,
            int maxInputLength = 120
        ) {
            Label = label;
            Kind = kind;
            Value = value;
            Change = change;
            AssignKey = assignKey;
            IsEnabled = isEnabled;
            ToggleValue = toggleValue;
            Normalized = normalized;
            SetNormalized = setNormalized;
            EditValue = editValue;
            CommitEdit = commitEdit;
            NumericInput = numericInput;
            Placeholder = placeholder;
            MaxInputLength = maxInputLength;
        }

        public bool Enabled() => IsEnabled?.Invoke() ?? true;

        public void InitializeVisuals() {
            ToggleAnimation = ToggleValue?.Invoke() == true ? 1f : 0f;
            SliderAnimation = Normalized?.Invoke() ?? 0f;
        }
    }

    private readonly record struct OverlayLayout(
        MaterialRect Panel,
        MaterialRect Header,
        MaterialRect Navigation,
        MaterialRect Body,
        MaterialRect ContentHeader,
        MaterialRect Rows,
        MaterialRect Footer
    ) {
        public const float TabHeight = 64f;
        private const float TabGap = 10f;

        public static OverlayLayout Create(float ease) {
            float offsetY = (1f - Ease.CubeOut(ease)) * 26f;
            MaterialRect panel = new MaterialRect(100f, 40f + offsetY, 1720f, 1000f);
            MaterialRect inner = panel.Inset(34f, 28f);
            MaterialRect[] vertical = MaterialLayout.Split(
                inner,
                MaterialAxis.Vertical,
                MaterialSpacing.Md,
                MaterialTrack.Fixed(82f),
                MaterialTrack.Flex(),
                MaterialTrack.Fixed(32f)
            );
            MaterialRect[] main = MaterialLayout.Split(
                vertical[1],
                MaterialAxis.Horizontal,
                MaterialSpacing.Lg,
                MaterialTrack.Fixed(272f),
                MaterialTrack.Flex()
            );
            MaterialRect body = main[1];
            MaterialRect[] content = MaterialLayout.Split(
                body,
                MaterialAxis.Vertical,
                MaterialSpacing.Sm,
                MaterialTrack.Fixed(52f),
                MaterialTrack.Flex()
            );
            MaterialRect rows = content[1].Inset(0f, 0f, 16f, 0f);
            return new OverlayLayout(panel, vertical[0], main[0], body, content[0], rows, vertical[2]);
        }

        public MaterialRect Tab(int index, int count) {
            _ = count;
            return new MaterialRect(
                Navigation.X + 10f,
                Navigation.Y + 12f + index * (TabHeight + TabGap),
                Navigation.Width - 20f,
                TabHeight
            );
        }

        public MaterialRect Row(int index, float scrollOffset) {
            int column = index % Columns;
            int band = index / Columns;
            float width = (Rows.Width - RowGap) / Columns;
            return new MaterialRect(
                Rows.X + column * (width + RowGap),
                Rows.Y + band * (RowHeight + RowGap) - scrollOffset,
                width,
                RowHeight
            );
        }
    }
}
