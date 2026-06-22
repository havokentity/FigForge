// =============================================================================
// FigForge — live import receiver. Runs a tiny loopback HTTP server in the
// Editor so the Figma plugin can POST a page bundle straight in (no zip, no
// MCP). On receive it writes the bundle to disk in the normal project.json
// layout and runs Build Page — Figma → Unity in one click.
//
// Transport: plugin UI `fetch` → http://127.0.0.1:<port>/import. Loopback only.
// Lifecycle: started on editor load, stopped before each domain reload, and
// re-started automatically afterwards. Requests are parsed off the main thread
// but all AssetDatabase / scene work is marshalled back onto it via
// EditorApplication.update.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [InitializeOnLoad]
    public static class FigForgeLiveImport
    {
        const string PrefEnabled = "FigForge.LiveImport.Enabled";
        const string PrefPort = "FigForge.LiveImport.Port";
        const string PrefToken = "FigForge.LiveImport.Token";
        const string TokenHeader = "X-FigForge-Token";
        const int DefaultPort = 1995;
        const string LiveRoot = "Assets/FigForge/Live";

        static HttpListener _listener;
        static readonly Queue<string> _inbox = new Queue<string>();
        static readonly object _gate = new object();

        // Cached so the off-main-thread request handler never touches EditorPrefs.
        // Primed on the main thread in Start() / the Token getter.
        static volatile string _token;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set { EditorPrefs.SetBool(PrefEnabled, value); if (value) Start(); else Stop(); }
        }

        public static int Port
        {
            get => EditorPrefs.GetInt(PrefPort, DefaultPort);
            set { value = Mathf.Clamp(value, 1024, 65535); EditorPrefs.SetInt(PrefPort, value); Restart(); }
        }

        public static bool Listening => _listener != null && _listener.IsListening;
        public static string Status { get; private set; } = "stopped";
        public static string Url => $"http://127.0.0.1:{Port}/import";

        /// <summary>Shared secret the Figma plugin must echo in the
        /// <c>X-FigForge-Token</c> header. Generated on first use and persisted,
        /// so only a client that knows it (your plugin, after a one-time paste)
        /// can trigger an import — a random web page on 127.0.0.1 cannot.</summary>
        public static string Token
        {
            get
            {
                if (string.IsNullOrEmpty(_token))
                {
                    var t = EditorPrefs.GetString(PrefToken, "");
                    if (string.IsNullOrEmpty(t))
                    {
                        t = Guid.NewGuid().ToString("N");
                        EditorPrefs.SetString(PrefToken, t);
                    }
                    _token = t;
                }
                return _token;
            }
        }

        public static void RegenerateToken()
        {
            var t = Guid.NewGuid().ToString("N");
            EditorPrefs.SetString(PrefToken, t);
            _token = t;
        }

        static FigForgeLiveImport()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (Enabled) Start();
        }

        public static void Restart() { Stop(); if (Enabled) Start(); }

        static void Start()
        {
            if (_listener != null) return;
            _ = Token; // prime the cache on the main thread before requests arrive
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{Port}/");
                l.Prefixes.Add($"http://localhost:{Port}/");
                l.Start();
                _listener = l;
                Status = $"listening on {Url}";
                l.BeginGetContext(OnContext, l);
            }
            catch (Exception e)
            {
                _listener = null;
                Status = $"failed to bind :{Port} — {e.Message}";
                Debug.LogWarning($"[FigForge] live import {Status}");
            }
        }

        static void Stop()
        {
            var l = _listener;
            _listener = null;
            if (l == null) return;
            try { l.Stop(); l.Close(); } catch { /* already torn down */ }
            Status = "stopped";
        }

        static void OnContext(IAsyncResult ar)
        {
            var l = ar.AsyncState as HttpListener;
            if (l == null || !l.IsListening) return;

            HttpListenerContext ctx;
            try { ctx = l.EndGetContext(ar); }
            catch { return; } // listener stopped mid-flight
            try { l.BeginGetContext(OnContext, l); } catch { /* stopping */ }

            try
            {
                var res = ctx.Response;
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type, " + TokenHeader);

                switch (ctx.Request.HttpMethod)
                {
                    case "OPTIONS":
                        Respond(res, 204, null);
                        break;
                    case "GET":
                        Respond(res, 200, "{\"ok\":true,\"product\":\"FigForge\",\"live\":true}");
                        break;
                    case "POST":
                        // Gate side effects on the shared secret. CORS can't stop a
                        // cross-origin POST (a simple text/plain request needs no
                        // preflight), so the token is what keeps a random web page
                        // from writing bundles and triggering builds.
                        var expected = _token;
                        var supplied = ctx.Request.Headers[TokenHeader];
                        if (string.IsNullOrEmpty(expected) ||
                            !string.Equals(supplied, expected, StringComparison.Ordinal))
                        {
                            Respond(res, 401, "{\"ok\":false,\"error\":\"unauthorized\"}");
                            break;
                        }
                        // The wire is always UTF-8 (the plugin posts JSON via fetch,
                        // which encodes JS strings as UTF-8). Request.ContentEncoding
                        // is NOT trustworthy: without an explicit charset in the
                        // Content-Type it falls back to a legacy default that turns
                        // each non-ASCII byte into '?' — "TEA → PCA" (U+2192, three
                        // UTF-8 bytes) arrived as "TEA ??? PCA".
                        string body;
                        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            body = sr.ReadToEnd();
                        lock (_gate) _inbox.Enqueue(body);
                        Respond(res, 202, "{\"ok\":true,\"queued\":true}");
                        break;
                    default:
                        Respond(res, 405, "{\"ok\":false,\"error\":\"method not allowed\"}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FigForge] live import request error: {e.Message}");
            }
        }

        static void Respond(HttpListenerResponse res, int status, string json)
        {
            try
            {
                res.StatusCode = status;
                if (!string.IsNullOrEmpty(json))
                {
                    res.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
                res.Close();
            }
            catch { /* client gone */ }
        }

        // ---- main thread ------------------------------------------------------
        static void Pump()
        {
            string body = null;
            lock (_gate) { if (_inbox.Count > 0) body = _inbox.Dequeue(); }
            if (body == null) return;

            try { ImportBundle(body); }
            catch (Exception e)
            {
                Status = $"import failed: {e.Message}";
                Debug.LogError($"[FigForge] live import failed: {e.Message}\n{e.StackTrace}");
            }
        }

        static void ImportBundle(string json)
        {
            var bundle = JsonUtility.FromJson<LiveBundle>(json);
            if (bundle == null || bundle.screens == null || bundle.screens.Length == 0)
                throw new Exception("empty or unparseable bundle");

            string projName = bundle.project != null && !string.IsNullOrEmpty(bundle.project.name)
                ? bundle.project.name : "Untitled";
            string destAssets = $"{LiveRoot}/{SafeName(projName)}";
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string destAbs = Path.Combine(projectRoot, destAssets.Replace('/', Path.DirectorySeparatorChar));

            // Clean re-import: drop the previous version of this project.
            if (Directory.Exists(destAbs)) Directory.Delete(destAbs, true);
            Directory.CreateDirectory(destAbs);

            var index = new ProjIndex
            {
                name = projName,
                initial = bundle.project != null ? bundle.project.initial : "",
                exportedAt = DateTime.UtcNow.ToString("o"),
            };
            var indexScreens = new List<ProjIndexScreen>();
            var used = new HashSet<string>();

            foreach (var s in bundle.screens)
            {
                string folder = SafeName(string.IsNullOrEmpty(s.name) ? "screen" : s.name);
                int n = 1;
                string baseFolder = folder;
                while (used.Contains(folder)) folder = $"{baseFolder}_{n++}";
                used.Add(folder);

                string folderAbs = Path.Combine(destAbs, folder);
                Directory.CreateDirectory(folderAbs);
                File.WriteAllText(Path.Combine(folderAbs, "manifest.json"), s.manifest ?? "{}");
                if (s.assets != null)
                    foreach (var a in s.assets)
                    {
                        if (a == null || string.IsNullOrEmpty(a.name)) continue;
                        // Flatten to the bare file name. Like ZipImporter, this
                        // neutralises any "../" path-traversal in the asset name
                        // so a crafted bundle can't escape the screen folder.
                        var assetName = Path.GetFileName(a.name);
                        if (string.IsNullOrEmpty(assetName)) continue;
                        byte[] bytes;
                        if (!string.IsNullOrEmpty(a.b64))
                        {
                            // v2.0 plugin: PNG bytes as a base64 string (~1.33×
                            // binary size on the wire vs ~3.7× for decimal
                            // number[] text, and far cheaper to JSON-parse).
                            try { bytes = Convert.FromBase64String(a.b64); }
                            catch (FormatException e)
                            {
                                Debug.LogWarning($"[FigForge] live import: asset '{assetName}' has malformed base64 — skipped ({e.Message})");
                                continue;
                            }
                        }
                        else if (a.data != null)
                        {
                            // Legacy v1.0 plugin: bytes as a JSON number array.
                            // Kept so an older plugin still works against this
                            // importer during the 1.0 → 2.0 transition.
                            // (JsonUtility can't union-type one field, hence a
                            // separate `b64` field rather than string-or-array
                            // detection on `data` itself.)
                            bytes = new byte[a.data.Length];
                            for (int i = 0; i < a.data.Length; i++) bytes[i] = (byte)a.data[i];
                        }
                        else continue;
                        File.WriteAllBytes(Path.Combine(folderAbs, assetName), bytes);
                    }

                indexScreens.Add(new ProjIndexScreen
                {
                    name = s.name,
                    manifest = $"{folder}/manifest.json",
                    section = s.section ?? "",
                    role = string.IsNullOrEmpty(s.role) ? "screen" : s.role,
                });
            }
            index.screens = indexScreens.ToArray();
            File.WriteAllText(Path.Combine(destAbs, "project.json"), JsonUtility.ToJson(index, true));

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            string projectJsonAsset = $"{destAssets}/project.json";
            Status = $"building {projName} ({index.screens.Length} screen(s))…";
            var w = EditorWindow.GetWindow<FigForgeImporterWindow>(false, "FigForge", true);
            if (!w.LiveBuildPage(projectJsonAsset))
            {
                Status = $"import failed: {projName} build rejected";
                throw new Exception($"bundle written, but importer rejected {projectJsonAsset}");
            }
            Status = $"imported {projName} ✓ ({DateTime.Now:HH:mm:ss})";
            Debug.Log($"[FigForge] live import: built '{projName}' from Figma → {destAssets}");
        }

        static string SafeName(string s)
        {
            s = string.IsNullOrEmpty(s) ? "Screen" : s;
            var chars = new char[s.Length];
            for (int i = 0; i < s.Length; i++) chars[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
            return new string(chars);
        }

        // ---- wire payload (mirrors the plugin's export-page-complete message) --
        [Serializable] class LiveBundle { public LiveProject project; public LiveScreen[] screens; }
        [Serializable] class LiveProject { public string name; public string initial; }
        [Serializable] class LiveScreen { public string name; public string manifest; public LiveAsset[] assets; public string section; public string role; }
        // `b64` (base64 string) is the v2.0 wire format; `data` (number array)
        // is the legacy v1.0 form — see the decode in ImportBundle.
        [Serializable] class LiveAsset { public string name; public string b64; public int[] data; }

        // ---- project.json index (mirrors the plugin's downloadProjectBundle) ---
        [Serializable] class ProjIndex
        {
            public string schema = "figforge/project";
            public string version = "2.0";
            public string generator = "FigForge";
            public string name;
            public string exportedAt;
            public string initial;
            public ProjIndexScreen[] screens;
        }
        [Serializable] class ProjIndexScreen { public string name; public string manifest; public string section; public string role; }
    }
}
