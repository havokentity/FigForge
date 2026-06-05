// =============================================================================
// FigForge — [ReadOnly] field attribute. Greys a serialized field out in the
// Inspector so it can't be hand-edited, while it stays serialized and fully
// settable from code. Used for importer-stamped identity (project/screen name,
// importKey, manifestHash) that must reflect the import, not manual edits.
// (Drawn by ReadOnlyDrawer in the editor assembly.)
// =============================================================================

using UnityEngine;

namespace FigForge
{
    public class ReadOnlyAttribute : PropertyAttribute { }
}
