// The DSL reference (docs/v3.0.0/guides/workflow-dsl-reference.md) is generated from the normative schema
// (WP-S.13): it lists every key the schema allows, and the committed file is current.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { referencePath, render, schemaPath } from '../../scripts/dsl-reference.mjs';

const schema = JSON.parse(readFileSync(schemaPath, 'utf8'));
const reference = render(schema);

describe('the workflow DSL reference', () => {
  it('is current: run npm run docs:dsl in studio/ after changing schemas/workflow-1.json', () => {
    expect(readFileSync(referencePath, 'utf8').replaceAll('\r\n', '\n')).toBe(reference);
  });

  it('documents every key of every element, with a description', () => {
    const keys = [];
    const walk = (node) => {
      if (node === null || typeof node !== 'object') {
        return;
      }

      for (const [key, value] of Object.entries(node.properties ?? {})) {
        keys.push(key);
        expect(value.description, `the schema describes ${key}`).toBeTruthy();
      }

      // anyOf and not hold constraints (which keys go together), not keys of their own.
      for (const [key, value] of Object.entries(node)) {
        if (key !== 'anyOf' && key !== 'not') {
          walk(value);
        }
      }
    };
    walk(schema);
    for (const key of new Set(keys)) {
      const row = (line) => line.startsWith('| `') && (line.startsWith('| `' + key + '` |') || line.includes('.' + key + '` |'));
      expect(reference.split('\n').map((line) => line.trimStart()).some(row), `a row for ${key}`).toBe(true);
    }

    for (const element of ['start', 'step', 'send', 'await', 'success', 'failure', 'timeout', 'retry', 'then', 'compensate', 'on']) {
      expect(reference).toContain(`<a id="${element}"></a>`);
    }
  });
});
