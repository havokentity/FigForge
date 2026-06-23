// =============================================================================
// FigForge — importer editor window. Window ▸ FigForge ▸ Importer.
//
// Detects FigForge manifests in the project, lets you configure canvas / fonts /
// textures / canonical library / multi-page output, and builds the uGUI page.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    public class FigForgeImporterWindow : EditorWindow
    {
        internal const int DefaultWarmUpBatchSize = 256;
        const int DefaultEditorColumns = 5;
        const string PrefWarmUpBatchSize = "FigForge.ImportWarmUpBatchSize";
        const string PrefEditorColumns = "FigForge.EditorColumns";
        internal static int WarmUpBatchSizePref
            => Mathf.Clamp(EditorPrefs.GetInt(PrefWarmUpBatchSize, DefaultWarmUpBatchSize), 1, 8192);
        internal static int EditorColumnsPref
            => Mathf.Clamp(EditorPrefs.GetInt(PrefEditorColumns, DefaultEditorColumns), 1, 50);
        internal static void SetEditorColumnsPref(int columns)
            => EditorPrefs.SetInt(PrefEditorColumns, Mathf.Clamp(columns, 1, 50));

        // ---- discovered manifests ----
        List<string> _manifestPaths = new List<string>();
        int _selected = 0;
        Manifest _manifest;
        List<string> _projectPaths = new List<string>();
        int _selectedProject = 0;

        // ---- config ----
        enum UIBackend { uGUI, UIToolkit }
        enum OutputMode { Scene, Prefab, Both }
        enum ScalePreset { MatchFigma, P720, P1080, Custom }

        UIBackend _backend = UIBackend.uGUI;
        string _uitkOutFolder = "Assets/FigForge/UI";
        bool _uitkCreateDoc = true;

        OutputMode _output = OutputMode.Scene;
        ScalePreset _scalePreset = ScalePreset.MatchFigma;
        float _customRefHeight = 1080f;
        bool _newCanvas = false;   // default: reuse the scene's canvas
        Canvas _existingCanvas;
        bool _connectedScene = true;       // build under a shared FrameManager
        bool _disableRaycasts = true;
        bool _includeGroupsInAccessors = true;
        bool _componentsOnlyAccessors = true;  // accessors for controls only; labels/images need a [s] name marker
        int _warmUpBatchSize = DefaultWarmUpBatchSize;
        int _editorColumns = DefaultEditorColumns;
        string _spriteFolder = "Assets/FigForge/Sprites";
        string _prefabFolder = "Assets/FigForge/Prefabs";

        CanonicalLibrary _canonicalLibrary;
        TextureImportSettings _tex = new TextureImportSettings();
        AtlasSettings _atlas = new AtlasSettings();

        // font mapping: "family|style" → TMP asset
        readonly Dictionary<string, TMP_FontAsset> _fontMap = new Dictionary<string, TMP_FontAsset>();
        List<TMP_FontAsset> _projectFonts = new List<TMP_FontAsset>();

        // ---- ui state ----
        Vector2 _scroll, _logScroll;
        readonly List<(string msg, MessageType kind)> _log = new List<(string, MessageType)>();
        bool _showCanvas, _showFonts, _showTextures, _showAtlas, _showCanonical, _showLive;

        WindowStyles _styles;

        static readonly Color Background = new Color(0.055f, 0.065f, 0.075f);
        static readonly Color Panel = new Color(0.105f, 0.115f, 0.125f);
        static readonly Color PanelSoft = new Color(0.13f, 0.145f, 0.155f);
        static readonly Color Border = new Color(0.24f, 0.27f, 0.28f);
        static readonly Color Accent = new Color(0.17f, 0.86f, 0.33f);
        static readonly Color AccentDim = new Color(0.1f, 0.44f, 0.22f);

        [MenuItem("Window/FigForge/Importer")]
        public static void Open()
        {
            var w = GetWindow<FigForgeImporterWindow>();
            w.titleContent = new GUIContent("FigForge");
            w.minSize = new Vector2(760, 560);
            w.Show();
        }

        // Rebuild every imported live page from its on-disk bundle — same code path
        // the live-import HTTP receiver uses, so importer upgrades (a new
        // CanonicalSchema) apply without re-sending the page from Figma.
        [MenuItem("Window/FigForge/Rebuild Live Pages")]
        public static void RebuildLivePages()
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            string liveAbs = dataPath + "/FigForge/Live";
            var projects = Directory.Exists(liveAbs)
                ? Directory.GetFiles(liveAbs, "project.json", SearchOption.AllDirectories)
                : new string[0];
            if (projects.Length == 0) { Debug.LogWarning("[FigForge] no live page bundles found under Assets/FigForge/Live."); return; }
            var w = GetWindow<FigForgeImporterWindow>(false, "FigForge", true);
            foreach (var abs in projects)
            {
                string rel = "Assets" + abs.Replace('\\', '/').Substring(dataPath.Length);
                Debug.Log($"[FigForge] rebuilding live page: {rel}");
                w.LiveBuildPage(rel);
            }
        }

        void OnEnable()
        {
            wantsMouseMove = true;
            _warmUpBatchSize = WarmUpBatchSizePref;
            _editorColumns = EditorColumnsPref;
            RefreshManifests();
            RefreshFonts();
        }

        // Destroy the GUIStyle backing textures WindowStyles created. _styles is rebuilt
        // by EnsureStyles after each domain reload, so without this the prior instance's
        // HideAndDontSave textures leak as orphaned native objects. Null it so a later
        // OnGUI rebuilds fresh styles rather than touching disposed textures.
        void OnDisable()
        {
            _styles?.Dispose();
            _styles = null;
        }

        void RefreshManifests()
        {
            _manifestPaths = Directory
                .GetFiles(Application.dataPath, "manifest.json", SearchOption.AllDirectories)
                .Select(p => "Assets" + p.Substring(Application.dataPath.Length).Replace('\\', '/'))
                .Where(IsFigForgeManifest)
                .ToList();
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _manifestPaths.Count - 1));

            _projectPaths = Directory
                .GetFiles(Application.dataPath, "project.json", SearchOption.AllDirectories)
                .Select(p => "Assets" + p.Substring(Application.dataPath.Length).Replace('\\', '/'))
                .Where(p => HasSchemaMarker(p, "figforge/project"))
                .ToList();
            _selectedProject = Mathf.Clamp(_selectedProject, 0, Mathf.Max(0, _projectPaths.Count - 1));

            RefreshFonts(); // keep _projectFonts current so BuildFontKeys/GuessFont never hit stale refs
            LoadSelected();
        }

        // Only FigForge manifests carry the "figforge/manifest" schema marker.
        // Requiring it keeps the scan from trying to parse foreign/old-schema
        // manifest.json files in the project (which throw and spam the log).
        static bool IsFigForgeManifest(string assetPath) => HasSchemaMarker(assetPath, "figforge/manifest");

        // The schema marker sits in the file head (it's the first/second JSON key the
        // plugin emits), so read only a bounded prefix instead of slurping the whole
        // file. RefreshManifests runs on every OnEnable/ImportZip/LiveBuildPage and
        // scans the entire Assets tree — full ReadAllText on every manifest.json /
        // project.json (some carry large inlined data) made that needlessly heavy.
        const int SchemaProbeBytes = 8 * 1024;
        static bool HasSchemaMarker(string assetPath, string marker)
        {
            try
            {
                var buffer = new char[SchemaProbeBytes];
                using (var reader = new StreamReader(assetPath))
                {
                    int read = reader.Read(buffer, 0, buffer.Length);
                    return read > 0 && new string(buffer, 0, read).Contains(marker);
                }
            }
            catch { return false; }
        }

        void LoadSelected()
        {
            if (_manifestPaths.Count == 0) { _manifest = null; return; }
            _manifest = ManifestParser.Load(_manifestPaths[_selected]);
            if (_manifest != null) BuildFontKeys();
        }

        void FocusFontsFoldout()
        {
            _showLive = false;
            _showCanvas = false;
            _showFonts = true;
            _showCanonical = false;
            _showTextures = false;
            _showAtlas = false;
        }

        void RefreshFonts()
        {
            _projectFonts = AssetDatabase.FindAssets("t:TMP_FontAsset")
                .Select(g => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(f => f != null).ToList();
        }

        void BuildFontKeys()
        {
            foreach (var f in _manifest.fonts)
                foreach (var s in f.styles)
                {
                    var key = $"{f.family}|{s}";
                    if (!_fontMap.ContainsKey(key)) _fontMap[key] = GuessFont(f.family, s);
                }
        }

        static void ApplyManifestSettings(Manifest manifest)
        {
            var dilate = manifest != null && manifest.settings != null
                ? manifest.settings.fontFaceDilate
                : FontAutoImporter.DefaultFontFaceDilate;
            FontAutoImporter.FaceDilate = Mathf.Clamp(dilate, 0f, 1f);
        }

        // Explicit assignment in the Fonts section wins; otherwise auto-import a
        // matching font (project → OS) and build a TMP asset for it.
        TMP_FontAsset ResolveFontAsset(string family, string style)
        {
            var key = $"{family}|{style}";
            if (_fontMap.TryGetValue(key, out var a) && a != null) return a;

            var resolved = FontAutoImporter.Resolve(family, style, m => Log(m, MessageType.Info));
            if (resolved != null) _fontMap[key] = resolved;
            return resolved;
        }

        TMP_FontAsset GuessFont(string family, string style)
        {
            // Only a confident family+weight match prefills the Fonts section; a
            // miss returns null so it falls through to FontAutoImporter (tiered
            // matching + can generate the exact weight). The old family-only /
            // first-available fallbacks were harmful — they cached e.g. a Regular
            // face for an "Extra Bold" request and shadowed the smart resolver.
            var fam = FontKey(family);
            var sty = FontKey(style);
            if (fam == "") return null;
            return _projectFonts.FirstOrDefault(f =>
            {
                if (f == null) return false; // guard stale/destroyed refs (fonts deleted since the last refresh)
                var n = FontKey(f.name);
                if (!n.Contains(fam)) return false;
                return sty == "" || sty == "regular" ? n.Contains("regular") : n.Contains(sty);
            });
        }

        static string FontKey(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // -----------------------------------------------------------------------
        void OnGUI()
        {
            EnsureStyles();
            if (Event.current != null && Event.current.type == EventType.MouseMove) Repaint();
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), Background);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            Header();
            LiveImportSection();
            ManifestPicker();
            if (_manifest != null)
            {
                CanvasSection();
                FontSection();
                CanonicalSection();
                TextureSection();
                AtlasSection();
                BuildBar();
            }
            RefsBar();
            LogSection();

            EditorGUILayout.EndScrollView();
        }

        // Maintenance for an already-built page: re-wire serialized accessor refs, or report
        // which ones are missing — across every FigForgeFrame in the open scene(s). Independent
        // of the loaded manifest, so it's available even without a manifest selected.
        void RefsBar()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                EditorGUILayout.LabelField("Page references", _styles.sectionTitle);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Validate Page Refs", _styles.button, GUILayout.Height(26))) FigForgeRefTools.ValidateSceneRefs();
                    if (GUILayout.Button("Populate Page Refs", _styles.button, GUILayout.Height(26))) FigForgeRefTools.PopulateSceneRefs();
                }
            }
        }

        void Header()
        {
            using (new EditorGUILayout.VerticalScope(_styles.hero))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("FIGFORGE", _styles.heroTitle);
                            GUILayout.Label("->", _styles.versionArrow, GUILayout.Width(18), GUILayout.Height(20));
                            GUILayout.Label($"v{PackageVersion()}", _styles.versionPill, GUILayout.Width(60), GUILayout.Height(18));
                            GUILayout.FlexibleSpace();
                        }
                        GUILayout.Label("Forge Figma frames into Unity UI, keep live import close, and rebuild pages without losing your scene work.", _styles.heroSubtitle);
                    }

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Rescan", _styles.primaryButton, GUILayout.Width(92), GUILayout.Height(30)))
                    { RefreshManifests(); RefreshFonts(); }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    SummaryCard("Page bundles", _projectPaths.Count.ToString(), "project.json", new Color(0.45f, 0.55f, 1f));
                    SummaryCard("Manifests", _manifestPaths.Count.ToString(), "single screens", new Color(0.82f, 0.54f, 1f));
                    SummaryCard("Elements", (_manifest?.elements.Count ?? 0).ToString(), _manifest?.screen?.name ?? "none loaded", Accent);
                    SummaryCard("Sprites", (_manifest?.assets.Count ?? 0).ToString(), "texture assets", new Color(0.28f, 0.75f, 1f));
                }
            }
        }

        void LiveImportSection()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showLive = Foldout(_showLive, "Live import (Figma to Unity)");
                if (!_showLive)
                {
                    using (new EditorGUI.IndentLevelScope())
                        LiveImportTokenRow(false);
                    return;
                }
                using (new EditorGUI.IndentLevelScope())
                {
                    bool en = EditorGUILayout.ToggleLeft("Run live import server", FigForgeLiveImport.Enabled, _styles.toggle);
                    if (en != FigForgeLiveImport.Enabled) FigForgeLiveImport.Enabled = en;

                    using (new EditorGUI.DisabledScope(!en))
                    {
                        int port = EditorGUILayout.DelayedIntField("Port", FigForgeLiveImport.Port);
                        if (port != FigForgeLiveImport.Port) FigForgeLiveImport.Port = port;

                        LiveImportTokenRow(true);
                    }

                    EditorGUILayout.LabelField(
                        (FigForgeLiveImport.Listening ? "Online  " : "Idle  ") + FigForgeLiveImport.Status,
                        _styles.subtleLabel);
                    EditorGUILayout.HelpBox(
                        "Paste this token into the Figma plugin (Unity token field) once. Then hit Send to Unity to build the page here automatically. Loopback only.",
                        MessageType.None);
                }
            }
        }

        void LiveImportTokenRow(bool allowRegenerate)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Plugin token");
                EditorGUILayout.SelectableLabel(FigForgeLiveImport.Token, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Copy", _styles.miniButton, GUILayout.Width(46)))
                    EditorGUIUtility.systemCopyBuffer = FigForgeLiveImport.Token;
                if (allowRegenerate && GUILayout.Button("New", _styles.miniButton, GUILayout.Width(40)) &&
                    EditorUtility.DisplayDialog("Regenerate live-import token?",
                        "The Figma plugin won't be able to import again until the new token is pasted into its Unity token field.",
                        "Regenerate", "Cancel"))
                    FigForgeLiveImport.RegenerateToken();
            }
        }

        /// <summary>Entry point used by the live-import HTTP receiver: discover
        /// the freshly-written bundle and build it with the window's settings.</summary>
        public bool LiveBuildPage(string projectJsonAssetPath)
        {
            RefreshManifests();
            FocusFontsFoldout();
            bool ok = BuildPageProject(projectJsonAssetPath);
            Repaint();
            return ok;
        }

        string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FigForgeImporterWindow).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;

            var packageJson = FindPackageJsonPath();
            if (!string.IsNullOrEmpty(packageJson))
            {
                try
                {
                    var meta = JsonConvert.DeserializeObject<PackageJsonMeta>(File.ReadAllText(packageJson));
                    if (meta != null && !string.IsNullOrEmpty(meta.version)) return meta.version;
                }
                catch { /* version label is cosmetic; keep the importer usable */ }
            }

            return "dev";
        }

        static string FindPackageJsonPath()
        {
            foreach (var guid in AssetDatabase.FindAssets("FigForge.Editor"))
            {
                var asmdefPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(asmdefPath) != "FigForge.Editor.asmdef") continue;
                var dir = Path.GetDirectoryName(asmdefPath);
                if (string.IsNullOrEmpty(dir)) continue;
                var candidate = Path.Combine(dir, "..", "package.json").Replace('\\', '/');
                if (File.Exists(candidate)) return candidate;
            }

            const string packagePath = "Packages/com.figforge.unity-importer/package.json";
            return File.Exists(packagePath) ? packagePath : null;
        }

        sealed class PackageJsonMeta { public string version; }

        void ManifestPicker()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                EditorGUILayout.LabelField("Import source", _styles.sectionTitle);

                // Whole-page project bundles (project.json) -> Forge Page.
                if (_projectPaths.Count > 0)
                {
                    _selectedProject = EditorGUILayout.Popup("Page bundle", _selectedProject,
                        _projectPaths.Select(p => Path.GetFileName(Path.GetDirectoryName(p)) + " / project.json").ToArray());
                    if (ForgePageButton($"Forge Page to {_backend} (all screens)",
                        _styles.pageForgeNormal, _styles.pageForgeHover, _styles.pageForgeActive, 38f))
                        BuildPageProject(_projectPaths[_selectedProject]);
                    EditorGUILayout.Space(4);
                    using (new EditorGUI.DisabledScope(_backend != UIBackend.uGUI))
                    {
                        if (ForgePageButton("Forge Page with Customizations to uGUI",
                            _styles.customForgeNormal, _styles.customForgeHover, _styles.customForgeActive, 38f))
                            BuildPageProject(_projectPaths[_selectedProject], includeUnityCustomizations: true);
                    }
                    EditorGUILayout.Space(5);
                }

                // Import straight from a FigForge export .zip - no manual unzip.
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Import a .zip...", _styles.button, GUILayout.Height(26)))
                        ImportZip();
                    if (_manifestPaths.Count > 0 &&
                        GUILayout.Button("Reveal folder", _styles.button, GUILayout.Width(110), GUILayout.Height(26)))
                        EditorUtility.RevealInFinder(_manifestPaths[_selected]);
                }

                if (_manifestPaths.Count == 0)
                {
                    EditorGUILayout.HelpBox("Import a FigForge export .zip above, drop an extracted folder under Assets/, or use the MCP export_unity tool, then press Rescan.", MessageType.Info);
                    return;
                }

                EditorGUI.BeginChangeCheck();
                _selected = EditorGUILayout.Popup("Manifest", _selected,
                    _manifestPaths.Select(p => Path.GetFileName(Path.GetDirectoryName(p)) + " / manifest.json").ToArray());
                if (EditorGUI.EndChangeCheck()) LoadSelected();

                if (_manifest != null)
                    EditorGUILayout.LabelField(
                        $"{_manifest.screen?.name}  |  {_manifest.elements.Count} elements  |  {_manifest.assets.Count} sprites  |  scale {_manifest.screen?.exportScale}x",
                        _styles.subtleLabel);
            }
        }

        void ImportZip()
        {
            var zip = EditorUtility.OpenFilePanel("Select a FigForge export (.zip)", "", "zip");
            if (string.IsNullOrEmpty(zip)) return;

            var dest = $"Assets/FigForge/Imports/{SafeName(Path.GetFileNameWithoutExtension(zip))}";

            // A prior import into the same folder is merged into, not replaced — same-named
            // files are overwritten but stale assets from the old import linger silently.
            // If the destination already holds an import, confirm before clearing it so the
            // result is exactly the zip's contents. Fresh/empty folder → no prompt.
            if (AssetDatabase.IsValidFolder(dest) &&
                Directory.EnumerateFileSystemEntries(
                    Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                        dest.Replace('/', Path.DirectorySeparatorChar))).Any())
            {
                if (!EditorUtility.DisplayDialog("Replace existing import?",
                    $"Folder \"{dest}\" already has an import — replace its contents?\n\nStale files from the previous import will be removed so the result matches this zip.",
                    "Replace", "Cancel"))
                    return;
                AssetDatabase.DeleteAsset(dest);
            }

            var manifestPath = ZipImporter.ExtractToAssets(zip, dest);
            if (string.IsNullOrEmpty(manifestPath)) return;

            RefreshManifests();
            var idx = _manifestPaths.IndexOf(manifestPath);
            if (idx >= 0) { _selected = idx; LoadSelected(); }
            FocusFontsFoldout();
        }

        void CanvasSection()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showCanvas = Foldout(_showCanvas, "Backend & Output");
                if (!_showCanvas) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    _backend = (UIBackend)EditorGUILayout.EnumPopup("UI backend", _backend);

                    if (_backend == UIBackend.UIToolkit)
                    {
                        _uitkOutFolder = EditorGUILayout.TextField("UXML/USS folder", _uitkOutFolder);
                        _uitkCreateDoc = EditorGUILayout.ToggleLeft("Create UIDocument + PanelSettings in scene", _uitkCreateDoc, _styles.toggle);
                        _connectedScene = EditorGUILayout.ToggleLeft("Connected scene (one UIDocument toggles pages)", _connectedScene, _styles.toggle);
                        EditorGUILayout.HelpBox("UI Toolkit emits a .uxml + .uss. Canonical layers become <Button> with a fge-ref-<name> USS class. Style that class once in your own stylesheet.", MessageType.None);
                    }
                    else
                    {
                        _output = (OutputMode)EditorGUILayout.EnumPopup("Output", _output);
                        _connectedScene = EditorGUILayout.ToggleLeft("Connected scene (FrameManager toggles pages)", _connectedScene, _styles.toggle);
                        _newCanvas = EditorGUILayout.ToggleLeft("Create new Canvas (off = add to existing)", _newCanvas, _styles.toggle);
                        if (!_newCanvas)
                        {
                            // Auto-fill the slot with the scene's first canvas when blank.
                            if (_existingCanvas == null) _existingCanvas = FirstSceneCanvas();
                            _existingCanvas = (Canvas)EditorGUILayout.ObjectField("Canvas", _existingCanvas, typeof(Canvas), true);
                            if (_existingCanvas == null)
                                EditorGUILayout.HelpBox("No Canvas in the scene yet - one will be created on Forge, then reused next time.", MessageType.None);
                        }
                        _scalePreset = (ScalePreset)EditorGUILayout.EnumPopup("Reference height", _scalePreset);
                        if (_scalePreset == ScalePreset.Custom)
                            _customRefHeight = EditorGUILayout.FloatField("Custom height", _customRefHeight);
                        _disableRaycasts = EditorGUILayout.ToggleLeft("Disable raycast targets on non-interactive graphics", _disableRaycasts, _styles.toggle);
                        _componentsOnlyAccessors = EditorGUILayout.ToggleLeft(new GUIContent("Accessors for components only (skip labels & images)", "On: only canonical controls (buttons, toggles, inputs, ...) get C# accessors. Off: labels and images do too (legacy behavior).\nEither way, prefix a layer name with [s] to force-include that one element; the [s] is dropped from the GameObject and variable name."), _componentsOnlyAccessors, _styles.toggle);
                        _includeGroupsInAccessors = EditorGUILayout.ToggleLeft("Generate C# accessors for Figma groups/frames", _includeGroupsInAccessors, _styles.toggle);
                        int warmUpBatchSize = Mathf.Clamp(EditorGUILayout.DelayedIntField("Import warmup batch size", _warmUpBatchSize), 1, 8192);
                        if (warmUpBatchSize != _warmUpBatchSize)
                        {
                            _warmUpBatchSize = warmUpBatchSize;
                            EditorPrefs.SetInt(PrefWarmUpBatchSize, _warmUpBatchSize);
                        }
                        if (_output != OutputMode.Scene)
                            _prefabFolder = EditorGUILayout.TextField("Prefab folder", _prefabFolder);
                    }
                }
            }
        }

        void FontSection()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showFonts = Foldout(_showFonts, $"Fonts ({_manifest.fonts.Count})");
                if (!_showFonts) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    if (_projectFonts.Count == 0)
                        EditorGUILayout.HelpBox("No TMP_FontAsset found. Create font assets (Window > TextMeshPro > Font Asset Creator).", MessageType.Warning);
                    foreach (var f in _manifest.fonts)
                        foreach (var s in f.styles)
                        {
                            var key = $"{f.family}|{s}";
                            _fontMap.TryGetValue(key, out var cur);
                            var next = (TMP_FontAsset)EditorGUILayout.ObjectField(key, cur, typeof(TMP_FontAsset), false);
                            _fontMap[key] = next;
                        }
                }
            }
        }

        void CanonicalSection()
        {
            int refCount = _manifest.canonicalRefs?.Count ?? 0;
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showCanonical = Foldout(_showCanonical, $"Canonical elements ({refCount})");
                if (!_showCanonical) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    _canonicalLibrary = (CanonicalLibrary)EditorGUILayout.ObjectField("Library", _canonicalLibrary, typeof(CanonicalLibrary), false);
                    if (refCount > 0)
                    {
                        EditorGUILayout.LabelField("Referenced by this design:", _styles.sectionTitle);
                        foreach (var r in _manifest.canonicalRefs)
                        {
                            bool resolved = _canonicalLibrary != null && _canonicalLibrary.Resolve("button", r) != null;
                            EditorGUILayout.LabelField($"   {(resolved ? "ok" : "missing")}  {r}", _styles.subtleLabel);
                        }
                        if (_canonicalLibrary == null)
                            EditorGUILayout.HelpBox("Assign a Canonical Library (Create > FigForge > Canonical Library) to instantiate real button prefabs; otherwise placeholders are used.", MessageType.Info);
                    }
                    else EditorGUILayout.LabelField("No canonical references (name layers Btn_<instance>_<ref>).", _styles.subtleLabel);
                }
            }
        }

        void TextureSection()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showTextures = Foldout(_showTextures, "Textures");
                if (!_showTextures) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    _spriteFolder = EditorGUILayout.TextField("Sprite folder", _spriteFolder);
                    _tex.autoMaxSize = EditorGUILayout.ToggleLeft("Auto max size", _tex.autoMaxSize, _styles.toggle);
                    if (!_tex.autoMaxSize) _tex.maxSize = EditorGUILayout.IntField("Max size", _tex.maxSize);
                    _tex.compression = (TextureImporterCompression)EditorGUILayout.EnumPopup("Compression", _tex.compression);
                }
            }
        }

        void AtlasSection()
        {
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                _showAtlas = Foldout(_showAtlas, "Sprite Atlas");
                if (!_showAtlas) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    _atlas.create = EditorGUILayout.ToggleLeft("Create sprite atlas", _atlas.create, _styles.toggle);
                    if (_atlas.create)
                    {
                        _atlas.padding = EditorGUILayout.IntField("Padding", _atlas.padding);
                        _atlas.allowRotation = EditorGUILayout.ToggleLeft("Allow rotation", _atlas.allowRotation, _styles.toggle);
                        _atlas.includeInBuild = EditorGUILayout.ToggleLeft("Include in build", _atlas.includeInBuild, _styles.toggle);
                    }
                }
            }
        }

        void BuildBar()
        {
            using (new EditorGUILayout.VerticalScope(_styles.buildCard))
                if (GUILayout.Button($"Forge \"{_manifest.screen?.name}\"", _styles.primaryButton, GUILayout.Height(34)))
                    Build();
        }

        void LogSection()
        {
            if (_log.Count == 0) return;
            using (new EditorGUILayout.VerticalScope(_styles.card))
            {
                EditorGUILayout.LabelField("Forge log", _styles.sectionTitle);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120));
                foreach (var (msg, kind) in _log) EditorGUILayout.HelpBox(msg, kind);
                EditorGUILayout.EndScrollView();
            }
        }

        // -----------------------------------------------------------------------
        void Build()
        {
            _log.Clear();
            FontAutoImporter.ClearCache();
            _editorColumns = EditorColumnsPref;
            if (_manifest?.screen == null) { Log("manifest has no screen", MessageType.Error); return; }
            ApplyManifestSettings(_manifest);
            if (_backend == UIBackend.UIToolkit) { BuildUITK(); return; }

            try
            {
                EditorUtility.DisplayProgressBar("FigForge", "Importing textures…", 0.15f);
                var sourceDir = Path.GetDirectoryName(_manifestPaths[_selected]);
                var screenFolder = $"{_spriteFolder}/{SafeName(_manifest.screen.name)}";
                var sprites = TextureImportHelper.Import(_manifest, sourceDir, screenFolder, _tex);
                Log($"imported {sprites.Count} sprite(s)", MessageType.Info);

                if (_atlas.create) SpriteAtlasHelper.Build(_manifest.screen.name, screenFolder, _atlas);

                EditorUtility.DisplayProgressBar("FigForge", "Forging hierarchy…", 0.55f);
                var canvas = ResolveCanvas(out bool canvasCreated);
                Transform parent = canvas.transform;

                // Treat the lone manifest as a one-screen bundle so it runs through the SAME
                // stamp-based reuse as Forge Page: re-Forging the same screen patches the frame in
                // place (no duplicate) instead of building a second copy, and the manual-control
                // guard runs.
                //
                // If an earlier Forge already imported this screen — a page bundle (stamped with the
                // Figma project name) OR a prior single Forge — adopt ITS import scope (project /
                // section / role) so importKey + manifestHash line up with how it was built and
                // ReuseOrBuildScreen patches that exact frame. Without this, a page-built frame
                // wouldn't match the single scope and we'd build a duplicate beside it. With no
                // prior frame, fall back to a fresh per-screen single scope so stale-removal can
                // never reach a sibling single import.
                var scope = ImportScope(parent);
                var prior = FindImportedByScreen(scope, _manifest.screen.name);
                var ps = prior != null
                    ? new ProjectScreen { name = _manifest.screen.name, manifest = "", section = prior.section, role = prior.role }
                    : new ProjectScreen { name = _manifest.screen.name, manifest = "", section = "", role = "screen" };
                var ls = new LoadedScreen
                {
                    m = _manifest, srcDir = sourceDir, ps = ps,
                    importKey = ImportKey(ps, _manifest),
                    manifestHash = ManifestHash(_manifest, ps, _includeGroupsInAccessors, _componentsOnlyAccessors),
                };
                string projectName = prior != null ? prior.projectName : "single/" + ls.importKey;

                if (!canvasCreated && !ConfirmPageForge(new List<LoadedScreen> { ls }, scope, projectName, false))
                { Log("Forge cancelled — manual controls preserved", MessageType.Info); return; }

                FrameManager mgr = null;
                if (_connectedScene)
                {
                    mgr = canvas.GetComponent<FrameManager>() ?? canvas.gameObject.AddComponent<FrameManager>();
                    mgr.editorColumns = _editorColumns;
                }

                // Reuse the existing frame when the design is unchanged; otherwise rebuild it in
                // place (the old one is replaced, not duplicated). builtCtx is null on reuse — then
                // GenerateAndWireFrame re-wires from the registry instead of doing a fresh build.
                var page = ReuseOrBuildScreen(ls, projectName, parent, sprites, false, out var ctx);
                if (page == null) { Log("build produced no page", MessageType.Error); return; }

                var screen = page.GetComponent<FigForgeFrame>() ?? page.AddComponent<FigForgeFrame>();
                if (mgr != null)
                {
                    mgr.Register(screen);
                    Log($"registered page '{screen.ScreenKey}' on FrameManager", MessageType.Info);
                }

                // Generate + wire the strongly-typed accessor layer (Frames.<Frame> + the
                // FigForgeFrame subclass). Compile is deferred a tick (a mid-import reload would
                // abort the build); the post-compile hook swaps in the subclass + wires its refs.
                GenerateAndWireFrame(page, _manifest, ctx, screen, "");
                if (mgr != null)
                    FigForgeFrameSceneTools.ArrangeRootFrames(canvas.GetComponent<FigForgeCanvasHelper>(), false);

                if (_output != OutputMode.Scene) SavePrefab(page);
                if (_output == OutputMode.Prefab) DestroyImmediate(page);

                // Creation-undo covers only what THIS import created: the canvas when we
                // made it (its children — the page — go with it), else the page alone
                // (unless Prefab-only output already destroyed the scene instance).
                // ReuseOrBuildScreen registers a NEWLY-built frame for creation-undo (a reused
                // frame deliberately survives undo); here we only add the canvas when we made it.
                if (canvasCreated)
                    Undo.RegisterCreatedObjectUndo(canvas.gameObject, "FigForge Build");
                Undo.SetCurrentGroupName("FigForge Build");
                EditorUtility.SetDirty(canvas);
                if (mgr != null) EditorUtility.SetDirty(mgr);
                if (_output != OutputMode.Prefab)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                        UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

                Log($"built '{_manifest.screen.name}' ✓", MessageType.Info);
            }
            catch (System.Exception e)
            {
                // Also surface in the Console: the in-window log is invisible when the
                // importer isn't focused (the per-screen inner catches already LogError).
                Debug.LogError($"[FigForge] build failed: {e}");
                Log($"build failed: {e.Message}\n{e.StackTrace}", MessageType.Error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
        }

        // ---- Whole-page (project bundle) build ---------------------------------
        struct LoadedScreen { public Manifest m; public string srcDir; public ProjectScreen ps; public string importKey; public string manifestHash; }

        BuildContext MakeContext(Manifest m, Dictionary<string, Sprite> sprites)
        {
            ApplyManifestSettings(m);
            float fh = m.screen != null && m.screen.figmaSize != null ? m.screen.figmaSize.h : 1080f;
            float sf = fh > 0 ? ReferenceHeight(fh) / fh : 1f;
            return new BuildContext
            {
                scaleFactor = sf, sprites = sprites, canonical = _canonicalLibrary, disableRaycasts = _disableRaycasts,
                warmUpBatchSize = _warmUpBatchSize,
                exportScale = m.screen != null ? m.screen.exportScale : 1f,
                vanilla = m.vanilla,
                resolveFont = ResolveFontAsset,
                log = mm => Log(mm, MessageType.Warning),
            };
        }

        static Transform FindContentSlot(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name.ToLowerInvariant();
                if (n == "content" || n == "content_slot") return t;
            }
            return null;
        }

        static string StableHash(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < (text ?? "").Length; i++)
                {
                    hash ^= text[i];
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16");
            }
        }

        static string ImportKey(ProjectScreen ps, Manifest m)
        {
            string role = string.IsNullOrEmpty(ps.role) ? "screen" : ps.role;
            string section = ps.section ?? "";
            string name = m.screen != null && !string.IsNullOrEmpty(m.screen.name)
                ? m.screen.name
                : (!string.IsNullOrEmpty(ps.name) ? ps.name : "screen");
            return $"{role}|{section}|{name}";
        }

        static string ManifestHash(Manifest m, ProjectScreen ps, bool includeGroupsInAccessors, bool componentsOnlyAccessors)
        {
            string exportedAt = m.exportedAt;
            m.exportedAt = "";
            try
            {
                string role = string.IsNullOrEmpty(ps.role) ? "screen" : ps.role;
                string section = ps.section ?? "";
                // Fold the importer's canonical build version into the hash so a
                // Unity-side generation change (e.g. raycastable control backgrounds)
                // busts the screen-level reuse cache and forces a rebuild even when
                // the Figma design itself is unchanged.
                return StableHash(JsonConvert.SerializeObject(m) + "\nrole=" + role + "\nsection=" + section
                    + "\nbuild=" + HierarchyBuilder.CanonicalSchema
                    + "\nincludeGroupsInAccessors=" + includeGroupsInAccessors
                    + "\ncomponentsOnlyAccessors=" + componentsOnlyAccessors);
            }
            finally
            {
                m.exportedAt = exportedAt;
            }
        }

        static FigForgeImportStamp FindImported(Transform scope, string projectName, string importKey)
        {
            if (scope == null) return null;
            foreach (var stamp in scope.GetComponentsInChildren<FigForgeImportStamp>(true))
                if (stamp != null && stamp.projectName == projectName && stamp.importKey == importKey)
                    return stamp;
            return null;
        }

        // Find a frame already imported for this screen by NAME, regardless of which Forge path
        // stamped it (page bundles use the Figma project name; single Forge a "single/..." scope).
        // Lets single Forge adopt a page-built frame and patch it in place instead of duplicating.
        static FigForgeImportStamp FindImportedByScreen(Transform scope, string screenName)
        {
            if (scope == null || string.IsNullOrEmpty(screenName)) return null;
            foreach (var stamp in scope.GetComponentsInChildren<FigForgeImportStamp>(true))
                if (stamp != null && stamp.screenName == screenName) return stamp;
            return null;
        }

        static Transform ImportScope(Transform parent)
        {
            var canvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            return canvas != null ? canvas.transform : (parent != null ? parent.root : null);
        }

        static void StampImported(GameObject go, string projectName, LoadedScreen screen)
        {
            if (go == null) return;
            var stamp = go.GetComponent<FigForgeImportStamp>() ?? go.AddComponent<FigForgeImportStamp>();
            stamp.projectName = projectName;
            stamp.screenName = screen.m.screen != null ? screen.m.screen.name : screen.ps.name;
            stamp.role = string.IsNullOrEmpty(screen.ps.role) ? "screen" : screen.ps.role;
            stamp.section = screen.ps.section ?? "";
            stamp.importKey = screen.importKey;
            stamp.manifestHash = screen.manifestHash;
        }

        void RemoveStaleImported(Transform scope, string projectName, HashSet<string> expectedKeys)
        {
            if (scope == null) return;
            var stamps = scope.GetComponentsInChildren<FigForgeImportStamp>(true).ToArray();
            foreach (var stamp in stamps)
            {
                if (stamp == null || stamp.projectName != projectName) continue;
                if (expectedKeys.Contains(stamp.importKey)) continue;
                Log($"removed stale screen '{stamp.screenName}'", MessageType.Info);
                DestroyImmediate(stamp.gameObject);
            }
        }

        GameObject ReuseOrBuildScreen(LoadedScreen screen, string projectName, Transform parent, Dictionary<string, Sprite> sprites, bool stretch, out BuildContext builtCtx)
        {
            builtCtx = null;
            var existing = FindImported(ImportScope(parent), projectName, screen.importKey);
            if (existing != null && existing.manifestHash == screen.manifestHash)
            {
                existing.transform.SetParent(parent, false);
                if (stretch) StretchToParent(existing.gameObject);
                EnsureImportMarkers(existing.gameObject); // migrate a legacy (pre-marker) frame
                Log($"reused unchanged '{screen.m.screen.name}'", MessageType.Info);
                return existing.gameObject; // reused → its generated accessors already exist
            }
            if (existing != null)
            {
                Log($"patched changed '{screen.m.screen.name}'", MessageType.Info);
                DestroyImmediate(existing.gameObject);
            }

            var ctx = MakeContext(screen.m, sprites);
            var page = HierarchyBuilder.BuildPage(screen.m, parent, ctx);
            if (page == null) return null;
            Undo.RegisterCreatedObjectUndo(page, "FigForge Build Page"); // newly built only — reused screens above must survive undo
            builtCtx = ctx; // expose the build context so the caller can generate + wire accessors
            if (stretch) StretchToParent(page);
            StampImported(page, projectName, screen);
            EnsureImportMarkers(page); // fresh build: everything here is imported
            return page;
        }

        // Generate the strongly-typed accessor layer for a freshly-built frame and register
        // each member by its identifier so the post-compile hook can wire the subclass.
        // Reused unchanged frames still get rewired from their FigForgeScreen registry; this
        // repairs stale/null serialized refs without forcing a rebuild.
        void GenerateAndWireFrame(GameObject page, Manifest m, BuildContext ctx, FigForgeFrame frame, string section, bool includeUnityCustomizations = false, bool isOverlay = false)
        {
            if (page == null || m == null) return;
            var model = FrameCodeGenDriver.BuildModel(m, section ?? "", _includeGroupsInAccessors, _componentsOnlyAccessors, isOverlay);
            var targets = new Dictionary<string, GameObject>();
            ResolveFrameMemberTargets(model, ctx, page.GetComponent<FigForgeScreen>(), targets);
            int customCount = includeUnityCustomizations ? AddUnityCustomizationMembers(page, model, targets) : 0;
            FrameCodeGenDriver.WriteFiles(model);
            // Overlays have no generated frame class to upgrade to (see WriteFiles) — leaving
            // generatedType empty keeps FrameCodeGenWire from trying to swap in a missing type.
            if (frame != null && !isOverlay) frame.generatedType = FrameCodeGen.GeneratedNamespace + "." + model.className;
            var reg = page.GetComponent<FigForgeScreen>();
            if (reg == null && model.members != null && model.members.Count > 0)
                reg = page.AddComponent<FigForgeScreen>();
            if (reg != null)
            {
                foreach (var mem in model.members)
                {
                    GameObject memGo = null;
                    if (!targets.TryGetValue(mem.sourceName, out memGo) || memGo == null)
                        memGo = reg.Get(mem.Key) ?? reg.Get(mem.sourceName);
                    if (memGo != null)
                    {
                        if (mem.isGroup)
                        {
                            var frameElement = memGo.GetComponent<FigForgeFrameElement>() ?? memGo.AddComponent<FigForgeFrameElement>();
                            frameElement.ConfigureType(mem.sourceType);
                            // Record the generated group component so the post-compile hook swaps
                            // this placeholder for it and wires the group's child refs.
                            if (!string.IsNullOrEmpty(mem.groupTypeName))
                                frameElement.generatedType = FrameCodeGen.GeneratedNamespace + "." + mem.groupTypeName;
                            EditorUtility.SetDirty(frameElement);
                        }
                        reg.Register(mem.Key, memGo);
                    }
                }
                EditorUtility.SetDirty(reg);
            }
            // Overlay layer: tag each dialog with the same key its generated Dialogs.<Name>
            // accessor resolves by, so FigForgeDialogHost can locate it globally.
            if (isOverlay && model.members != null)
            {
                foreach (var mem in model.members)
                {
                    if (mem.csharpType != "FigForgeModal") continue;
                    if (!targets.TryGetValue(mem.sourceName, out var memGo) || memGo == null)
                        memGo = reg != null ? (reg.Get(mem.Key) ?? reg.Get(mem.sourceName)) : null;
                    var modal = memGo != null ? memGo.GetComponent<FigForgeModal>() : null;
                    if (modal != null) { modal.dialogKey = mem.registryKey; EditorUtility.SetDirty(modal); }
                }
            }
            if (frame != null && frame.GetType() != typeof(FigForgeFrame))
            {
                frame.__WireFrame(reg);
                EditorUtility.SetDirty(frame);
            }
            Log($"{(ctx != null ? "generated" : "refreshed")} accessors for frame '{model.className}' ({model.members.Count} member(s))", MessageType.Info);
            if (customCount > 0)
                Log($"included {customCount} Unity customization accessor(s) for frame '{model.className}'", MessageType.Info);
            // When the generated code is unchanged, no compile follows this import and the
            // [DidReloadScripts] upgrade never fires — the rebuilt page would stay on the
            // base FigForgeFrame (Frames.X resolves null). Upgrade now; if a compile IS
            // pending, the reload hook covers it instead (idempotent).
            FrameCodeGenWire.RequestUpgrade();
        }

        static void ResolveFrameMemberTargets(FrameModel model, BuildContext ctx, FigForgeScreen reg, Dictionary<string, GameObject> targets)
        {
            if (model.members == null) return;
            foreach (var mem in model.members)
            {
                GameObject go = null;
                if (ctx != null)
                    ctx.byElementId.TryGetValue(mem.sourceName, out go);
                if (go == null && reg != null)
                    go = reg.Get(mem.Key) ?? reg.Get(mem.sourceName);
                if (go != null && !string.IsNullOrEmpty(mem.sourceName))
                    targets[mem.sourceName] = go;
            }
        }

        int AddUnityCustomizationMembers(GameObject page, FrameModel model, Dictionary<string, GameObject> targets)
        {
            if (page == null || model.members == null) return 0;

            var existingTargets = new HashSet<GameObject>(targets.Values.Where(go => go != null));
            var existingControlRoots = new List<Transform>();
            foreach (var go in existingTargets)
                if (go != null && IsAccessorControlRoot(go))
                    existingControlRoots.Add(go.transform);

            var taken = new HashSet<string>(model.members.Select(m => m.Key));
            var customControlRoots = new List<Transform>();
            int added = 0;

            foreach (var tr in page.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == page.transform) continue;
                var go = tr.gameObject;
                if (existingTargets.Contains(go)) continue;
                if (IsGeneratedInfrastructure(go)) continue;
                if (IsDescendantOfAny(tr, existingControlRoots)) continue;
                if (IsDescendantOfAny(tr, customControlRoots)) continue;
                if (!TryGetUnityCustomizationType(go, out var csharpType, out var isControlRoot)) continue;

                string identifier = UniqueIdentifier(IdentifierUtil.ToIdentifier(go.name), taken);
                model.members.Add(new FrameMember
                {
                    identifier = identifier,
                    csharpType = csharpType,
                    sourceName = identifier.TrimStart('@'),
                    sourceType = "UNITY",
                    parentId = "",
                    scopeParentId = null,
                    exposeOnFrame = true,
                    isGroup = false,
                });
                targets[identifier.TrimStart('@')] = go;
                if (isControlRoot) customControlRoots.Add(tr);
                added++;
            }
            return added;
        }

        static bool IsGeneratedInfrastructure(GameObject go)
        {
            if (go == null) return true;
            return go.GetComponent<FigForgeFrame>() != null
                || go.GetComponent<FigForgeScreen>() != null
                || go.GetComponent<FigForgeImportStamp>() != null
                || go.GetComponent<FigForgePageCompositor>() != null;
        }

        static bool IsAccessorControlRoot(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<Selectable>() != null
                || go.GetComponent<FigForgeProgress>() != null
                || go.GetComponent<FigForgeStepper>() != null
                || go.GetComponent<FigForgeList>() != null
                || go.GetComponent<FigForgeTable>() != null
                || go.GetComponent<FigForgeModal>() != null
                || go.GetComponent<FigForgeToastHost>() != null;
        }

        static bool TryGetUnityCustomizationType(GameObject go, out string csharpType, out bool isControlRoot)
        {
            csharpType = null;
            isControlRoot = false;
            if (go == null) return false;

            if (go.GetComponent<FigForgeButton>() != null) { csharpType = "FigForgeButton"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeSwitch>() != null) { csharpType = "FigForgeSwitch"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeToggle>() != null) { csharpType = "FigForgeToggle"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeDropdown>() != null) { csharpType = "FigForgeDropdown"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeInputField>() != null) { csharpType = "FigForgeInputField"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeStepper>() != null) { csharpType = "FigForgeStepper"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeSlider>() != null) { csharpType = "FigForgeSlider"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeProgress>() != null) { csharpType = "FigForgeProgress"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeList>() != null) { csharpType = "FigForgeList"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeTable>() != null) { csharpType = "FigForgeTable"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeModal>() != null) { csharpType = "FigForgeModal"; isControlRoot = true; return true; }
            if (go.GetComponent<FigForgeToastHost>() != null) { csharpType = "FigForgeToastHost"; isControlRoot = true; return true; }

            if (go.GetComponent<Button>() != null) { csharpType = "Button"; isControlRoot = true; return true; }
            if (go.GetComponent<Toggle>() != null) { csharpType = "Toggle"; isControlRoot = true; return true; }
            if (go.GetComponent<TMP_Dropdown>() != null) { csharpType = "TMP_Dropdown"; isControlRoot = true; return true; }
            if (go.GetComponent<TMP_InputField>() != null) { csharpType = "TMP_InputField"; isControlRoot = true; return true; }
            if (go.GetComponent<Slider>() != null) { csharpType = "Slider"; isControlRoot = true; return true; }
            if (go.GetComponent<Scrollbar>() != null) { csharpType = "Scrollbar"; isControlRoot = true; return true; }

            return false;
        }

        // Stamp every imported control with a FigForgeImportedControl marker, so the manual-control
        // check can tell imported from hand-added with certainty — the name-keyed registry can't,
        // since repeated Figma names collapse to one entry (real imported controls then look manual).
        // Idempotent + migration-safe: stamps only when the frame carries NO markers yet — a fresh
        // build (all imported) or a legacy frame being migrated (assumed all-imported, true unless
        // the developer already hand-added a control). A frame already marked is left alone, so
        // controls added LATER stay unmarked and are correctly flagged.
        static void EnsureImportMarkers(GameObject frameRoot)
        {
            if (frameRoot == null) return;
            if (frameRoot.GetComponentInChildren<FigForgeImportedControl>(true) != null) return; // already marked
            foreach (var tr in frameRoot.GetComponentsInChildren<Transform>(true))
            {
                var go = tr.gameObject;
                if (IsGeneratedInfrastructure(go)) continue;
                if (TryGetUnityCustomizationType(go, out _, out _))
                    go.AddComponent<FigForgeImportedControl>();
            }
        }

        // Controls a developer added to a built frame BY HAND — those WITHOUT the importer's
        // FigForgeImportedControl marker. A destructive re-Forge rebuilds the frame, so these don't
        // survive it; we surface them first. A frame with NO markers at all predates this (or hasn't
        // been re-Forged since) — we can't judge it, so report nothing rather than false-flagging
        // every imported control (EnsureImportMarkers migrates it on this same Forge).
        static List<GameObject> CollectUserAddedControls(GameObject frameRoot)
        {
            var result = new List<GameObject>();
            if (frameRoot == null) return result;
            if (frameRoot.GetComponentInChildren<FigForgeImportedControl>(true) == null) return result;

            var userRoots = new List<Transform>();
            foreach (var tr in frameRoot.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == frameRoot.transform) continue;
                var go = tr.gameObject;
                if (IsGeneratedInfrastructure(go)) continue;                      // FigForge plumbing
                if (go.GetComponent<FigForgeImportedControl>() != null) continue; // imported → not manual
                if (IsDescendantOfAny(tr, userRoots)) continue;                   // already counted via its root
                if (!TryGetUnityCustomizationType(go, out _, out var isRoot)) continue;
                result.Add(go);
                if (isRoot) userRoots.Add(tr);
            }
            return result;
        }

        // One consolidated notice covering hand-added controls a Forge touches, split by fate:
        //   • lost — on a frame being rebuilt/removed → won't survive (a real warning).
        //   • kept — on a reused (unchanged) frame → preserved, but NOT generated as an accessor
        //     unless you Forge with Customizations (informational — this is the case people miss:
        //     the frame is reused, not destroyed, so the control is still there, just not in code).
        // Returns true to proceed, false to cancel the whole Forge.
        static bool ReportManualControls(List<(string frame, List<GameObject> controls)> lost,
                                         List<(string frame, List<GameObject> controls)> kept)
        {
            int nLost = 0; foreach (var a in lost) nLost += a.controls.Count;
            int nKept = 0; foreach (var a in kept) nKept += a.controls.Count;
            if (nLost == 0 && nKept == 0) return true;

            var lines = new List<string>();
            if (nLost > 0)
            {
                lines.Add("⚠  LOST — on frames being rebuilt/removed, these won't survive:");
                AppendControls(lines, lost);
            }
            if (nKept > 0)
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add("KEPT — the unchanged frame is reused (not destroyed), so these survive, but");
                lines.Add("they are NOT generated as code accessors. Use “Forge Page with Customizations”");
                lines.Add("to include them:");
                AppendControls(lines, kept);
            }
            string body = string.Join("\n", lines);
            Debug.LogWarning($"[FigForge] manual controls detected on Forge:\n{body}");

            string head = nLost > 0
                ? $"{nLost} manual control(s) will be LOST" + (nKept > 0 ? $" and {nKept} kept-but-unexposed." : ".")
                : $"{nKept} manual control(s) are kept, but not exposed as code accessors.";
            string proceed = nLost > 0 ? "Forge anyway" : "Continue";
            string tail = nLost > 0 ? "\n\nForge anyway? (Cancel leaves the scene untouched.)" : "";
            return EditorUtility.DisplayDialog("FigForge — manual controls", head + "\n\n" + body + tail, proceed, "Cancel");
        }

        static void AppendControls(List<string> lines, List<(string frame, List<GameObject> controls)> groups)
        {
            foreach (var a in groups)
            {
                lines.Add("• " + a.frame + ":");
                int shown = 0;
                foreach (var go in a.controls)
                {
                    if (shown++ == 10) { lines.Add("      … and " + (a.controls.Count - 10) + " more"); break; }
                    lines.Add("      – " + go.name);
                }
            }
        }

        // Pre-Forge guard for the whole-page build. Splits hand-added controls by fate:
        //   • a changed frame is rebuilt → its controls are LOST.
        //   • a dropped frame is removed → its controls are LOST.
        //   • an unchanged frame is reused → its controls are KEPT (but not exposed as accessors
        //     unless Forge-with-Customizations is on, which turns them into accessors).
        static bool ConfirmPageForge(List<LoadedScreen> loaded, Transform scope, string projectName, bool includeCustomizations)
        {
            if (scope == null) return true;
            var lost = new List<(string, List<GameObject>)>();
            var kept = new List<(string, List<GameObject>)>();
            var expected = new HashSet<string>(loaded.Select(s => s.importKey));

            foreach (var s in loaded)
            {
                var existing = FindImported(scope, projectName, s.importKey);
                if (existing == null) continue;
                var c = CollectUserAddedControls(existing.gameObject);
                if (c.Count == 0) continue;
                if (existing.manifestHash == s.manifestHash) kept.Add((existing.screenName, c)); // reused → preserved
                else lost.Add((existing.screenName, c));                                          // rebuilt → lost
            }
            foreach (var stamp in scope.GetComponentsInChildren<FigForgeImportStamp>(true))
            {
                if (stamp == null || stamp.projectName != projectName || expected.Contains(stamp.importKey)) continue;
                var c = CollectUserAddedControls(stamp.gameObject);
                if (c.Count > 0) lost.Add((stamp.screenName + " (removed)", c));
            }

            // Forge-with-Customizations turns kept controls INTO accessors, so they need no notice.
            if (includeCustomizations) kept.Clear();
            return ReportManualControls(lost, kept);
        }

        static bool IsDescendantOfAny(Transform tr, List<Transform> roots)
        {
            if (tr == null || roots == null || roots.Count == 0) return false;
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root != null && tr != root && tr.IsChildOf(root)) return true;
            }
            return false;
        }

        static string UniqueIdentifier(string desired, HashSet<string> taken)
        {
            if (string.IsNullOrEmpty(desired)) desired = "_";
            bool escaped = desired.StartsWith("@");
            string stem = desired.TrimStart('@');
            string candidate = desired;
            string key = stem;
            int suffix = 2;
            while (taken.Contains(key))
            {
                candidate = (escaped ? "@" : "") + stem + suffix++;
                key = candidate.TrimStart('@');
            }
            taken.Add(key);
            return candidate;
        }

        static void StretchToParent(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // Lay a root frame out in an authoring grid (top-left anchored) so the frames
        // don't overlap in the editor. Runtime FrameManager.Show still fills one frame.
        void SpreadFrame(GameObject page, int index, Manifest m, int columns)
        {
            var rt = page.GetComponent<RectTransform>();
            if (rt == null) return;
            // Size to the Figma frame in the CANVAS coordinate space (figmaSize * scaleFactor),
            // matching the CanvasScaler reference. NOT screen.referenceResolution — that is
            // figmaSize * EXPORT scale (the asset pixel resolution), which at 2x makes the
            // frame double-size with content stranded in the top-left quarter.
            float fw = m.screen != null && m.screen.figmaSize != null ? m.screen.figmaSize.w : 1920f;
            float fh = m.screen != null && m.screen.figmaSize != null ? m.screen.figmaSize.h : 1080f;
            float sf = fh > 0f ? ReferenceHeight(fh) / fh : 1f;
            float w = fw * sf;
            float h = fh * sf;
            const float gap = 80f;
            columns = Mathf.Max(1, columns);
            int col = index % columns;
            int row = index / columns;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); // canvas top-left
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(col * (w + gap), -row * (h + gap));
            EditorUtility.SetDirty(rt);
            EditorUtility.SetDirty(page);
        }

        void WarmUpImportedFrames(FrameManager mgr)
        {
            if (mgr == null) return;
            var warmed = new HashSet<GameObject>();
            for (int i = 0; i < mgr.screens.Count; i++)
            {
                var frame = mgr.screens[i];
                if (frame == null || !warmed.Add(frame.gameObject)) continue;
                HierarchyBuilder.WarmUpGeneratedGraphics(frame.gameObject, _warmUpBatchSize);
            }
            if (mgr.shell != null && warmed.Add(mgr.shell))
                HierarchyBuilder.WarmUpGeneratedGraphics(mgr.shell, _warmUpBatchSize);
            Canvas.ForceUpdateCanvases();
            SceneView.RepaintAll();
        }

        bool BuildPageProject(string projectPath, bool includeUnityCustomizations = false)
        {
            _log.Clear();
            FontAutoImporter.ClearCache();
            _editorColumns = EditorColumnsPref;
            var proj = ManifestParser.LoadProject(projectPath);
            if (proj == null || proj.screens.Count == 0) { Log("project.json is empty or invalid", MessageType.Error); return false; }
            var baseDir = Path.GetDirectoryName(projectPath).Replace('\\', '/');

            var loaded = new List<LoadedScreen>();
            // baseDir is trusted (the folder we were pointed at); ps.manifest comes from
            // project.json and is untrusted. Resolve to a full path and require it to stay
            // under baseDir so a crafted "../../.." value can't read an arbitrary file.
            var baseDirFull = Path.GetFullPath(baseDir);
            foreach (var ps in proj.screens)
            {
                var mp = $"{baseDir}/{ps.manifest}".Replace('\\', '/');
                var mpFull = Path.GetFullPath(mp);
                var fence = baseDirFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? baseDirFull : baseDirFull + Path.DirectorySeparatorChar;
                if (!mpFull.Equals(baseDirFull, System.StringComparison.Ordinal) &&
                    !mpFull.StartsWith(fence, System.StringComparison.Ordinal))
                {
                    Log($"skip '{ps.name}': manifest path escapes bundle folder — rejected ({ps.manifest})", MessageType.Warning);
                    continue;
                }
                var m = ManifestParser.Load(mp);
                if (m == null) { Log($"skip '{ps.name}': manifest missing or rejected — see Console ({mp})", MessageType.Warning); continue; }
                loaded.Add(new LoadedScreen
                {
                    m = m,
                    srcDir = Path.GetDirectoryName(mp),
                    ps = ps,
                    importKey = ImportKey(ps, m),
                    manifestHash = ManifestHash(m, ps, _includeGroupsInAccessors, _componentsOnlyAccessors),
                });
            }
            if (loaded.Count == 0) { Log("no buildable screens in bundle", MessageType.Error); return false; }
            _manifest = loaded[0].m; // for ResolveCanvas / PanelSettings / header

            try
            {
                if (_backend == UIBackend.UIToolkit) { BuildPageUITK(proj, loaded); return true; }

                var canvas = ResolveCanvas(out bool canvasCreated);
                if (!canvasCreated && !ConfirmPageForge(loaded, ImportScope(canvas.transform), proj.name, includeUnityCustomizations))
                { Log("Forge cancelled — manual controls preserved", MessageType.Info); return false; }
                var mgr = canvas.GetComponent<FrameManager>() ?? canvas.gameObject.AddComponent<FrameManager>();
                mgr.editorColumns = _editorColumns;
                mgr.screens.Clear();
                mgr.shell = null;
                RemoveStaleImported(canvas.transform, proj.name, new HashSet<string>(loaded.Select(s => s.importKey)));

                // Declare the full set of frame class names this import will generate so every
                // per-frame WriteFiles shares one reserved set (sections resolve to a single
                // nested class) and EndBatch can sweep Frames.<Old>.g.cs left by a Figma rename/
                // remove. Overlays have no frame class (see WriteFiles), so they're excluded —
                // mirrors BuildModel's className = ToIdentifier(displayName ?? name).
                var expectedFrameClasses = new HashSet<string>();
                foreach (var s in loaded)
                {
                    if (FrameRoles.IsOverlay(s.ps.role)) continue;
                    var sc = s.m != null ? s.m.screen : null;
                    if (sc == null) continue;
                    expectedFrameClasses.Add(IdentifierUtil.ToIdentifier(
                        !string.IsNullOrEmpty(sc.displayName) ? sc.displayName : sc.name));
                }
                FrameCodeGenDriver.BeginBatch(expectedFrameClasses);

                // 1. Persistent Shells (optional) — one per Section. Screens in the
                // same Section mount into that shell's Content slot. Shell frames are
                // registered too, so they can be shown directly like any other frame.
                var shellContentBySection = new Dictionary<string, Transform>();
                int shellCount = 0;
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (!FrameRoles.IsShell(loaded[i].ps.role)) continue;
                    var sh = loaded[i];
                    string shellKey = sh.ps.section ?? "";
                    EditorUtility.DisplayProgressBar("FigForge", $"Forging shell {sh.m.screen.name}…", (float)i / loaded.Count);
                    var shSprites = TextureImportHelper.Import(sh.m, sh.srcDir, $"{_spriteFolder}/{SafeName(sh.m.screen.name)}", _tex);
                    var shellGo = ReuseOrBuildScreen(sh, proj.name, canvas.transform, shSprites, false, out var shellCtx);
                    if (shellGo == null) continue;

                    var shellFrame = shellGo.GetComponent<FigForgeFrame>() ?? shellGo.AddComponent<FigForgeFrame>();
                    shellFrame.isShell = true;
                    shellFrame.usesShell = false;
                    shellFrame.shellKey = shellKey;
                    GenerateAndWireFrame(shellGo, sh.m, shellCtx, shellFrame, sh.ps.section, includeUnityCustomizations);
                    FigForgeFrameSceneTools.RefreshCompositors(shellGo);
                    mgr.Register(shellFrame);
                    shellCount++;

                    if (string.IsNullOrEmpty(shellKey)) continue;
                    if (shellContentBySection.ContainsKey(shellKey))
                    {
                        Log($"multiple Shell frames in Section '{shellKey}' — screens will use the first one.", MessageType.Warning);
                        continue;
                    }
                    var shellContent = FindContentSlot(shellGo);
                    if (shellContent == null) { Log($"Shell '{sh.m.screen.name}' has no 'Content' slot — screens mount at shell root.", MessageType.Warning); shellContent = shellGo.transform; }
                    shellContentBySection[shellKey] = shellContent;
                }

                // 1.5 Overlay layers (optional) — global dialog/notification layers that sit
                // ABOVE shells and screens and are NEVER swapped by Show(). Authored as a
                // top-level frame with role=overlay; the FigForgeModals inside become global
                // Dialogs.<Name>. Not registered with the manager, so it never hides.
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (!FrameRoles.IsOverlay(loaded[i].ps.role)) continue;
                    var ov = loaded[i];
                    // An overlay must NEVER be able to abort the screens build — isolate it.
                    try
                    {
                        EditorUtility.DisplayProgressBar("FigForge", $"Forging overlay {ov.m.screen.name}…", (float)i / loaded.Count);
                        var ovSprites = TextureImportHelper.Import(ov.m, ov.srcDir, $"{_spriteFolder}/{SafeName(ov.m.screen.name)}", _tex);
                        var ovGo = ReuseOrBuildScreen(ov, proj.name, canvas.transform, ovSprites, false, out var ovCtx);
                        if (ovGo == null) continue;
                        // Explicit == null (NOT ??): in the editor GetComponent on a MISSING
                        // component returns Unity's fake-null stub, which ?? treats as found —
                        // so AddComponent never runs and the stub throws on first access.
                        var ovFrame = ovGo.GetComponent<FigForgeFrame>();
                        if (ovFrame == null) ovFrame = ovGo.AddComponent<FigForgeFrame>();
                        ovFrame.isShell = false;
                        ovFrame.usesShell = false;
                        // Render above everything via a top-sorted nested canvas (just under the
                        // Toasts host at 32760). Needs its own raycaster so dialog buttons work.
                        var ovCanvas = ovGo.GetComponent<Canvas>();
                        if (ovCanvas == null) ovCanvas = ovGo.AddComponent<Canvas>();
                        ovCanvas.overrideSorting = true;
                        ovCanvas.sortingOrder = 32750;
                        if (ovGo.GetComponent<GraphicRaycaster>() == null) ovGo.AddComponent<GraphicRaycaster>();
                        // Fills the canvas at play-time (it's never FrameManager-driven), so its
                        // dialogs cover the screen wherever the editor spread left the layer.
                        if (ovGo.GetComponent<FigForgeOverlayLayer>() == null) ovGo.AddComponent<FigForgeOverlayLayer>();
                        GenerateAndWireFrame(ovGo, ov.m, ovCtx, ovFrame, ov.ps.section, includeUnityCustomizations, isOverlay: true);
                        FigForgeFrameSceneTools.RefreshCompositors(ovGo);
                        // Deliberately NOT mgr.Register(ovFrame): overlays are global, not screens.
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[FigForge] overlay '{ov.m.screen.name}' build failed (screens still built): {ex}");
                        Log($"overlay '{ov.m.screen.name}' build failed: {ex.Message}", MessageType.Error);
                    }
                }

                // 2. Screens.
                int built = 0;
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (FrameRoles.IsShell(loaded[i].ps.role) || FrameRoles.IsOverlay(loaded[i].ps.role)) continue;
                    var m = loaded[i].m;
                    // Isolate each screen like the overlay loop above: ONE bad frame must never
                    // abort the rest of the page — or skip the arrange/finalize that runs AFTER
                    // this loop (which is what left frames un-arranged when a build threw).
                    try
                    {
                        EditorUtility.DisplayProgressBar("FigForge", $"Forging {m.screen.name}…", (float)i / loaded.Count);
                        var sprites = TextureImportHelper.Import(m, loaded[i].srcDir, $"{_spriteFolder}/{SafeName(m.screen.name)}", _tex);
                        string shellKey = loaded[i].ps.section ?? "";
                        bool usesShell = !string.IsNullOrEmpty(shellKey) && shellContentBySection.ContainsKey(shellKey);
                        var page = ReuseOrBuildScreen(loaded[i], proj.name, canvas.transform, sprites, false, out var frameCtx);
                        if (page == null) continue;
                        var bs = page.GetComponent<FigForgeFrame>() ?? page.AddComponent<FigForgeFrame>();
                        bs.isShell = false;
                        bs.usesShell = usesShell;
                        bs.shellKey = usesShell ? shellKey : "";
                        GenerateAndWireFrame(page, m, frameCtx, bs, loaded[i].ps.section, includeUnityCustomizations);
                        FigForgeFrameSceneTools.RefreshCompositors(page);
                        mgr.Register(bs);
                        built++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[FigForge] screen '{m.screen?.name}' build failed (other screens still built): {ex}");
                        Log($"screen '{m.screen?.name}' build failed: {ex.Message}", MessageType.Error);
                    }
                }

                mgr.initialScreen = mgr.Find(proj.initial);
                if (canvas.GetComponent<FigForgeNavBinder>() == null) canvas.gameObject.AddComponent<FigForgeNavBinder>();

                // Editor convenience: keep every imported frame visible/editable.
                // Runtime Start() still switches to one active frame via Show().
                foreach (var s in mgr.screens) if (s != null) s.gameObject.SetActive(true);
                FigForgeFrameSceneTools.ArrangeRootFrames(canvas.GetComponent<FigForgeCanvasHelper>(), false);
                WarmUpImportedFrames(mgr);

                // Only a canvas THIS import created gets creation-undo (a reused one
                // must survive Ctrl+Z); freshly-built screens are registered in
                // ReuseOrBuildScreen, reused ones deliberately not.
                if (canvasCreated)
                    Undo.RegisterCreatedObjectUndo(canvas.gameObject, "FigForge Build Page");
                Undo.SetCurrentGroupName("FigForge Build Page");
                EditorUtility.SetDirty(canvas);
                EditorUtility.SetDirty(mgr);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
                var shellSummary = shellCount > 0 ? " + " + shellCount + " shell(s)" : "";
                var customizationSummary = includeUnityCustomizations ? " with Unity customizations" : "";
                Log($"built page '{proj.name}'{customizationSummary} — {built} screen(s){shellSummary}, initial '{proj.initial}' ✓", MessageType.Info);
                return true;
            }
            // Also LogError so the failure shows in the Console even when the importer
            // window isn't focused (the per-screen inner catches already do this).
            catch (System.Exception e) { Debug.LogError($"[FigForge] page build failed: {e}"); Log($"page build failed: {e.Message}\n{e.StackTrace}", MessageType.Error); return false; }
            // EndBatch sweeps orphan Frames.<Old>.g.cs and clears the shared reserved set. In the
            // finally so a mid-import throw can't leave the batch open (which would make the NEXT
            // single-frame import wrongly think it's still in a full run).
            finally { FrameCodeGenDriver.EndBatch(); EditorUtility.ClearProgressBar(); AssetDatabase.SaveAssets(); }
        }

        void BuildPageUITK(ProjectData proj, List<LoadedScreen> loaded)
        {
            var panel = ResolvePanelSettings();
            var go = new GameObject("FigForge UI", typeof(UnityEngine.UIElements.UIDocument));
            var doc = go.GetComponent<UnityEngine.UIElements.UIDocument>();
            if (panel != null) doc.panelSettings = panel;
            var mgr = go.AddComponent<UIScreenManager>();

            int built = 0;
            for (int i = 0; i < loaded.Count; i++)
            {
                var m = loaded[i].m;
                ApplyManifestSettings(m);
                EditorUtility.DisplayProgressBar("FigForge", $"Generating {m.screen.name}…", (float)i / loaded.Count);
                var sprites = TextureImportHelper.Import(m, loaded[i].srcDir, $"{_spriteFolder}/{SafeName(m.screen.name)}", _tex);
                var ctx = new UITKContext
                {
                    outFolder = _uitkOutFolder, sprites = sprites, log = mm => Log(mm, MessageType.Warning),
                    resolveFontPath = (fam, sty) =>
                    {
                        var fa = ResolveFontAsset(fam, sty);
                        return new TMP_FontAssetRef { assetPath = fa != null ? AssetDatabase.GetAssetPath(fa) : null };
                    },
                };
                var res = UxmlBuilder.Build(m, ctx);
                var vta = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(res.uxmlPath);
                if (vta != null) { mgr.Register(m.screen.name, vta); built++; }
            }
            mgr.initialScreen = mgr.pages.Find(p => p != null && p.name == proj.initial)?.tree;
            Undo.RegisterCreatedObjectUndo(go, "FigForge Build Page");
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            Log($"built UITK page '{proj.name}' — {built} screen(s) ✓", MessageType.Info);
        }

        // ---- UI Toolkit backend ------------------------------------------------
        void BuildUITK()
        {
            try
            {
                ApplyManifestSettings(_manifest);
                EditorUtility.DisplayProgressBar("FigForge", "Importing assets…", 0.15f);
                var sourceDir = Path.GetDirectoryName(_manifestPaths[_selected]);
                var screenFolder = $"{_spriteFolder}/{SafeName(_manifest.screen.name)}";
                var sprites = TextureImportHelper.Import(_manifest, sourceDir, screenFolder, _tex);
                Log($"imported {sprites.Count} sprite(s)", MessageType.Info);

                EditorUtility.DisplayProgressBar("FigForge", "Generating UXML + USS…", 0.55f);
                var ctx = new UITKContext
                {
                    outFolder = _uitkOutFolder,
                    sprites = sprites,
                    resolveFontPath = (fam, sty) =>
                    {
                        var fa = ResolveFontAsset(fam, sty);
                        return new TMP_FontAssetRef { assetPath = fa != null ? AssetDatabase.GetAssetPath(fa) : null };
                    },
                    log = m => Log(m, MessageType.Warning),
                };

                var res = UxmlBuilder.Build(_manifest, ctx);
                Log($"wrote {res.uxmlPath} (+ .uss), {res.elementCount} elements", MessageType.Info);

                if (_uitkCreateDoc)
                    CreateUIDocument(res);

                Log($"built '{_manifest.screen.name}' (UI Toolkit) ✓", MessageType.Info);
            }
            catch (System.Exception e)
            {
                Log($"UITK build failed: {e.Message}\n{e.StackTrace}", MessageType.Error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        void CreateUIDocument(UxmlResult res)
        {
            var vta = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(res.uxmlPath);
            if (vta == null) { Log("could not load generated UXML as VisualTreeAsset", MessageType.Warning); return; }

            var panel = ResolvePanelSettings();

            // Reuse an existing FigForge UIDocument when building a connected scene.
            var existing = Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(FindObjectsSortMode.None)
                .FirstOrDefault(d => d.GetComponent<UIScreenManager>() != null || d.name == "FigForge UI");

            GameObject go;
            UnityEngine.UIElements.UIDocument doc;
            bool createdDoc = false;
            if (_connectedScene && existing != null) { doc = existing; go = existing.gameObject; }
            else
            {
                go = new GameObject("FigForge UI", typeof(UnityEngine.UIElements.UIDocument));
                doc = go.GetComponent<UnityEngine.UIElements.UIDocument>();
                createdDoc = true;
            }
            if (panel != null) doc.panelSettings = panel;

            if (_connectedScene)
            {
                var mgr = go.GetComponent<UIScreenManager>() ?? go.AddComponent<UIScreenManager>();
                mgr.Register(_manifest.screen.name, vta);
                Log($"registered UITK page '{_manifest.screen.name}' on UIScreenManager", MessageType.Info);
            }
            else
            {
                doc.visualTreeAsset = vta;
            }

            if (createdDoc) // a reused UIDocument must survive Ctrl+Z
                Undo.RegisterCreatedObjectUndo(go, "FigForge UITK Build");
            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        }

        UnityEngine.UIElements.PanelSettings ResolvePanelSettings()
        {
            const string path = "Assets/FigForge/FigForgePanelSettings.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(path);
            if (existing != null) return existing;

            var panel = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
            // Scale like a CanvasScaler "match height" so figma-px coords map across resolutions.
            panel.scaleMode = UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize;
            float fw = _manifest.screen.figmaSize != null ? _manifest.screen.figmaSize.w : 1920f;
            float fh = _manifest.screen.figmaSize != null ? _manifest.screen.figmaSize.h : 1080f;
            panel.referenceResolution = new Vector2Int(Mathf.RoundToInt(fw), Mathf.RoundToInt(fh));
            panel.match = 1f;

            // A PanelSettings needs a theme; grab any ThemeStyleSheet in the project.
            var themeGuid = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            if (themeGuid.Length > 0)
            {
                var theme = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(
                    AssetDatabase.GUIDToAssetPath(themeGuid[0]));
                if (theme != null) panel.themeStyleSheet = theme;
            }
            else
            {
                Log("No ThemeStyleSheet found — assign one to FigForgePanelSettings (Assets ▸ Create ▸ UI Toolkit ▸ TSS Theme File).", MessageType.Warning);
            }

            TextureImportHelper.EnsureFolder("Assets/FigForge");
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();
            return panel;
        }

        // First canvas in the open scene — prefers a root FigForge one (Camera or Overlay).
        static Canvas FirstSceneCanvas()
        {
            var all = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID);
            return all.FirstOrDefault(c => c.transform.parent == null && IsFigForgeCanvasMode(c.renderMode))
                ?? all.FirstOrDefault();
        }

        // Render modes FigForge uses for a page canvas. Screen Space - Camera is the
        // default (lets the blend compositor capture the page to a RenderTexture); Overlay
        // is still accepted so older scenes reuse their existing canvas on re-import.
        static bool IsFigForgeCanvasMode(RenderMode mode)
            => mode == RenderMode.ScreenSpaceCamera || mode == RenderMode.ScreenSpaceOverlay;

        // `created` reports whether THIS call made the canvas — undo registration
        // must only cover objects the import created (registering a reused canvas
        // for creation-undo would have Ctrl+Z destroy the user's canvas).
        Canvas ResolveCanvas(out bool created)
        {
            EnsureEventSystem(); // always — even when an existing canvas is reused
            var canvas = ResolveCanvasObject(out created);
            // Always render the FigForge page through the dedicated FigForge camera (Screen
            // Space - Camera), UPGRADING a reused Overlay canvas too. Overlay can't be captured
            // by the blend compositor, and in the Scene view it renders at screen-pixel scale
            // (content shrinks into a corner) — Camera mode is consistent in edit + play.
            ConfigureCanvasForCamera(canvas);
            EnsureCanvasHelper(canvas);
            return canvas;
        }

        static void EnsureCanvasHelper(Canvas canvas)
        {
            if (canvas == null) return;
            if (canvas.GetComponent<FigForgeCanvasHelper>() != null) return;
            canvas.gameObject.AddComponent<FigForgeCanvasHelper>();
            EditorUtility.SetDirty(canvas.gameObject);
        }

        Canvas ResolveCanvasObject(out bool created)
        {
            created = false;
            if (!_newCanvas && _existingCanvas != null) return _existingCanvas;

            var existing = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.transform.parent == null && IsFigForgeCanvasMode(c.renderMode));
            if (!_newCanvas && existing != null) return existing;
            if (_connectedScene && existing != null && existing.GetComponent<FrameManager>() != null) return existing;

            created = true;
            var go = new GameObject("FigForge Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.pixelPerfect = true;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            float rh = ReferenceHeight(_manifest.screen.figmaSize.h);
            scaler.referenceResolution = new Vector2(
                _manifest.screen.figmaSize.w * (rh / Mathf.Max(1f, _manifest.screen.figmaSize.h)), rh);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        // Point a FigForge page canvas at the dedicated FigForge camera in Screen Space -
        // Camera mode. Idempotent — also upgrades a reused Overlay canvas.
        static void ConfigureCanvasForCamera(Canvas canvas)
        {
            if (canvas == null) return;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.pixelPerfect = true;
            var scaler = canvas.GetComponent<CanvasScaler>();
            float refH = scaler != null && scaler.referenceResolution.y > 1f ? scaler.referenceResolution.y : 1080f;
            canvas.worldCamera = EnsureFigForgeCamera(refH);
            canvas.planeDistance = 100f;
        }

        // A dedicated orthographic camera FigForge owns — so it never hijacks or fights the
        // scene's main camera. If another camera already exists it OVERLAYS it (clears depth
        // only, higher depth) so the existing scene/skybox shows behind the UI; if FigForge's
        // is the only camera it clears to a solid background. Reused if already present.
        static Camera EnsureFigForgeCamera(float refHeight)
        {
            const string camName = "FigForge Camera";
            var all = Object.FindObjectsByType<Camera>(FindObjectsSortMode.InstanceID);
            var cam = all.FirstOrDefault(c => c.name == camName);
            bool otherCamera = all.Any(c => c != cam && c.name != camName);

            if (cam == null)
                cam = new GameObject(camName, typeof(Camera)).GetComponent<Camera>();

            cam.orthographic = true;
            // Pixel-matched: ortho half-height = reference half-height, so the canvas (and a
            // figmaSize frame) is 1 world unit per reference pixel — large + workable in the
            // Scene view. Screen Space - Camera still fills the viewport in play, so the
            // rendered result is unchanged; only the world size differs.
            cam.orthographicSize = Mathf.Max(1f, refHeight * 0.5f);
            cam.cullingMask = ~0;                       // everything; a UI-only scene draws just the UI
            cam.clearFlags = otherCamera ? CameraClearFlags.Depth : CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            cam.depth = 100f;                           // render on top of the scene camera
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 1000f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.transform.rotation = Quaternion.identity;
            return cam;
        }

        // An EventSystem with the RIGHT input module, or buttons never react. On a
        // project whose Active Input Handling is "Input System Package" (new), the
        // legacy StandaloneInputModule throws every frame and nothing is clickable;
        // the Input System needs InputSystemUIInputModule WITH its default actions
        // assigned (a bare AddComponent leaves them null → no pointer input at all).
        // Done via reflection so the importer keeps NO hard dependency on the Input
        // System package — legacy-only projects fall back to StandaloneInputModule.
        // Also repairs an existing mis-configured EventSystem (e.g. one left with the
        // legacy module, or an Input System module with no actions).
        static void EnsureEventSystem()
        {
            var found = Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None);
            var es = found.Length > 0 ? found[0] : null;
            if (es == null)
                es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem))
                    .GetComponent<UnityEngine.EventSystems.EventSystem>();
            var go = es.gameObject;

            var ismType = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            if (ismType != null)
            {
                // Input System present → it's the safe choice for "new" and "both".
                var legacy = go.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                if (legacy != null) Object.DestroyImmediate(legacy);

                bool added = go.GetComponent(ismType) == null;
                var ism = go.GetComponent(ismType) as Behaviour ?? go.AddComponent(ismType) as Behaviour;
                bool assigned = AssignDefaultUiActions(ismType, ism);
                if (added || legacy != null || assigned)
                    Debug.Log("[FigForge] EventSystem → InputSystemUIInputModule with default actions"
                        + (legacy != null ? " (replaced legacy StandaloneInputModule)." : "."));
            }
            else if (go.GetComponent<UnityEngine.EventSystems.BaseInputModule>() == null)
            {
                go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }
        }

        static System.Type FindType(string fullName)
        {
            var t = System.Type.GetType(fullName);
            if (t != null) return t;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // Wire the Input System UI module's default actions (Point/LeftClick/…) when
        // none are assigned — otherwise the module reads no device and the UI is dead.
        // Respect a user-assigned actions asset. Reflection-only (no package dep).
        static bool AssignDefaultUiActions(System.Type ismType, object ism)
        {
            if (ism == null) return false;
            try
            {
                var assetProp = ismType.GetProperty("actionsAsset");
                if (assetProp != null && assetProp.GetValue(ism) != null) return false; // user already wired actions
                var assign = ismType.GetMethod("AssignDefaultActions", System.Type.EmptyTypes);
                if (assign != null) { assign.Invoke(ism, null); return true; }
            }
            catch { /* older Input System without AssignDefaultActions — best effort */ }
            return false;
        }

        float ReferenceHeight(float figmaH)
        {
            switch (_scalePreset)
            {
                case ScalePreset.P720: return 720f;
                case ScalePreset.P1080: return 1080f;
                case ScalePreset.Custom: return _customRefHeight;
                default: return figmaH;
            }
        }

        void SavePrefab(GameObject page)
        {
            TextureImportHelper.EnsureFolder(_prefabFolder);
            var path = $"{_prefabFolder}/{SafeName(_manifest.screen.name)}.prefab";
            PrefabUtility.SaveAsPrefabAsset(page, path);
            Log($"saved prefab → {path}", MessageType.Info);
        }

        // ---- helpers -----------------------------------------------------------
        void Log(string msg, MessageType kind) => _log.Add((msg, kind));
        static string SafeName(string s) => new string((s ?? "Screen").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        void SummaryCard(string title, string value, string caption, Color color)
        {
            var rect = GUILayoutUtility.GetRect(120, 68, GUILayout.ExpandWidth(true), GUILayout.Height(68));
            EditorGUI.DrawRect(rect, Panel);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), Border);

            GUI.Label(new Rect(rect.x + 10, rect.y + 8, rect.width - 20, 16), title, _styles.metricLabel);
            GUI.Label(new Rect(rect.x + 10, rect.y + 27, rect.width - 20, 22), value, _styles.metricValue);
            GUI.Label(new Rect(rect.x + 10, rect.y + 50, rect.width - 20, 14), caption, _styles.metricCaption);
        }

        void EnsureStyles()
        {
            _styles ??= new WindowStyles();
        }
        bool Foldout(bool state, string label) => EditorGUILayout.Foldout(state, label, true, _styles.foldout);
        bool ForgePageButton(string label, GUIStyle normal, GUIStyle hover, GUIStyle active, float height)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(height));
            int id = GUIUtility.GetControlID("FigForgeForgePageButton".GetHashCode(), FocusType.Passive, rect);
            bool enabled = GUI.enabled;
            bool hot = enabled && rect.Contains(Event.current.mousePosition);
            bool held = enabled && GUIUtility.hotControl == id && hot;
            var style = enabled ? (held ? active : hot ? hover : normal) : _styles.forgeButtonDisabled;
            if (Event.current.type == EventType.Repaint)
            {
                if (!held) GUI.Box(new Rect(rect.x, rect.y + 2f, rect.width, rect.height), GUIContent.none, _styles.forgeButtonShadow);
                GUI.Box(rect, GUIContent.none, style);
                var labelRect = held ? new Rect(rect.x, rect.y + 1f, rect.width, rect.height) : rect;
                using (new EditorGUI.DisabledScope(!enabled))
                    GUI.Label(labelRect, label, enabled ? _styles.forgeButtonLabel : _styles.forgeButtonDisabledLabel);
            }

            var e = Event.current;
            if (!enabled || e == null) return false;
            if (e.type == EventType.MouseDown && e.button == 0 && hot)
            {
                GUIUtility.hotControl = id;
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                Repaint();
                e.Use();
                return hot;
            }
            return false;
        }

        void Divider()
        {
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.1f));
            EditorGUILayout.Space(2);
        }

        sealed class WindowStyles
        {
            // Every Texture2D MakeTexture/MakeButtonTexture/MakeForgeButtonTexture builds is
            // collected here so Dispose() can DestroyImmediate them. They use
            // HideFlags.HideAndDontSave (never serialized), so without an explicit destroy they
            // leak as orphaned native textures each time _styles is rebuilt (e.g. domain reload).
            readonly List<Texture2D> _textures = new List<Texture2D>();
            // Instance field initializers below can't call instance methods (CS0236), so the
            // static Make* helpers stash each created texture here; the constructor (which runs
            // after all field initializers) takes ownership into _textures so Dispose() can
            // destroy them. The editor is single-threaded, so this scratch list is safe.
            static readonly List<Texture2D> s_pending = new List<Texture2D>();

            public WindowStyles()
            {
                _textures.AddRange(s_pending);
                s_pending.Clear();
            }

            public readonly GUIStyle hero = new GUIStyle
            {
                padding = new RectOffset(16, 16, 14, 12),
                margin = new RectOffset(8, 8, 8, 8),
                normal = { background = MakeTexture(new Color(0.075f, 0.087f, 0.095f)) }
            };

            public readonly GUIStyle heroTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                normal = { textColor = Color.white }
            };

            public readonly GUIStyle heroSubtitle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.68f, 0.74f, 0.76f) },
                wordWrap = true
            };

            public readonly GUIStyle card = new GUIStyle
            {
                padding = new RectOffset(14, 14, 10, 12),
                margin = new RectOffset(8, 8, 0, 8),
                normal = { background = MakeTexture(Panel) }
            };

            public readonly GUIStyle buildCard = new GUIStyle
            {
                padding = new RectOffset(14, 14, 12, 12),
                margin = new RectOffset(8, 8, 0, 8),
                normal = { background = MakeTexture(new Color(0.075f, 0.087f, 0.095f)) }
            };

            public readonly GUIStyle foldout = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                onNormal = { textColor = Color.white },
                focused = { textColor = Color.white },
                onFocused = { textColor = Color.white }
            };

            public readonly GUIStyle sectionTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = new Color(0.88f, 0.93f, 0.94f) }
            };

            public readonly GUIStyle subtleLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.55f, 0.62f, 0.64f) },
                wordWrap = true
            };

            public readonly GUIStyle toggle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.76f, 0.82f, 0.84f) },
                onNormal = { textColor = Color.white }
            };

            public readonly GUIStyle button = new GUIStyle(EditorStyles.miniButton)
            {
                border = new RectOffset(7, 7, 7, 7),
                padding = new RectOffset(10, 10, 4, 4),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.82f, 0.9f, 0.9f), background = MakeButtonTexture(new Color(0.15f, 0.17f, 0.18f), new Color(0.12f, 0.14f, 0.15f), new Color(0.25f, 0.29f, 0.3f)) },
                hover = { textColor = Color.white, background = MakeButtonTexture(new Color(0.23f, 0.27f, 0.28f), new Color(0.16f, 0.19f, 0.2f), new Color(0.34f, 0.39f, 0.4f)) },
                active = { textColor = Color.white, background = MakeButtonTexture(new Color(0.1f, 0.12f, 0.13f), new Color(0.16f, 0.18f, 0.19f), new Color(0.19f, 0.23f, 0.24f)) }
            };

            public readonly GUIStyle primaryButton = new GUIStyle(EditorStyles.miniButton)
            {
                border = new RectOffset(7, 7, 7, 7),
                padding = new RectOffset(12, 12, 4, 4),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = MakeButtonTexture(new Color(0.12f, 0.5f, 0.25f), AccentDim, new Color(0.2f, 0.72f, 0.36f)) },
                hover = { textColor = Color.white, background = MakeButtonTexture(new Color(0.2f, 0.76f, 0.38f), new Color(0.1f, 0.5f, 0.24f), new Color(0.28f, 0.9f, 0.46f)) },
                active = { textColor = Color.white, background = MakeButtonTexture(new Color(0.07f, 0.32f, 0.16f), new Color(0.12f, 0.5f, 0.25f), new Color(0.16f, 0.58f, 0.28f)) }
            };

            public readonly GUIStyle warningButton = new GUIStyle(EditorStyles.miniButton)
            {
                border = new RectOffset(7, 7, 7, 7),
                padding = new RectOffset(12, 12, 4, 4),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = MakeButtonTexture(new Color(0.5f, 0.22f, 0.14f), new Color(0.32f, 0.13f, 0.08f), new Color(0.7f, 0.32f, 0.2f)) },
                hover = { textColor = Color.white, background = MakeButtonTexture(new Color(0.74f, 0.31f, 0.2f), new Color(0.48f, 0.18f, 0.12f), new Color(0.94f, 0.43f, 0.27f)) },
                active = { textColor = Color.white, background = MakeButtonTexture(new Color(0.25f, 0.1f, 0.07f), new Color(0.42f, 0.16f, 0.1f), new Color(0.55f, 0.22f, 0.14f)) }
            };

            public readonly GUIStyle forgeButtonLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white }
            };

            public readonly GUIStyle forgeButtonDisabledLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(1f, 1f, 1f, 0.48f) }
            };

            public readonly GUIStyle pageForgeNormal = ForgeButtonStyle(new Color(0.28f, 0.6f, 0.38f), new Color(0.18f, 0.24f, 0.21f), new Color(0.46f, 0.5f, 0.48f));
            public readonly GUIStyle pageForgeHover = ForgeButtonStyle(new Color(0.36f, 0.72f, 0.48f), new Color(0.22f, 0.3f, 0.26f), new Color(0.56f, 0.62f, 0.58f));
            public readonly GUIStyle pageForgeActive = ForgeButtonStyle(new Color(0.13f, 0.24f, 0.18f), new Color(0.27f, 0.46f, 0.33f), new Color(0.34f, 0.38f, 0.36f));
            public readonly GUIStyle customForgeNormal = ForgeButtonStyle(new Color(0.46f, 0.43f, 0.68f), new Color(0.22f, 0.22f, 0.31f), new Color(0.48f, 0.49f, 0.56f));
            public readonly GUIStyle customForgeHover = ForgeButtonStyle(new Color(0.56f, 0.52f, 0.82f), new Color(0.27f, 0.26f, 0.39f), new Color(0.58f, 0.58f, 0.68f));
            public readonly GUIStyle customForgeActive = ForgeButtonStyle(new Color(0.18f, 0.17f, 0.28f), new Color(0.35f, 0.32f, 0.52f), new Color(0.36f, 0.36f, 0.43f));
            public readonly GUIStyle forgeButtonDisabled = ForgeButtonStyle(new Color(0.25f, 0.26f, 0.27f), new Color(0.16f, 0.17f, 0.18f), new Color(0.38f, 0.4f, 0.41f));
            public readonly GUIStyle forgeButtonShadow = ForgeButtonStyle(new Color(0f, 0f, 0f, 0.2f), new Color(0f, 0f, 0f, 0.28f), new Color(0f, 0f, 0f, 0f));

            public readonly GUIStyle miniButton = new GUIStyle(EditorStyles.miniButton)
            {
                border = new RectOffset(6, 6, 6, 6),
                normal = { textColor = new Color(0.78f, 0.84f, 0.86f), background = MakeButtonTexture(new Color(0.15f, 0.17f, 0.18f), new Color(0.11f, 0.13f, 0.14f), new Color(0.24f, 0.27f, 0.28f), 6) },
                hover = { textColor = Color.white, background = MakeButtonTexture(new Color(0.22f, 0.25f, 0.26f), new Color(0.15f, 0.18f, 0.19f), new Color(0.32f, 0.36f, 0.37f), 6) }
            };

            public readonly GUIStyle versionPill = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                border = new RectOffset(7, 7, 7, 7),
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(7, 7, 1, 1),
                normal =
                {
                    textColor = new Color(0.9f, 0.98f, 0.92f),
                    background = MakeButtonTexture(new Color(0.13f, 0.3f, 0.19f), new Color(0.08f, 0.18f, 0.12f), new Color(0.22f, 0.72f, 0.36f), 7)
                }
            };

            public readonly GUIStyle versionArrow = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 5, 0),
                normal = { textColor = Accent }
            };

            public readonly GUIStyle metricLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.62f, 0.7f, 0.72f) },
                clipping = TextClipping.Clip
            };

            public readonly GUIStyle metricValue = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 17,
                normal = { textColor = Color.white },
                clipping = TextClipping.Clip
            };

            public readonly GUIStyle metricCaption = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.45f, 0.52f, 0.54f) },
                clipping = TextClipping.Clip
            };

            static Texture2D MakeTexture(Color color)
            {
                var texture = new Texture2D(1, 1)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                texture.SetPixel(0, 0, color);
                texture.Apply();
                s_pending.Add(texture);
                return texture;
            }

            static Texture2D MakeButtonTexture(Color top, Color bottom, Color border, int radius = 7)
            {
                const int size = 32;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };

                float innerRadius = Mathf.Max(0, radius - 1);
                for (int y = 0; y < size; y++)
                {
                    float t = 1f - y / (float)(size - 1);
                    Color fill = Color.Lerp(top, bottom, t);
                    for (int x = 0; x < size; x++)
                    {
                        float d = RoundedRectDistance(x + 0.5f, y + 0.5f, size, size, radius);
                        if (d > 1f)
                        {
                            texture.SetPixel(x, y, Color.clear);
                            continue;
                        }

                        Color c = d > 0f || RoundedRectDistance(x + 0.5f, y + 0.5f, size, size, innerRadius) > 0f
                            ? border
                            : fill;
                        c.a = Mathf.Clamp01(1f - Mathf.Max(0f, d));
                        texture.SetPixel(x, y, c);
                    }
                }

                texture.Apply();
                s_pending.Add(texture);
                return texture;
            }

            static GUIStyle ForgeButtonStyle(Color top, Color bottom, Color border)
            {
                return new GUIStyle
                {
                    border = new RectOffset(8, 8, 8, 8),
                    normal = { background = MakeForgeButtonTexture(top, bottom, border, 7) }
                };
            }

            static Texture2D MakeForgeButtonTexture(Color top, Color bottom, Color bevel, int radius = 7)
            {
                const int size = 32;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };

                float innerRadius = Mathf.Max(0, radius - 1);
                for (int y = 0; y < size; y++)
                {
                    float t = 1f - y / (float)(size - 1);
                    Color fill = Color.Lerp(top, bottom, t);
                    for (int x = 0; x < size; x++)
                    {
                        float d = RoundedRectDistance(x + 0.5f, y + 0.5f, size, size, radius);
                        if (d > 1f)
                        {
                            texture.SetPixel(x, y, Color.clear);
                            continue;
                        }

                        float innerD = RoundedRectDistance(x + 0.5f, y + 0.5f, size, size, innerRadius);
                        float edge = Mathf.Clamp01(innerD + 1f);
                        Color c = Color.Lerp(fill, bevel, edge * 0.28f);
                        c.a = Mathf.Clamp01(1f - Mathf.Max(0f, d));
                        texture.SetPixel(x, y, c);
                    }
                }

                texture.Apply();
                s_pending.Add(texture);
                return texture;
            }

            static float RoundedRectDistance(float x, float y, float width, float height, float radius)
            {
                float px = Mathf.Abs(x - width * 0.5f) - (width * 0.5f - radius);
                float py = Mathf.Abs(y - height * 0.5f) - (height * 0.5f - radius);
                float ax = Mathf.Max(px, 0f);
                float ay = Mathf.Max(py, 0f);
                return Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
            }

            // Destroy the native textures this instance created. Only our own
            // HideAndDontSave textures are tracked here, so this never touches shared
            // or asset textures. Guarded + cleared so a double-call is a no-op.
            public void Dispose()
            {
                foreach (var t in _textures)
                    if (t != null) DestroyImmediate(t);
                _textures.Clear();
            }
        }
    }
}
