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

        public static FrameModel BuildModel(Manifest m, string section, bool includeGroups = true)
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
                if (e == null || e.id == rootId || !IsMember(e, includeGroups)) continue;
                picked.Add(e);
                rawIds.Add(IdentifierUtil.ToIdentifier(!string.IsNullOrEmpty(e.displayName) ? e.displayName : e.name));
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
                        ? "FigForge.FigForgeFrameElement"
                        : FrameCodeGen.CSharpType(picked[i]),
                    sourceName = picked[i].id, // element id — stable handle for prefab wiring
                    sourceType = picked[i].type,
                    parentId = picked[i].parentId,
                    scopeParentId = scopeParentId,
                    exposeOnFrame = string.IsNullOrEmpty(scopeParentId),
                    isGroup = isScope,
                });
            }
            AssignScopeTypeNames(members);

            return new FrameModel
            {
                className = className,
                screenKey = m.screen.name,
                section = IdentifierUtil.ToIdentifier(section ?? "") == "_" ? "" : IdentifierUtil.ToIdentifier(section ?? ""),
                scriptGuid = FrameCodeGen.DeterministicGuid(className),
                members = members,
            };
        }

        // Which elements get a typed accessor: canonical controls + text + image/graphics.
        // Structural Figma containers can also be surfaced as generated child scopes
        // when the importer option is on. Treat FRAME like GROUP here: a nested
        // frame with an image fill is still a scope, not just an Image leaf.
        static bool IsMember(ElementData e, bool includeGroups)
        {
            if (e.canonical != null && !string.IsNullOrEmpty(e.canonical.kind)) return true;
            if (e.type == "TEXT") return true;
            if (e.components != null && (e.components.Contains("TextMeshProUGUI") || e.components.Contains("Image"))) return true;
            if (includeGroups && IsScopeContainer(e)) return true;
            return false;
        }

        static bool IsScopeContainer(ElementData e)
            => e != null && (e.type == "GROUP" || e.type == "FRAME");

        static void AssignScopeTypeNames(List<FrameMember> members)
        {
            var taken = new HashSet<string>();
            foreach (var member in members)
                taken.Add((member.identifier ?? "").TrimStart('@'));

            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (!m.isGroup) continue;

                string stem = (m.identifier ?? "").TrimStart('@');
                string typeSuffix = m.sourceType == "FRAME" ? "Frame" : "Group";
                string baseName = IdentifierUtil.ToIdentifier(stem + typeSuffix).TrimStart('@');
                string typeName = baseName;
                int suffix = 2;
                while (!taken.Add(typeName))
                    typeName = baseName + suffix++;

                m.groupTypeName = typeName;
                members[i] = m;
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

            string frameCs = FramesDir + "/" + f.className + ".g.cs";
            changed |= WriteIfChanged(frameCs, FrameCodeGen.EmitFrameClass(f));
            changed |= WriteIfChanged(frameCs + ".meta", FrameCodeGen.EmitScriptMeta(f.scriptGuid));

            changed |= WriteIfChanged(GenRoot + "/Frames." + f.className + ".g.cs", FrameCodeGen.EmitFrameManagerForFrame(f));
            changed |= WriteIfChanged(GenRoot + "/Frames.Core.g.cs", FrameCodeGen.EmitFramesCore()); // navigation (Show/Current)

            // Compile on the next tick — never mid-import (a domain reload would abort it).
            if (changed)
                EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return false;
            File.WriteAllText(path, content);
            return true;
        }
    }
}
