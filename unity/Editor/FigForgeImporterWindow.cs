// =============================================================================
// FigForge — importer editor window. Window ▸ FigForge ▸ Importer.
//
// Detects FigForge manifests in the project, lets you configure canvas / fonts /
// textures / canonical library / multi-page output, and builds the uGUI page.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    public class FigForgeImporterWindow : EditorWindow
    {
        // ---- discovered manifests ----
        List<string> _manifestPaths = new List<string>();
        int _selected = 0;
        Manifest _manifest;

        // ---- config ----
        enum OutputMode { Scene, Prefab, Both }
        enum ScalePreset { MatchFigma, P720, P1080, Custom }

        OutputMode _output = OutputMode.Scene;
        ScalePreset _scalePreset = ScalePreset.MatchFigma;
        float _customRefHeight = 1080f;
        bool _newCanvas = true;
        Canvas _existingCanvas;
        bool _connectedScene = true;       // build under a shared ScreenManager
        bool _disableRaycasts = true;
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
        bool _showCanvas = true, _showFonts = true, _showTextures, _showAtlas, _showCanonical = true;

        GUIStyle _h1;

        [MenuItem("Window/FigForge/Importer")]
        public static void Open()
        {
            var w = GetWindow<FigForgeImporterWindow>();
            w.titleContent = new GUIContent("FigForge");
            w.minSize = new Vector2(360, 520);
            w.Show();
        }

        void OnEnable()
        {
            RefreshManifests();
            RefreshFonts();
        }

        void RefreshManifests()
        {
            _manifestPaths = Directory
                .GetFiles(Application.dataPath, "manifest.json", SearchOption.AllDirectories)
                .Select(p => "Assets" + p.Substring(Application.dataPath.Length).Replace('\\', '/'))
                .Where(IsFigForgeManifest)
                .ToList();
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _manifestPaths.Count - 1));
            LoadSelected();
        }

        static bool IsFigForgeManifest(string assetPath)
        {
            try
            {
                var head = File.ReadAllText(assetPath);
                return head.Contains("figforge/manifest") || (head.Contains("\"elements\"") && head.Contains("\"screen\""));
            }
            catch { return false; }
        }

        void LoadSelected()
        {
            if (_manifestPaths.Count == 0) { _manifest = null; return; }
            _manifest = ManifestParser.Load(_manifestPaths[_selected]);
            if (_manifest != null) BuildFontKeys();
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

        TMP_FontAsset GuessFont(string family, string style)
        {
            var fam = (family ?? "").Replace(" ", "").ToLower();
            var sty = (style ?? "").Replace(" ", "").ToLower();
            return _projectFonts.FirstOrDefault(f =>
                       f.name.Replace(" ", "").ToLower().Contains(fam) &&
                       f.name.Replace(" ", "").ToLower().Contains(sty))
                   ?? _projectFonts.FirstOrDefault(f => f.name.Replace(" ", "").ToLower().Contains(fam))
                   ?? (_projectFonts.Count > 0 ? _projectFonts[0] : null);
        }

        // -----------------------------------------------------------------------
        void OnGUI()
        {
            EnsureStyles();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            Header();
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
            LogSection();

            EditorGUILayout.EndScrollView();
        }

        void Header()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("◆ FigForge", _h1);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Rescan", EditorStyles.miniButton, GUILayout.Width(64)))
                { RefreshManifests(); RefreshFonts(); }
            }
            EditorGUILayout.LabelField("Figma → Unity UI importer", EditorStyles.miniLabel);
            Divider();
        }

        void ManifestPicker()
        {
            // Import straight from a FigForge export .zip — no manual unzip.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import a .zip…", GUILayout.Height(22)))
                    ImportZip();
                if (_manifestPaths.Count > 0 &&
                    GUILayout.Button("Reveal folder", EditorStyles.miniButton, GUILayout.Width(96)))
                    EditorUtility.RevealInFinder(_manifestPaths[_selected]);
            }

            if (_manifestPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("Import a FigForge export .zip above, drop an extracted folder under Assets/, or use the MCP 'export_unity' tool — then press Rescan.", MessageType.Info);
                Divider();
                return;
            }

            EditorGUI.BeginChangeCheck();
            _selected = EditorGUILayout.Popup("Manifest", _selected,
                _manifestPaths.Select(p => Path.GetFileName(Path.GetDirectoryName(p)) + " / manifest.json").ToArray());
            if (EditorGUI.EndChangeCheck()) LoadSelected();

            if (_manifest != null)
                EditorGUILayout.LabelField(
                    $"{_manifest.screen?.name}  ·  {_manifest.elements.Count} elements  ·  {_manifest.assets.Count} sprites  ·  scale {_manifest.screen?.exportScale}×",
                    EditorStyles.miniLabel);
            Divider();
        }

        void ImportZip()
        {
            var zip = EditorUtility.OpenFilePanel("Select a FigForge export (.zip)", "", "zip");
            if (string.IsNullOrEmpty(zip)) return;

            var dest = $"Assets/FigForge/Imports/{SafeName(Path.GetFileNameWithoutExtension(zip))}";
            var manifestPath = ZipImporter.ExtractToAssets(zip, dest);
            if (string.IsNullOrEmpty(manifestPath)) return;

            RefreshManifests();
            var idx = _manifestPaths.IndexOf(manifestPath);
            if (idx >= 0) { _selected = idx; LoadSelected(); }
        }

        void CanvasSection()
        {
            _showCanvas = Foldout(_showCanvas, "Canvas & Output");
            if (!_showCanvas) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _output = (OutputMode)EditorGUILayout.EnumPopup("Output", _output);
                _connectedScene = EditorGUILayout.ToggleLeft("Connected scene (ScreenManager toggles pages)", _connectedScene);
                _newCanvas = EditorGUILayout.ToggleLeft("Create new Canvas", _newCanvas);
                if (!_newCanvas)
                    _existingCanvas = (Canvas)EditorGUILayout.ObjectField("Canvas", _existingCanvas, typeof(Canvas), true);
                _scalePreset = (ScalePreset)EditorGUILayout.EnumPopup("Reference height", _scalePreset);
                if (_scalePreset == ScalePreset.Custom)
                    _customRefHeight = EditorGUILayout.FloatField("Custom height", _customRefHeight);
                _disableRaycasts = EditorGUILayout.ToggleLeft("Disable raycast targets on non-interactive graphics", _disableRaycasts);
                if (_output != OutputMode.Scene)
                    _prefabFolder = EditorGUILayout.TextField("Prefab folder", _prefabFolder);
            }
        }

        void FontSection()
        {
            _showFonts = Foldout(_showFonts, $"Fonts ({_manifest.fonts.Count})");
            if (!_showFonts) return;
            using (new EditorGUI.IndentLevelScope())
            {
                if (_projectFonts.Count == 0)
                    EditorGUILayout.HelpBox("No TMP_FontAsset found. Create font assets (Window ▸ TextMeshPro ▸ Font Asset Creator).", MessageType.Warning);
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

        void CanonicalSection()
        {
            int refCount = _manifest.canonicalRefs?.Count ?? 0;
            _showCanonical = Foldout(_showCanonical, $"Canonical elements ({refCount})");
            if (!_showCanonical) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _canonicalLibrary = (CanonicalLibrary)EditorGUILayout.ObjectField("Library", _canonicalLibrary, typeof(CanonicalLibrary), false);
                if (refCount > 0)
                {
                    EditorGUILayout.LabelField("Referenced by this design:", EditorStyles.miniBoldLabel);
                    foreach (var r in _manifest.canonicalRefs)
                    {
                        bool resolved = _canonicalLibrary != null && _canonicalLibrary.Resolve("button", r) != null;
                        EditorGUILayout.LabelField($"   {(resolved ? "✓" : "✗")}  {r}", EditorStyles.miniLabel);
                    }
                    if (_canonicalLibrary == null)
                        EditorGUILayout.HelpBox("Assign a Canonical Library (Create ▸ FigForge ▸ Canonical Library) to instantiate real button prefabs; otherwise placeholders are used.", MessageType.Info);
                }
                else EditorGUILayout.LabelField("No canonical references (name layers Btn_<instance>_<ref>).", EditorStyles.miniLabel);
            }
        }

        void TextureSection()
        {
            _showTextures = Foldout(_showTextures, "Textures");
            if (!_showTextures) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _spriteFolder = EditorGUILayout.TextField("Sprite folder", _spriteFolder);
                _tex.autoMaxSize = EditorGUILayout.ToggleLeft("Auto max size", _tex.autoMaxSize);
                if (!_tex.autoMaxSize) _tex.maxSize = EditorGUILayout.IntField("Max size", _tex.maxSize);
                _tex.compression = (TextureImporterCompression)EditorGUILayout.EnumPopup("Compression", _tex.compression);
            }
        }

        void AtlasSection()
        {
            _showAtlas = Foldout(_showAtlas, "Sprite Atlas");
            if (!_showAtlas) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _atlas.create = EditorGUILayout.ToggleLeft("Create sprite atlas", _atlas.create);
                if (_atlas.create)
                {
                    _atlas.padding = EditorGUILayout.IntField("Padding", _atlas.padding);
                    _atlas.allowRotation = EditorGUILayout.ToggleLeft("Allow rotation", _atlas.allowRotation);
                    _atlas.includeInBuild = EditorGUILayout.ToggleLeft("Include in build", _atlas.includeInBuild);
                }
            }
        }

        void BuildBar()
        {
            Divider();
            GUI.backgroundColor = new Color(0.49f, 0.36f, 1f);
            if (GUILayout.Button($"Build “{_manifest.screen?.name}”", GUILayout.Height(34)))
                Build();
            GUI.backgroundColor = Color.white;
        }

        void LogSection()
        {
            if (_log.Count == 0) return;
            Divider();
            EditorGUILayout.LabelField("Build log", EditorStyles.miniBoldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120));
            foreach (var (msg, kind) in _log) EditorGUILayout.HelpBox(msg, kind);
            EditorGUILayout.EndScrollView();
        }

        // -----------------------------------------------------------------------
        void Build()
        {
            _log.Clear();
            if (_manifest?.screen == null) { Log("manifest has no screen", MessageType.Error); return; }

            try
            {
                EditorUtility.DisplayProgressBar("FigForge", "Importing textures…", 0.15f);
                var sourceDir = Path.GetDirectoryName(_manifestPaths[_selected]);
                var screenFolder = $"{_spriteFolder}/{SafeName(_manifest.screen.name)}";
                var sprites = TextureImportHelper.Import(_manifest, sourceDir, screenFolder, _tex);
                Log($"imported {sprites.Count} sprite(s)", MessageType.Info);

                if (_atlas.create) SpriteAtlasHelper.Build(_manifest.screen.name, screenFolder, _atlas);

                EditorUtility.DisplayProgressBar("FigForge", "Building hierarchy…", 0.55f);
                var canvas = ResolveCanvas();
                Transform parent = canvas.transform;
                ScreenManager mgr = null;
                if (_connectedScene)
                {
                    mgr = canvas.GetComponent<ScreenManager>() ?? canvas.gameObject.AddComponent<ScreenManager>();
                }

                float refH = ReferenceHeight();
                float sf = _manifest.screen.figmaSize.h > 0 ? refH / _manifest.screen.figmaSize.h : 1f;

                var ctx = new BuildContext
                {
                    scaleFactor = sf,
                    sprites = sprites,
                    canonical = _canonicalLibrary,
                    disableRaycasts = _disableRaycasts,
                    resolveFont = (fam, sty) => _fontMap.TryGetValue($"{fam}|{sty}", out var a) ? a : null,
                    log = m => Log(m, MessageType.Warning),
                };

                var page = HierarchyBuilder.BuildPage(_manifest, parent, ctx);
                if (page == null) { Log("build produced no page", MessageType.Error); return; }

                var screen = page.GetComponent<BaseScreen>() ?? page.AddComponent<BaseScreen>();
                screen.screenName = _manifest.screen.name;
                if (mgr != null) { mgr.Register(screen); Log($"registered page '{screen.screenName}' on ScreenManager", MessageType.Info); }

                if (_output != OutputMode.Scene) SavePrefab(page);
                if (_output == OutputMode.Prefab) DestroyImmediate(page);

                Undo.RegisterCreatedObjectUndo(canvas.gameObject, "FigForge Build");
                EditorUtility.SetDirty(canvas);
                if (_output != OutputMode.Prefab)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                        UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

                Log($"built '{_manifest.screen.name}' ✓", MessageType.Info);
            }
            catch (System.Exception e)
            {
                Log($"build failed: {e.Message}\n{e.StackTrace}", MessageType.Error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
        }

        Canvas ResolveCanvas()
        {
            if (!_newCanvas && _existingCanvas != null) return _existingCanvas;

            var existing = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.transform.parent == null && c.renderMode == RenderMode.ScreenSpaceOverlay);
            if (!_newCanvas && existing != null) return existing;
            if (_connectedScene && existing != null && existing.GetComponent<ScreenManager>() != null) return existing;

            var go = new GameObject("FigForge Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(
                _manifest.screen.figmaSize.w * (ReferenceHeight() / Mathf.Max(1f, _manifest.screen.figmaSize.h)),
                ReferenceHeight());
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length == 0)
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            return canvas;
        }

        float ReferenceHeight()
        {
            switch (_scalePreset)
            {
                case ScalePreset.P720: return 720f;
                case ScalePreset.P1080: return 1080f;
                case ScalePreset.Custom: return _customRefHeight;
                default: return _manifest.screen.figmaSize.h;
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

        void EnsureStyles()
        {
            if (_h1 != null) return;
            _h1 = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
        }
        bool Foldout(bool state, string label) => EditorGUILayout.Foldout(state, label, true, EditorStyles.foldoutHeader);
        void Divider()
        {
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.1f));
            EditorGUILayout.Space(2);
        }
    }
}
