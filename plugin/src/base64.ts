// =============================================================================
// FigForge — minimal base64 encoder for PNG byte payloads.
//
// Why hand-rolled: the JSON wire to Unity (live-import POST, MCP bridge) needs
// asset bytes as base64 strings, but Figma's plugin MAIN thread has no `btoa`
// and no Node `Buffer`, and `String.fromCharCode.apply(null, bytes)` overflows
// the call stack on multi-MB arrays. This encodes straight from the bytes in a
// single O(n) pass, flushing to the output in bounded chunks so no single
// string operation scales with the asset size. Used by both bundles (main.ts
// for the MCP path, ui.ts for the live-import POST).
// =============================================================================

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';

export function bytesToBase64(bytes: Uint8Array): string {
  const n = bytes.length;
  const parts: string[] = [];
  let s = '';
  let i = 0;
  for (; i + 2 < n; i += 3) {
    const b0 = bytes[i];
    const b1 = bytes[i + 1];
    const b2 = bytes[i + 2];
    s +=
      B64[b0 >> 2] +
      B64[((b0 & 0x03) << 4) | (b1 >> 4)] +
      B64[((b1 & 0x0f) << 2) | (b2 >> 6)] +
      B64[b2 & 0x3f];
    // Flush so `s` stays small: appending to a short string is cheap and the
    // final join concatenates a few hundred parts instead of growing one
    // multi-MB string four chars at a time.
    if (s.length >= 0x8000) {
      parts.push(s);
      s = '';
    }
  }
  if (i < n) {
    // 1 or 2 trailing bytes → 4-char group with '=' padding.
    const b0 = bytes[i];
    const b1 = i + 1 < n ? bytes[i + 1] : 0;
    s +=
      B64[b0 >> 2] +
      B64[((b0 & 0x03) << 4) | (b1 >> 4)] +
      (i + 1 < n ? B64[(b1 & 0x0f) << 2] : '=') +
      '=';
  }
  parts.push(s);
  return parts.join('');
}
