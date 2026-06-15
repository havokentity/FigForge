import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { normalizeVariantEntries } from '../../plugin/src/variants.ts';

describe('variant metadata normalizer', () => {
  it('normalizes common state aliases and casing', () => {
    const result = normalizeVariantEntries([
      { name: 'State', value: 'Regular' },
      { name: 'state', value: 'Rollover' },
      { name: 'Status', value: 'Pressed' },
      { name: 'Focused', value: true, type: 'BOOLEAN' },
    ]);

    assert.equal(result?.raw[0].axis, 'state');
    assert.equal(result?.raw[0].value, 'normal');
    assert.equal(result?.raw[1].value, 'hover');
    assert.equal(result?.raw[2].value, 'pressed');
    assert.equal(result?.raw[3].value, 'focused');
  });

  it('normalizes boolean checked/value aliases', () => {
    const checked = normalizeVariantEntries([{ name: 'Checked', value: true, type: 'BOOLEAN' }]);
    const unchecked = normalizeVariantEntries([{ name: 'checked', value: 'Unchecked', type: 'VARIANT' }]);

    assert.equal(checked?.value, 'on');
    assert.equal(checked?.axes.value, 'on');
    assert.equal(unchecked?.value, 'off');
  });

  it('normalizes size and tone-like axes while keeping original values', () => {
    const result = normalizeVariantEntries([
      { name: 'Size', value: 'Extra Large' },
      { name: 'Intent', value: 'Danger' },
      { name: 'Density', value: 'Compact' },
    ]);

    assert.equal(result?.size, 'xl');
    assert.equal(result?.intent, 'danger');
    assert.equal(result?.axes.Density, 'compact');
    assert.equal(result?.original?.size, 'Extra Large');
    assert.equal(result?.raw[0].originalName, 'Size');
  });

  it('returns undefined when metadata is absent', () => {
    assert.equal(normalizeVariantEntries([]), undefined);
  });
});
