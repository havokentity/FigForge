// =============================================================================
// FigForge - editor component icons.
//
// Gives FigForge runtime scripts sensible Unity-style icons in the Inspector,
// Project view, and Add Component search by assigning each MonoScript a built-in
// icon that matches its closest uGUI concept.
// =============================================================================

using System;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [InitializeOnLoad]
    internal static class FigForgeComponentIcons
    {
        const string IconVersion = "figforge-component-icons-v1";
        const string PrefKey = "FigForge.ComponentIcons.Version";

        readonly struct IconEntry
        {
            public readonly string fullName;
            public readonly Type iconType;

            public IconEntry(string fullName, Type iconType)
            {
                this.fullName = fullName;
                this.iconType = iconType;
            }
        }

        static readonly IconEntry[] Entries =
        {
            new IconEntry("FigForge.FigForgeButton", typeof(Button)),
            new IconEntry("FigForge.FigForgeToggle", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeSwitch", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeInputField", typeof(TMP_InputField)),
            new IconEntry("FigForge.FigForgeDropdown", typeof(TMP_Dropdown)),
            new IconEntry("FigForge.FigForgeSlider", typeof(Slider)),
            new IconEntry("FigForge.FigForgeStepper", typeof(TMP_InputField)),
            new IconEntry("FigForge.FigForgeProgress", typeof(Slider)),
            new IconEntry("FigForge.FigForgeModal", typeof(CanvasGroup)),
            new IconEntry("FigForge.FigForgeToastHost", typeof(Canvas)),
            new IconEntry("FigForge.FigForgeToastItem", typeof(CanvasGroup)),

            new IconEntry("FigForge.FigForgeList", typeof(ScrollRect)),
            new IconEntry("FigForge.FigForgeListRow", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeTable", typeof(ScrollRect)),
            new IconEntry("FigForge.FigForgeTableRow", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeScrollRect", typeof(ScrollRect)),

            new IconEntry("FigForge.FigForgeRoundedRect", typeof(Image)),
            new IconEntry("FigForge.FigForgeLayeredRect", typeof(Image)),
            new IconEntry("FigForge.FigForgeVectorGraphic", typeof(Image)),
            new IconEntry("FigForge.FigForgeRing", typeof(Image)),
            new IconEntry("FigForge.FigForgeImageBlend", typeof(Image)),
            new IconEntry("FigForge.FigForgeTextBlend", typeof(TMP_Text)),

            new IconEntry("FigForge.FigForgeButtonStateColors", typeof(Button)),
            new IconEntry("FigForge.FigForgeButtonStateObjects", typeof(Button)),
            new IconEntry("FigForge.FigForgeToggleStateColors", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeToggleGraphicObject", typeof(Toggle)),
            new IconEntry("FigForge.FigForgeScrollbarStates", typeof(Scrollbar)),
            new IconEntry("FigForge.FigForgeGraphicStateColors", typeof(Image)),
            new IconEntry("FigForge.FigForgeGraphicStateSprites", typeof(Image)),
            new IconEntry("FigForge.FigForgeDropdownOptionEdges", typeof(TMP_Dropdown)),

            new IconEntry("FigForge.FigForgeCanvasHelper", typeof(Canvas)),
            new IconEntry("FigForge.FigForgePageCompositor", typeof(Canvas)),
            new IconEntry("FigForge.FigForgeBindings", typeof(RectTransform)),
            new IconEntry("FigForge.FigForgeFrame", typeof(RectTransform)),
            new IconEntry("FigForge.FigForgeFrameElement", typeof(RectTransform)),
            new IconEntry("FigForge.FigForgeScreen", typeof(Canvas)),
            new IconEntry("FigForge.UIScreenManager", typeof(Canvas)),
            new IconEntry("FigForge.FigForgeNavBinder", typeof(Button)),
            new IconEntry("FigForge.FigForgeNavLink", typeof(Button)),
            new IconEntry("FigForge.FigForgeImportStamp", typeof(TextAsset)),
        };

        static FigForgeComponentIcons()
        {
            EditorApplication.delayCall += ApplyOncePerVersion;
        }

        [MenuItem("Tools/FigForge/Refresh Component Icons")]
        static void RefreshFromMenu()
        {
            int changed = Apply(force: true);
            EditorUtility.DisplayDialog("FigForge", $"Refreshed {changed} component icon(s).", "OK");
        }

        static void ApplyOncePerVersion()
        {
            if (EditorPrefs.GetString(PrefKey, "") == IconVersion) return;
            Apply(force: false);
            EditorPrefs.SetString(PrefKey, IconVersion);
        }

        static int Apply(bool force)
        {
            int changed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in Entries)
                {
                    var script = FindScript(entry.fullName);
                    if (script == null) continue;

                    var icon = ResolveIcon(entry.iconType);
                    if (icon == null) continue;

                    var path = AssetDatabase.GetAssetPath(script);
                    var importer = AssetImporter.GetAtPath(path) as MonoImporter;
                    if (importer == null) continue;

                    if (!force && importer.GetIcon() == icon) continue;
                    importer.SetIcon(icon);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return changed;
        }

        static MonoScript FindScript(string fullName)
        {
            string shortName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            foreach (var guid in AssetDatabase.FindAssets(shortName + " t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var cls = script != null ? script.GetClass() : null;
                if (cls != null && cls.FullName == fullName) return script;
            }
            return null;
        }

        static Texture2D ResolveIcon(Type type)
        {
            var icon = EditorGUIUtility.ObjectContent(null, type).image as Texture2D;
            if (icon != null) return icon;

            icon = EditorGUIUtility.IconContent("cs Script Icon").image as Texture2D;
            if (icon != null) return icon;

            return EditorGUIUtility.IconContent("ScriptableObject Icon").image as Texture2D;
        }
    }
}
