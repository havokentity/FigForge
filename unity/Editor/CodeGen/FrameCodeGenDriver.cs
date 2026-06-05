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
        public static FrameModel Generate(Manifest m, string section = "")
        {
            var model = BuildModel(m, section);
            WriteFiles(model);
            return model;
        }

        public static FrameModel BuildModel(Manifest m, string section)
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
                if (e == null || e.id == rootId || !IsMember(e)) continue;
                picked.Add(e);
                rawIds.Add(IdentifierUtil.ToIdentifier(!string.IsNullOrEmpty(e.displayName) ? e.displayName : e.name));
            }
            var ids = IdentifierUtil.Dedupe(rawIds, (orig, renamed) =>
                Debug.LogWarning($"[FigForge] frame '{className}': duplicate accessor name '{orig}' → '{renamed}'."));

            var members = new List<FrameMember>(picked.Count);
            for (int i = 0; i < picked.Count; i++)
                members.Add(new FrameMember
                {
                    identifier = ids[i],
                    csharpType = FrameCodeGen.CSharpType(picked[i]),
                    sourceName = picked[i].id, // element id — stable handle for prefab wiring
                });

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
        // Plain structural containers are skipped (they're layout, not handles).
        static bool IsMember(ElementData e)
        {
            if (e.canonical != null && !string.IsNullOrEmpty(e.canonical.kind)) return true;
            if (e.type == "TEXT") return true;
            if (e.components != null && (e.components.Contains("TextMeshProUGUI") || e.components.Contains("Image"))) return true;
            return false;
        }

        public static void WriteFiles(FrameModel f)
        {
            Directory.CreateDirectory(FramesDir);
            WriteIfChanged(GenRoot + "/FigForge.Generated.asmdef", FrameCodeGen.EmitAsmdef());

            string frameCs = FramesDir + "/" + f.className + ".g.cs";
            WriteIfChanged(frameCs, FrameCodeGen.EmitFrameClass(f));
            WriteIfChanged(frameCs + ".meta", FrameCodeGen.EmitScriptMeta(f.scriptGuid));

            WriteIfChanged(GenRoot + "/Frames." + f.className + ".g.cs", FrameCodeGen.EmitFrameManagerForFrame(f));

            // Compile on the next tick — never mid-import (a domain reload would abort it).
            EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        static void WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content);
        }
    }
}
