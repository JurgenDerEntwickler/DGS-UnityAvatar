using UnityEditor;
using UnityEngine;

/// Import fixes for Microsoft Rocketbox avatars.
/// Based on FixRocketboxMaxImport.cs from github.com/microsoft/Microsoft-Rocketbox (MIT).
/// Use THIS file INSTEAD of the original (never both). Differences:
///   * Only touches files inside a folder named "Rocketbox" -> the Mixamo avatar and the
///     sign clips are left alone (the original runs on every FBX and errors on them).
///   * Humanoid rig instead of Generic -> the Mixamo-based sign clips retarget onto it and
///     SmoothSignPlayer / SignCameraFraming work.
///   * Sets sensible import defaults on the FIRST import only (later changes you make in the
///     Inspector are kept): blendshapes on, external materials, textures max 2048 px.
public class RocketboxImport : AssetPostprocessor
{
    static bool IsRocketbox(string path) => path.Replace('\\', '/').Contains("/Rocketbox/");

    void OnPreprocessModel()
    {
        if (!IsRocketbox(assetPath)) return;
        var m = (ModelImporter)assetImporter;
        if (!m.importSettingsMissing) return;               // only the very first import

        m.animationType = ModelImporterAnimationType.Human;
        m.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        m.importAnimation = false;                          // the avatar file has no clips we need
        m.importBlendShapes = true;                         // the face (AU_ / AK_ shapes)
        m.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        m.materialLocation = ModelImporterMaterialLocation.External;   // editable .mat files
        m.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        m.materialSearch = ModelImporterMaterialSearch.RecursiveUp;    // finds ../Textures
    }

    void OnPreprocessTexture()
    {
        if (!IsRocketbox(assetPath)) return;
        var t = (TextureImporter)assetImporter;
        if (assetPath.ToLower().Contains("normal"))
        {
            t.textureType = TextureImporterType.NormalMap;
            t.convertToNormalmap = false;
        }
        if (t.importSettingsMissing) t.maxTextureSize = 2048;   // keeps the WebGL build small
    }

    void OnPostprocessMaterial(Material material)
    {
        if (!IsRocketbox(assetPath)) return;
        // 3ds Max: the texture IS the colour. Unity multiplies it with the flat colour.
        material.color = Color.white;
        // Transparent (3) shows specular highlights -> hair looks like glass. Use Fade (2).
        if (material.HasProperty("_Mode") && Mathf.Approximately(material.GetFloat("_Mode"), 3f))
            MakeFade(material);
    }

    void OnPostprocessMeshHierarchy(GameObject go)
    {
        if (!IsRocketbox(assetPath)) return;
        // Files contain several detail levels; only show the highest one.
        string n = go.name.ToLower();
        if (n.Contains("poly") && !n.Contains("hipoly")) go.SetActive(false);
    }

    /// Standard shader "Fade": changing _Mode alone does not update the blend state.
    static void MakeFade(Material m)
    {
        m.SetFloat("_Mode", 2f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.DisableKeyword("_ALPHATEST_ON");
        m.EnableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}
