// =============================================================================
// FigForge — C# identifier helper for the frame codegen. Turns a designer's Figma
// layer name into a valid C# identifier while PRESERVING the original casing and
// intentional underscores (characters C# cannot use are removed); dedupes
// names within a scope with _2/_3 suffixes in document order and reports
// collisions for a console warning.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace FigForge
{
    internal static class IdentifierUtil
    {
        // The full set of C# reserved keywords (contextual keywords excluded — they're
        // legal as identifiers). A name matching one is @-escaped rather than mangled.
        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked",
            "class","const","continue","decimal","default","delegate","do","double","else",
            "enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params",
            "private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true",
            "try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual",
            "void","volatile","while",
        };

        /// <summary>
        /// Convert a Figma display name to a valid C# identifier, preserving case.
        /// Characters C# cannot use are removed; any Unicode letter or digit is kept
        /// (so CJK / accented layer names survive instead of collapsing to "_");
        /// intentional underscores are preserved; a leading digit gets a '_' prefix;
        /// a reserved keyword gets an '@' prefix. Empty/garbage falls back to "_".
        /// </summary>
        public static string ToIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                // Roslyn accepts Unicode identifiers, so keep any letter or digit
                // (categories Lu/Ll/Lt/Lm/Lo, Nd) — not just ASCII a-z/A-Z/0-9.
                bool ok = char.IsLetterOrDigit(c) || c == '_';
                if (ok)
                {
                    sb.Append(c);
                }
            }

            string s = sb.ToString();
            if (s.Length == 0) return "_";
            // A C# identifier may not start with a digit (ASCII or Unicode Nd).
            if (char.IsDigit(s[0])) s = "_" + s;
            if (Keywords.Contains(s)) s = "@" + s;
            return s;
        }

        // ---------------------------------------------------------------------
        // Force-serialize marker. A layer named "[s]Total" opts that one element
        // into the generated accessor layer even when it's a label or image the
        // importer skips in components-only mode. Detected on the RAW display name
        // (case-insensitive, optional surrounding spaces) and STRIPPED here so the
        // "[s]" never leaks into the GameObject name or the C# variable.
        // ---------------------------------------------------------------------
        public static bool HasSerializeMarker(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            int i = 0;
            while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
            return i + 2 < raw.Length && raw[i] == '[' && (raw[i + 1] == 's' || raw[i + 1] == 'S') && raw[i + 2] == ']';
        }

        public static string StripSerializeMarker(string raw)
        {
            if (!HasSerializeMarker(raw)) return raw;
            int i = 0;
            while (char.IsWhiteSpace(raw[i])) i++;
            return raw.Substring(i + 3).TrimStart();
        }

        // ---------------------------------------------------------------------
        // Reserved accessor names — the inherited (public + protected) members of
        // the runtime base classes a generated accessor identifier must NOT shadow.
        // A frame class is `partial class <Frame> : FigForgeFrame` and a group class
        // is `: FigForgeFrameElement`; an accessor named like an inherited member
        // would emit CS0108 (and shadowing the nav `Show()` breaks user call sites).
        // HAND-MAINTAINED: keep these in sync with FigForgeFrame.cs / FigForgeFrameElement.cs
        // (and the Unity base members a layer could plausibly be named after). Seed the
        // dedupe scope with these so a colliding accessor gets suffixed (_2) instead.
        // Stored in @-stripped (Key) form — none are C# keywords so none carry an '@'.
        // ---------------------------------------------------------------------

        // FigForgeFrame's inherited surface (frame accessors live on this base).
        public static readonly IReadOnlyCollection<string> ReservedFrameMembers = new[]
        {
            // FigForgeFrame public/protected members
            "isShell", "usesShell", "persistent", "shellKey", "generatedType",
            "isVisible", "IsBound", "ScreenKey",
            "Show", "Hide", "SetVisible", "Guard", "OnShow", "OnHide", "OnBind",
            "__WireFrame", "__Get", "__GetList",
            // Common inherited Unity (Component/Behaviour/MonoBehaviour/Object) members
            // a Figma layer could realistically be named after.
            "transform", "gameObject", "enabled", "tag", "name", "hideFlags",
        };

        // FigForgeFrameElement's inherited surface (group/element accessors live on this base).
        public static readonly IReadOnlyCollection<string> ReservedElementMembers = new[]
        {
            // FigForgeFrameElement public/protected members
            "generatedType",
            "FigmaType", "FigmaTypeKey", "RectTransform", "GameObject", "isVisible",
            "SetVisible", "GetVisible", "ConfigureType",
            "__WireGroup", "__Get", "__GetList",
            // Common inherited Unity members (see note above).
            "transform", "gameObject", "enabled", "tag", "name", "hideFlags",
        };

        // Union of both base surfaces. A single dedupe pass in the codegen produces
        // identifiers for frame accessors, group scopes, AND group-child accessors,
        // so it seeds with the union to dodge a collision on whichever base it lands on.
        public static readonly IReadOnlyCollection<string> ReservedAccessorNames = BuildReservedUnion();

        static IReadOnlyCollection<string> BuildReservedUnion()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in ReservedFrameMembers) set.Add(n);
            foreach (var n in ReservedElementMembers) set.Add(n);
            return set;
        }

        /// <summary>
        /// Resolve a list of identifiers within one scope (a frame). The first occurrence
        /// keeps its name; later collisions get _2, _3, ... in the given (document) order.
        /// `onCollision(original, renamed)` fires per renamed entry for a console warning.
        /// `reserved` pre-seeds the taken set (inherited base members) so an accessor named
        /// like one is suffixed instead of shadowing it; reserved names never trigger
        /// `onCollision` for an unused entry — only an actual identifier collision does.
        /// </summary>
        public static List<string> Dedupe(IList<string> ids, Action<string, string> onCollision = null,
            IReadOnlyCollection<string> reserved = null)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (reserved != null) foreach (var r in reserved) taken.Add(r);
            var result = new List<string>(ids.Count);
            foreach (var id in ids)
            {
                if (taken.Add(id)) { result.Add(id); continue; }
                int n = 2;
                string renamed;
                do { renamed = id + "_" + n++; } while (!taken.Add(renamed));
                result.Add(renamed);
                onCollision?.Invoke(id, renamed);
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Collection detection helpers. Repeated same-typed siblings whose
        // identifiers share a stem + trailing index — "Item"/"Item_2"/"Item_3"
        // (from duplicate Figma names, deduped above) or "Slot0"/"Slot1"
        // (designer-numbered) — collapse into one ordered list accessor.
        // ---------------------------------------------------------------------

        /// <summary>The stem of an identifier for collection grouping: the name with a trailing
        /// run of digits removed (and a single trailing '_' dropped if it remains), e.g.
        /// "Slot12"→"Slot", "Item_2"→"Item". A name with no trailing digit IS its own stem
        /// ("Item"→"Item") so a deduped bare lead groups with its "_2"/"_3" siblings. Returns ""
        /// for a pure-digit name (no stem to share).</summary>
        public static string CollectionStem(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            int cut = id.Length;
            while (cut > 0 && id[cut - 1] >= '0' && id[cut - 1] <= '9') cut--;
            if (cut == id.Length) return id;            // no trailing digit → the name is its own stem
            string stem = id.Substring(0, cut);
            if (stem.EndsWith("_", StringComparison.Ordinal)) stem = stem.Substring(0, stem.Length - 1);
            return stem;
        }

        /// <summary>Parsed trailing-digit index of an identifier, for ordering within a collection.
        /// A name with no trailing digit (a deduped bare lead like "Item") returns -1 so it sorts
        /// first, ahead of "Item_2". Overlong digit runs are clamped defensively.</summary>
        public static int CollectionIndex(string id)
        {
            if (string.IsNullOrEmpty(id) || id[id.Length - 1] < '0' || id[id.Length - 1] > '9') return -1;
            int cut = id.Length;
            while (cut > 0 && id[cut - 1] >= '0' && id[cut - 1] <= '9') cut--;
            string digits = id.Substring(cut);
            if (digits.Length > 9) digits = digits.Substring(digits.Length - 9);
            return int.Parse(digits);
        }

        /// <summary>The collection accessor name: the stem, naively pluralised. "Slot"→"Slots",
        /// "Entry"→"Entries", "Box"→"Boxes". A stem ending in '_' keeps it: "Slot_"→"Slot_s".</summary>
        public static string Pluralize(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem;
            char last = stem[stem.Length - 1];
            if (stem.Length >= 2 && (last == 'y' || last == 'Y'))
            {
                char prev = stem[stem.Length - 2];
                bool prevVowel = "aeiouAEIOU".IndexOf(prev) >= 0;
                if (!prevVowel) return stem.Substring(0, stem.Length - 1) + (char.IsUpper(last) ? "IES" : "ies");
            }
            if (last == 's' || last == 'x' || last == 'z' ||
                stem.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
                return stem + (char.IsUpper(last) ? "ES" : "es");
            return stem + "s";
        }
    }
}
