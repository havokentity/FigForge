// =============================================================================
// FigForge — frame codegen driver. After a frame is built, this turns its manifest
// into a FrameModel (frame-scoped members, deduped case-preserved identifiers,
// mapped C# types) and writes the generated accessor files into the project:
//   Assets/FigForge/Generated/
//     FigForge.Generated.asmdef
//     UIFrames/<Frame>.g.cs (+ .meta with a deterministic GUID)
//     UIFrames.<Frame>.g.cs   (one partial per frame; no cross-import clobber)
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
        const string UIFramesDir = GenRoot + "/UIFrames";

        // ---- import batch (full multi-frame import) -----------------------------------
        // A full project import calls WriteFiles once per frame. Without a shared notion of
        // "every frame in THIS import", each WriteFiles rebuilds its cross-frame reserved set
        // by scanning UIFrames.*.g.cs on disk — which (a) varies between frames as files are
        // written, so two frames in one section can compute different section class names
        // (CS0102), and (b) harvests orphaned files left by renamed/removed frames as if they
        // were real, and never deletes them. A batch fixes both: the importer declares the
        // expected frame class names up front (single source of truth), every per-frame emit
        // sees the SAME reserved set, and EndBatch sweeps the orphans. Outside a batch (a
        // single-frame incremental rebuild) we fall back to the on-disk scan and DO NOT sweep,
        // so a partial run can never delete files for frames it simply didn't touch.
        static HashSet<string> _batchFrameClassNames;

        /// <summary>Open a full-import batch. <paramref name="expectedFrameClassNames"/> is the
        /// complete set of frame class names this import will generate (overlays excluded — they
        /// have no frame class). While open, every frame's cross-frame reserved set is this exact
        /// set, so all frames in a section resolve to one nested class. Must be paired with
        /// <see cref="EndBatch"/> (call it from a finally).</summary>
        public static void BeginBatch(IEnumerable<string> expectedFrameClassNames)
        {
            _batchFrameClassNames = new HashSet<string>(System.StringComparer.Ordinal);
            if (expectedFrameClassNames != null)
                foreach (var n in expectedFrameClassNames)
                    if (!string.IsNullOrEmpty(n)) _batchFrameClassNames.Add(n);
        }

        /// <summary>Close a full-import batch and sweep orphaned UIFrames.&lt;Old&gt;.g.cs /
        /// UIFrames/&lt;Old&gt;.g.cs left by frames renamed or removed in Figma. Only runs for a
        /// full import (a batch was open); a single-frame rebuild never sweeps.</summary>
        public static void EndBatch()
        {
            var expected = _batchFrameClassNames;
            _batchFrameClassNames = null;
            if (expected == null) return;
            SweepOrphanFrameFiles(expected);
        }

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
            // Seed the dedupe scope with the inherited base-class members (FigForgeFrame +
            // FigForgeFrameElement) so an accessor named like one (Show, ScreenKey, isVisible,
            // …) is suffixed instead of shadowing it (CS0108). The pass produces identifiers
            // for frame accessors, group scopes, AND group-child accessors, so it reserves the
            // union of both bases.
            var ids = IdentifierUtil.Dedupe(rawIds, (orig, renamed) =>
                Debug.LogWarning($"[FigForge] frame '{className}': accessor name '{orig}' collides with a base member or sibling → '{renamed}'."),
                IdentifierUtil.ReservedAccessorNames);

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
            // Also reserve inherited base-class members so a collection accessor (and its
            // "_<Name>" backing field) named like one doesn't shadow it (CS0108).
            foreach (var r in IdentifierUtil.ReservedAccessorNames) taken.Add(r);

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
            Directory.CreateDirectory(UIFramesDir);
            bool changed = false;
            changed |= WriteIfChanged(GenRoot + "/FigForge.Generated.asmdef", FrameCodeGen.EmitAsmdef());
            // Deterministic .asmdef.meta so the asmdef GUID is source-control-stable across machines
            // (otherwise Unity mints a random GUID on first import → churn / cross-clone ref drift).
            changed |= WriteIfChanged(GenRoot + "/FigForge.Generated.asmdef.meta", FrameCodeGen.EmitAsmdefMeta());

            // Overlay layers are NOT navigable screens — they don't get a `<Frame> : FigForgeFrame`
            // class or a UIFrames.X accessor (and a layer named "Dialogs" would collide with the static
            // `Dialogs` accessor class). They only contribute global Dialogs.<Name> accessors below.
            if (!f.isOverlay)
            {
                string frameCs = UIFramesDir + "/" + f.className + ".g.cs";
                changed |= WriteIfChanged(frameCs, FrameCodeGen.EmitFrameClass(f));
                changed |= WriteIfChanged(frameCs + ".meta", FrameCodeGen.EmitScriptMeta(f.scriptGuid));

                changed |= WriteGroupFiles(f); // one component per group, in UIFrames/<Frame>.g/

                // A section shares `partial class UIFrames` with every frame's `<Frame> <Frame>`
                // member; reserve this frame's section against ALL frame class names (gathered
                // from sibling UIFrames.<class>.g.cs filenames + this frame) so a section that equals
                // a frame's class name is renamed instead of emitting a duplicate member (CS0102).
                var frameClassNames = SiblingFrameClassNames(f.className);
                changed |= WriteIfChanged(GenRoot + "/UIFrames." + f.className + ".g.cs", FrameCodeGen.EmitFrameManagerForFrame(f, frameClassNames));
                changed |= WriteIfChanged(GenRoot + "/UIFrames.Core.g.cs", FrameCodeGen.EmitFramesCore()); // navigation (Show/Current)
            }

            // Overlay layers also surface their FigForgeModals as global Dialogs.<Name>.
            string dialogsCs = GenRoot + "/Dialogs." + f.className + ".g.cs";
            if (f.isOverlay && FrameCodeGen.HasDialogs(f))
            {
                // Every per-overlay file feeds the SAME `partial class Dialogs`; two overlays whose
                // modals sanitize to one identifier would emit duplicate members across files
                // (CS0102, invisible to WriteIfChanged). Reserve against the dialog identifiers
                // other overlays already own so this file suffixes its colliding accessors.
                var reservedDialogIds = SiblingDialogIdentifiers(dialogsCs);
                changed |= WriteIfChanged(dialogsCs, FrameCodeGen.EmitDialogsForFrame(f, reservedDialogIds));
                changed |= WriteIfChanged(GenRoot + "/Dialogs.Core.g.cs", FrameCodeGen.EmitDialogsCore());
            }
            else if (File.Exists(dialogsCs))
            {
                // Frame stopped being an overlay (or lost its dialogs) — drop the stale accessors.
                // Via the AssetDatabase so the .meta + asset DB don't go stale.
                if (DeleteAsset(dialogsCs)) changed = true;
            }

            // Compile on the next tick — never mid-import (a domain reload would abort it).
            if (changed)
                EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        // One generated component file per group, in a folder beside the frame: UIFrames/<Frame>.g/.
        // Files are deterministically named + GUID'd (seeded by frame + group type) so prefab
        // YAML can reference the group component before it compiles. Stale files from removed or
        // renamed groups are deleted, and the folder is dropped when the frame has no groups.
        static bool WriteGroupFiles(FrameModel f)
        {
            string dir = UIFramesDir + "/" + f.className + ".g";
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
                    // Via the AssetDatabase so the .meta + asset DB stay consistent.
                    if (DeleteAsset(dir + "/" + Path.GetFileName(file))) changed = true;
                }
                if (expected.Count == 0) // no groups left — drop the folder entirely
                {
                    if (DeleteAsset(dir)) changed = true;
                    else Debug.LogWarning($"[FigForge] could not delete empty group folder '{dir}' — remove it manually.");
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

        // Delete a generated asset through the AssetDatabase so the .meta + asset DB stay
        // consistent (a raw File.Delete leaves an orphan .meta and a stale DB entry). Falls
        // back to File.Delete only if the asset isn't under the project's Assets/ DB. Returns
        // true if anything was removed.
        static bool DeleteAsset(string path)
        {
            if (path.StartsWith("Assets/", System.StringComparison.Ordinal) && AssetDatabase.DeleteAsset(path))
                return true;
            bool removed = false;
            if (File.Exists(path)) { File.Delete(path); removed = true; }
            if (File.Exists(path + ".meta")) { File.Delete(path + ".meta"); removed = true; }
            return removed;
        }

        // Full-import sweep (EndBatch only): delete UIFrames.<Old>.g.cs files and UIFrames/<Old>.g.cs
        // frame classes (+ their <Old>.g group folders) whose class name is no longer expected —
        // i.e. a frame renamed or removed in Figma. The filename is the authoritative class name
        // (body-free), same convention SiblingFrameClassNames relies on. Scoped to a FULL import so
        // it can't delete a frame that simply wasn't part of a partial rebuild.
        static void SweepOrphanFrameFiles(HashSet<string> expected)
        {
            if (!Directory.Exists(GenRoot)) return;

            // Stale UIFrames.<class>.g.cs (the per-frame accessor partial).
            const string fmPrefix = "UIFrames.";
            const string suffix = ".g.cs";
            foreach (var file in Directory.GetFiles(GenRoot, "UIFrames.*.g.cs"))
            {
                string name = Path.GetFileName(file);
                if (name == "UIFrames.Core.g.cs") continue;          // the fixed core, not a frame
                if (name.Length <= fmPrefix.Length + suffix.Length) continue;
                string cls = name.Substring(fmPrefix.Length, name.Length - fmPrefix.Length - suffix.Length);
                if (expected.Contains(cls)) continue;
                DeleteAsset(GenRoot + "/" + name);
            }

            // Stale Dialogs.<class>.g.cs (overlay accessors whose frame was renamed/removed) —
            // keyed by the same frame className, and DialogFrameClassNames harvests these too, so
            // an orphan would poison the reserved set just like an orphan UIFrames.<class>.g.cs.
            const string dlgPrefix = "Dialogs.";
            foreach (var file in Directory.GetFiles(GenRoot, "Dialogs.*.g.cs"))
            {
                string name = Path.GetFileName(file);
                if (name == "Dialogs.Core.g.cs") continue;          // the fixed core, not a frame
                if (name.Length <= dlgPrefix.Length + suffix.Length) continue;
                string cls = name.Substring(dlgPrefix.Length, name.Length - dlgPrefix.Length - suffix.Length);
                if (expected.Contains(cls)) continue;
                DeleteAsset(GenRoot + "/" + name);
            }

            // Stale UIFrames/<class>.g.cs (the FigForgeFrame subclass) + its <class>.g group folder.
            if (Directory.Exists(UIFramesDir))
            {
                foreach (var file in Directory.GetFiles(UIFramesDir, "*.g.cs"))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length <= suffix.Length) continue;
                    string cls = name.Substring(0, name.Length - suffix.Length);
                    if (expected.Contains(cls)) continue;
                    DeleteAsset(UIFramesDir + "/" + name);
                    string groupDir = UIFramesDir + "/" + cls + ".g";
                    if (Directory.Exists(groupDir) && !DeleteAsset(groupDir))
                        Debug.LogWarning($"[FigForge] could not delete stale group folder '{groupDir}' — remove it manually.");
                }
            }
        }

        // ---- cross-frame identifier reservation (CS0102 guards) -----------------------
        // UIFrames are generated one at a time, each writing its own `UIFrames.<class>.g.cs` /
        // `Dialogs.<class>.g.cs` into a SHARED partial class. A duplicate member emitted by a
        // sibling file is a cross-file CS0102 that WriteIfChanged can't catch (it only diffs the
        // one file). These helpers read what siblings already own so the current file can dodge it.

        // Every frame's class name, harvested from the sibling `UIFrames.<class>.g.cs` filenames
        // (the filename is the authoritative, body-free source) plus the frame being written.
        static HashSet<string> SiblingFrameClassNames(string selfClassName)
        {
            // During a full import the expected set is fixed up front — return it verbatim so
            // EVERY frame in the run (and so every frame in a section) sees the same reserved set
            // and resolves a section to one nested class. On-disk orphans are deliberately NOT
            // consulted here: they're swept at EndBatch instead of poisoning the reserved set.
            if (_batchFrameClassNames != null)
            {
                var batch = new HashSet<string>(_batchFrameClassNames, System.StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(selfClassName)) batch.Add(selfClassName);
                return batch;
            }

            var names = new HashSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(selfClassName)) names.Add(selfClassName);
            if (!Directory.Exists(GenRoot)) return names;
            const string prefix = "UIFrames.";
            const string suffix = ".g.cs";
            foreach (var file in Directory.GetFiles(GenRoot, "UIFrames.*.g.cs"))
            {
                string name = Path.GetFileName(file);
                if (name == "UIFrames.Core.g.cs") continue;          // the fixed core, not a frame
                if (name.Length <= prefix.Length + suffix.Length) continue;
                names.Add(name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length));
            }
            return names;
        }

        // The dialog accessor identifiers owned by OTHER overlay files — parsed from the stable
        // `public static FigForgeModal <id> =>` lines this generator emits. `selfPath` (the file
        // about to be rewritten) is skipped so a frame never reserves against its own prior output.
        static HashSet<string> SiblingDialogIdentifiers(string selfPath)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            if (!Directory.Exists(GenRoot)) return ids;
            string self = Path.GetFullPath(selfPath);
            const string marker = "public static FigForgeModal ";
            foreach (var file in Directory.GetFiles(GenRoot, "Dialogs.*.g.cs"))
            {
                if (Path.GetFileName(file) == "Dialogs.Core.g.cs") continue;
                if (Path.GetFullPath(file) == self) continue;       // don't reserve against ourselves
                foreach (var line in File.ReadAllLines(file))
                {
                    int at = line.IndexOf(marker, System.StringComparison.Ordinal);
                    if (at < 0) continue;
                    int start = at + marker.Length;
                    int end = start;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_' || line[end] == '@'))
                        end++;
                    if (end > start) ids.Add(line.Substring(start, end - start));
                }
            }
            return ids;
        }
    }
}
