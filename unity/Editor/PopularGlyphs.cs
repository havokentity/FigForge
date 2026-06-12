using System;
using System.Collections.Generic;
using System.Text;

namespace FigForge
{
    public static class PopularGlyphs
    {
        public static string All { get; }

        static PopularGlyphs()
        {
            var seen = new HashSet<int>();
            var builder = new StringBuilder();

            // Basic Latin printable.
            AppendRange(builder, seen, 0x0020, 0x007E);

            // Latin-1 Supplement printable: Western European punctuation, accents, and symbols.
            AppendRange(builder, seen, 0x00A0, 0x00FF);

            // Latin Extended-A: Central and Northern European letters.
            AppendRange(builder, seen, 0x0100, 0x017F);

            // General Punctuation commonly seen in product copy and UI text.
            AppendRange(builder, seen, 0x2010, 0x2027);
            Append(builder, seen, 0x2030, 0x2032, 0x2033, 0x2039, 0x203A, 0x2044);

            // Superscripts used for exponents, ordinal-style labels, and small UI annotations.
            Append(builder, seen, 0x2070);
            AppendRange(builder, seen, 0x2074, 0x2079);
            AppendRange(builder, seen, 0x207A, 0x207E);

            // Currency marks used by major locales and common design mocks.
            Append(builder, seen, 0x20A9, 0x20AA, 0x20AB, 0x20AC, 0x20B9, 0x20BA, 0x20BD);

            // Letterlike symbols that often appear in brand, legal, and recipe text.
            Append(builder, seen, 0x2113, 0x2116, 0x2122, 0x2126);

            // Directional arrows for navigation, affordances, and shortcut labels.
            AppendRange(builder, seen, 0x2190, 0x2199);
            Append(builder, seen, 0x21A9, 0x21AA);
            AppendRange(builder, seen, 0x21D0, 0x21D4);

            // Mathematical operators used in dashboards, specs, and technical copy.
            Append(builder, seen, 0x2202, 0x2206, 0x220F, 0x2211, 0x2212, 0x2215, 0x2219, 0x221A,
                0x221E, 0x222B, 0x2248, 0x2260, 0x2261, 0x2264, 0x2265);

            // Geometric shapes used as bullets, indicators, and icon-like text.
            Append(builder, seen, 0x25A0, 0x25A1, 0x25AA, 0x25AB, 0x25B2, 0x25B3, 0x25B6, 0x25BA,
                0x25BC, 0x25BD, 0x25C0, 0x25C4, 0x25C6, 0x25C7, 0x25CB, 0x25CF);

            // Miscellaneous UI symbols: stars, checkboxes, warning, checks, crosses, and heart.
            Append(builder, seen, 0x2605, 0x2606, 0x2610, 0x2611, 0x26A0, 0x2713, 0x2714, 0x2715,
                0x2717, 0x2718, 0x2764);

            // Unicode replacement character for explicitly broken or substituted text.
            Append(builder, seen, 0xFFFD);

            All = builder.ToString();
        }

        static void AppendRange(StringBuilder builder, HashSet<int> seen, int first, int last)
        {
            for (int codePoint = first; codePoint <= last; codePoint++)
                Append(builder, seen, codePoint);
        }

        static void Append(StringBuilder builder, HashSet<int> seen, params int[] codePoints)
        {
            foreach (int codePoint in codePoints)
            {
                if (seen.Add(codePoint))
                    builder.Append(char.ConvertFromUtf32(codePoint));
            }
        }
    }
}
