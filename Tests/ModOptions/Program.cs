using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;

int assertions = 0;
void Check(bool condition, string message) {
    assertions++;
    if (!condition) throw new Exception(message);
}

List<object> Entries(params (string Label, int Value)[] pairs) =>
    pairs.Select(pair => (object)Tuple.Create(pair.Label, pair.Value)).ToList();

foreach (string value in new[] {
             "0", "42", "-5", "+7", "1.5", "1,000", "1e3", "1x", "2×", "80%", "10 fps", "1.5 ms"
         }) {
    Check(OptionRules.LooksNumeric(value), $"expected a numeric label: \"{value}\"");
}
foreach (string value in new[] {
             "", " ", "-", "+", "1.2.3", "1 (recommended)", "1280×720", "low", "NaN", "Infinity"
         }) {
    Check(!OptionRules.LooksNumeric(value), $"expected a non-numeric label: \"{value}\"");
}

Check(OptionRules.EntryValue(Tuple.Create("label", 7)) is 7, "EntryValue should read Item2");

Check(OptionRules.Classify(true, false, false, null, [], []) == OptionKind.Switch,
    "bool option should be a switch");
Check(OptionRules.Classify(false, false, false, null, ["Off", "On"], []) == OptionKind.Dropdown,
    "non-int option should be a dropdown");
Check(OptionRules.Classify(false, false, true, null, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Range,
    "consecutive numeric int option should be a range");
Check(OptionRules.Classify(false, false, true, null, ["0", "5", "10"],
        Entries(("0", 0), ("5", 5), ("10", 10))) == OptionKind.Dropdown,
    "sparse int option should be a dropdown");
Check(OptionRules.Classify(false, false, true, null, ["Low", "Mid", "High"],
        Entries(("Low", 0), ("Mid", 1), ("High", 2))) == OptionKind.Dropdown,
    "named int option should be a dropdown");
Check(OptionRules.Classify(false, true, true, null, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Range,
    "numeric slider with an unknown backing should be a range");
Check(OptionRules.Classify(false, true, true, false, ["Low", "Mid"],
        Entries(("Low", 0), ("Mid", 1))) == OptionKind.Range,
    "int-backed slider should be a range");
Check(OptionRules.Classify(false, true, true, true, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Dropdown,
    "enum-backed slider should be a dropdown");
Check(OptionRules.Classify(false, false, true, true, ["Low", "Mid"],
        Entries(("Low", 0), ("Mid", 1))) == OptionKind.Dropdown,
    "enum-backed int option should be a dropdown");

var enumProperty = typeof(TestModeHolder).GetProperty(nameof(TestModeHolder.Value))!;
var intProperty = typeof(TestIntHolder).GetProperty(nameof(TestIntHolder.Value))!;
var enumChange = new Action<int>(new PropertyHolder { EnumProperty = enumProperty }.Apply);
var intChange = new Action<int>(new PropertyHolder { IntProperty = intProperty }.Apply);
var mixedChange = new Action<int>(new PropertyHolder {
    EnumProperty = enumProperty,
    IntProperty = intProperty
}.Apply);

Check(OptionRules.ChangeTargetsEnum(null) is null, "a missing handler should be unknown");
Check(OptionRules.ChangeTargetsEnum(enumChange) is true, "enum property should be detected");
Check(OptionRules.ChangeTargetsEnum(intChange) is false, "int property should be detected");
Check(OptionRules.ChangeTargetsEnum(mixedChange) is null, "mixed captures should be ambiguous");

Console.WriteLine($"OptionRules tests passed ({assertions} assertions)");

// --- TextEditBuffer: real spaces, caret, selection, cut/paste semantics ---

var edit = new TextEditBuffer("ab c", 10);
Check(edit.Text == "ab c", "text should be kept verbatim, spaces included");
Check(edit.Cursor == 4, "the caret should start at the end");
Check(!edit.HasSelection, "a fresh buffer should not select anything");

edit.MoveHome();
edit.MoveRight(extend: true);
edit.MoveRight(extend: true);
Check(edit.SelectionStart == 0 && edit.SelectionLength == 2,
    "shift+right should extend the selection from the caret");
Check(edit.SelectedText == "ab", "the selection should expose the selected text");
edit.MoveLeft(extend: true);
Check(edit.SelectionLength == 1, "shift+left should shrink the selection");
edit.MoveLeft();
Check(!edit.HasSelection && edit.Cursor == 0,
    "a plain left should collapse the selection to its start");
edit.MoveRight();
Check(edit.Cursor == 1, "a plain right on a collapsed caret should move one character");

edit = new TextEditBuffer("ac", 5);
edit.MoveHome();
edit.MoveRight();
Check(edit.Insert('b') && edit.Text == "abc" && edit.Cursor == 2,
    "inserting should happen at the caret, not at the end");

edit = new TextEditBuffer("hello", 5);
edit.SelectAll();
Check(edit.Backspace() && edit.Text == "" && edit.Cursor == 0,
    "backspace should remove the selection");
edit = new TextEditBuffer("hello", 5);
edit.MoveHome();
edit.MoveRight();
Check(edit.Delete() && edit.Text == "hllo", "delete should remove the character after the caret");

edit = new TextEditBuffer("old value", 20);
edit.SelectAll();
Check(edit.InsertText("new") && edit.Text == "new" && edit.Cursor == 3,
    "pasting should replace the selection");
edit = new TextEditBuffer("", 20);
Check(edit.InsertText("a\r\n\tb") && edit.Text == "ab",
    "line breaks and tabs should be dropped from pasted text");
Check(new TextEditBuffer("  a  ", 10).Text == "  a  ",
    "leading and trailing spaces should survive");

edit = new TextEditBuffer("abc", 3);
edit.MoveEnd();
Check(!edit.InsertText("de"), "an insert at the limit should be rejected");
edit.SelectAll();
Check(edit.InsertText("abcdef") && edit.Text == "abc",
    "replacing a selection should stay within the maximum length");

edit = new TextEditBuffer("hello", 10);
edit.MoveHome();
edit.MoveEnd(extend: true);
Check(edit.SelectionStart == 0 && edit.SelectionLength == 5,
    "shift+end should select from the anchor to the end");
edit.SetCursor(9);
Check(edit.Cursor == 5, "the caret should clamp to the text length");

// --- TextKeyRepeat: press, delay, repeat interval, release ---

var repeat = new TextKeyRepeat();
Check(repeat.Update(true, 1f / 60f), "a press should act on the first frame");
Check(!repeat.Update(true, TextKeyRepeat.InitialDelay * 0.5f),
    "a held key should wait for the initial delay");
Check(repeat.Update(true, TextKeyRepeat.InitialDelay * 0.6f),
    "a held key should repeat after the initial delay");
Check(!repeat.Update(true, TextKeyRepeat.Interval * 0.5f),
    "repeats should respect the repeat interval");
Check(repeat.Update(true, TextKeyRepeat.Interval * 0.6f),
    "a held key should keep repeating");
repeat.Update(false, 0f);
Check(repeat.Update(true, 0f), "releasing and pressing again should act immediately");

Console.WriteLine($"Text editing tests passed ({assertions} assertions)");

enum TestMode {
    Off,
    On,
    Auto
}

static class TestModeHolder {
    public static TestMode Value { get; set; }
}

static class TestIntHolder {
    public static int Value { get; set; }
}

sealed class PropertyHolder {
    public PropertyInfo? EnumProperty;
    public PropertyInfo? IntProperty;
    public void Apply(int value) { }
}
