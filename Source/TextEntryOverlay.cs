using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Full-screen in-game text entry prompt with a QWERTY keyboard, number and symbol pages.
/// Gamepad navigation keeps the directional + confirm flow, while mouse and touch type
/// directly on the tapped key (long-press on the space bar repeats spaces).
/// </summary>
internal sealed class TextEntryOverlay : Entity {
    private const float PanelX = 260f;
    private const float PanelY = 120f;
    private const float PanelWidth = 1400f;
    private const float PanelHeight = 720f;
    private const float PanelInset = 40f;
    private const float FieldOffsetY = 84f;
    private const float FieldHeight = 88f;
    private const float KeyboardOffsetY = 236f;
    private const float KeyGap = 12f;
    private const float KeyHeight = 104f;
    private const float ActionHeight = 68f;
    private const float ActionBottomMargin = 30f;
    private const int Columns = 10;
    private const float SpaceRepeatDelay = 0.36f;
    private const float SpaceRepeatInterval = 0.08f;

    private static readonly string[] ActionLabels = ["取消", "空格", "退格", "接受"];
    private static readonly float[] ActionWeights = [1.4f, 3.4f, 1.4f, 1.5f];

    private enum Page {
        Letters,
        Numbers,
        Symbols
    }

    private enum KeyAction {
        Character,
        Shift,
        PageLetters,
        PageNumbers,
        PageSymbols,
        Space,
        Backspace,
        Accept,
        Cancel
    }

    private readonly record struct Key(string Label, char Character, KeyAction Action, string Hint = "");

    private readonly string title;
    private readonly int maxLength;
    private readonly int minLength;
    private readonly Func<string, bool> commit;
    private readonly Action closed;
    private readonly MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));

    private TextEditBuffer edit;
    private Page page = Page.Letters;
    private bool fieldFocused;
    private bool upper;
    private Key[][] rows = [];
    private int cursorRow;
    private int cursorColumn;
    private int actionIndex;
    private bool inActions;
    private int hoverRow = -1;
    private int hoverColumn = -1;
    private int hoverAction = -1;
    private float spaceHold;
    private float spaceRepeat;
    private float error;
    private bool opened;

    public TextEntryOverlay(
        string title,
        string initial,
        int maxLength,
        int minLength,
        Func<string, bool> commit,
        Action closed
    ) {
        this.title = title;
        this.maxLength = Math.Max(1, maxLength);
        this.minLength = Math.Clamp(minLength, 0, this.maxLength);
        this.commit = commit;
        this.closed = closed;
        // Keep an existing longer value intact; the limit only applies to new characters.
        edit = new TextEditBuffer(initial ?? "", this.maxLength);
        RebuildRows();
        Tag = Tags.HUD | Tags.PauseUpdate;
        Depth = -3_000_000;
    }

    public string Buffer => edit.Text;

    public override void Added(Scene scene) {
        base.Added(scene);
        opened = true;
        TextInput.OnInput += OnTextInput;
        MaterialTextInputFocus.Focus(this);
    }

    public override void Removed(Scene scene) {
        TextInput.OnInput -= OnTextInput;
        MaterialTextInputFocus.Blur(this);
        base.Removed(scene);
    }

    private void OnTextInput(char character) {
        if (character == '\b') {
            Backspace();
            return;
        }
        if (char.IsControl(character)) return;
        Append(character);
    }

    public override void Update() {
        base.Update();
        error = Calc.Approach(error, 0f, Engine.RawDeltaTime * 4f);
        if (!opened) return;

        if (Input.MenuCancel.Pressed || MaterialTextInputFocus.Pressed(Keys.Escape)) {
            Cancel();
            return;
        }
        // A physical Enter accepts the text; the gamepad confirm types the focused key.
        if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
            Accept();
            return;
        }
        UpdateKeyboardCaret();
        UpdateHover();
        if (Input.MenuConfirm.Pressed) {
            if (fieldFocused) {
                Accept();
            } else if (inActions) {
                RunAction(actionIndex);
            } else {
                PressKey(rows[cursorRow][cursorColumn]);
            }
            return;
        }
        UpdateNavigation();
        UpdateLongPress();
    }

    private void UpdateKeyboardCaret() {
        if (MaterialTextInputFocus.Pressed(Keys.Left)) edit.MoveLeft();
        else if (MaterialTextInputFocus.Pressed(Keys.Right)) edit.MoveRight();
        else if (MaterialTextInputFocus.Pressed(Keys.Home)) edit.MoveHome();
        else if (MaterialTextInputFocus.Pressed(Keys.End)) edit.MoveEnd();
        else if (MaterialTextInputFocus.Pressed(Keys.Delete)) edit.Delete();
    }

    private void UpdateHover() {
        Vector2 mouse = MaterialTouch.Position;
        MaterialRect field = FieldRect(new MaterialRect(PanelX, PanelY, PanelWidth, PanelHeight));
        hoverRow = RowAt(mouse);
        hoverColumn = hoverRow >= 0 ? ColumnAt(hoverRow, mouse) : -1;
        hoverAction = ActionAt(mouse);

        // Keep the gamepad focus stable while the pointer rests; it only follows the
        // pointer when the pointer actually moves (or taps).
        if (MaterialTouch.WasMoved || MaterialTouch.PressedLeftButton) {
            if (field.Contains(mouse)) {
                fieldFocused = true;
            } else if (hoverRow >= 0) {
                fieldFocused = false;
                inActions = false;
                cursorRow = hoverRow;
                cursorColumn = hoverColumn;
            } else if (hoverAction >= 0) {
                fieldFocused = false;
                inActions = true;
                actionIndex = hoverAction;
            }
        }

        if (!MaterialTouch.PressedLeftButton) return;
        if (field.Contains(mouse)) {
            SetCursorFromPointer(mouse, field);
        } else if (hoverRow >= 0) {
            PressKey(rows[hoverRow][hoverColumn]);
        } else if (hoverAction >= 0) {
            RunAction(hoverAction);
        }
    }

    private void SetCursorFromPointer(Vector2 mouse, MaterialRect field) {
        float textWidth = field.Width - 100f;
        TextEditBuffer.TextWindow window = edit.Window(textWidth, 0.36f, UiFontWeight.Bold);
        edit.SetCursorFromX(mouse.X - (field.X + 24f), window, 0.36f, UiFontWeight.Bold);
    }

    private void UpdateNavigation() {
        bool left = Input.MenuLeft.Pressed;
        bool right = Input.MenuRight.Pressed;
        bool up = Input.MenuUp.Pressed;
        bool down = Input.MenuDown.Pressed;
        if (fieldFocused) {
            if (left) edit.MoveLeft();
            else if (right) edit.MoveRight();
            else if (up || down) fieldFocused = false;
            return;
        }
        if (left) MoveHorizontal(-1);
        else if (right) MoveHorizontal(1);
        else if (up) MoveVertical(-1);
        else if (down) MoveVertical(1);
    }

    private void MoveHorizontal(int direction) {
        if (inActions) {
            actionIndex = Wrap(actionIndex + direction, ActionLabels.Length);
        } else {
            cursorColumn = Wrap(cursorColumn + direction, rows[cursorRow].Length);
        }
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void MoveVertical(int direction) {
        if (direction > 0) {
            if (inActions) return;
            if (cursorRow + 1 >= rows.Length) {
                inActions = true;
                actionIndex = Math.Clamp(cursorColumn * 4 / Math.Max(1, rows[^1].Length), 0,
                    ActionLabels.Length - 1);
            } else {
                cursorRow++;
                cursorColumn = Math.Min(cursorColumn, rows[cursorRow].Length - 1);
            }
        } else {
            if (inActions) {
                inActions = false;
                cursorRow = rows.Length - 1;
                cursorColumn = Math.Clamp(actionIndex, 0, rows[cursorRow].Length - 1);
            } else if (cursorRow > 0) {
                cursorRow--;
                cursorColumn = Math.Min(cursorColumn, rows[cursorRow].Length - 1);
            } else {
                fieldFocused = true;
            }
        }
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void UpdateLongPress() {
        bool holdingSpace = (hoverAction == 1 && MaterialTouch.CheckLeftButton)
            || (inActions && actionIndex == 1 && Input.MenuConfirm.Check);
        if (!holdingSpace) {
            spaceHold = 0f;
            spaceRepeat = 0f;
            return;
        }
        spaceHold += Engine.RawDeltaTime;
        if (spaceHold < SpaceRepeatDelay) return;
        spaceRepeat += Engine.RawDeltaTime;
        while (spaceRepeat >= SpaceRepeatInterval) {
            spaceRepeat -= SpaceRepeatInterval;
            Append(' ');
        }
    }

    private void PressKey(Key key) {
        switch (key.Action) {
            case KeyAction.Character:
                Append(key.Character);
                break;
            case KeyAction.Shift:
                upper = !upper;
                RebuildRows();
                Audio.Play("event:/ui/main/button_toggle_on");
                break;
            case KeyAction.PageLetters:
            case KeyAction.PageNumbers:
            case KeyAction.PageSymbols:
                page = key.Action switch {
                    KeyAction.PageLetters => Page.Letters,
                    KeyAction.PageNumbers => Page.Numbers,
                    _ => Page.Symbols
                };
                RebuildRows();
                Audio.Play("event:/ui/main/button_toggle_on");
                break;
            case KeyAction.Space:
                Append(' ');
                break;
            case KeyAction.Backspace:
                Backspace();
                break;
            case KeyAction.Accept:
                Accept();
                break;
            case KeyAction.Cancel:
                Cancel();
                break;
        }
    }

    private void RunAction(int index) {
        switch (index) {
            case 0:
                Cancel();
                break;
            case 1:
                Append(' ');
                break;
            case 2:
                Backspace();
                break;
            case 3:
                Accept();
                break;
        }
    }

    private void Append(char character) {
        if (!edit.Insert(character)) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        error = 0f;
        Audio.Play("event:/ui/main/button_select");
    }

    private void Backspace() {
        if (!edit.Backspace()) return;
        error = 0f;
        Audio.Play("event:/ui/main/button_back");
    }

    private void Accept() {
        if (edit.Text.Length < minLength) {
            error = 1f;
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        if (!commit(edit.Text)) {
            error = 1f;
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        Close();
    }

    private void Cancel() {
        Audio.Play("event:/ui/main/button_back");
        Close();
    }

    private void Close() {
        if (!opened) return;
        opened = false;
        closed();
        RemoveSelf();
    }

    private void RebuildRows() {
        rows = page switch {
            Page.Letters => [
                Characters(upper ? "QWERTYUIOP" : "qwertyuiop", "1234567890"),
                Characters(upper ? "ASDFGHJKL" : "asdfghjkl", "~@#!%&*()"),
                [
                    new Key(upper ? "⇩" : "⇧", '\0', KeyAction.Shift),
                    ..Characters(upper ? "ZXCVBNM" : "zxcvbnm", "`/-_:;?"),
                    new Key("123", '\0', KeyAction.PageNumbers),
                    new Key("#+=", '\0', KeyAction.PageSymbols)
                ]
            ],
            Page.Numbers => [
                Characters("1234567890"),
                Characters("-/:;()$&@\""),
                [
                    ..Characters(".,?!'"),
                    new Key("ABC", '\0', KeyAction.PageLetters),
                    new Key("#+=", '\0', KeyAction.PageSymbols)
                ]
            ],
            _ => [
                Characters("[]{}#%^*+="),
                Characters("_\\|~<>`\"'&"),
                [
                    ..Characters("@$:;,./?"),
                    new Key("ABC", '\0', KeyAction.PageLetters),
                    new Key("123", '\0', KeyAction.PageNumbers)
                ]
            ]
        };
        cursorRow = Math.Clamp(cursorRow, 0, rows.Length - 1);
        cursorColumn = Math.Clamp(cursorColumn, 0, rows[cursorRow].Length - 1);
        inActions = false;
    }

    private static Key[] Characters(string characters, string hints = "") {
        Key[] keys = new Key[characters.Length];
        for (int index = 0; index < characters.Length; index++) {
            char character = characters[index];
            string hint = index < hints.Length ? hints[index].ToString() : "";
            keys[index] = new Key(character.ToString(), character, KeyAction.Character, hint);
        }
        return keys;
    }

    /// <summary>Touch targets published by the owning page while this prompt is open.</summary>
    public IEnumerable<MaterialInteractionTarget> InteractionTargets() {
        for (int row = 0; row < rows.Length; row++) {
            for (int column = 0; column < rows[row].Length; column++) {
                yield return new MaterialInteractionTarget($"text-entry.key.{row}.{column}",
                    KeyRect(row, column), Focused: !inActions && cursorRow == row && cursorColumn == column);
            }
        }
        for (int index = 0; index < ActionLabels.Length; index++) {
            yield return new MaterialInteractionTarget($"text-entry.action.{index}", ActionRect(index),
                Focused: inActions && actionIndex == index);
        }
    }

    public override void Render() {
        Draw.Rect(0f, 0f, 1920f, 1080f, Color.Black * 0.7f);
        MaterialRect panel = new(PanelX, PanelY, PanelWidth, PanelHeight);
        MaterialUiKit.Surface(panel, 30f, palette);
        MaterialUi.RoundedOutline(panel.X, panel.Y, panel.Width, panel.Height, 30f, 1f,
            palette.Outline * 0.8f);

        MaterialUiKit.Text($"编辑 · {title}", new Vector2(panel.X + PanelInset, panel.Y + 30f),
            Vector2.Zero, MaterialTextRole.Section, palette.OnSurface, 1f, scaleOverride: 0.38f);
        MaterialUiKit.Text(PageHint(),
            new Vector2(panel.Right - PanelInset, panel.Y + 38f), new Vector2(1f, 0f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, 0.9f, scaleOverride: 0.26f);

        RenderField(panel);
        RenderKeyboard();
        RenderActions();
    }

    private string PageHint() => page switch {
        Page.Letters => "点按输入 · ↑ 光标区 ←→ 移动 · 角标仅提示",
        Page.Numbers => "数字页 · ABC 回字母 · #+= 换符号 · Esc 取消",
        _ => "符号页 · ABC 回字母 · 123 换数字 · Esc 取消"
    };

    private void RenderField(MaterialRect panel) {
        MaterialRect field = FieldRect(panel);
        MaterialUi.RoundedRect(field.X, field.Y, field.Width, field.Height, 18f,
            palette.SurfaceHighest);
        MaterialUi.RoundedOutline(field.X, field.Y, field.Width, field.Height, 18f,
            fieldFocused || error > 0f ? 2f : 1f,
            (error > 0f ? Color.OrangeRed : palette.Primary)
            * (fieldFocused || error > 0f ? 1f : 0.7f));

        float textWidth = field.Width - 100f;
        TextEditBuffer.TextWindow window = edit.Window(textWidth, 0.36f, UiFontWeight.Bold);
        int caretOffset = Math.Clamp(window.CaretOffset, 0, window.Text.Length);
        string before = window.Text[..caretOffset];
        string after = window.Text[caretOffset..];
        bool empty = edit.Text.Length == 0;
        Color color = empty ? palette.OnSurfaceVariant * 0.7f : palette.OnSurface;
        MaterialUiKit.Text(empty ? "输入内容…" : before + after,
            new Vector2(field.X + 24f, field.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, color, 1f, scaleOverride: 0.36f);

        if (Scene.BetweenInterval(0.5f)) {
            float width = SystemTtfFont.MeasureVisible(before, 0.36f, UiFontWeight.Bold).X;
            float caretX = Math.Min(field.Right - 100f, field.X + 24f + width);
            MaterialUi.Line(new Vector2(caretX, field.Y + 20f),
                new Vector2(caretX, field.Bottom - 20f), 2.5f, palette.Primary);
        }

        MaterialUiKit.Text($"{edit.Text.Length}/{maxLength}",
            new Vector2(field.Right - 22f, field.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Caption, error > 0f ? Color.OrangeRed : palette.OnSurfaceVariant,
            1f, scaleOverride: 0.27f);

        if (fieldFocused) {
            MaterialUiKit.Text("←→ 移动光标 · ↓ 回键盘",
                new Vector2(field.X + 24f, field.Bottom - 6f), new Vector2(0f, 0f),
                MaterialTextRole.Caption, palette.Primary, 1f, scaleOverride: 0.22f);
        }
    }

    private void RenderKeyboard() {
        for (int row = 0; row < rows.Length; row++) {
            Key[] keys = rows[row];
            float width = KeyWidth();
            float rowWidth = keys.Length * width + (keys.Length - 1) * KeyGap;
            float x = PanelX + (PanelWidth - rowWidth) / 2f;
            float y = KeyY(row);
            for (int column = 0; column < keys.Length; column++) {
                MaterialRect rect = new(x + column * (width + KeyGap), y, width, KeyHeight);
                RenderKey(keys[column], rect,
                    !inActions && cursorRow == row && cursorColumn == column,
                    hoverRow == row && hoverColumn == column);
            }
        }
    }

    private void RenderKey(Key key, MaterialRect rect, bool focused, bool hovered) {
        bool activeToggle = key.Action == KeyAction.Shift && upper;
        Color fill;
        Color text;
        if (hovered) {
            fill = palette.Primary;
            text = palette.OnPrimary;
        } else if (activeToggle) {
            fill = palette.Primary * 0.82f;
            text = palette.OnPrimary;
        } else if (focused) {
            fill = palette.SurfaceHighest;
            text = palette.Primary;
        } else {
            fill = palette.SurfaceHighest * 0.58f;
            text = key.Action == KeyAction.Character ? palette.OnSurface : palette.OnSurfaceVariant;
        }
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 14f, fill);
        if (focused && !hovered) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 14f, 2f,
                palette.Primary);
        }
        if (key.Hint.Length > 0) {
            MaterialUiKit.Text(key.Hint, new Vector2(rect.Right - 14f, rect.Y + 12f),
                new Vector2(1f, 0f), MaterialTextRole.Caption,
                (hovered ? palette.OnPrimary : palette.OnSurfaceVariant) * 0.72f, 1f,
                scaleOverride: 0.19f);
        }
        MaterialUiKit.Text(key.Label, new Vector2(rect.Center.X, rect.Center.Y + 8f),
            new Vector2(0.5f), MaterialTextRole.Label, text, 1f,
            scaleOverride: key.Action == KeyAction.Character ? 0.36f : 0.24f);
    }

    private void RenderActions() {
        for (int index = 0; index < ActionLabels.Length; index++) {
            MaterialRect button = ActionRect(index);
            bool focused = inActions && actionIndex == index;
            bool hovered = hoverAction == index;
            bool danger = index == 0;
            bool accept = index == 3;
            Color fill = hovered || focused
                ? danger ? new Color(198, 74, 84) : palette.Primary
                : palette.SurfaceHighest;
            Color text = hovered || focused
                ? danger ? Color.White : palette.OnPrimary
                : accept ? palette.Primary : palette.OnSurface;
            MaterialUi.RoundedRect(button.X, button.Y, button.Width, button.Height, 18f, fill);
            if (!hovered && !focused) {
                MaterialUi.RoundedOutline(button.X, button.Y, button.Width, button.Height, 18f,
                    1f, palette.Outline * 0.7f);
            }
            MaterialUiKit.Text(ActionLabels[index], button.Center, new Vector2(0.5f),
                MaterialTextRole.Label, text, 1f, scaleOverride: 0.3f);
        }
    }

    private MaterialRect FieldRect(MaterialRect panel) =>
        new(panel.X + PanelInset, panel.Y + FieldOffsetY, panel.Width - PanelInset * 2f,
            FieldHeight);

    private static float KeyWidth() => (PanelWidth - PanelInset * 2f - KeyGap * (Columns - 1))
        / Columns;

    private static float KeyY(int row) => PanelY + KeyboardOffsetY + row * (KeyHeight + KeyGap);

    private MaterialRect KeyRect(int row, int column) {
        Key[] keys = rows[row];
        float width = KeyWidth();
        float rowWidth = keys.Length * width + (keys.Length - 1) * KeyGap;
        float x = PanelX + (PanelWidth - rowWidth) / 2f;
        return new MaterialRect(x + column * (width + KeyGap), KeyY(row), width, KeyHeight);
    }

    private static float ActionWidth(int index) {
        float total = ActionWeights.Sum();
        float available = PanelWidth - PanelInset * 2f - KeyGap * (ActionLabels.Length - 1);
        return available * ActionWeights[index] / total;
    }

    private static MaterialRect ActionRect(int index) {
        float x = PanelX + PanelInset;
        for (int previous = 0; previous < index; previous++) x += ActionWidth(previous) + KeyGap;
        return new MaterialRect(x, PanelY + PanelHeight - ActionBottomMargin - ActionHeight,
            ActionWidth(index), ActionHeight);
    }

    private int RowAt(Vector2 point) {
        for (int row = 0; row < rows.Length; row++) {
            if (point.Y < KeyY(row) || point.Y > KeyY(row) + KeyHeight) continue;
            return ColumnAt(row, point) >= 0 ? row : -1;
        }
        return -1;
    }

    private int ColumnAt(int row, Vector2 point) {
        Key[] keys = rows[row];
        for (int column = 0; column < keys.Length; column++) {
            if (KeyRect(row, column).Contains(point)) return column;
        }
        return -1;
    }

    private int ActionAt(Vector2 point) {
        for (int index = 0; index < ActionLabels.Length; index++) {
            if (ActionRect(index).Contains(point)) return index;
        }
        return -1;
    }

    private static int Wrap(int value, int count) =>
        count <= 0 ? 0 : (value % count + count) % count;

    private static string EllipsizeFromLeft(string value, float maxWidth, float scale,
        UiFontWeight weight) {
        if (SystemTtfFont.MeasureVisible(value, scale, weight).X <= maxWidth) return value;
        const string ellipsis = "…";
        for (int start = 1; start < value.Length; start++) {
            string candidate = ellipsis + value[start..];
            if (SystemTtfFont.MeasureVisible(candidate, scale, weight).X <= maxWidth) return candidate;
        }
        return ellipsis;
    }
}
