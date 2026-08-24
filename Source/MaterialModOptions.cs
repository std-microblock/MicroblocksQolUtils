using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Celeste.Mod.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Material replacement for Everest's global Mod Options page. Everest still builds the
/// TextMenu, so custom entries and binding/config sub-pages keep their original behavior;
/// this class only owns grouping, navigation, layout and mouse interaction.
/// </summary>
public sealed class MaterialModOptions : Oui, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const float TabHeight = 52f;
    private const float TabGap = 8f;
    private const float RowGap = 10f;
    private const float DropdownItemHeight = 46f;
    private const int DropdownMaxVisibleItems = 8;
    private const int DropdownOptionLimit = 24;

    private static Hook? gotoRoutineHook;
    private static bool hookFailed;

    private readonly List<ModTab> tabs = [];
    private readonly Dictionary<TextMenu.Item, bool> naturalVisibility = [];
    private readonly MaterialScrollController tabScroll = new();
    private readonly MaterialScrollController rowScroll = new();
    private readonly MaterialScrollViewport tabViewport = new("mqol-mod-options-tabs");
    private readonly MaterialScrollViewport rowViewport = new("mqol-mod-options-rows");
    private readonly MaterialMotionController motion = new();

    private TextMenu? menu;
    private Level? pauseLevel;
    private int pauseReturnIndex;
    private bool pauseMinimal;
    private bool oldAllowHudHide;
    private int selectedTab;
    private float ease;
    private bool display;
    private float inputDelay;
    private CloseDestination closeDestination;
    private bool saveStarted;
    private string? savedTabId;
    private int savedItemOrdinal = -1;
    private TextMenu.Item? dropdownItem;
    private int dropdownHighlight;
    private int dropdownFirstVisible;

    public bool SuppressNormalRender { get; set; }

    internal static MaterialModOptions? ActivePage {
        get {
            if (Engine.Scene is not Overworld overworld) return null;
            MaterialModOptions? page = overworld.Current as MaterialModOptions
                ?? overworld.Next as MaterialModOptions;
            return page is { Visible: true } ? page : null;
        }
    }

    public MaterialModOptions() {
        Tag = Tags.HUD | Tags.PauseUpdate | Tags.TransitionUpdate;
    }

    private MaterialModOptions(Level level, int returnIndex, bool minimal, bool allowHudHide) : this() {
        pauseLevel = level;
        pauseReturnIndex = returnIndex;
        pauseMinimal = minimal;
        oldAllowHudHide = allowHudHide;
        Depth = -2_000_000;
    }

    public static void Load() {
        Everest.Events.Level.OnCreatePauseMenuButtons += ReplacePauseMenuButton;
        if (gotoRoutineHook is not null || hookFailed) return;
        try {
            MethodInfo method = typeof(Overworld).GetMethod(
                "GotoRoutine",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new MissingMethodException(typeof(Overworld).FullName, "GotoRoutine");
            gotoRoutineHook = new Hook(method, (GotoRoutineDetour)DetourGotoRoutine);
        } catch (Exception exception) {
            hookFailed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialModOptions");
        }
    }

    public static void Unload() {
        Everest.Events.Level.OnCreatePauseMenuButtons -= ReplacePauseMenuButton;
        gotoRoutineHook?.Dispose();
        gotoRoutineHook = null;
        hookFailed = false;
    }

    public override bool IsStart(Overworld overworld, Overworld.StartMode start) => false;

    public override IEnumerator Enter(Oui from) {
        Visible = true;
        display = true;
        ease = 0f;
        inputDelay = 0.18f;
        closeDestination = CloseDestination.None;
        saveStarted = false;
        BuildMenu(inGame: false);
        AttachMenu();
        RestoreTab(from is OuiModOptions.ISubmenu);
        Audio.Play("event:/ui/main/whoosh_large_in");
        yield return null;
    }

    public override IEnumerator Leave(Oui next) {
        display = false;
        menu?.Focused = false;
        SaveTabState();
        for (float timer = 0f; timer < 0.16f; timer += Engine.RawDeltaTime) yield return null;
        yield return Everest.SaveSettings();
        DetachMenu();
        Visible = false;
    }

    public override void Added(Scene scene) {
        base.Added(scene);
        if (pauseLevel is null) return;
        Visible = true;
        display = true;
        ease = 0f;
        inputDelay = 0.18f;
        BuildMenu(inGame: true);
        AttachMenu();
        RestoreTab(restoreSaved: false);
    }

    public override void Removed(Scene scene) {
        DetachMenu();
        if (pauseLevel is not null) pauseLevel.AllowHudHide = oldAllowHudHide;
        tabViewport.Dispose();
        rowViewport.Dispose();
        motion.Dispose();
        base.Removed(scene);
    }

    public override void Update() {
        base.Update();
        ModOptionsLayout layout = ModOptionsLayout.Create(1f - Ease.CubeOut(ease));
        tabScroll.Update(MaxTabScroll(layout));
        rowScroll.Update(MaxRowScroll(layout));
        ease = Calc.Approach(ease, display ? 1f : 0f, Engine.RawDeltaTime * 7.5f);
        inputDelay -= Engine.RawDeltaTime;
        UpdateInteractions(layout);

        if (!display) {
            if (pauseLevel is not null && ease <= 0.001f && !saveStarted) {
                saveStarted = true;
                Add(new Coroutine(SaveAndFinishPause()));
            }
            return;
        }

        if (menu is null) return;
        bool acceptsInput = pauseLevel is not null || Selected && Focused;
        if (!menu.Focused) {
            menu.Update();
            ForceDescriptionsVisible();
            CaptureNaturalVisibility();
            return;
        }
        if (!acceptsInput || inputDelay > 0f) {
            menu.Focused = false;
            menu.Update();
            menu.Focused = true;
            ForceDescriptionsVisible();
            CaptureNaturalVisibility();
            return;
        }

        if (dropdownItem is not null) {
            UpdateDropdown(layout);
            return;
        }

        if (Input.Pause.Pressed && pauseLevel is not null) {
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            if (pauseLevel is null) {
                Audio.Play("event:/ui/main/button_back");
                Overworld.Goto<OuiMainMenu>();
            } else {
                BeginClose(CloseDestination.PauseMenu);
            }
            return;
        }

        if (MInput.Keyboard.Pressed(Keys.Tab)
            || MInput.Keyboard.Pressed(Keys.PageDown)
            || MInput.Keyboard.Pressed(Keys.PageUp)) {
            bool backwards = MInput.Keyboard.Pressed(Keys.PageUp)
                || MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift);
            SelectTab(selectedTab + (backwards ? -1 : 1));
        }

        if ((Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space))
            && menu.Current is { } current
            && TryGetOption(current, out OptionSnapshot option)) {
            if (option.Options.Count == 2) {
                SetOptionIndex(current, option.Index == 0 ? 1 : 0);
                return;
            }
            if (CanUseDropdown(option)) {
                OpenDropdown(current, option);
                return;
            }
        }

        UpdateMouse(layout);
        int previousSelection = menu.Selection;
        menu.Update();
        ForceDescriptionsVisible();
        CaptureNaturalVisibility();
        if (menu.Selection != previousSelection) EnsureSelectionVisible(layout);
    }

    public override void Render() {
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        if (!Visible || ease <= 0f || menu is null) return;
        float alpha = Ease.CubeOut(ease);
        ModOptionsLayout layout = ModOptionsLayout.Create(1f - alpha);
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * (0.90f * alpha));
        MaterialUiKit.Surface(
            layout.Frame,
            40f,
            palette with { SurfaceHigh = palette.Surface * (acrylicActive ? 0.78f : 0.96f) },
            alpha
        );

        MaterialUiKit.Icon("extension", new Vector2(layout.Header.X + 20f, layout.Header.Center.Y),
            34f, palette.Primary, alpha, filled: true);
        MaterialUiKit.Text(UiText("microblocks_qol_modoptions_title", "模组设置"),
            new Vector2(layout.Header.X + 52f, layout.Header.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Display, palette.OnSurface, alpha, scaleOverride: 0.72f);
        MaterialUiKit.Text($"{tabs.Count} {UiText("microblocks_qol_modoptions_tabs", "个分类")}",
            new Vector2(layout.Header.Right, layout.Header.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.29f);

        RenderTabs(layout, palette, alpha);
        RenderRows(layout, palette, alpha);
        if (dropdownItem is not null) RenderDropdown(layout, palette, alpha);
        RenderFooter(layout, palette, alpha);
        MaterialUiKit.Cursor(MInput.Mouse.Position, palette, alpha);
    }

    private void BuildMenu(bool inGame) {
        DetachMenu();
        tabs.Clear();
        naturalVisibility.Clear();
        menu = OuiModOptions.CreateMenu(inGame, null!);
        menu.Active = false;
        menu.Visible = false;
        menu.Focused = true;
        menu.AutoScroll = false;
        menu.ItemSpacing = RowGap;

        List<TextMenu.Item> leading = [];
        ModTab? current = null;
        foreach (TextMenu.Item item in menu.Items) {
            ForceDescriptionVisible(item);
            naturalVisibility[item] = item.Visible;
            if (IsEverestHeaderImage(item)) {
                naturalVisibility[item] = false;
                continue;
            }
            if (item is TextMenu.SubHeader header
                && TrySplitModuleHeader(header.Title, out string title, out string version)) {
                current = new ModTab($"{header.Title}#{tabs.Count}", title, version, []);
                tabs.Add(current);
                naturalVisibility[item] = false;
                continue;
            }
            if (current is null) leading.Add(item);
            else current.Items.Add(item);
        }

        if (leading.Any(item => naturalVisibility.GetValueOrDefault(item))) {
            tabs.Insert(0, new ModTab("__status", UiText("microblocks_qol_modoptions_status", "状态"), "", leading));
        } else {
            foreach (TextMenu.Item item in leading) naturalVisibility[item] = false;
        }
        if (tabs.Count == 0) {
            tabs.Add(new ModTab("__empty", UiText("microblocks_qol_modoptions_empty", "没有可用设置"), "", []));
        }
        ForceDescriptionsVisible();
    }

    private void AttachMenu() {
        if (menu is null || Scene is null || menu.Scene is not null) return;
        Scene.Add(menu);
    }

    private void DetachMenu() {
        if (menu is null) return;
        if (menu.Scene is not null) menu.RemoveSelf();
        menu = null;
    }

    private void RestoreTab(bool restoreSaved) {
        int restored = restoreSaved && savedTabId is not null
            ? tabs.FindIndex(tab => tab.Id == savedTabId)
            : -1;
        selectedTab = restored >= 0 ? restored : 0;
        if (restoreSaved && savedItemOrdinal >= 0 && savedItemOrdinal < tabs[selectedTab].Items.Count) {
            tabs[selectedTab].Selection = menu?.IndexOf(tabs[selectedTab].Items[savedItemOrdinal]) ?? -1;
        }
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
    }

    private void SelectTab(int index) {
        if (tabs.Count == 0) return;
        CloseDropdown(playSound: false);
        SaveTabState();
        selectedTab = (index % tabs.Count + tabs.Count) % tabs.Count;
        rowScroll.Reset();
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
        Audio.Play(index < 0 ? "event:/ui/main/rollover_up" : "event:/ui/main/rollover_down");
    }

    private void SaveTabState() {
        if (menu is null || tabs.Count == 0) return;
        CaptureNaturalVisibility();
        ModTab tab = tabs[Math.Clamp(selectedTab, 0, tabs.Count - 1)];
        savedTabId = tab.Id;
        tab.Selection = menu.Selection;
        savedItemOrdinal = menu.Current is { } current ? tab.Items.IndexOf(current) : -1;
    }

    private void ApplyTabVisibility() {
        if (menu is null || tabs.Count == 0) return;
        menu.Current?.OnLeave?.Invoke();
        ModTab tab = tabs[selectedTab];
        HashSet<TextMenu.Item> active = tab.Items.ToHashSet();
        foreach (TextMenu.Item item in menu.Items) {
            item.Visible = active.Contains(item) && naturalVisibility.GetValueOrDefault(item);
        }
        menu.MinWidth = Math.Max(600f, ModOptionsLayout.Create(0f).Rows.Width - 54f);
        menu.RecalculateSize();
        int selection = tab.Selection;
        if (selection < 0 || selection >= menu.Items.Count || !CanSelect(menu.Items[selection])) {
            selection = menu.Items.FindIndex(CanSelect);
        }
        menu.Selection = selection;
        if (selection >= 0) menu.Items[selection].OnEnter?.Invoke();
        ForceDescriptionsVisible();
    }

    private void CaptureNaturalVisibility() {
        if (menu is null || tabs.Count == 0) return;
        foreach (TextMenu.Item item in tabs[selectedTab].Items) naturalVisibility[item] = item.Visible;
    }

    private void UpdateMouse(ModOptionsLayout layout) {
        if (menu is null) return;
        Vector2 mouse = MInput.Mouse.Position;
        if (MInput.Mouse.WheelDelta != 0) {
            float direction = -Math.Sign(MInput.Mouse.WheelDelta);
            if (layout.Navigation.Contains(mouse)) {
                tabScroll.Scroll(direction * 180f, MaxTabScroll(layout));
            } else if (layout.Rows.Contains(mouse)) {
                rowScroll.Scroll(direction * 220f, MaxRowScroll(layout));
            }
        }

        if (!MInput.Mouse.WasMoved && !MInput.Mouse.PressedLeftButton) return;
        int tabIndex = TabIndexAt(mouse, layout);
        if (tabIndex >= 0) {
            if (MInput.Mouse.PressedLeftButton && tabIndex != selectedTab) SelectTab(tabIndex);
            return;
        }

        RowPlacement? placement = RowAt(mouse, layout);
        if (placement is null || !placement.Value.Item.Hoverable) return;
        SelectItem(placement.Value.Item);
        if (!MInput.Mouse.PressedLeftButton) return;
        TextMenu.Item item = placement.Value.Item;
        if (item.Disabled) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        MaterialRect rect = placement.Value.Rect;
        if (TryGetOption(item, out OptionSnapshot option)) {
            if (option.Options.Count == 2) {
                SetOptionIndex(item, option.Index == 0 ? 1 : 0);
            } else if (CanUseDropdown(option)) {
                OpenDropdown(item, option);
            } else {
                SetOptionFromMouse(item, option, SliderControlRect(rect), mouse.X);
            }
        } else if (TryGetIntSlider(item, out IntSliderSnapshot slider)) {
            SetIntSliderFromMouse(item, slider, SliderControlRect(rect), mouse.X);
        } else {
            item.ConfirmPressed();
        }
        motion.Pulse(ItemKey(item), mouse);
    }

    private void UpdateDropdown(ModOptionsLayout layout) {
        TextMenu.Item? item = dropdownItem;
        if (item is null || !item.Visible || !TryGetOption(item, out OptionSnapshot option)
            || !CanUseDropdown(option)) {
            CloseDropdown(playSound: false);
            return;
        }
        if (Input.Pause.Pressed && pauseLevel is not null) {
            CloseDropdown(playSound: false);
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            CloseDropdown();
            return;
        }
        if (Input.MenuUp.Pressed) {
            MoveDropdown(-1, option.Options.Count);
            return;
        }
        if (Input.MenuDown.Pressed) {
            MoveDropdown(1, option.Options.Count);
            return;
        }
        if (Input.MenuConfirm.Pressed
            || MInput.Keyboard.Pressed(Keys.Enter)
            || MInput.Keyboard.Pressed(Keys.Space)) {
            CommitDropdown(option);
            return;
        }

        MaterialRect dropdown = DropdownRect(layout, item, option.Options.Count);
        int visibleCount = DropdownVisibleCount(option.Options.Count);
        if (MInput.Mouse.WheelDelta != 0 && dropdown.Contains(MInput.Mouse.Position)) {
            dropdownFirstVisible = Math.Clamp(
                dropdownFirstVisible - Math.Sign(MInput.Mouse.WheelDelta),
                0,
                Math.Max(0, option.Options.Count - visibleCount)
            );
            dropdownHighlight = Math.Clamp(dropdownHighlight, dropdownFirstVisible,
                dropdownFirstVisible + visibleCount - 1);
        }
        if (MInput.Mouse.WasMoved || MInput.Mouse.PressedLeftButton) {
            for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
                MaterialRect row = DropdownItemRect(dropdown, visibleIndex);
                if (!row.Contains(MInput.Mouse.Position)) continue;
                dropdownHighlight = dropdownFirstVisible + visibleIndex;
                if (MInput.Mouse.PressedLeftButton) CommitDropdown(option);
                return;
            }
        }
        if (MInput.Mouse.PressedLeftButton) CloseDropdown();
    }

    private void OpenDropdown(TextMenu.Item item, OptionSnapshot option) {
        dropdownItem = item;
        dropdownHighlight = Math.Clamp(option.Index, 0, option.Options.Count - 1);
        dropdownFirstVisible = 0;
        EnsureDropdownHighlightVisible(option.Options.Count);
        Audio.Play("event:/ui/main/button_select");
    }

    private void MoveDropdown(int direction, int optionCount) {
        dropdownHighlight = (dropdownHighlight + Math.Sign(direction) + optionCount) % optionCount;
        EnsureDropdownHighlightVisible(optionCount);
        Audio.Play(direction < 0 ? "event:/ui/main/rollover_up" : "event:/ui/main/rollover_down");
    }

    private void EnsureDropdownHighlightVisible(int optionCount) {
        int visibleCount = DropdownVisibleCount(optionCount);
        if (dropdownHighlight < dropdownFirstVisible) dropdownFirstVisible = dropdownHighlight;
        else if (dropdownHighlight >= dropdownFirstVisible + visibleCount)
            dropdownFirstVisible = dropdownHighlight - visibleCount + 1;
        dropdownFirstVisible = Math.Clamp(dropdownFirstVisible, 0,
            Math.Max(0, optionCount - visibleCount));
    }

    private void CommitDropdown(OptionSnapshot option) {
        TextMenu.Item? item = dropdownItem;
        if (item is null) return;
        SetOptionIndex(item, Math.Clamp(dropdownHighlight, 0, option.Options.Count - 1));
        dropdownItem = null;
    }

    private void CloseDropdown(bool playSound = true) {
        if (dropdownItem is null) return;
        dropdownItem = null;
        if (playSound) Audio.Play("event:/ui/main/button_back");
    }

    private void SelectItem(TextMenu.Item item) {
        if (menu is null || menu.Current == item) return;
        menu.Current?.OnLeave?.Invoke();
        menu.Current = item;
        item.OnEnter?.Invoke();
        item.SelectWiggler?.Start();
        Audio.Play("event:/ui/main/rollover_down");
        EnsureSelectionVisible(ModOptionsLayout.Create(0f));
    }

    private void EnsureSelectionVisible(ModOptionsLayout layout) {
        if (menu is null || menu.Selection < 0) return;
        float top = 0f;
        foreach (TextMenu.Item item in tabs[selectedTab].Items) {
            if (!item.Visible) continue;
            float height = RowHeight(item);
            if (item == menu.Current) {
                rowScroll.EnsureVisible(top, top + height, layout.Rows.Height, MaxRowScroll(layout));
                return;
            }
            top += height + RowGap;
        }
    }

    private void EnsureTabVisible(ModOptionsLayout layout) {
        float top = selectedTab * (TabHeight + TabGap);
        tabScroll.EnsureVisible(top, top + TabHeight, layout.NavigationItems.Height, MaxTabScroll(layout));
    }

    private void UpdateInteractions(ModOptionsLayout layout) {
        List<MaterialInteractionTarget> targets = [];
        for (int index = 0; index < tabs.Count; index++) {
            MaterialRect rect = layout.Tab(index, tabScroll.Offset);
            if (rect.Bottom < layout.NavigationItems.Y || rect.Y > layout.NavigationItems.Bottom) continue;
            targets.Add(new MaterialInteractionTarget($"mod-options.tab.{tabs[index].Id}", rect,
                Focused: index == selectedTab));
        }
        if (menu is not null && tabs.Count > 0) {
            foreach (RowPlacement placement in RowPlacements(layout)) {
                if (!placement.Item.Hoverable) continue;
                targets.Add(new MaterialInteractionTarget(ItemKey(placement.Item), placement.Rect,
                    Focused: menu.Current == placement.Item));
            }
        }
        motion.Update(targets);
    }

    private void RenderTabs(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        MaterialUi.RoundedRect(layout.Navigation.X, layout.Navigation.Y, layout.Navigation.Width,
            layout.Navigation.Height, 28f, palette.SurfaceHigh * (0.72f * alpha));
        MaterialUiKit.Text(UiText("microblocks_qol_modoptions_mods", "模组与分类"),
            new Vector2(layout.NavigationItems.X, layout.Navigation.Y + 30f), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, palette.OnSurfaceVariant, alpha, scaleOverride: 0.28f);

        tabViewport.Render(layout.NavigationItems, () => {
            for (int index = 0; index < tabs.Count; index++) {
                ModTab tab = tabs[index];
                MaterialRect rect = layout.Tab(index, tabScroll.Offset);
                if (rect.Bottom < layout.NavigationItems.Y || rect.Y > layout.NavigationItems.Bottom) continue;
                bool selected = index == selectedTab;
                if (selected) {
                    MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 20f,
                        palette.Primary * (0.92f * alpha));
                }
                motion.RenderStateLayer($"mod-options.tab.{tab.Id}", rect, 20f,
                    selected ? palette.OnPrimary : palette.Primary, alpha);
                MaterialUiKit.Icon(index == 0 && tab.Id == "__status" ? "info" : "extension",
                    new Vector2(rect.X + 26f, rect.Center.Y), 21f,
                    selected ? palette.OnPrimary : palette.Primary, alpha, filled: selected);
                string title = MaterialTextUtil.Ellipsize(tab.Title, rect.Width - 72f, 0.29f, UiFontWeight.Bold);
                MaterialUiKit.Text(title, new Vector2(rect.X + 48f, rect.Center.Y), new Vector2(0f, 0.5f),
                    MaterialTextRole.Label, selected ? palette.OnPrimary : palette.OnSurfaceVariant,
                    alpha, scaleOverride: 0.29f);
            }
        });
    }

    private void RenderRows(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        ModTab tab = tabs[selectedTab];
        MaterialUi.RoundedRect(layout.Content.X, layout.Content.Y, layout.Content.Width,
            layout.Content.Height, 28f, palette.Surface * (0.46f * alpha));
        MaterialUiKit.Icon("tune", new Vector2(layout.ContentHeader.X + 15f, layout.ContentHeader.Center.Y),
            27f, palette.Primary, alpha, filled: true);
        MaterialUiKit.Text(tab.Title,
            new Vector2(layout.ContentHeader.X + 42f, layout.ContentHeader.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Title, palette.OnSurface, alpha, scaleOverride: 0.47f);
        if (tab.Version.Length > 0) {
            MaterialUiKit.Chip(tab.Version, new Vector2(layout.ContentHeader.Right, layout.ContentHeader.Y + 9f),
                palette, selected: false, alpha);
        }

        menu!.Alpha = alpha;
        menu.HighlightColor = palette.Primary;
        menu.MinWidth = Math.Max(600f, layout.Rows.Width - 54f);
        menu.RecalculateSize();
        rowViewport.Render(layout.Rows, () => {
            bool renderedAny = false;
            foreach (RowPlacement placement in RowPlacements(layout)) {
                TextMenu.Item item = placement.Item;
                MaterialRect rect = placement.Rect;
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                renderedAny = true;
                bool selected = menu.Current == item && item.Hoverable;
                if (item is TextMenuExt.EaseInSubHeaderExt description) {
                    RenderDescription(description.Title, rect, palette, alpha);
                    continue;
                }
                if (item is TextMenu.SubHeader header) {
                    string title = MaterialTextUtil.Ellipsize(header.Title, rect.Width - 16f,
                        0.34f, UiFontWeight.Bold);
                    MaterialUiKit.Text(title, new Vector2(rect.X + 8f, rect.Center.Y),
                        new Vector2(0f, 0.5f), MaterialTextRole.Section,
                        palette.Primary, alpha, scaleOverride: 0.34f);
                    continue;
                }
                MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 22f,
                    (selected ? palette.SurfaceHighest : palette.SurfaceHigh) * ((selected ? 0.94f : 0.64f) * alpha));
                if (selected) {
                    MaterialUi.RoundedRect(rect.X, rect.Y + 14f, 4f, rect.Height - 28f, 2f,
                        palette.Primary * alpha);
                }
                motion.RenderStateLayer(ItemKey(item), rect, 22f, palette.Primary, alpha);
                if (!RenderStandardItem(item, rect, palette, alpha, selected)) {
                    item.Render(new Vector2(rect.X + 26f, rect.Center.Y), selected);
                }
            }
            if (!renderedAny && tabs[selectedTab].Items.All(item => !item.Visible)) {
                MaterialUiKit.Text(UiText("microblocks_qol_modoptions_empty", "没有可用设置"),
                    layout.Rows.Center, new Vector2(0.5f), MaterialTextRole.Body,
                    palette.OnSurfaceVariant, alpha, scaleOverride: 0.36f);
            }
        });

        float maximum = MaxRowScroll(layout);
        if (maximum > 0f) {
            float ratio = layout.Rows.Height / (layout.Rows.Height + maximum);
            float thumbHeight = Math.Max(52f, layout.Rows.Height * ratio);
            float travel = layout.Rows.Height - thumbHeight;
            float y = layout.Rows.Y + rowScroll.Offset / maximum * travel;
            MaterialUi.RoundedRect(layout.Rows.Right + 8f, layout.Rows.Y, 5f, layout.Rows.Height, 2.5f,
                palette.Outline * (0.20f * alpha));
            MaterialUi.RoundedRect(layout.Rows.Right + 8f, y, 5f, thumbHeight, 2.5f,
                palette.Primary * (0.72f * alpha));
        }
    }

    private bool RenderStandardItem(TextMenu.Item item, MaterialRect rect, MaterialPalette palette,
        float alpha, bool selected) {
        bool enabled = !item.Disabled;
        Color labelColor = enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f;
        if (TryGetOption(item, out OptionSnapshot option)) {
            MaterialRect control = option.Options.Count == 2
                ? SwitchControlArea(rect)
                : CanUseDropdown(option)
                    ? DropdownControlRect(rect)
                    : SliderControlArea(rect);
            RenderItemLabel(option.Label, rect, control.X - rect.X - 28f, labelColor, alpha);
            if (option.Options.Count == 2) {
                RenderSwitch(option, rect, palette, alpha, enabled);
            } else if (CanUseDropdown(option)) {
                RenderOptionControl(item, option, rect, palette, alpha, enabled);
            } else {
                RenderOptionSlider(option, rect, palette, alpha, enabled);
            }
            return true;
        }
        if (TryGetIntSlider(item, out IntSliderSnapshot slider)) {
            MaterialRect control = SliderControlArea(rect);
            RenderItemLabel(slider.Label, rect, control.X - rect.X - 28f, labelColor, alpha);
            RenderIntSlider(slider, rect, palette, alpha, enabled);
            return true;
        }
        if (item is TextMenu.Button button) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(button.Label, rect, action.X - rect.X - 28f, labelColor, alpha);
            RenderActionControl(action, UiText("microblocks_qol_modoptions_open", "打开"),
                palette, alpha, enabled);
            return true;
        }
        if (item is TextMenu.Setting setting) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(setting.Label, rect, action.X - rect.X - 28f, labelColor, alpha);
            string value = setting.Values.Count == 0
                ? UiText("microblocks_qol_modoptions_unbound", "未绑定")
                : string.Format(UiText("microblocks_qol_modoptions_bound", "已绑定 {0} 项"), setting.Values.Count);
            RenderActionControl(action, value, palette, alpha, enabled);
            return true;
        }
        if (IsExpandedComposite(item)) return false;
        if (TryGetLabel(item, out string label)) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(label, rect, action.X - rect.X - 28f, labelColor, alpha);
            RenderActionControl(action, selected
                    ? UiText("microblocks_qol_modoptions_active", "已展开")
                    : UiText("microblocks_qol_modoptions_open", "打开"),
                palette, alpha, enabled);
            return true;
        }
        return false;
    }

    private static void RenderItemLabel(string label, MaterialRect rect, float maximumWidth,
        Color color, float alpha) {
        string shown = MaterialTextUtil.Ellipsize(label, Math.Max(80f, maximumWidth),
            0.34f, UiFontWeight.Bold);
        MaterialUiKit.Text(shown, new Vector2(rect.X + 24f, rect.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, color, alpha, scaleOverride: 0.34f);
    }

    private static void RenderSwitch(OptionSnapshot option, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        bool on = option.Index > 0;
        MaterialRect track = SwitchRect(rect);
        Color trackColor = on ? palette.Primary : palette.Outline * 0.52f;
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            trackColor * (alpha * (enabled ? 1f : 0.45f)));
        float knobX = on ? track.Right - 15f : track.X + 15f;
        MaterialUi.Circle(new Vector2(knobX, track.Center.Y), 11f,
            (on ? palette.OnPrimary : palette.OnSurfaceVariant) * alpha);
        string current = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(current, 250f, 0.29f, UiFontWeight.Bold),
            new Vector2(track.X - 18f, track.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurfaceVariant : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.29f);
    }

    private void RenderOptionControl(TextMenu.Item item, OptionSnapshot option, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect control = DropdownControlRect(rect);
        bool open = dropdownItem == item;
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 16f,
            open ? 2f : 1f, (open ? palette.Primary : palette.Outline) * (alpha * (open ? 1f : 0.52f)));
        string current = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(current, control.Width - 52f, 0.28f, UiFontWeight.Bold),
            new Vector2(control.X + 15f, control.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.28f);
        MaterialUiKit.Text(open ? "▲" : "▼", new Vector2(control.Right - 16f, control.Center.Y),
            new Vector2(1f, 0.5f), MaterialTextRole.Label, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.20f);
    }

    private static void RenderOptionSlider(OptionSnapshot option, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect track = SliderControlRect(rect);
        float amount = option.Options.Count <= 1 ? 0f : option.Index / (float)(option.Options.Count - 1);
        RenderSliderTrack(track, amount, palette, alpha, enabled);
        string value = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(value, 390f, 0.27f, UiFontWeight.Bold),
            new Vector2(track.Center.X, track.Y - 12f), new Vector2(0.5f, 1f),
            MaterialTextRole.Label, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
    }

    private static void RenderIntSlider(IntSliderSnapshot slider, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect track = SliderControlRect(rect);
        float amount = slider.Maximum == slider.Minimum
            ? 0f
            : (slider.Value - slider.Minimum) / (float)(slider.Maximum - slider.Minimum);
        RenderSliderTrack(track, amount, palette, alpha, enabled);
        MaterialUiKit.Text(slider.Value.ToString(), new Vector2(track.Center.X, track.Y - 12f),
            new Vector2(0.5f, 1f), MaterialTextRole.Label, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.27f);
    }

    private static void RenderSliderTrack(MaterialRect track, float amount, MaterialPalette palette,
        float alpha, bool enabled) {
        amount = Math.Clamp(amount, 0f, 1f);
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            palette.Outline * (0.34f * alpha));
        float fill = Math.Max(track.Height, track.Width * amount);
        MaterialUi.RoundedRect(track.X, track.Y, fill, track.Height, track.Height / 2f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialUi.Circle(new Vector2(track.X + track.Width * amount, track.Center.Y), 10f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
    }

    private static void RenderActionControl(MaterialRect control, string value, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Primary * (0.26f * alpha));
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(value, control.Width - 48f, 0.27f, UiFontWeight.Bold),
            new Vector2(control.X + 14f, control.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.27f);
        MaterialUiKit.Icon("arrow_forward", new Vector2(control.Right - 18f, control.Center.Y), 18f,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f, alpha);
    }

    private static void RenderDescription(string text, MaterialRect rect, MaterialPalette palette, float alpha) {
        MaterialUi.RoundedRect(rect.X + 12f, rect.Y, rect.Width - 24f, rect.Height, 18f,
            palette.Primary * (0.13f * alpha));
        MaterialUiKit.Icon("info", new Vector2(rect.X + 36f, rect.Y + 25f), 19f,
            palette.Primary, alpha, filled: true);
        List<string> lines = MaterialTextUtil.WrapLines(text, rect.Width - 92f, 0.27f, 5);
        float y = rect.Y + 13f;
        foreach (string line in lines) {
            MaterialUiKit.Text(line, new Vector2(rect.X + 58f, y), Vector2.Zero,
                MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
            y += 28f;
        }
    }

    private void RenderDropdown(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        TextMenu.Item? item = dropdownItem;
        if (item is null || !TryGetOption(item, out OptionSnapshot option)) return;
        MaterialRect dropdown = DropdownRect(layout, item, option.Options.Count);
        MaterialUi.RoundedRect(dropdown.X, dropdown.Y + 5f, dropdown.Width, dropdown.Height, 18f,
            Color.Black * (0.24f * alpha));
        MaterialUi.RoundedRect(dropdown.X, dropdown.Y, dropdown.Width, dropdown.Height, 18f,
            palette.SurfaceHighest * alpha);
        MaterialUi.RoundedOutline(dropdown.X, dropdown.Y, dropdown.Width, dropdown.Height, 18f,
            1f, palette.Outline * (0.62f * alpha));

        int visibleCount = DropdownVisibleCount(option.Options.Count);
        for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
            int optionIndex = dropdownFirstVisible + visibleIndex;
            MaterialRect row = DropdownItemRect(dropdown, visibleIndex);
            bool highlighted = optionIndex == dropdownHighlight;
            bool current = optionIndex == option.Index;
            if (highlighted) {
                MaterialUi.RoundedRect(row.X, row.Y, row.Width, row.Height, 13f,
                    palette.Primary * (0.90f * alpha));
            }
            MaterialUiKit.Text(MaterialTextUtil.Ellipsize(option.Options[optionIndex], row.Width - 52f,
                    0.28f, UiFontWeight.Bold),
                new Vector2(row.X + 14f, row.Center.Y), new Vector2(0f, 0.5f),
                MaterialTextRole.Label, highlighted ? palette.OnPrimary : palette.OnSurface,
                alpha, scaleOverride: 0.28f);
            if (current) {
                MaterialUiKit.Icon("check", new Vector2(row.Right - 20f, row.Center.Y), 19f,
                    highlighted ? palette.OnPrimary : palette.Primary, alpha, filled: true);
            }
        }

        if (option.Options.Count > visibleCount) {
            float trackHeight = dropdown.Height - 16f;
            float thumbHeight = Math.Max(28f, trackHeight * visibleCount / option.Options.Count);
            float maximum = option.Options.Count - visibleCount;
            float thumbY = dropdown.Y + 8f
                + (trackHeight - thumbHeight) * dropdownFirstVisible / maximum;
            MaterialUi.RoundedRect(dropdown.Right - 5f, dropdown.Y + 8f, 3f, trackHeight, 1.5f,
                palette.Outline * (0.28f * alpha));
            MaterialUi.RoundedRect(dropdown.Right - 5f, thumbY, 3f, thumbHeight, 1.5f,
                palette.Primary * (0.84f * alpha));
        }
    }

    private void RenderFooter(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        string hint = UiText("microblocks_qol_modoptions_help",
            "Tab / PgUp / PgDn 切换分类  ·  方向键调整  ·  Enter 确认  ·  鼠标滚轮滚动  ·  Esc 返回");
        MaterialUiKit.Text(hint, new Vector2(layout.Footer.X, layout.Footer.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
    }

    private List<RowPlacement> RowPlacements(ModOptionsLayout layout) {
        List<RowPlacement> placements = [];
        float y = layout.Rows.Y - rowScroll.Offset;
        foreach (TextMenu.Item item in tabs[selectedTab].Items) {
            if (!item.Visible) continue;
            float height = RowHeight(item);
            placements.Add(new RowPlacement(item, new MaterialRect(layout.Rows.X, y, layout.Rows.Width, height)));
            y += height + RowGap;
        }
        return placements;
    }

    private float MaxRowScroll(ModOptionsLayout layout) {
        if (tabs.Count == 0) return 0f;
        float height = 0f;
        int count = 0;
        foreach (TextMenu.Item item in tabs[selectedTab].Items) {
            if (!item.Visible) continue;
            height += RowHeight(item);
            count++;
        }
        height += Math.Max(0, count - 1) * RowGap;
        return Math.Max(0f, height - layout.Rows.Height);
    }

    private float MaxTabScroll(ModOptionsLayout layout) => Math.Max(0f,
        tabs.Count * (TabHeight + TabGap) - TabGap - layout.NavigationItems.Height);

    private int TabIndexAt(Vector2 point, ModOptionsLayout layout) {
        if (!layout.NavigationItems.Contains(point)) return -1;
        for (int index = 0; index < tabs.Count; index++) {
            if (layout.Tab(index, tabScroll.Offset).Contains(point)) return index;
        }
        return -1;
    }

    private RowPlacement? RowAt(Vector2 point, ModOptionsLayout layout) {
        if (!layout.Rows.Contains(point)) return null;
        foreach (RowPlacement placement in RowPlacements(layout)) {
            if (placement.Rect.Contains(point)) return placement;
        }
        return null;
    }

    private void ForceDescriptionsVisible() {
        if (tabs.Count == 0) return;
        foreach (TextMenu.Item item in tabs[selectedTab].Items) {
            ForceDescriptionVisible(item);
            if (item is TextMenuExt.EaseInSubHeaderExt) naturalVisibility[item] = true;
        }
    }

    private static void ForceDescriptionVisible(TextMenu.Item item) {
        if (item is TextMenuExt.EaseInSubHeaderExt description) {
            description.FadeVisible = true;
            description.Visible = true;
        }
        if (item is TextMenuExt.SubMenu submenu) {
            foreach (TextMenu.Item child in submenu.Items) ForceDescriptionVisible(child);
        }
    }

    private static bool TryGetOption(TextMenu.Item item, out OptionSnapshot snapshot) {
        Type? optionType = null;
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(TextMenu.Option<>)) continue;
            optionType = type;
            break;
        }
        if (optionType is null) {
            snapshot = default!;
            return false;
        }
        FieldInfo? labelField = optionType.GetField("Label", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo? indexField = optionType.GetField("Index", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo? previousIndexField = optionType.GetField("PreviousIndex", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo? valuesField = optionType.GetField("Values", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo? changeField = optionType.GetField("OnValueChange", BindingFlags.Instance | BindingFlags.Public);
        if (labelField is null || indexField is null || valuesField is null
            || valuesField.GetValue(item) is not IEnumerable values) {
            snapshot = default!;
            return false;
        }
        List<string> labels = [];
        List<object> entries = [];
        foreach (object? entry in values) {
            if (entry is null) continue;
            object? label = entry.GetType().GetProperty("Item1")?.GetValue(entry)
                ?? entry.GetType().GetField("Item1")?.GetValue(entry);
            labels.Add(label?.ToString() ?? "");
            entries.Add(entry);
        }
        snapshot = new OptionSnapshot(
            labelField.GetValue(item)?.ToString() ?? "",
            Math.Clamp((int)(indexField.GetValue(item) ?? 0), 0, Math.Max(0, labels.Count - 1)),
            labels,
            entries,
            indexField,
            previousIndexField,
            changeField
        );
        return true;
    }

    private static bool TryGetIntSlider(TextMenu.Item item, out IntSliderSnapshot snapshot) {
        if (item is not TextMenuExt.IntSlider slider) {
            snapshot = default;
            return false;
        }
        Type type = typeof(TextMenuExt.IntSlider);
        FieldInfo? minimumField = type.GetField("min", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? maximumField = type.GetField("max", BindingFlags.Instance | BindingFlags.NonPublic);
        int minimum = (int)(minimumField?.GetValue(slider) ?? slider.Index);
        int maximum = (int)(maximumField?.GetValue(slider) ?? slider.Index);
        snapshot = new IntSliderSnapshot(slider.Label, slider.Index, minimum, maximum);
        return true;
    }

    private static bool TryGetLabel(TextMenu.Item item, out string label) {
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            FieldInfo? field = type.GetField("Label",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field?.GetValue(item) is string value && value.Length > 0) {
                label = value;
                return true;
            }
        }
        label = item.SearchLabel() ?? "";
        return label.Length > 0;
    }

    private static bool IsExpandedComposite(TextMenu.Item item) {
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            FieldInfo? field = type.GetField("Focused",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field?.FieldType == typeof(bool) && field.GetValue(item) is true) return true;
        }
        return false;
    }

    private static bool CanUseDropdown(OptionSnapshot option) =>
        option.Options.Count is >= 3 and <= DropdownOptionLimit;

    private static void SetOptionIndex(TextMenu.Item item, int targetIndex) {
        if (!TryGetOption(item, out OptionSnapshot option) || option.Options.Count == 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, option.Options.Count - 1);
        if (targetIndex == option.Index) return;
        option.PreviousIndexField?.SetValue(item, option.Index);
        option.IndexField.SetValue(item, targetIndex);
        object entry = option.Entries[targetIndex];
        object? value = entry.GetType().GetProperty("Item2")?.GetValue(entry)
            ?? entry.GetType().GetField("Item2")?.GetValue(entry);
        if (option.ChangeField?.GetValue(item) is Delegate changed) changed.DynamicInvoke(value);
        item.ValueWiggler?.Start();
        Audio.Play(targetIndex > option.Index
            ? "event:/ui/main/button_toggle_on"
            : "event:/ui/main/button_toggle_off");
    }

    private static void SetOptionFromMouse(TextMenu.Item item, OptionSnapshot option,
        MaterialRect track, float mouseX) {
        if (option.Options.Count == 0) return;
        float amount = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        SetOptionIndex(item, (int)MathF.Round(amount * (option.Options.Count - 1)));
    }

    private static void SetIntSliderFromMouse(TextMenu.Item item, IntSliderSnapshot slider,
        MaterialRect track, float mouseX) {
        if (item is not TextMenuExt.IntSlider intSlider) return;
        float amount = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        int value = (int)MathF.Round(MathHelper.Lerp(slider.Minimum, slider.Maximum, amount));
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        if (value == intSlider.Index) return;
        intSlider.PreviousIndex = intSlider.Index;
        intSlider.Index = value;
        intSlider.OnValueChange?.Invoke(value);
        intSlider.ValueWiggler?.Start();
        Audio.Play(value > slider.Value
            ? "event:/ui/main/button_toggle_on"
            : "event:/ui/main/button_toggle_off");
    }

    private MaterialRect DropdownRect(ModOptionsLayout layout, TextMenu.Item item, int optionCount) {
        RowPlacement? placement = RowPlacements(layout).FirstOrDefault(row => row.Item == item);
        MaterialRect control = placement is { Item: not null } row
            ? DropdownControlRect(row.Rect)
            : DropdownControlRect(new MaterialRect(layout.Rows.X, layout.Rows.Y, layout.Rows.Width, 78f));
        float height = DropdownVisibleCount(optionCount) * DropdownItemHeight + 12f;
        float y = control.Bottom + 6f;
        if (y + height > layout.Content.Bottom - 12f) y = control.Y - height - 6f;
        y = Math.Clamp(y, layout.Content.Y + 10f, Math.Max(layout.Content.Y + 10f,
            layout.Content.Bottom - height - 10f));
        return new MaterialRect(control.X, y, control.Width, height);
    }

    private static MaterialRect DropdownItemRect(MaterialRect dropdown, int visibleIndex) => new(
        dropdown.X + 6f,
        dropdown.Y + 6f + visibleIndex * DropdownItemHeight,
        dropdown.Width - 12f,
        DropdownItemHeight
    );

    private static int DropdownVisibleCount(int optionCount) =>
        Math.Min(DropdownMaxVisibleItems, optionCount);

    private static MaterialRect SwitchControlArea(MaterialRect rect) => new(
        rect.Right - 360f,
        rect.Y,
        336f,
        rect.Height
    );

    private static MaterialRect SwitchRect(MaterialRect rect) => new(
        rect.Right - 86f,
        rect.Center.Y - 16f,
        62f,
        32f
    );

    private static MaterialRect DropdownControlRect(MaterialRect rect) => new(
        rect.Right - 424f,
        rect.Center.Y - 24f,
        400f,
        48f
    );

    private static MaterialRect SliderControlArea(MaterialRect rect) => new(
        rect.Right - 504f,
        rect.Y,
        480f,
        rect.Height
    );

    private static MaterialRect SliderControlRect(MaterialRect rect) => new(
        rect.Right - 474f,
        rect.Center.Y + 9f,
        430f,
        8f
    );

    private static MaterialRect ActionControlRect(MaterialRect rect) => new(
        rect.Right - 304f,
        rect.Center.Y - 22f,
        280f,
        44f
    );

    private void BeginClose(CloseDestination destination) {
        if (pauseLevel is null || closeDestination != CloseDestination.None) return;
        CloseDropdown(playSound: false);
        closeDestination = destination;
        display = false;
        if (menu is not null) menu.Focused = false;
        SaveTabState();
        Audio.Play("event:/ui/main/button_back");
    }

    private IEnumerator SaveAndFinishPause() {
        yield return Everest.SaveSettings();
        Level? level = pauseLevel;
        if (level is null) yield break;
        level.AllowHudHide = oldAllowHudHide;
        CloseDestination destination = closeDestination;
        RemoveSelf();
        if (destination == CloseDestination.PauseMenu) {
            level.Pause(pauseReturnIndex, pauseMinimal, false);
        } else {
            level.Paused = false;
            Engine.FreezeTimer = 0.15f;
        }
    }

    private static void ReplacePauseMenuButton(Level level, TextMenu pauseMenu, bool minimal) {
        if (!MicroblocksQolUtilsModule.Settings.ReplaceEverestModOptions) return;
        string label = Dialog.Clean("menu_pause_modoptions");
        TextMenu.Button? original = pauseMenu.Items.OfType<TextMenu.Button>()
            .FirstOrDefault(button => button.Label == label);
        if (original is null) return;
        int index = pauseMenu.IndexOf(original);
        pauseMenu.Remove(original);
        TextMenu.Item replacement = new TextMenu.Button(label);
        replacement.Pressed(() => {
            int returnIndex = pauseMenu.IndexOf(replacement);
            pauseMenu.RemoveSelf();
            level.PauseMainMenuOpen = false;
            level.Paused = true;
            bool allowHudHide = level.AllowHudHide;
            level.AllowHudHide = false;
            level.Add(new MaterialModOptions(level, returnIndex, minimal, allowHudHide));
        });
        pauseMenu.Insert(index, replacement);
    }

    private static IEnumerator DetourGotoRoutine(GotoRoutineOrig orig, Overworld self, Oui next) {
        if (next is OuiModOptions
            && MicroblocksQolUtilsModule.Settings.ReplaceEverestModOptions
            && self.GetUI<MaterialModOptions>() is { } material) {
            next = material;
        }
        return orig(self, next);
    }

    private static bool TrySplitModuleHeader(string value, out string title, out string version) {
        const string marker = " | v.";
        int markerIndex = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0 || markerIndex + marker.Length >= value.Length) {
            title = "";
            version = "";
            return false;
        }
        title = value[..markerIndex].Trim();
        version = "v." + value[(markerIndex + marker.Length)..].Trim();
        return title.Length > 0;
    }

    private static bool IsEverestHeaderImage(TextMenu.Item item) =>
        item.GetType().Name.Contains("HeaderImage", StringComparison.Ordinal);

    private static bool CanSelect(TextMenu.Item item) => item.Visible && item.Selectable;

    private static float RowHeight(TextMenu.Item item) {
        if (item is TextMenuExt.EaseInSubHeaderExt description) {
            int lines = Math.Max(1, MaterialTextUtil.WrapLines(description.Title, 1180f, 0.27f, 5).Count);
            return 26f + lines * 28f;
        }
        if (item is TextMenu.SubHeader) return 54f;
        if (IsExpandedComposite(item))
            return Math.Max(78f, item.Height() + 18f);
        return 78f;
    }

    private static string ItemKey(TextMenu.Item item) =>
        $"mod-options.item.{RuntimeHelpers.GetHashCode(item)}";

    private static string UiText(string key, string fallback) {
        string value = Dialog.Clean(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private delegate IEnumerator GotoRoutineOrig(Overworld self, Oui next);
    private delegate IEnumerator GotoRoutineDetour(GotoRoutineOrig orig, Overworld self, Oui next);

    private sealed class ModTab(string id, string title, string version, List<TextMenu.Item> items) {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Version { get; } = version;
        public List<TextMenu.Item> Items { get; } = items;
        public int Selection { get; set; } = -1;
    }

    private sealed record OptionSnapshot(
        string Label,
        int Index,
        List<string> Options,
        List<object> Entries,
        FieldInfo IndexField,
        FieldInfo? PreviousIndexField,
        FieldInfo? ChangeField
    );

    private readonly record struct IntSliderSnapshot(
        string Label,
        int Value,
        int Minimum,
        int Maximum
    );

    private readonly record struct RowPlacement(TextMenu.Item Item, MaterialRect Rect);

    private enum CloseDestination {
        None,
        PauseMenu,
        Game
    }

    private readonly record struct ModOptionsLayout(
        MaterialRect Frame,
        MaterialRect Header,
        MaterialRect Navigation,
        MaterialRect NavigationItems,
        MaterialRect Content,
        MaterialRect ContentHeader,
        MaterialRect Rows,
        MaterialRect Footer
    ) {
        public static ModOptionsLayout Create(float transition) {
            float rise = transition * 32f;
            MaterialRect frame = new(28f, 24f + rise, 1864f, 1030f);
            MaterialRect inner = frame.Inset(38f, 28f, 38f, 26f);
            MaterialRect[] vertical = MaterialLayout.Split(
                inner,
                MaterialAxis.Vertical,
                14f,
                MaterialTrack.Fixed(72f),
                MaterialTrack.Flex(),
                MaterialTrack.Fixed(42f)
            );
            MaterialRect[] body = MaterialLayout.Split(
                vertical[1],
                MaterialAxis.Horizontal,
                24f,
                MaterialTrack.Fixed(330f),
                MaterialTrack.Flex()
            );
            MaterialRect navigationItems = new(
                body[0].X + 12f,
                body[0].Y + 58f,
                body[0].Width - 24f,
                body[0].Height - 72f
            );
            MaterialRect contentHeader = new(body[1].X + 24f, body[1].Y + 12f, body[1].Width - 48f, 58f);
            MaterialRect rows = new(
                body[1].X + 24f,
                body[1].Y + 82f,
                body[1].Width - 56f,
                body[1].Height - 100f
            );
            return new ModOptionsLayout(frame, vertical[0], body[0], navigationItems,
                body[1], contentHeader, rows, vertical[2]);
        }

        public MaterialRect Tab(int index, float scrollOffset) => new(
            NavigationItems.X,
            NavigationItems.Y + index * (TabHeight + TabGap) - scrollOffset,
            NavigationItems.Width,
            TabHeight
        );
    }
}
