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
        /// Characters C# cannot use are removed; intentional underscores are preserved;
        /// a leading digit gets a '_' prefix; a reserved keyword gets an '@' prefix.
        /// Empty/garbage falls back to "_".
        /// </summary>
        public static string ToIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (ok)
                {
                    sb.Append(c);
                }
            }

            string s = sb.ToString();
            if (s.Length == 0) return "_";
            if (s[0] >= '0' && s[0] <= '9') s = "_" + s;
            if (Keywords.Contains(s)) s = "@" + s;
            return s;
        }

        /// <summary>
        /// Resolve a list of identifiers within one scope (a frame). The first occurrence
        /// keeps its name; later collisions get _2, _3, ... in the given (document) order.
        /// `onCollision(original, renamed)` fires per renamed entry for a console warning.
        /// </summary>
        public static List<string> Dedupe(IList<string> ids, Action<string, string> onCollision = null)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
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
    }
}
