using System.Runtime.CompilerServices;

// Expose FigForge.Editor internals (e.g. IdentifierUtil) to the EditMode test
// assembly. The test asmdef is gated behind UNITY_INCLUDE_TESTS, so this has no
// effect on shipped builds — it only matters when tests are compiled.
[assembly: InternalsVisibleTo("FigForge.Editor.Tests")]
