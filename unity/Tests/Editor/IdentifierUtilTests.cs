// =============================================================================
// EditMode tests for FigForge.IdentifierUtil.ToIdentifier — focused on the
// Unicode-letter behaviour (CJK / accented / Greek layer names survive instead
// of collapsing to "_") while confirming the existing ASCII, leading-digit and
// keyword-escaping behaviour is unchanged.
// =============================================================================

using NUnit.Framework;

namespace FigForge.Tests
{
    public class IdentifierUtilTests
    {
        // --- Unicode letters are preserved (the i18n fix) -------------------

        [TestCase("名前", "名前")]              // CJK ideographs (category Lo)
        [TestCase("café", "café")]             // accented Latin (é, Ll)
        [TestCase("naïveButton", "naïveButton")]
        [TestCase("Größe", "Größe")]           // German ö / ß
        [TestCase("Ψυχή", "Ψυχή")]            // Greek
        public void ToIdentifier_KeepsUnicodeLetters(string raw, string expected)
        {
            Assert.AreEqual(expected, IdentifierUtil.ToIdentifier(raw));
        }

        // --- ASCII behaviour is unchanged ----------------------------------

        [TestCase("Total", "Total")]
        [TestCase("My Button", "MyButton")]      // space removed
        [TestCase("price$", "price")]            // punctuation removed
        [TestCase("a-b/c", "abc")]               // hyphen + slash removed
        [TestCase("snake_case", "snake_case")]   // intentional underscore preserved
        public void ToIdentifier_StripsIllegalCharsPreservesAscii(string raw, string expected)
        {
            Assert.AreEqual(expected, IdentifierUtil.ToIdentifier(raw));
        }

        // --- Leading digit handling (ASCII and Unicode) --------------------

        [TestCase("3D", "_3D")]
        [TestCase("123", "_123")]
        [TestCase("٢abc", "_٢abc")]             // Arabic-Indic digit two (Nd) leading
        public void ToIdentifier_PrefixesLeadingDigit(string raw, string expected)
        {
            Assert.AreEqual(expected, IdentifierUtil.ToIdentifier(raw));
        }

        // --- Reserved keyword escaping survives ----------------------------

        [TestCase("class", "@class")]
        [TestCase("int", "@int")]
        public void ToIdentifier_EscapesKeywords(string raw, string expected)
        {
            Assert.AreEqual(expected, IdentifierUtil.ToIdentifier(raw));
        }

        // --- Empty / garbage / non-letter falls back to "_" ----------------

        [TestCase("", "_")]
        [TestCase("   ", "_")]                    // whitespace only
        [TestCase("!@#$", "_")]                   // punctuation only
        [TestCase("😀", "_")]                     // supplementary-plane emoji → surrogates filtered
        public void ToIdentifier_FallsBackToUnderscore(string raw, string expected)
        {
            Assert.AreEqual(expected, IdentifierUtil.ToIdentifier(raw));
        }

        // Distinct Unicode-only names no longer collapse to a bare "_" (which
        // previously forced needless _2/_3 dedupe in a scope of CJK layers).
        [Test]
        public void ToIdentifier_DistinctUnicodeNamesStayDistinct()
        {
            string a = IdentifierUtil.ToIdentifier("名前");
            string b = IdentifierUtil.ToIdentifier("番号");
            Assert.AreNotEqual("_", a);
            Assert.AreNotEqual("_", b);
            Assert.AreNotEqual(a, b);
        }
    }
}
