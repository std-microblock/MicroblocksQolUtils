using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

public sealed class MaterialChapterSelect : Oui, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const int Columns = 4;
    private const float CardHeight = 164f;
    private const float CardHorizontalGap = 18f;
    private const float CardVerticalGap = 18f;

    private static Hook? gotoRoutineHook;
    private static bool hookFailed;
    private static bool replaceNextChapterSelect;
    private static bool materialSessionActive;

    private readonly List<ChapterEntry> allEntries = [];
    private readonly List<ChapterEntry> entries = [];
    private readonly List<LevelSetEntry> levelSets = [];
    private readonly MaterialScrollController cardScroll = new();
    private readonly MaterialScrollController levelSetScroll = new();
    private readonly MaterialScrollViewport cardViewport = new("mqol-chapter-cards");
    private readonly MaterialScrollViewport levelSetViewport = new("mqol-chapter-levelsets");
    private int selectedIndex;
    private int selectedLevelSet;
    private float ease;
    private bool display;
    private Color paletteSeed = new(126, 99, 184);
    private string searchText = "";
    private string imeText = "";
    private bool searchFocused;

    public bool SuppressNormalRender { get; set; }

    internal static MaterialChapterSelect? ActivePage {
        get {
            if (Engine.Scene is not Overworld overworld) return null;
            MaterialChapterSelect? page = overworld.Current as MaterialChapterSelect
                ?? overworld.Next as MaterialChapterSelect;
            return page is { Visible: true } ? page : null;
        }
    }

    public static void Load() {
        if (gotoRoutineHook is not null || hookFailed) return;
        try {
            On.Celeste.OuiFileSelectSlot.OnContinueSelected += FileSelectContinue;
            MethodInfo method = typeof(Overworld).GetMethod(
                "GotoRoutine",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new MissingMethodException(typeof(Overworld).FullName, "GotoRoutine");
            gotoRoutineHook = new Hook(method, (GotoRoutineDetour)DetourGotoRoutine);
        } catch (Exception exception) {
            On.Celeste.OuiFileSelectSlot.OnContinueSelected -= FileSelectContinue;
            hookFailed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialChapterSelect");
        }
    }

    public static void Unload() {
        On.Celeste.OuiFileSelectSlot.OnContinueSelected -= FileSelectContinue;
        gotoRoutineHook?.Dispose();
        gotoRoutineHook = null;
        hookFailed = false;
        replaceNextChapterSelect = false;
        materialSessionActive = false;
    }

    public override bool IsStart(Overworld overworld, Overworld.StartMode start) => false;

    public override IEnumerator Enter(Oui from) {
        Visible = true;
        display = true;
        ease = 0f;
        Overworld.ShowInputUI = false;
        Overworld.Mountain.AllowUserRotation = false;
        Overworld.Maddy.Hide();
        RebuildEntries();
        paletteSeed = entries.Count == 0
            ? new Color(126, 99, 184)
            : entries[Math.Clamp(selectedIndex, 0, entries.Count - 1)].Area.TitleBaseColor;
        materialSessionActive = true;
        Audio.Play("event:/ui/world_map/icon/roll_right");
        yield return null;
    }

    public override IEnumerator Leave(Oui next) {
        display = false;
        SetSearchFocused(false);
        float duration = 0.16f;
        for (float timer = 0f; timer < duration; timer += Engine.DeltaTime) yield return null;
        Visible = false;
        Overworld.ShowInputUI = true;
        Overworld.Mountain.AllowUserRotation = true;
    }

    public override void Update() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        cardScroll.Update(MaxCardScroll(layout));
        levelSetScroll.Update(MaxLevelSetScroll(layout));
        ease = Calc.Approach(ease, display ? 1f : 0f, Engine.DeltaTime * 7f);
        if (Focused && display) UpdateInput();
        base.Update();
    }

    public override void Removed(Scene scene) {
        SetSearchFocused(false);
        cardViewport.Dispose();
        levelSetViewport.Dispose();
        base.Removed(scene);
    }

    public override void Render() {
        if (SuppressNormalRender) return;
        Matrix renderMatrix = HiresRenderer.DrawToBuffer ? Matrix.Identity : Engine.ScreenMatrix;
        RenderMaterialContent(acrylicActive: false, renderMatrix);
    }

    public void RenderMaterialContent(bool acrylicActive, Matrix renderMatrix) {
        if (!Visible || ease <= 0f) return;
        ChapterEntry? selected = entries.Count == 0 ? null : entries[Math.Clamp(selectedIndex, 0, entries.Count - 1)];
        MaterialPalette palette = MaterialPalette.FromSeed(paletteSeed);
        float eased = Ease.CubeOut(ease);
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * eased);

        float rise = (1f - eased) * 34f;
        ChapterLayout layout = ChapterLayout.Create(rise);
        MaterialUiKit.Surface(layout.Frame, 42f,
            palette with { SurfaceHigh = palette.Surface * (acrylicActive ? 0.78f : 0.94f) }, eased);

        MaterialUiKit.Text(UiText("microblocks_qol_chapter_title", "选择章节"),
            new Vector2(layout.Header.X, layout.Header.Y), Vector2.Zero, MaterialTextRole.Display,
            palette.OnSurface, eased);
        string subtitle = CollabUtils2Bridge.Available
            ? UiText("microblocks_qol_chapter_subtitle_collab", "Material You  ·  键盘 / 鼠标  ·  CollabUtils2")
            : UiText("microblocks_qol_chapter_subtitle", "Material You  ·  键盘 / 鼠标");
        MaterialUiKit.Text(subtitle, new Vector2(layout.Header.X + 2f, layout.Header.Y + 55f), Vector2.Zero,
            MaterialTextRole.Body, palette.OnSurfaceVariant, eased);
        RenderSearchBox(palette, layout, eased);

        RenderLevelSets(palette, layout, eased, renderMatrix);
        RenderCards(palette, layout, eased, renderMatrix);
        RenderFooter(palette, selected, layout, eased);
        RenderMouseCursor(palette, eased);
    }

    private void UpdateInput() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        Vector2 mouse = MInput.Mouse.Position;
        if (MInput.Mouse.PressedLeftButton && layout.Search.Contains(mouse)) {
            SetSearchFocused(true);
            Audio.Play("event:/ui/main/button_select");
            return;
        }
        if (MInput.Keyboard.Check(Keys.LeftControl, Keys.RightControl)
            && MInput.Keyboard.Pressed(Keys.F)) {
            SetSearchFocused(true);
            return;
        }
        if (searchFocused) {
            if (Input.MenuCancel.Pressed || MaterialTextInputFocus.Pressed(Keys.Escape)) {
                SetSearchFocused(false);
            } else if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
                SetSearchFocused(false);
            } else if (MInput.Mouse.PressedLeftButton && !layout.Search.Contains(mouse)) {
                SetSearchFocused(false);
            }
            return;
        }

        if (Input.MenuCancel.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            Audio.Play("event:/ui/main/button_back");
            materialSessionActive = false;
            Overworld.Goto<OuiFileSelect>();
            return;
        }

        if (MInput.Keyboard.Pressed(Keys.Tab)) {
            int direction = MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift) ? -1 : 1;
            SelectLevelSet(selectedLevelSet + direction);
        }

        if (entries.Count > 0) {
            if (Input.MenuLeft.Pressed) MoveSelection(-1);
            else if (Input.MenuRight.Pressed) MoveSelection(1);
            else if (Input.MenuUp.Pressed) MoveSelection(-Columns);
            else if (Input.MenuDown.Pressed) MoveSelection(Columns);
            else if (Input.MenuConfirm.Pressed || MInput.Keyboard.Pressed(Keys.Enter)) ActivateSelected();
        }

        bool inSidebar = layout.Sidebar.Contains(mouse);
        if (MInput.Mouse.WheelDelta != 0) {
            if (inSidebar) {
                levelSetScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * 150f,
                    MaxLevelSetScroll(layout));
            } else {
                cardScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * 220f,
                    MaxCardScroll(layout));
            }
        }

        if (MInput.Mouse.WasMoved || MInput.Mouse.PressedLeftButton) {
            int sidebar = SidebarIndexAt(mouse, layout);
            if (sidebar >= 0) {
                if (MInput.Mouse.PressedLeftButton) SelectLevelSet(sidebar);
                return;
            }
            int card = CardIndexAt(mouse, layout);
            if (card >= 0) {
                if (selectedIndex != card) {
                    selectedIndex = card;
                    Audio.Play("event:/ui/world_map/icon/roll_right");
                }
                if (MInput.Mouse.PressedLeftButton) ActivateSelected();
            }
        }
    }

    private void RebuildEntries() {
        allEntries.Clear();
        levelSets.Clear();
        levelSets.Add(new LevelSetEntry("", UiText("microblocks_qol_chapter_all_maps", "全部地图")));
        bool showCollabMaps = MicroblocksQolUtilsModule.Settings.ChapterSelectShowCollabMaps;
        SaveData? save = SaveData.Instance;
        foreach (AreaData area in AreaData.Areas) {
            if (area.Mode is null || area.Mode.Length == 0 || area.Mode[0] is null) continue;
            string sid = area.SID ?? area.ID.ToString();
            bool collabMap = CollabUtils2Bridge.IsCollabMap(sid);
            bool collabGym = CollabUtils2Bridge.IsCollabGym(sid);
            bool collabLobby = CollabUtils2Bridge.IsCollabLobby(sid);
            if (!showCollabMaps && (collabMap || collabGym)) continue;
            if (save is not null
                && area.LevelSet == "Celeste"
                && area.ID > save.UnlockedAreas
                && !(save.AssistMode && area.ID <= save.MaxAssistArea)) continue;

            string levelSet = area.LevelSet ?? "Celeste";
            string title = CleanName(area.Name, sid);
            string levelSetTitle = levelSet == "Celeste" ? "Celeste" : Dialog.CleanLevelSet(levelSet);
            // GetCollabNameForSID also recognizes special collab maps such as the prologue,
            // which CollabUtils2 intentionally excludes from IsCollabLobby. Group by the
            // collab root for every SID so the lobby, its maps, gyms, and prologue stay in
            // one tab.
            string? collabName = CollabUtils2Bridge.GetCollabName(sid);
            string groupId = string.IsNullOrWhiteSpace(collabName) ? levelSet : collabName;
            string groupTitle = string.IsNullOrWhiteSpace(collabName)
                ? levelSetTitle
                : CleanGroupTitle(collabName, levelSetTitle);
            string badge = collabLobby ? "LOBBY"
                : collabGym ? "GYM"
                : collabMap ? "COLLAB"
                : levelSet == "Celeste" ? UiText("microblocks_qol_chapter_official", "官方") : "MOD";
            allEntries.Add(new ChapterEntry(area, sid, levelSet, groupId, title, levelSetTitle, badge));
            if (levelSets.All(item => !string.Equals(item.Id, groupId, StringComparison.Ordinal)))
                levelSets.Add(new LevelSetEntry(groupId, groupTitle));
        }

        string currentSid = save?.LastArea_Safe.SID ?? "";
        string currentGroup = allEntries.FirstOrDefault(entry => entry.Sid == currentSid)?.GroupId ?? "";
        selectedLevelSet = Math.Max(0, levelSets.FindIndex(item => item.Id == currentGroup));
        FilterEntries(keepArea: true);
    }

    private void FilterEntries(bool keepArea) {
        string? previousSid = keepArea && entries.Count > 0 && selectedIndex < entries.Count
            ? entries[selectedIndex].Sid
            : SaveData.Instance?.LastArea_Safe.SID;
        entries.Clear();
        string group = levelSets.Count == 0 ? "" : levelSets[selectedLevelSet].Id;
        IEnumerable<ChapterEntry> filtered = group.Length == 0
            ? allEntries
            : allEntries.Where(entry => entry.GroupId == group);
        if (!string.IsNullOrWhiteSpace(searchText)) {
            string search = searchText.Trim();
            filtered = filtered.Where(entry =>
                entry.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.LevelSetTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Sid.Contains(search, StringComparison.OrdinalIgnoreCase)
            );
        }
        entries.AddRange(filtered);
        selectedIndex = Math.Max(0, entries.FindIndex(entry => entry.Sid == previousSid));
        if (entries.Count == 0) selectedIndex = 0;
        cardScroll.Reset();
        EnsureSelectionVisible();
        EnsureLevelSetVisible();
    }

    private void SelectLevelSet(int index) {
        if (levelSets.Count == 0) return;
        index = (index % levelSets.Count + levelSets.Count) % levelSets.Count;
        if (index == selectedLevelSet) return;
        selectedLevelSet = index;
        Audio.Play("event:/ui/world_map/icon/roll_right");
        FilterEntries(keepArea: false);
    }

    private void MoveSelection(int delta) {
        if (entries.Count == 0) return;
        int next = Math.Clamp(selectedIndex + delta, 0, entries.Count - 1);
        if (next == selectedIndex) return;
        selectedIndex = next;
        Audio.Play(delta < 0 ? "event:/ui/world_map/icon/roll_left" : "event:/ui/world_map/icon/roll_right");
        EnsureSelectionVisible();
    }

    private void EnsureSelectionVisible() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        int row = selectedIndex / Columns;
        float top = row * (CardHeight + CardVerticalGap);
        cardScroll.EnsureVisible(top, top + CardHeight, layout.Cards.Height, MaxCardScroll(layout));
    }

    private void EnsureLevelSetVisible() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        float top = selectedLevelSet * (ChapterLayout.SidebarItemHeight + ChapterLayout.SidebarItemGap);
        levelSetScroll.EnsureVisible(top, top + ChapterLayout.SidebarItemHeight,
            layout.SidebarItems.Height, MaxLevelSetScroll(layout));
    }

    private void ActivateSelected() {
        SaveData? save = SaveData.Instance;
        if (entries.Count == 0 || save is null) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        ChapterEntry entry = entries[selectedIndex];
        Audio.Play("event:/ui/world_map/icon/select");
        save.LastArea_Safe = entry.Area.ToKey();
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils/ChapterSelect",
            $"Selected {entry.Sid} area={entry.Area.ID} levelSet={entry.LevelSet}");
        // Match the vanilla chapter select and leave saving to the normal game flow.
        // Saving here invokes SaveData.AfterInitialize; CollabUtils2 uses that hook to
        // replace a collab map's LastArea with its lobby, so OuiChapterPanel would open
        // (and then start) the lobby instead of the selected map.
        Overworld.Goto<OuiChapterPanel>();
    }

    private void RenderLevelSets(
        MaterialPalette palette,
        ChapterLayout layout,
        float alpha,
        Matrix renderMatrix
    ) {
        MaterialUiKit.Surface(layout.Sidebar,
            28f, palette with { SurfaceHigh = palette.SurfaceHigh * 0.82f }, alpha);
        MaterialUiKit.Text(UiText("microblocks_qol_chapter_level_sets", "地图集"),
            new Vector2(layout.Sidebar.X + MaterialSpacing.Lg, layout.Sidebar.Y + MaterialSpacing.Md), Vector2.Zero,
            MaterialTextRole.Section, palette.OnSurface, alpha);
        levelSetViewport.Render(layout.SidebarItems, renderMatrix, () => {
            for (int index = 0; index < levelSets.Count; index++) {
                MaterialRect item = layout.SidebarItem(index, levelSetScroll.Offset);
                if (item.Bottom < layout.SidebarItems.Y || item.Y > layout.SidebarItems.Bottom) continue;
                bool selected = index == selectedLevelSet;
                MaterialUiKit.NavigationPill(item, palette, selected, alpha);
                SystemTtfFont.Draw(
                    Trim(levelSets[index].Title, 20),
                    new Vector2(item.X + 20f, item.Y + 10f),
                    Vector2.Zero,
                    0.37f,
                    (selected ? palette.OnPrimary : palette.OnSurfaceVariant) * alpha,
                    weight: selected ? UiFontWeight.Bold : UiFontWeight.Regular
                );
            }
        });
    }

    private void RenderCards(
        MaterialPalette palette,
        ChapterLayout layout,
        float alpha,
        Matrix renderMatrix
    ) {
        cardViewport.Render(layout.Cards, renderMatrix, () => {
            for (int index = 0; index < entries.Count; index++) {
                MaterialRect card = layout.Card(index, cardScroll.Offset);
                if (card.Bottom < layout.Cards.Y || card.Y > layout.Cards.Bottom) continue;
                bool selected = index == selectedIndex;
                Color surface = selected ? palette.SurfaceHighest : palette.SurfaceHigh;
                MaterialUiKit.Card(card,
                    palette with { SurfaceHigh = surface * (selected ? 0.98f : 0.85f) }, selected, alpha);
                RenderCard(entries[index], card, selected, palette, alpha);
            }
            if (entries.Count == 0) {
                SystemTtfFont.Draw(UiText("microblocks_qol_chapter_empty", "这个地图集中没有可选章节"),
                    layout.Cards.Center, new Vector2(0.5f), 0.56f, palette.OnSurfaceVariant * alpha);
            }
        });
    }

    private static void RenderCard(
        ChapterEntry entry,
        MaterialRect card,
        bool selected,
        MaterialPalette palette,
        float alpha
    ) {
        float x = card.X;
        float y = card.Y;
        MaterialRect content = card.Inset(20f, 18f);
        float iconSize = 48f;
        if (!string.IsNullOrWhiteSpace(entry.Area.Icon) && GFX.Gui.Has(entry.Area.Icon)) {
            MTexture icon = GFX.Gui[entry.Area.Icon];
            float scale = Math.Min(1f, iconSize / Math.Max(icon.Width, icon.Height));
            icon.DrawCentered(new Vector2(content.X + iconSize / 2f, content.Y + iconSize / 2f),
                Color.White * alpha, scale);
        } else {
            MaterialUi.RoundedRect(content.X, content.Y, iconSize, iconSize, 17f,
                palette.Primary * 0.42f * alpha);
        }
        float textX = content.X + iconSize + 14f;
        SystemTtfFont.Draw(Trim(entry.Title, 17), new Vector2(textX, content.Y - 3f),
            Vector2.Zero, 0.40f, palette.OnSurface * alpha, weight: UiFontWeight.Bold);
        SystemTtfFont.Draw(Trim(entry.LevelSetTitle, 22), new Vector2(textX, content.Y + 34f),
            Vector2.Zero, 0.27f, palette.OnSurfaceVariant * alpha);

        AreaStats? stats = SaveData.Instance?.GetAreaStatsFor(entry.Area.ToKey());
        AreaModeStats? mode = stats?.Modes is { Length: > 0 } ? stats.Modes[0] : null;
        float statsY = card.Bottom - 35f;
        string state = mode is null
            ? UiText("microblocks_qol_chapter_never_entered", "尚未游玩")
            : UiText(mode.Completed ? "microblocks_qol_chapter_cleared" : "microblocks_qol_chapter_uncleared",
                mode.Completed ? "已完成" : "进行中");
        SystemTtfFont.Draw(state, new Vector2(content.X, statsY - 8f), Vector2.Zero, 0.27f,
            mode?.Completed == true ? palette.Primary * alpha : palette.OnSurfaceVariant * alpha,
            weight: mode?.Completed == true ? UiFontWeight.Bold : UiFontWeight.Regular);
        if (mode is not null) {
            DrawStat("collectables/strawberry", mode.TotalStrawberries,
                new Vector2(content.X + 92f, statsY), palette.OnSurfaceVariant, alpha);
            DrawStat("collectables/skullBlue", mode.Deaths,
                new Vector2(content.X + 160f, statsY), palette.OnSurfaceVariant, alpha);
        }

        MaterialUiKit.Chip(entry.Badge,
            new Vector2(card.Right - 16f, card.Bottom - 44f), palette, selected, alpha);
    }

    private static void DrawStat(string texture, int value, Vector2 position, Color color, float alpha) {
        MTexture icon = GFX.Gui[texture];
        float scale = 20f / Math.Max(icon.Width, icon.Height);
        icon.DrawCentered(position, Color.White * alpha, scale);
        SystemTtfFont.Draw(value.ToString(), position + new Vector2(16f, -8f), Vector2.Zero, 0.27f,
            color * alpha);
    }

    private static void RenderFooter(
        MaterialPalette palette,
        ChapterEntry? selected,
        ChapterLayout layout,
        float alpha
    ) {
        string detail = selected is null
            ? UiText("microblocks_qol_chapter_no_available", "没有可用章节")
            : selected.Sid;
        SystemTtfFont.Draw(Trim(detail, 72), new Vector2(layout.Footer.X, layout.Footer.Y), Vector2.Zero, 0.31f,
            palette.OnSurfaceVariant * alpha);
        SystemTtfFont.Draw(UiText("microblocks_qol_chapter_controls",
                "Enter / 左键：打开   Esc：返回   Tab：切换地图集   滚轮：滚动"),
            new Vector2(layout.Footer.Right, layout.Footer.Y), new Vector2(1f, 0f), 0.31f,
            palette.OnSurfaceVariant * alpha);
    }

    private static void RenderMouseCursor(MaterialPalette palette, float alpha) {
        Vector2 mouse = MInput.Mouse.Position;
        MaterialUiKit.Cursor(mouse, palette, alpha);
    }

    private int SidebarIndexAt(Vector2 mouse, ChapterLayout layout) {
        if (!layout.SidebarItems.Contains(mouse)) return -1;
        for (int index = 0; index < levelSets.Count; index++) {
            if (layout.SidebarItem(index, levelSetScroll.Offset).Contains(mouse)) return index;
        }
        return -1;
    }

    private int CardIndexAt(Vector2 mouse, ChapterLayout layout) {
        if (!layout.Cards.Contains(mouse)) return -1;
        for (int index = 0; index < entries.Count; index++) {
            if (layout.Card(index, cardScroll.Offset).Contains(mouse)) return index;
        }
        return -1;
    }

    private float MaxCardScroll(ChapterLayout layout) {
        int rows = (entries.Count + Columns - 1) / Columns;
        float contentHeight = rows == 0
            ? 0f
            : rows * CardHeight + (rows - 1) * CardVerticalGap;
        return Math.Max(0f, contentHeight - layout.Cards.Height);
    }

    private float MaxLevelSetScroll(ChapterLayout layout) {
        float contentHeight = levelSets.Count == 0
            ? 0f
            : levelSets.Count * ChapterLayout.SidebarItemHeight
                + (levelSets.Count - 1) * ChapterLayout.SidebarItemGap;
        return Math.Max(0f, contentHeight - layout.SidebarItems.Height);
    }

    private void RenderSearchBox(MaterialPalette palette, ChapterLayout layout, float alpha) {
        Color fill = searchFocused ? palette.SurfaceHighest : palette.SurfaceHigh;
        MaterialUi.RoundedRect(layout.Search.X, layout.Search.Y, layout.Search.Width, layout.Search.Height,
            layout.Search.Height / 2f, fill * alpha);
        MaterialUi.RoundedOutline(layout.Search.X, layout.Search.Y, layout.Search.Width, layout.Search.Height,
            layout.Search.Height / 2f, searchFocused ? 2f : 1f,
            (searchFocused ? palette.Primary : palette.Outline) * alpha);
        string shown = searchText + (searchFocused ? imeText : "");
        string text = shown.Length == 0 ? "搜索地图、地图集或 SID…" : shown;
        Color color = shown.Length == 0 ? palette.OnSurfaceVariant * 0.68f : palette.OnSurface;
        Vector2 textPosition = new(layout.Search.X + 24f, layout.Search.Y + 12f);
        SystemTtfFont.Draw(Trim(text, 46), textPosition, Vector2.Zero, 0.36f, color * alpha);
        if (searchFocused && Scene.BetweenInterval(0.5f)) {
            float caretX = textPosition.X + SystemTtfFont.Measure(Trim(shown, 46), 0.36f).X + 2f;
            MaterialUi.Line(new Vector2(caretX, layout.Search.Y + 11f),
                new Vector2(caretX, layout.Search.Bottom - 11f), 2f, palette.Primary * alpha);
        }
        float xScale = Engine.ViewWidth / ScreenWidth;
        float yScale = Engine.ViewHeight / ScreenHeight;
        TextInputEXT.SetInputRectangle(new Rectangle(
            (int)(layout.Search.X * xScale),
            (int)(layout.Search.Y * yScale),
            Math.Max(1, (int)(layout.Search.Width * xScale)),
            Math.Max(1, (int)(layout.Search.Height * yScale))
        ));
    }

    private void OnTextInput(char character) {
        if (!searchFocused) return;
        if (character == '\b') {
            if (searchText.Length > 0) searchText = searchText[..^1];
        } else if (!char.IsControl(character) && searchText.Length < 80) {
            searchText += character;
        } else {
            return;
        }
        imeText = "";
        FilterEntries(keepArea: true);
    }

    private void SetSearchFocused(bool focused) {
        if (searchFocused == focused) return;
        searchFocused = focused;
        if (focused) {
            TextInput.OnInput += OnTextInput;
            TextInputEXT.TextEditing += OnTextEditing;
            MaterialTextInputFocus.Focus(this);
        } else {
            TextInput.OnInput -= OnTextInput;
            TextInputEXT.TextEditing -= OnTextEditing;
            MaterialTextInputFocus.Blur(this);
            imeText = "";
        }
    }

    private void OnTextEditing(string? text, int start, int length) {
        _ = start;
        _ = length;
        if (searchFocused) imeText = text ?? "";
    }

    private static string CleanGroupTitle(string collabName, string fallback) {
        string localized = Dialog.CleanLevelSet(collabName);
        if (!string.IsNullOrWhiteSpace(localized)
            && !string.Equals(localized, collabName, StringComparison.Ordinal)) return localized;
        string value = System.Text.RegularExpressions.Regex.Replace(collabName, "(?<=[a-z])(?=[A-Z])", " ");
        value = System.Text.RegularExpressions.Regex.Replace(value, "(?<=[0-9])(?=[A-Z][a-z])", " ");
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string CleanName(string dialogKey, string fallback) {
        string value = Dialog.Clean(dialogKey ?? "");
        return string.IsNullOrWhiteSpace(value) || value == dialogKey ? fallback : value;
    }

    private static string UiText(string key, string fallback) {
        string value = Dialog.Clean(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private static string Trim(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : value[..Math.Max(1, maxCharacters - 1)] + "…";

    private static IEnumerator DetourGotoRoutine(GotoRoutineOrig orig, Overworld self, Oui next) {
        if (next is OuiChapterSelect vanilla
            && MicroblocksQolUtilsModule.Settings.ReplaceChapterSelect
            && (replaceNextChapterSelect || materialSessionActive && self.Current is OuiChapterPanel)
            && !IsAutoAdvancing(vanilla)
            && self.GetUI<MaterialChapterSelect>() is { } material) {
            next = material;
        }
        return orig(self, next);
    }

    private static void FileSelectContinue(
        On.Celeste.OuiFileSelectSlot.orig_OnContinueSelected orig,
        OuiFileSelectSlot self
    ) {
        replaceNextChapterSelect = true;
        try {
            orig(self);
        } finally {
            replaceNextChapterSelect = false;
        }
    }

    private static bool IsAutoAdvancing(OuiChapterSelect select) {
        try {
            return (bool?)typeof(OuiChapterSelect)
                .GetField("autoAdvancing", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(select) == true;
        } catch {
            return false;
        }
    }

    private delegate IEnumerator GotoRoutineOrig(Overworld self, Oui next);
    private delegate IEnumerator GotoRoutineDetour(GotoRoutineOrig orig, Overworld self, Oui next);

    private readonly record struct ChapterLayout(
        MaterialRect Frame,
        MaterialRect Header,
        MaterialRect Search,
        MaterialRect Sidebar,
        MaterialRect SidebarItems,
        MaterialRect Cards,
        MaterialRect Footer
    ) {
        private const float SidebarHeaderHeight = 64f;
        public const float SidebarItemHeight = 50f;
        public const float SidebarItemGap = MaterialSpacing.Xs;

        public static ChapterLayout Create(float rise) {
            MaterialRect frame = new(28f, 24f + rise, 1864f, 1030f);
            MaterialRect inner = frame.Inset(MaterialSpacing.Xxl, 30f, MaterialSpacing.Xxl, 28f);
            MaterialRect[] rows = MaterialLayout.Split(
                inner,
                MaterialAxis.Vertical,
                18f,
                MaterialTrack.Fixed(95f),
                MaterialTrack.Flex(),
                MaterialTrack.Fixed(44f)
            );
            MaterialRect[] body = MaterialLayout.Split(
                rows[1],
                MaterialAxis.Horizontal,
                28f,
                MaterialTrack.Fixed(296f),
                MaterialTrack.Flex()
            );
            MaterialRect search = new(rows[0].Right - 620f, rows[0].Y + 8f, 620f, 54f);
            MaterialRect sidebarItems = new(
                body[0].X + MaterialSpacing.Sm,
                body[0].Y + SidebarHeaderHeight,
                body[0].Width - MaterialSpacing.Lg,
                body[0].Height - SidebarHeaderHeight - MaterialSpacing.Md
            );
            return new ChapterLayout(frame, rows[0], search, body[0], sidebarItems, body[1], rows[2]);
        }

        public MaterialRect SidebarItem(int index, float scrollOffset) => new(
            SidebarItems.X,
            SidebarItems.Y + index * (SidebarItemHeight + SidebarItemGap) - scrollOffset,
            SidebarItems.Width,
            SidebarItemHeight
        );

        public MaterialRect Card(int index, float scrollOffset) {
            float width = (Cards.Width - CardHorizontalGap * (Columns - 1)) / Columns;
            int column = index % Columns;
            int row = index / Columns;
            return new MaterialRect(
                Cards.X + column * (width + CardHorizontalGap),
                Cards.Y + row * (CardHeight + CardVerticalGap) - scrollOffset,
                width,
                CardHeight
            );
        }
    }

    private sealed record ChapterEntry(
        AreaData Area,
        string Sid,
        string LevelSet,
        string GroupId,
        string Title,
        string LevelSetTitle,
        string Badge
    );

    private sealed record LevelSetEntry(string Id, string Title);
}
