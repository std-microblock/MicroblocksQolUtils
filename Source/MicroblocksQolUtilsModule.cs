using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public sealed class MicroblocksQolUtilsModule : EverestModule {
    public static MicroblocksQolUtilsModule Instance { get; private set; } = null!;
    public static QolSettings Settings => (QolSettings)Instance._Settings;

    public override Type SettingsType => typeof(QolSettings);

    public MicroblocksQolUtilsModule() {
        Instance = this;
    }

    public override void Load() {
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "Loading microblock's QoL Utils");
        CollabUtils2Bridge.Load();
        MaterialChapterSelect.Load();
        QolPauseMenu.Load();
        MaterialAcrylicRenderer.Load();
        MaterialUiSmoke.Load();
        NativeCaptureBridge.Initialize(Path.GetDirectoryName(Metadata.DLL));
        NativeCaptureSmoke.Load();
        FrameProfiler.Load();
        InstantTransitions.Load();
        InstantDeaths.Load();
        AutoRecorder.Load(Path.GetDirectoryName(Metadata.DLL) ?? "");
        Everest.Events.Level.OnLoadLevel += OnLoadLevel;
        On.Monocle.Engine.Update += EngineUpdate;
    }

    public override void Unload() {
        On.Monocle.Engine.Update -= EngineUpdate;
        Everest.Events.Level.OnLoadLevel -= OnLoadLevel;
        NativeCaptureSmoke.Unload();
        MaterialAcrylicRenderer.Unload();
        MaterialChapterSelect.Unload();
        QolPauseMenu.Unload();
        MaterialUiSmoke.Unload();
        NativeCaptureCommands.Unload();
        AutoRecorder.Unload();
        InstantDeaths.Unload();
        InstantTransitions.Unload();
        FrameProfiler.Unload();
        MiaoNetBridge.Unload();
        MotionSmoothingBridge.Unload();
        MaterialUi.Dispose();
        SystemTtfFont.Dispose();
    }

    private static void OnLoadLevel(Level level, Player.IntroTypes intro, bool fromLoader) {
        if (level.Tracker.GetEntity<QolHud>() is null) level.Add(new QolHud());
    }

    private static void EngineUpdate(On.Monocle.Engine.orig_Update orig, Engine self, Microsoft.Xna.Framework.GameTime gameTime) {
        FrameProfiler.BeginFrame();
        orig(self, gameTime);
        MaterialUiSmoke.Update();
    }
}
