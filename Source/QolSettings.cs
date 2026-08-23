using System.ComponentModel;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.MicroblocksQolUtils;

public enum MiniMapShape {
    Circle,
    Square
}

public enum MiniMapNameMode {
    None,
    WatchedOnly,
    Everyone
}

public enum RecordingPolicy {
    EveryRoom,
    GoldenRunsOnly
}

public enum BgmRecordingMode {
    CaptureGameMix,
    SfxOnlyWithPostMix
}

public sealed class QolSettings : EverestModuleSettings {
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DefaultValue(true)]
    public bool MaterialAcrylicBackground { get; set; } = true;

    [DefaultValue(true)]
    public bool HudMaterialSurfaces { get; set; } = true;

    [SettingRange(1, 12)]
    [DefaultValue(7)]
    public int MaterialAcrylicBlurStrength { get; set; } = 7;

    [DefaultValue(false)]
    public bool ReplaceChapterSelect { get; set; }

    [DefaultValue(false)]
    public bool ChapterSelectShowCollabMaps { get; set; }

    [DefaultValue("Microsoft YaHei UI")]
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    [SettingIgnore]
    [DefaultValue("")]
    public string FontFile { get; set; } = "";

    public void CreateFontFamilyEntry(TextMenu menu, bool inGame) {
        string current = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily.Trim();
        TextMenu.Option<string> fonts = new(Dialog.Clean("modoptions_microblocksqolutils_fontfamily"));

        if (!UiFontCatalog.InstalledFamilies.Contains(current, StringComparer.OrdinalIgnoreCase))
            fonts.Add(current, current, selected: true);

        foreach (string family in UiFontCatalog.InstalledFamilies)
            fonts.Add(family, family, string.Equals(family, current, StringComparison.OrdinalIgnoreCase));

        fonts.Change(family => {
            FontFamily = family;
            FontFile = "";
        });
        menu.Add(fonts);
    }

    [DefaultValue(true)]
    public bool MiniMapEnabled { get; set; } = true;

    [SettingRange(96, 384)]
    [DefaultValue(220)]
    public int MiniMapSize { get; set; } = 220;

    [SettingRange(0, 12)]
    [DefaultValue(3)]
    public int MiniMapZoom { get; set; } = 3;

    [SettingIgnore]
    [DefaultValue(Keys.OemPlus)]
    public Keys MiniMapZoomInKey { get; set; } = Keys.OemPlus;

    [SettingIgnore]
    [DefaultValue(Keys.OemMinus)]
    public Keys MiniMapZoomOutKey { get; set; } = Keys.OemMinus;

    [SettingName("放大小地图")]
    [DefaultButtonBinding(0, Keys.OemPlus)]
    public ButtonBinding MiniMapZoomInBinding { get; set; } = new(0, Keys.OemPlus);

    [SettingName("缩小小地图")]
    [DefaultButtonBinding(0, Keys.OemMinus)]
    public ButtonBinding MiniMapZoomOutBinding { get; set; } = new(0, Keys.OemMinus);

    [SettingIgnore]
    [DefaultValue(false)]
    public bool MiniMapBindingsMigrated { get; set; }

    [DefaultValue(MiniMapShape.Circle)]
    public MiniMapShape MiniMapShape { get; set; } = MiniMapShape.Circle;

    [DefaultValue(true)]
    public bool MiniMapBackground { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapBorder { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapRoomBounds { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapHighlightRoute { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapCollectibles { get; set; } = true;

    [SettingRange(0, 10)]
    [DefaultValue(6)]
    public int MiniMapBackgroundOpacity { get; set; } = 6;

    [DefaultValue(true)]
    public bool MiniMapAdaptiveColors { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowMiaoNetPlayers { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapShowOffscreenPlayers { get; set; } = true;

    [DefaultValue(MiniMapNameMode.WatchedOnly)]
    public MiniMapNameMode MiniMapNames { get; set; } = MiniMapNameMode.WatchedOnly;

    [DefaultValue(true)]
    public bool HideMiaoNetOffscreenNames { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowRoomsRemaining { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowMapPlayerCount { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowClock { get; set; } = true;

    [DefaultValue(true)]
    public bool WatchedPlayerNotifications { get; set; } = true;

    public List<string> WatchedPlayers { get; set; } = [];

    [DefaultValue(false)]
    public bool RemoveRoomTransitions { get; set; }

    [DefaultValue(false)]
    public bool RemoveDeathAnimation { get; set; }

    [DefaultValue(true)]
    public bool ShowFps { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowFrameTime { get; set; } = true;

    [DefaultValue(false)]
    public bool ShowPhysicalAndRenderFps { get; set; }

    [DefaultValue(false)]
    public bool EnableFrameProfiler { get; set; }

    [SettingRange(20, 250)]
    [DefaultValue(34)]
    public int FrameSpikeThresholdMs { get; set; } = 34;

    [DefaultValue(false)]
    public bool AutoRecorderEnabled { get; set; }

    [DefaultValue(true)]
    public bool ShowRecordingIndicator { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowRecordingDuration { get; set; } = true;

    [DefaultValue(RecordingPolicy.EveryRoom)]
    public RecordingPolicy RecordingPolicy { get; set; } = RecordingPolicy.EveryRoom;

    [DefaultValue(BgmRecordingMode.SfxOnlyWithPostMix)]
    public BgmRecordingMode BgmMode { get; set; } = BgmRecordingMode.SfxOnlyWithPostMix;

    [DefaultValue(true)]
    public bool RecordingIncludeUiSfx { get; set; } = true;

    [DefaultValue(60)]
    [SettingRange(30, 120)]
    public int RecordingFrameRate { get; set; } = 60;

    [DefaultValue(12000)]
    [SettingRange(2000, 50000)]
    public int RecordingBitrateKbps { get; set; } = 12000;

    [DefaultValue(100)]
    [SettingRange(0, 500)]
    public int RecordingRetentionCount { get; set; } = 100;

    [DefaultValue("")]
    public string RecordingDirectory { get; set; } = "";

    [DefaultValue("auto")]
    public string RecordingEncoder { get; set; } = "auto";

    [DefaultValue("")]
    public string BgmEventMapFile { get; set; } = "";

    internal void MigrateMiniMapBindings() {
        if (MiniMapBindingsMigrated) return;
        SetLegacyKey(MiniMapZoomInBinding, MiniMapZoomInKey);
        SetLegacyKey(MiniMapZoomOutBinding, MiniMapZoomOutKey);
        MiniMapBindingsMigrated = true;
    }

    private static void SetLegacyKey(ButtonBinding binding, Keys key) {
        binding.Keys.Clear();
        if (key != Keys.None) binding.Keys.Add(key);
    }
}
