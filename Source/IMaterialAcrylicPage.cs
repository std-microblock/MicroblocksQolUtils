using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

internal interface IMaterialAcrylicPage {
    bool SuppressNormalRender { get; set; }
    void RenderMaterialContent(bool acrylicActive, Matrix renderMatrix);
}
