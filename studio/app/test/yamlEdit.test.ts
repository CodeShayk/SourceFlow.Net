// STU-UT-26..31: the minimal-edit YAML writer, against every WF-GOLD workflow: edits change only their own range,
// inverse edits restore the file byte for byte, and the result reads back with the new value.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { isMap, isScalar, isSeq, type Node, type Pair } from 'yaml';
import { describe, expect, it } from 'vitest';
import { blockItem, EditError, flow, insertItem, itemLines, parse, remove, scalar, setValue, valueAt, type Path } from '../src/edit/yamlEdit.ts';

const cases = join(process.cwd(), '..', '..', 'tests', 'workflow-golden', 'cases');
const golden = readdirSync(cases).sort().map((name) => ({ name, text: readFileSync(join(cases, name, 'workflow.sfflow.yaml'), 'utf8').replaceAll('\r\n', '\n') }));
const readable = golden.filter(({ text }) => {
  const doc = parse(text);
  return doc.errors.length === 0 && isMap(doc.contents);
});
const fulfilment = golden.find((g) => g.name === '01-order-fulfilment')!.text;
const NL = '\n';

/** Every scalar leaf of a document, with its path. */
function leaves(node: unknown, path: Path = []): Path[] {
  if (isMap(node)) {
    return (node.items as Pair[]).flatMap((p) => (isScalar(p.key) ? leaves(p.value, [...path, String(p.key.value)]) : []));
  }

  if (isSeq(node)) {
    return node.items.flatMap((item, i) => leaves(item, [...path, i]));
  }

  return isScalar(node) ? [path] : [];
}

/** The lengths of the common prefix and suffix of two texts. */
function changed(before: string, after: string): { prefix: number; suffix: number } {
  let prefix = 0;
  while (prefix < before.length && before[prefix] === after[prefix]) prefix++;
  let suffix = 0;
  while (suffix < before.length - prefix && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix++;
  return { prefix, suffix };
}

describe('STU-UT-26 scalars and flow values', () => {
  it('quotes only what would not read back as the same string', () => {
    expect(scalar('orders.place-order.v1')).toBe('orders.place-order.v1');
    expect(scalar('$data.orderId')).toBe('$data.orderId');
    expect(scalar('$data.amount > 5000')).toBe('$data.amount > 5000');
    expect(scalar('on')).toBe('on');
    expect(scalar('true')).toBe('"true"');
    expect(scalar('12')).toBe('"12"');
    expect(scalar('a: b')).toBe('"a: b"');
    expect(scalar('x # y')).toBe('"x # y"');
    expect(scalar('-x')).toBe('"-x"');
    expect(scalar('')).toBe('""');
    expect(scalar(3)).toBe('3');
    expect(scalar(false)).toBe('false');
    expect(scalar(null)).toBe('null');
    expect(flow({ max: 3, backoff: 'fixed', on: ['a', 'b'], nested: {} })).toBe('{ max: 3, backoff: fixed, on: [a, b], nested: {} }');
    expect(blockItem({ id: 'ship', send: { command: 'c', map: { id: '$data.id' } } }, '  ')).toBe('  - id: ship' + NL + '    send: { command: c, map: { id: $data.id } }');
    for (const value of ['true', '12', 'a: b', 'x # y', '-x', '', '[x]', '{x}', 'a,b', '@x', '%x', "it's", 'a' + NL + 'b', 'on', 'yes']) {
      expect(parse('k: ' + scalar(value) + NL).toJS()).toEqual({ k: value });
    }
  });
});

describe('STU-UT-27 scalars of every golden workflow are edited in place', () => {
  it.each(readable.map((g) => [g.name, g.text] as const))('%s', (_name, text) => {
    const before = parse(text);
    const all = leaves(before.contents);
    expect(all.length).toBeGreaterThan(0);
    const every = Math.max(1, Math.floor(all.length / 25)); // up to 25 values, spread through the file
    for (const path of all.filter((_, i) => i % every === 0)) {
      const node = before.getIn(path, true) as Node & { range: [number, number, number] };
      const edited = setValue(text, path, 'edited-value');
      const { prefix, suffix } = changed(text, edited);
      expect(prefix).toBeGreaterThanOrEqual(node.range[0]);
      expect(text.length - suffix).toBeLessThanOrEqual(node.range[1]);
      const expected = before.toJS() as Record<string | number, unknown>;
      let target: Record<string | number, unknown> = expected;
      path.slice(0, -1).forEach((key) => {
        target = target[key] as Record<string | number, unknown>;
      });
      target[path[path.length - 1]!] = 'edited-value';
      expect(parse(edited).toJS()).toEqual(expected);
    }
  }, 30_000);
});

describe('STU-UT-28 adding and removing keys restores the file byte for byte', () => {
  it.each(readable.map((g) => [g.name, g.text] as const))('%s', (_name, text) => {
    const doc = parse(text);
    const steps = doc.getIn(['steps']);
    const targets: Path[] = [['description-added-by-test']];
    if (isSeq(steps)) {
      steps.items.forEach((step, i) => {
        targets.push(['steps', i, 'added-by-test']);
        if (isMap(step)) {
          for (const pair of step.items as Pair[]) {
            if (isMap(pair.value) && isScalar(pair.key)) {
              targets.push(['steps', i, String(pair.key.value), 'added-by-test']);
            }
          }
        }
      });
    }

    for (const target of targets) {
      const added = setValue(text, target, { max: 3, then: 'fail' });
      expect(valueAt(added, target)).toEqual({ max: 3, then: 'fail' });
      expect(remove(added, target)).toBe(text);
    }
  }, 30_000);
});

describe('STU-UT-29 inserting and removing steps restores the file byte for byte', () => {
  const withSteps = readable.filter((g) => {
    const steps = parse(g.text).getIn(['steps']);
    return isSeq(steps) && steps.items.length > 0;
  });
  it.each(withSteps.map((g) => [g.name, g.text] as const))('%s', (_name, text) => {
    const count = (parse(text).getIn(['steps']) as { items: unknown[] }).items.length;
    const step = { id: 'added', send: { command: 'orders.cancel-order.v1', map: { orderId: '$data.orderId' } }, await: { success: 'orders.order-cancelled.v1' } };
    for (const index of [0, count]) {
      const inserted = insertItem(text, ['steps'], index, step);
      expect(valueAt(inserted, ['steps', index])).toEqual(step);
      expect((valueAt(inserted, ['steps']) as unknown[]).length).toBe(count + 1);
      expect(remove(inserted, ['steps', index])).toBe(text);
    }
  });
});

describe('STU-UT-30 edits on the order-fulfilment workflow keep its comments and layout', () => {
  it('adds a retry and a compensation, removes a failure, edits flow mappings', () => {
    let text = setValue(fulfilment, ['steps', 0, 'await', 'failure', 0, 'retry'], { max: 2 }); // + 1 line
    expect(valueAt(text, ['steps', 0, 'await', 'failure', 0, 'retry'])).toEqual({ max: 2 });
    text = setValue(text, ['steps', 2, 'compensate'], { send: { command: 'orders.cancel-order.v1', map: { orderId: '$data.orderId' } } }); // + 1 line
    text = remove(text, ['steps', 1, 'await', 'failure', 1]); // - 2 lines
    text = setValue(text, ['steps', 0, 'timeout', 'after'], 'PT10M');
    text = setValue(text, ['steps', 0, 'send', 'map', 'lines'], '$data.items');
    for (const comment of ['# local domain event of the owner', '# business retry', '# point of no return', '# forward recovery after pivot']) {
      expect(text).toContain(comment);
    }

    expect(valueAt(text, ['steps', 0, 'timeout'])).toEqual({ after: 'PT10M', retry: { max: 2 }, then: 'fail' });
    expect(valueAt(text, ['steps', 1, 'await', 'failure'])).toHaveLength(1);
    expect(valueAt(text, ['steps', 2, 'compensate', 'send', 'command'])).toBe('orders.cancel-order.v1');
    expect(text.split(NL).length).toBe(fulfilment.split(NL).length);
    expect(parse(text).errors).toEqual([]);
  });
});

describe('STU-UT-31 errors, flow collections, empty values and item lines', () => {
  it('refuses impossible edits and maps sequence items to their lines', () => {
    expect(() => setValue('- a' + NL, ['x'], 1)).toThrow(EditError);
    expect(() => setValue('a: 1' + NL, [], 1)).toThrow(EditError);
    expect(() => remove('a: 1' + NL, ['b'])).toThrow(EditError);
    expect(() => remove('a: 1' + NL, ['a'])).toThrow(/only key/);
    expect(() => insertItem('a: 1' + NL, ['a'], 0, 'x')).toThrow(/not a sequence/);
    expect(() => setValue('a: 1' + NL, ['a', 'b'], 1)).toThrow(/not a mapping/);
    expect(() => setValue('a: 1' + NL, ['b', 2], 'x')).toThrow(/only item 0/);
    expect(insertItem('a: [x, y]' + NL, ['a'], 1, 'z')).toBe('a: [x, z, y]' + NL);
    expect(insertItem('a: [x]' + NL, ['a'], 1, 'z')).toBe('a: [x, z]' + NL);
    expect(remove('a: [x, y]' + NL, ['a', 0])).toBe('a: [y]' + NL);
    expect(remove('a: { b: 1, c: 2 }' + NL, ['a', 'c'])).toBe('a: { b: 1 }' + NL);
    expect(remove('a: { b: 1 }' + NL, ['a', 'b'])).toBe('a: {}' + NL);
    expect(setValue('a: {}' + NL + 'b: 2' + NL, ['a', 'x'], 1)).toBe('a: { x: 1 }' + NL + 'b: 2' + NL);
    expect(setValue('a: { b: 1 }' + NL, ['a', 'list', 0], 'x')).toBe('a: { b: 1, list: [x] }' + NL);
    expect(setValue('a:' + NL + 'b: 2' + NL, ['a'], 'v')).toBe('a: v' + NL + 'b: 2' + NL);
    expect(setValue('a:' + NL + '  b: 1' + NL + 'c: 2' + NL, ['a'], 'flat')).toBe('a: flat' + NL + 'c: 2' + NL);

    const lines = itemLines(fulfilment, ['steps']);
    expect(lines).toHaveLength(5);
    const source = fulfilment.split(NL);
    expect(source[lines[0]!.from - 1]).toContain('- id: reserve-stock');
    expect(source[lines[1]!.from - 1]).toContain('- id: charge-payment');
    expect(lines[0]!.to).toBeLessThan(lines[1]!.from);
    expect(itemLines('a: 1' + NL, ['steps'])).toEqual([]);
  });
});
