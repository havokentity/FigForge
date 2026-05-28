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
        bool _showCanvas = true, _showFonts = true, _showTextures, _showAtlas, _showCanonical = true, _showLive = true;

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

            _projectPaths = Directory
                .GetFiles(Application.dataPath, "project.json", SearchOption.AllDirectories)
                .Select(p => "Assets" + p.Substring(Application.dataPath.Length).Replace('\\', '/'))
                .Where(p => { try { return File.ReadAllText(p).Contains("figforge/project"); } catch { return false; } })
                .ToList();
            _selectedProject = Mathf.Clamp(_selectedProject, 0, Mathf.Max(0, _projectPaths.Count - 1));

            LoadSelected();
        }

        // Only FigForge manifests carry the "figforge/manifest" schema marker.
        // Requiring it keeps the scan from trying to parse foreign/old-schema
        // manifest.json files in the project (which throw and spam the log).
        static bool IsFigForgeManifest(string assetPath)
        {
            try { return File.ReadAllText(assetPath).Contains("figforge/manifest"); }
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

        // Explicit assignment in the Fonts section wins; otherwise auto-import a
        // matching font (project → OS) and build a TMP asset for it.
        TMP_FontAsset ResolveFontAsset(string family, string style)
        {
            if (_fontMap.TryGetValue($"{family}|{style}", out var a) && a != null) return a;
            return FontAutoImporter.Resolve(family, style, m => Log(m, MessageType.Info));
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
            LogSection();

            EditorGUILayout.EndScrollView();
        }

        void Header()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("◆ FigForge", _h1);
                GUILayout.Label($"v{PackageVersion()}", EditorStyles.miniLabel, GUILayout.Width(56));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Rescan", EditorStyles.miniButton, GUILayout.Width(64)))
                { RefreshManifests(); RefreshFonts(); }
            }
            EditorGUILayout.LabelField("Figma → Unity UI importer", EditorStyles.miniLabel);
            Divider();
        }

        void LiveImportSection()
        {
            _showLive = Foldout(_showLive, "Live import (Figma → Unity)");
            if (!_showLive) return;
            using (new EditorGUI.IndentLevelScope())
            {
                bool en = EditorGUILayout.ToggleLeft("Run live import server", FigForgeLiveImport.Enabled);
                if (en != FigForgeLiveImport.Enabled) FigForgeLiveImport.Enabled = en;

                using (new EditorGUI.DisabledScope(!en))
                {
                    int port = EditorGUILayout.DelayedIntField("Port", FigForgeLiveImport.Port);
                    if (port != FigForgeLiveImport.Port) FigForgeLiveImport.Port = port;
                }

                EditorGUILayout.LabelField(
                    (FigForgeLiveImport.Listening ? "● " : "○ ") + FigForgeLiveImport.Status,
                    EditorStyles.miniLabel);
                EditorGUILayout.HelpBox(
                    "In the Figma plugin, hit “Export → Unity” to build the page here automatically — no zip, loopback only.",
                    MessageType.None);
            }
            Divider();
        }

        /// <summary>Entry point used by the live-import HTTP receiver: discover
        /// the freshly-written bundle and build it with the window's settings.</summary>
        public void LiveBuildPage(string projectJsonAssetPath)
        {
            RefreshManifests();
            BuildPageProject(projectJsonAssetPath);
            Repaint();
        }

        string _version;
        string PackageVersion()
        {
            if (_version != null) return _version;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FigForgeImporterWindow).Assembly);
            _version = info != null ? info.version : "dev";
            return _version;
        }

        void ManifestPicker()
        {
            // Whole-page project bundles (project.json) → Build Page.
            if (_projectPaths.Count > 0)
            {
                _selectedProject = EditorGUILayout.Popup("Page bundle", _selectedProject,
                    _projectPaths.Select(p => Path.GetFileName(Path.GetDirectoryName(p)) + " / project.json").ToArray());
                GUI.backgroundColor = new Color(0.49f, 0.36f, 1f);
                if (GUILayout.Button($"Build Page → {_backend} (all screens)", GUILayout.Height(26)))
                    BuildPageProject(_projectPaths[_selectedProject]);
                GUI.backgroundColor = Color.white;
                Divider();
            }

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
            _showCanvas = Foldout(_showCanvas, "Backend & Output");
            if (!_showCanvas) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _backend = (UIBackend)EditorGUILayout.EnumPopup("UI backend", _backend);

                if (_backend == UIBackend.UIToolkit)
                {
                    _uitkOutFolder = EditorGUILayout.TextField("UXML/USS folder", _uitkOutFolder);
                    _uitkCreateDoc = EditorGUILayout.ToggleLeft("Create UIDocument + PanelSettings in scene", _uitkCreateDoc);
                    _connectedScene = EditorGUILayout.ToggleLeft("Connected scene (one UIDocument toggles pages)", _connectedScene);
                    EditorGUILayout.HelpBox("UI Toolkit emits a .uxml + .uss. Canonical layers become <Button> with a `fge-ref-<name>` USS class — style that class once in your own stylesheet.", MessageType.None);
                }
                else
                {
                    _output = (OutputMode)EditorGUILayout.EnumPopup("Output", _output);
                    _connectedScene = EditorGUILayout.ToggleLeft("Connected scene (ScreenManager toggles pages)", _connectedScene);
                    _newCanvas = EditorGUILayout.ToggleLeft("Create new Canvas (off = add to existing)", _newCanvas);
                    if (!_newCanvas)
                    {
                        // Auto-fill the slot with the scene's first canvas when blank.
                        if (_existingCanvas == null) _existingCanvas = FirstSceneCanvas();
                        _existingCanvas = (Canvas)EditorGUILayout.ObjectField("Canvas", _existingCanvas, typeof(Canvas), true);
                        if (_existingCanvas == null)
                            EditorGUILayout.HelpBox("No Canvas in the scene yet — one will be created on Build, then reused next time.", MessageType.None);
                    }
                    _scalePreset = (ScalePreset)EditorGUILayout.EnumPopup("Reference height", _scalePreset);
                    if (_scalePreset == ScalePreset.Custom)
                        _customRefHeight = EditorGUILayout.FloatField("Custom height", _customRefHeight);
                    _disableRaycasts = EditorGUILayout.ToggleLeft("Disable raycast targets on non-interactive graphics", _disableRaycasts);
                    if (_output != OutputMode.Scene)
                        _prefabFolder = EditorGUILayout.TextField("Prefab folder", _prefabFolder);
                }
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
            FontAutoImporter.ClearCache();
            if (_manifest?.screen == null) { Log("manifest has no screen", MessageType.Error); return; }
            if (_backend == UIBackend.UIToolkit) { BuildUITK(); return; }

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

                float refH = ReferenceHeight(_manifest.screen.figmaSize.h);
                float sf = _manifest.screen.figmaSize.h > 0 ? refH / _manifest.screen.figmaSize.h : 1f;

                var ctx = new BuildContext
                {
                    scaleFactor = sf,
                    sprites = sprites,
                    canonical = _canonicalLibrary,
                    disableRaycasts = _disableRaycasts,
                    resolveFont = ResolveFontAsset,
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

        // ---- Whole-page (project bundle) build ---------------------------------
        struct LoadedScreen { public Manifest m; public string srcDir; public ProjectScreen ps; }

        BuildContext MakeContext(Manifest m, Dictionary<string, Sprite> sprites)
        {
            float fh = m.screen != null && m.screen.figmaSize != null ? m.screen.figmaSize.h : 1080f;
            float sf = fh > 0 ? ReferenceHeight(fh) / fh : 1f;
            return new BuildContext
            {
                scaleFactor = sf, sprites = sprites, canonical = _canonicalLibrary, disableRaycasts = _disableRaycasts,
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

        static void StretchToParent(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        void BuildPageProject(string projectPath)
        {
            _log.Clear();
            FontAutoImporter.ClearCache();
            var proj = ManifestParser.LoadProject(projectPath);
            if (proj == null || proj.screens.Count == 0) { Log("project.json is empty or invalid", MessageType.Error); return; }
            var baseDir = Path.GetDirectoryName(projectPath).Replace('\\', '/');

            var loaded = new List<LoadedScreen>();
            foreach (var ps in proj.screens)
            {
                var mp = $"{baseDir}/{ps.manifest}".Replace('\\', '/');
                var m = ManifestParser.Load(mp);
                if (m == null) { Log($"skip '{ps.name}': manifest not found ({mp})", MessageType.Warning); continue; }
                loaded.Add(new LoadedScreen { m = m, srcDir = Path.GetDirectoryName(mp), ps = ps });
            }
            if (loaded.Count == 0) { Log("no buildable screens in bundle", MessageType.Error); return; }
            _manifest = loaded[0].m; // for ResolveCanvas / PanelSettings / header

            try
            {
                if (_backend == UIBackend.UIToolkit) { BuildPageUITK(proj, loaded); return; }

                var canvas = ResolveCanvas();
                var mgr = canvas.GetComponent<ScreenManager>() ?? canvas.gameObject.AddComponent<ScreenManager>();

                // 1. Persistent Shell (optional) — built once; screens mount into its Content slot.
                Transform shellContent = null;
                string shellSection = null;
                int idx = loaded.FindIndex(ls => ls.ps.role == "shell");
                if (idx >= 0)
                {
                    var sh = loaded[idx];
                    EditorUtility.DisplayProgressBar("FigForge", $"Building shell {sh.m.screen.name}…", 0.1f);
                    var shSprites = TextureImportHelper.Import(sh.m, sh.srcDir, $"{_spriteFolder}/{SafeName(sh.m.screen.name)}", _tex);
                    var shellGo = HierarchyBuilder.BuildPage(sh.m, canvas.transform, MakeContext(sh.m, shSprites));
                    if (shellGo != null)
                    {
                        mgr.shell = shellGo;
                        shellSection = sh.ps.section ?? "";
                        shellContent = FindContentSlot(shellGo);
                        if (shellContent == null) { Log("Shell has no 'Content' slot — screens mount at shell root.", MessageType.Warning); shellContent = shellGo.transform; }
                    }
                }

                // 2. Screens.
                int built = 0;
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (loaded[i].ps.role == "shell") continue;
                    var m = loaded[i].m;
                    EditorUtility.DisplayProgressBar("FigForge", $"Building {m.screen.name}…", (float)i / loaded.Count);
                    var sprites = TextureImportHelper.Import(m, loaded[i].srcDir, $"{_spriteFolder}/{SafeName(m.screen.name)}", _tex);
                    bool usesShell = shellContent != null && !string.IsNullOrEmpty(shellSection) && loaded[i].ps.section == shellSection;
                    var parent = usesShell ? shellContent : canvas.transform;
                    var page = HierarchyBuilder.BuildPage(m, parent, MakeContext(m, sprites));
                    if (page == null) continue;
                    if (usesShell) StretchToParent(page);
                    var bs = page.GetComponent<BaseScreen>() ?? page.AddComponent<BaseScreen>();
                    bs.screenName = m.screen.name;
                    bs.usesShell = usesShell;
                    mgr.Register(bs);
                    built++;
                }

                mgr.initialScreen = proj.initial;
                if (canvas.GetComponent<FigForgeNavBinder>() == null) canvas.gameObject.AddComponent<FigForgeNavBinder>();

                // Editor convenience: show only the initial screen + the shell if it uses one.
                foreach (var s in mgr.screens) if (s != null) s.gameObject.SetActive(s.screenName == proj.initial);
                var init = mgr.screens.Find(s => s != null && s.screenName == proj.initial);
                if (mgr.shell != null) mgr.shell.SetActive(init != null && init.usesShell);

                Undo.RegisterCreatedObjectUndo(canvas.gameObject, "FigForge Build Page");
                EditorUtility.SetDirty(canvas);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
                Log($"built page '{proj.name}' — {built} screen(s){(mgr.shell != null ? " + shell" : "")}, initial '{proj.initial}' ✓", MessageType.Info);
            }
            catch (System.Exception e) { Log($"page build failed: {e.Message}\n{e.StackTrace}", MessageType.Error); }
            finally { EditorUtility.ClearProgressBar(); AssetDatabase.SaveAssets(); }
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
                EditorUtility.DisplayProgressBar("FigForge", $"Generating {m.screen.name}…", (float)i / loaded.Count);
                var sprites = TextureImportHelper.Import(m, loaded[i].srcDir, $"{_spriteFolder}/{SafeName(m.screen.name)}", _tex);
                var ctx = new UITKContext
                {
                    outFolder = _uitkOutFolder, sprites = sprites, log = mm => Log(mm, MessageType.Warning),
                    resolveFontPath = (fam, sty) =>
                    {
                        var fa = _fontMap.TryGetValue($"{fam}|{sty}", out var a) ? a : null;
                        return new TMP_FontAssetRef { assetPath = fa != null ? AssetDatabase.GetAssetPath(fa) : null };
                    },
                };
                var res = UxmlBuilder.Build(m, ctx);
                var vta = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(res.uxmlPath);
                if (vta != null) { mgr.Register(m.screen.name, vta); built++; }
            }
            mgr.initialScreen = proj.initial;
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
                        var fa = _fontMap.TryGetValue($"{fam}|{sty}", out var a) ? a : null;
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
            if (_connectedScene && existing != null) { doc = existing; go = existing.gameObject; }
            else
            {
                go = new GameObject("FigForge UI", typeof(UnityEngine.UIElements.UIDocument));
                doc = go.GetComponent<UnityEngine.UIElements.UIDocument>();
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

        // First canvas in the open scene — prefers a root ScreenSpaceOverlay one.
        static Canvas FirstSceneCanvas()
        {
            var all = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID);
            return all.FirstOrDefault(c => c.transform.parent == null && c.renderMode == RenderMode.ScreenSpaceOverlay)
                ?? all.FirstOrDefault();
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
            float rh = ReferenceHeight(_manifest.screen.figmaSize.h);
            scaler.referenceResolution = new Vector2(
                _manifest.screen.figmaSize.w * (rh / Mathf.Max(1f, _manifest.screen.figmaSize.h)), rh);
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length == 0)
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            return canvas;
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
