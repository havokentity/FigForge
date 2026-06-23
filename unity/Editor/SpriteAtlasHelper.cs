// =============================================================================
// FigForge — optional SpriteAtlas generation from the imported sprite folder.
// =============================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace FigForge
{
    public class AtlasSettings
    {
        public bool create = false;
        public int padding = 2;
        public bool allowRotation = false;
        public bool includeInBuild = true;
        public bool tightPacking = false;
    }

    public static class SpriteAtlasHelper
    {
        public static SpriteAtlas Build(string screenName, string spriteFolder, AtlasSettings settings)
        {
            if (!settings.create) return null;

            string atlasFolder = $"{spriteFolder}/Atlas";
            TextureImportHelper.EnsureFolder(atlasFolder);
            string path = $"{atlasFolder}/{Sanitize(screenName)}_Atlas.spriteatlas";

            // Reconfigure an existing atlas in place rather than CreateAsset over it:
            // re-creating churns the .spriteatlas GUID, breaking any references (and
            // the late-binding SpriteAtlasManager lookups) that point at the old one.
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            bool isNew = atlas == null;
            if (isNew) atlas = new SpriteAtlas();

            var packing = new SpriteAtlasPackingSettings
            {
                padding = settings.padding,
                enableRotation = settings.allowRotation,
                enableTightPacking = settings.tightPacking,
            };
            atlas.SetPackingSettings(packing);
            atlas.SetIncludeInBuild(settings.includeInBuild);

            var folderAsset = AssetDatabase.LoadAssetAtPath<Object>(spriteFolder);
            // Clear prior packables so a reconfigured atlas doesn't accumulate stale
            // folder entries across rebuilds; then add the current sprite folder.
            if (!isNew)
            {
                var prior = atlas.GetPackables();
                if (prior != null && prior.Length > 0) atlas.Remove(prior);
            }
            if (folderAsset != null) atlas.Add(new Object[] { folderAsset });

            if (isNew) AssetDatabase.CreateAsset(atlas, path);
            else EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget);
            return atlas;
        }

        static string Sanitize(string s)
        {
            var chars = new List<char>();
            foreach (var c in s ?? "atlas") chars.Add(char.IsLetterOrDigit(c) ? c : '_');
            return new string(chars.ToArray());
        }
    }
}
