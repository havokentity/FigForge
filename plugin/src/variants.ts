import type { CanonicalVariantAxis, CanonicalVariantProps } from './types';

export type VariantSource = CanonicalVariantAxis['source'];

export interface VariantInput {
  name: string;
  value: string | boolean;
  type?: string;
  source?: VariantSource;
  options?: string[];
}

function stripFigmaSuffix(name: string): string {
  return name.replace(/#[\d:]+$/, '').trim();
}

function words(value: string): string {
  return stripFigmaSuffix(value)
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_/|=-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

function key(value: string): string {
  return words(value).replace(/\s+/g, '');
}

function axisFor(name: string, value: string | boolean): string {
  const k = key(name);
  const v = typeof value === 'boolean' ? '' : key(value);
  if (['state', 'status', 'interaction', 'interactive', 'mode'].includes(k)) return 'state';
  if (['value', 'checked', 'check', 'selected', 'selection', 'current', 'on', 'enabled'].includes(k)) return 'value';
  if (['size', 'scale'].includes(k)) return 'size';
  if (['tone', 'intent', 'severity', 'type', 'variant', 'emphasis'].includes(k)) {
    if (['primary', 'secondary', 'success', 'warning', 'warn', 'error', 'info', 'danger'].includes(v)) {
      if (k === 'intent') return 'intent';
      if (k === 'severity' || v === 'success' || v === 'warning' || v === 'warn' || v === 'error' || v === 'danger' || v === 'info') return 'severity';
      return 'tone';
    }
  }
  if (typeof value === 'boolean') {
    if (['pressed', 'active', 'hover', 'rollover', 'highlighted', 'disabled', 'focused', 'focus'].includes(k)) return 'state';
    if (['checked', 'selected', 'current', 'on'].includes(k)) return 'value';
  }
  return stripFigmaSuffix(name).replace(/\s+/g, ' ').trim() || name;
}

function stateValue(name: string, value: string | boolean): string | undefined {
  if (typeof value === 'boolean') {
    if (!value) return undefined;
    const k = key(name);
    if (k === 'disabled') return 'disabled';
    if (k === 'focused' || k === 'focus') return 'focused';
    if (k === 'selected' || k === 'current') return 'selected';
    if (k === 'pressed' || k === 'active') return 'pressed';
    if (k === 'hover' || k === 'rollover' || k === 'highlighted') return 'hover';
    return undefined;
  }
  switch (key(value)) {
    case 'normal':
    case 'default':
    case 'regular':
    case 'enabled':
      return 'normal';
    case 'hover':
    case 'rollover':
    case 'highlight':
    case 'highlighted':
      return 'hover';
    case 'press':
    case 'pressed':
    case 'active':
      return 'pressed';
    case 'select':
    case 'selected':
    case 'current':
      return 'selected';
    case 'disabled':
    case 'disable':
      return 'disabled';
    case 'focused':
    case 'focus':
      return 'focused';
    default:
      return undefined;
  }
}

function valueValue(name: string, value: string | boolean): string | undefined {
  if (typeof value === 'boolean') {
    const k = key(name);
    if (k === 'enabled') return value ? 'on' : 'off';
    return value ? 'on' : 'off';
  }
  switch (key(value)) {
    case 'on':
    case 'true':
    case 'checked':
    case 'selected':
    case 'yes':
      return 'on';
    case 'off':
    case 'false':
    case 'unchecked':
    case 'unselected':
    case 'no':
      return 'off';
    default:
      return words(value) || String(value);
  }
}

function sizeValue(value: string | boolean): string {
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  switch (key(value)) {
    case 'xsmall':
    case 'extrasmall':
    case 'extra small':
    case 'xs':
      return 'xs';
    case 'small':
    case 'sm':
    case 's':
      return 's';
    case 'medium':
    case 'md':
    case 'm':
      return 'm';
    case 'large':
    case 'lg':
    case 'l':
      return 'l';
    case 'xlarge':
    case 'extralarge':
    case 'extra large':
    case 'xl':
      return 'xl';
    default:
      return words(value) || String(value);
  }
}

function toneValue(value: string | boolean): string | undefined {
  if (typeof value === 'boolean') return undefined;
  switch (key(value)) {
    case 'primary':
      return 'primary';
    case 'secondary':
      return 'secondary';
    case 'success':
      return 'success';
    case 'warning':
    case 'warn':
      return 'warning';
    case 'error':
      return 'error';
    case 'info':
      return 'info';
    case 'danger':
      return 'danger';
    default:
      return words(value) || String(value);
  }
}

function normalizeValue(axis: string, name: string, value: string | boolean): string | undefined {
  if (axis === 'state') return stateValue(name, value);
  if (axis === 'value') return valueValue(name, value);
  if (axis === 'size') return sizeValue(value);
  if (axis === 'tone' || axis === 'intent' || axis === 'severity') return toneValue(value);
  return typeof value === 'boolean' ? (value ? 'true' : 'false') : (words(value) || String(value));
}

// Recognized axis buckets legitimately collapse several differently-named
// properties onto one key (e.g. both "State" and "Interaction" → 'state'), and
// first-wins there is intentional. The fallback branch of axisFor instead returns
// the trimmed property name, so two DISTINCT fallback properties that trim to the
// same string would otherwise silently drop the second — those we disambiguate.
const RECOGNIZED_AXES = new Set(['state', 'value', 'size', 'tone', 'intent', 'severity']);

// When the SAME axis is supplied by more than one source, the instance's live,
// resolved value must win over component/variant defaults — relying on array push
// order made this fragile. Higher number = higher precedence. componentProperties
// is the instance's currently-bound value; componentSet is the master default.
const SOURCE_PRIORITY: Record<string, number> = {
  componentProperties: 4,
  variantProperties: 3,
  mainComponent: 2,
  componentSet: 1,
};
function sourcePriority(source: string | undefined): number {
  return source ? (SOURCE_PRIORITY[source] ?? 0) : 0;
}

export function normalizeVariantEntries(entries: VariantInput[]): CanonicalVariantProps | undefined {
  const raw: CanonicalVariantAxis[] = [];
  const axes: Record<string, string> = {};
  const original: Record<string, string> = {};
  // Priority of the source that currently owns each axis value — a later entry
  // from a higher-priority source (the instance's live value) overwrites it.
  const axisPriority: Record<string, number> = {};
  const sources = new Set<string>();

  for (const entry of entries) {
    if (!entry || !entry.name) continue;
    let axis = axisFor(entry.name, entry.value);
    const value = normalizeValue(axis, entry.name, entry.value);
    if (value === undefined) continue;
    const source = entry.source ?? 'componentProperties';
    const priority = sourcePriority(source);
    const originalValue = typeof entry.value === 'boolean' ? (entry.value ? 'true' : 'false') : String(entry.value);
    // Fallback (unrecognized) axis collision: a distinct property trimmed to an
    // already-used key. Suffix it (axis_2, axis_3, …) so it isn't silently lost.
    if (axes[axis] !== undefined && !RECOGNIZED_AXES.has(axis)) {
      let n = 2;
      while (axes[`${axis}_${n}`] !== undefined) n++;
      axis = `${axis}_${n}`;
    }
    // Set the axis if unseen, or if this source outranks the one that set it — so
    // the instance's resolved value wins over component/variant defaults instead
    // of whichever source happened to be pushed first.
    if (axes[axis] === undefined || priority > (axisPriority[axis] ?? -1)) {
      axes[axis] = value;
      original[axis] = originalValue;
      axisPriority[axis] = priority;
    }
    sources.add(source);
    raw.push({
      axis,
      value,
      originalName: entry.name,
      originalValue,
      source,
      type: entry.type,
      options: entry.options,
    });
  }

  if (raw.length === 0) return undefined;
  const out: CanonicalVariantProps = {
    axes,
    original,
    raw,
    source: [...sources].join('+'),
    diagnostics: [`axes: ${Object.keys(axes).map((a) => `${a}=${axes[a]}`).join(', ')}`],
  };
  if (axes.state !== undefined) out.state = axes.state;
  if (axes.value !== undefined) out.value = axes.value;
  if (axes.size !== undefined) out.size = axes.size;
  if (axes.tone !== undefined) out.tone = axes.tone;
  if (axes.intent !== undefined) out.intent = axes.intent;
  if (axes.severity !== undefined) out.severity = axes.severity;
  return out;
}

function variantEntriesFromObject(
  props: Record<string, unknown> | undefined | null,
  source: VariantSource,
  definitions?: ComponentPropertyDefinitions
): VariantInput[] {
  const out: VariantInput[] = [];
  if (!props) return out;
  for (const [name, prop] of Object.entries(props)) {
    if (prop == null) continue;
    if (typeof prop === 'string' || typeof prop === 'boolean') {
      out.push({ name, value: prop, source, options: definitions?.[name]?.variantOptions });
      continue;
    }
    if (typeof prop !== 'object') continue;
    const rec = prop as { value?: string | boolean; type?: string };
    if (rec.value === undefined) continue;
    out.push({ name, value: rec.value, type: rec.type, source, options: definitions?.[name]?.variantOptions });
  }
  return out;
}

export async function extractVariantProps(node: SceneNode): Promise<CanonicalVariantProps | undefined> {
  const entries: VariantInput[] = [];
  const inst = node.type === 'INSTANCE' ? node as InstanceNode : undefined;
  const main = inst ? await mainComponentOf(inst) : (node.type === 'COMPONENT' ? node as ComponentNode : null);
  const set = main?.parent?.type === 'COMPONENT_SET' ? main.parent as ComponentSetNode : null;
  const definitions = set?.componentPropertyDefinitions ?? main?.componentPropertyDefinitions;

  if (inst) {
    entries.push(...variantEntriesFromObject(inst.componentProperties, 'componentProperties', definitions));
    entries.push(...variantEntriesFromObject(inst.variantProperties, 'variantProperties', definitions));
  }
  if (main) {
    entries.push(...variantEntriesFromObject(main.variantProperties, 'mainComponent', definitions));
  }
  if (set && definitions) {
    for (const [name, def] of Object.entries(definitions)) {
      if (def.type !== 'VARIANT') continue;
      entries.push({ name, value: def.defaultValue, type: def.type, source: 'componentSet', options: def.variantOptions });
    }
  }

  return normalizeVariantEntries(entries);
}

export function variantValueOriginal(variants: CanonicalVariantProps | undefined, axis: string): string | undefined {
  return variants?.original?.[axis];
}

async function mainComponentOf(node: InstanceNode): Promise<ComponentNode | null> {
  try {
    return await node.getMainComponentAsync();
  } catch {
    return null;
  }
}
