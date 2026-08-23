namespace Celeste.Mod.MicroblocksQolUtils;

internal interface IMaterialAcrylicPage {
    bool SuppressNormalRender { get; set; }
    void RenderMaterialContent(bool acrylicActive);
}
