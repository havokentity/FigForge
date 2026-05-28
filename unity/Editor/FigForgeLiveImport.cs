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
        const int DefaultPort = 1995;
        const string LiveRoot = "Assets/FigForge/Live";

        static HttpListener _listener;
        static readonly Queue<string> _inbox = new Queue<string>();
        static readonly object _gate = new object();

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
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{Port}/");
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
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                switch (ctx.Request.HttpMethod)
                {
                    case "OPTIONS":
                        Respond(res, 204, null);
                        break;
                    case "GET":
                        Respond(res, 200, "{\"ok\":true,\"product\":\"FigForge\",\"live\":true}");
                        break;
                    case "POST":
                        string body;
                        using (var sr = new StreamReader(ctx.Request.InputStream,
                                   ctx.Request.ContentEncoding ?? Encoding.UTF8))
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
                        if (a == null || string.IsNullOrEmpty(a.name) || a.data == null) continue;
                        var bytes = new byte[a.data.Length];
                        for (int i = 0; i < a.data.Length; i++) bytes[i] = (byte)a.data[i];
                        File.WriteAllBytes(Path.Combine(folderAbs, a.name), bytes);
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
            w.LiveBuildPage(projectJsonAsset);
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
        [Serializable] class LiveAsset { public string name; public int[] data; }

        // ---- project.json index (mirrors the plugin's downloadProjectBundle) ---
        [Serializable] class ProjIndex
        {
            public string schema = "figforge/project";
            public string version = "1.0";
            public string generator = "FigForge";
            public string name;
            public string exportedAt;
            public string initial;
            public ProjIndexScreen[] screens;
        }
        [Serializable] class ProjIndexScreen { public string name; public string manifest; public string section; public string role; }
    }
}
