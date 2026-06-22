// =============================================================================
// FigForge — frame codegen driver. After a frame is built, this turns its manifest
// into a FrameModel (frame-scoped members, deduped case-preserved identifiers,
// mapped C# types) and writes the generated accessor files into the project:
//   Assets/FigForge/Generated/
//     FigForge.Generated.asmdef
//     Frames/<Frame>.g.cs (+ .meta with a deterministic GUID)
//     FrameManager.<Frame>.g.cs   (one partial per frame; no cross-import clobber)
//
// The compile is scheduled via delayCall so the script import + domain reload happen
// AFTER the current import method returns (writing .cs + Refresh mid-import would
// reload the domain and abort the build). The prefab-YAML wiring that attaches the
// generated component + wires its [SerializeField] refs is a separate step.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    internal static class FrameCodeGenDriver
    {
        const string GenRoot = "Assets/FigForge/Generated";
        const string FramesDir = GenRoot + "/Frames";

        /// <summary>Generate the accessor layer for one imported frame. Returns the model
        /// so the caller can wire the prefab against the same identifiers.</summary>
        public static FrameModel Generate(Manifest m, string section = "", bool includeGroups = true)
        {
            var model = BuildModel(m, section, includeGroups);
            WriteFiles(model);
            return model;
        }

        public static FrameModel BuildModel(Manifest m, string section, bool includeGroups = true, bool componentsOnly = true, bool isOverlay = false)
        {
            string className = IdentifierUtil.ToIdentifier(
                !string.IsNullOrEmpty(m.screen.displayName) ? m.screen.displayName : m.screen.name);

            // root = the element with no parent; its named descendants hoist flat onto the
            // frame (frame-as-scope). Canonical control internals aren't in this list
            // (they live in partTrees), so a control is naturally a leaf.
            string rootId = null;
            foreach (var e in m.elements) if (string.IsNullOrEmpty(e.parentId)) { rootId = e.id; break; }

            var picked = new List<ElementData>();
            var rawIds = new List<string>();
            foreach (var e in m.elements)
            {
                if (e == null || e.id == rootId || !IsMember(e, includeGroups, componentsOnly)) continue;
                picked.Add(e);
                rawIds.Add(IdentifierUtil.ToIdentifier(
                    IdentifierUtil.StripSerializeMarker(!string.IsNullOrEmpty(e.displayName) ? e.displayName : e.name)));
            }
            var ids = IdentifierUtil.Dedupe(rawIds, (orig, renamed) =>
                Debug.LogWarning($"[FigForge] frame '{className}': duplicate accessor name '{orig}' → '{renamed}'."));

            var index = new Dictionary<string, ElementData>();
            foreach (var e in m.elements)
                if (e != null && !string.IsNullOrEmpty(e.id))
                    index[e.id] = e;

            var members = new List<FrameMember>(picked.Count);
            for (int i = 0; i < picked.Count; i++)
            {
                string scopeParentId = includeGroups ? NearestGroupAncestorId(picked[i], index, rootId) : null;
                bool isScope = includeGroups && IsScopeContainer(picked[i]);
                members.Add(new FrameMember
                {
                    identifier = ids[i],
                    csharpType = isScope
                        ? "FigForgeFrameElement"
                        : FrameCodeGen.CSharpType(picked[i]),
                    sourceName = picked[i].id, // element id — stable handle for prefab wiring
                    sourceType = picked[i].type,
                    registryKey = picked[i].name ?? "", // sanitized name — runtime Dialogs.X lookup key

                    parentId = picked[i].parentId,
                    scopeParentId = scopeParentId,
                    exposeOnFrame = string.IsNullOrEmpty(scopeParentId),
                    isGroup = isScope,
                });
            }
            AssignScopeTypeNames(className, members);
            DetectCollections(className, members);

            return new FrameModel
            {
                className = className,
                screenKey = m.screen.name,
                section = IdentifierUtil.ToIdentifier(section ?? "") == "_" ? "" : IdentifierUtil.ToIdentifier(section ?? ""),
                scriptGuid = FrameCodeGen.DeterministicGuid(className),
                isOverlay = isOverlay,
                members = members,
            };
        }

        // Which elements get a typed accessor. Canonical controls (buttons, toggles,
        // inputs, …) always qualify. Labels (text) and plain images qualify only when
        // components-only mode is OFF — by default they're skipped to keep the generated
        // API focused on interactive controls. A "[s]" name marker force-includes any
        // single element regardless. Structural Figma containers surface as child scopes
        // when the groups toggle is on. Treat FRAME like GROUP here: a nested frame with
        // an image fill is still a scope, not just an Image leaf.
        static bool IsMember(ElementData e, bool includeGroups, bool componentsOnly)
        {
            if (e == null) return false;
            if (IdentifierUtil.HasSerializeMarker(e.displayName)) return true; // [s] force-include
            if (e.canonical != null && !string.IsNullOrEmpty(e.canonical.kind)) return true;
            if (includeGroups && IsScopeContainer(e)) return true;
            if (!componentsOnly)
            {
                if (e.type == "TEXT") return true;
                if (e.components != null && (e.components.Contains("TextMeshProUGUI") || e.components.Contains("Image"))) return true;
            }
            return false;
        }

        static bool IsScopeContainer(ElementData e)
            => e != null && (e.type == "GROUP" || e.type == "FRAME");

        // Each group becomes a top-level FigForgeFrameElement subclass (its own component +
        // file), so its type name must be unique in the FigForge.Generated namespace — we
        // frame-qualify it (e.g. LaunchPage_HeaderGroup) and dedupe within the frame. The
        // group's ref is typed as that component, so set csharpType to match.
        static void AssignScopeTypeNames(string className, List<FrameMember> members)
        {
            var taken = new HashSet<string> { className }; // never collide with the frame class itself

            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (!m.isGroup) continue;

                string stem = (m.identifier ?? "").TrimStart('@');
                string typeSuffix = m.sourceType == "FRAME" ? "Frame" : "Group";
                string baseName = className + "_" + IdentifierUtil.ToIdentifier(stem + typeSuffix).TrimStart('@');
                string typeName = baseName;
                int suffix = 2;
                while (!taken.Add(typeName))
                    typeName = baseName + suffix++;

                m.groupTypeName = typeName;
                m.csharpType = typeName; // the group ref is typed as its generated component
                members[i] = m;
            }
        }

        // Repeated same-typed leaf siblings whose identifiers share a stem + trailing index —
        // "Item"/"Item_2"/"Item_3" (four Figma layers all named "Item", deduped) or "Slot0"/
        // "Slot1" (designer-numbered) — collapse into ONE ordered IReadOnlyList<T> accessor named
        // after the pluralised stem. Per-element members survive (registered + wired individually);
        // only their single accessors are suppressed. Auto, naming-convention based, scope-aware:
        // members only group with siblings sharing the same scope parent and C# type.
        static void DetectCollections(string className, List<FrameMember> members)
        {
            // Names already taken by individual members or the frame's reserved key — a collection
            // accessor (and its "_<Name>" backing field) must dodge them.
            var taken = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var m in members) taken.Add(m.Key);
            taken.Add("__ScreenKey");

            // Bucket candidate leaves by (scope parent, csharpType, stem), first-seen order.
            var order = new List<(string scope, string type, string stem)>();
            var groups = new Dictionary<(string, string, string), List<int>>();
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (m.isGroup) continue;                                   // scopes never collect
                string stem = IdentifierUtil.CollectionStem(m.Key);
                if (string.IsNullOrEmpty(stem)) continue;                  // pure-digit name → no stem
                var key = (m.scopeParentId ?? "", m.csharpType ?? "", stem);
                if (!groups.TryGetValue(key, out var list)) { list = new List<int>(); groups[key] = list; order.Add(key); }
                list.Add(i);
            }

            foreach (var key in order)
            {
                var idxs = groups[key];
                if (idxs.Count < 2) continue;                              // a lone Slot0 stays a single member

                string accessor = IdentifierUtil.Pluralize(key.stem);
                if (!taken.Add(accessor))
                {
                    Debug.LogWarning($"[FigForge] frame '{className}': collection '{accessor}' (from '{key.stem}*') " +
                        "collides with an existing accessor — left as individual members.");
                    continue;
                }

                idxs.Sort((a, b) => IdentifierUtil.CollectionIndex(members[a].Key)
                                        .CompareTo(IdentifierUtil.CollectionIndex(members[b].Key)));
                foreach (int i in idxs)
                {
                    var m = members[i];
                    m.collectionName = accessor;
                    m.collectionIndex = IdentifierUtil.CollectionIndex(m.Key);
                    members[i] = m;
                }
            }
        }

        static string NearestGroupAncestorId(ElementData e, Dictionary<string, ElementData> index, string rootId)
        {
            string parentId = e != null ? e.parentId : null;
            var seen = new HashSet<string>();
            while (!string.IsNullOrEmpty(parentId) && seen.Add(parentId))
            {
                if (!index.TryGetValue(parentId, out var parent) || parent == null) return null;
                if (parent.id != rootId && IsScopeContainer(parent)) return parent.id;
                parentId = parent.parentId;
            }
            return null;
        }

        public static void WriteFiles(FrameModel f)
        {
            Directory.CreateDirectory(FramesDir);
            bool changed = false;
            changed |= WriteIfChanged(GenRoot + "/FigForge.Generated.asmdef", FrameCodeGen.EmitAsmdef());

            // Overlay layers are NOT navigable screens — they don't get a `<Frame> : FigForgeFrame`
            // class or a Frames.X accessor (and a layer named "Dialogs" would collide with the static
            // `Dialogs` accessor class). They only contribute global Dialogs.<Name> accessors below.
            if (!f.isOverlay)
            {
                string frameCs = FramesDir + "/" + f.className + ".g.cs";
                changed |= WriteIfChanged(frameCs, FrameCodeGen.EmitFrameClass(f));
                changed |= WriteIfChanged(frameCs + ".meta", FrameCodeGen.EmitScriptMeta(f.scriptGuid));

                changed |= WriteGroupFiles(f); // one component per group, in Frames/<Frame>.g/

                changed |= WriteIfChanged(GenRoot + "/Frames." + f.className + ".g.cs", FrameCodeGen.EmitFrameManagerForFrame(f));
                changed |= WriteIfChanged(GenRoot + "/Frames.Core.g.cs", FrameCodeGen.EmitFramesCore()); // navigation (Show/Current)
            }

            // Overlay layers also surface their FigForgeModals as global Dialogs.<Name>.
            string dialogsCs = GenRoot + "/Dialogs." + f.className + ".g.cs";
            if (f.isOverlay && FrameCodeGen.HasDialogs(f))
            {
                changed |= WriteIfChanged(dialogsCs, FrameCodeGen.EmitDialogsForFrame(f));
                changed |= WriteIfChanged(GenRoot + "/Dialogs.Core.g.cs", FrameCodeGen.EmitDialogsCore());
            }
            else if (File.Exists(dialogsCs))
            {
                // Frame stopped being an overlay (or lost its dialogs) — drop the stale accessors.
                File.Delete(dialogsCs);
                if (File.Exists(dialogsCs + ".meta")) File.Delete(dialogsCs + ".meta");
                changed = true;
            }

            // Compile on the next tick — never mid-import (a domain reload would abort it).
            if (changed)
                EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        // One generated component file per group, in a folder beside the frame: Frames/<Frame>.g/.
        // Files are deterministically named + GUID'd (seeded by frame + group type) so prefab
        // YAML can reference the group component before it compiles. Stale files from removed or
        // renamed groups are deleted, and the folder is dropped when the frame has no groups.
        static bool WriteGroupFiles(FrameModel f)
        {
            string dir = FramesDir + "/" + f.className + ".g";
            bool changed = false;
            var expected = new HashSet<string>();

            if (f.members != null)
            {
                foreach (var m in f.members)
                {
                    if (!m.isGroup) continue;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string fileName = m.groupTypeName + ".g.cs";
                    string path = dir + "/" + fileName;
                    string guid = FrameCodeGen.DeterministicGuid(f.className + "." + m.groupTypeName);
                    changed |= WriteIfChanged(path, FrameCodeGen.EmitGroupClass(f, m));
                    changed |= WriteIfChanged(path + ".meta", FrameCodeGen.EmitScriptMeta(guid));
                    expected.Add(fileName);
                }
            }

            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.g.cs"))
                {
                    if (expected.Contains(Path.GetFileName(file))) continue;
                    File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                    changed = true;
                }
                if (expected.Count == 0) // no groups left — drop the folder entirely
                {
                    try { Directory.Delete(dir, true); } catch { /* best-effort */ }
                    if (File.Exists(dir + ".meta")) File.Delete(dir + ".meta");
                    changed = true;
                }
            }
            return changed;
        }

        static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return false;
            File.WriteAllText(path, content);
            return true;
        }
    }
}
