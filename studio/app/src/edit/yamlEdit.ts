// The minimal-edit YAML writer (doc 15 §6.2: the DSL text is the model). Every visual edit becomes a splice of the
// smallest range of the original text: everything else (comments, ordering, spacing, alignment, quoting) stays byte for
// byte. The `yaml` parser only locates nodes; this module never re-serialises a document.
import { isMap, isPair, isScalar, isSeq, parseDocument, type Document, type Node, type Pair, type ParsedNode, type YAMLMap, type YAMLSeq } from 'yaml';

export type PathKey = string | number;
export type Path = readonly PathKey[];
export type Value = string | number | boolean | null | readonly Value[] | { readonly [key: string]: Value };

/** An edit the writer can't make (the path doesn't exist, or the document isn't a YAML mapping). */
export class EditError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'EditError';
  }
}

export function parse(text: string): Document.Parsed {
  return parseDocument(text, { keepSourceTokens: true, uniqueKeys: false });
}

/** The document, for an edit: a file that doesn't parse is edited as text only, so nothing is spliced into it. */
export function editable(text: string): Document.Parsed {
  const doc = parse(text);
  const error = doc.errors[0];
  if (error !== undefined) {
    const line = error.linePos?.[0].line;
    throw new EditError(`The YAML doesn't parse${line === undefined ? '' : ` (line ${line})`}: fix it in the YAML tab first.`);
  }

  return doc;
}

/** The JavaScript value at `path`, or undefined. */
export function valueAt(text: string, path: Path): unknown {
  const node = nodeAt(parse(text), path);
  return node === undefined ? undefined : (node as Node).toJSON();
}

// ------------------------------------------------------------------ formatting

// YAML 1.2 core schema (what the DSL reader and this parser use): `on`, `yes` and `no` are plain strings.
const RESERVED = /^(?:true|True|TRUE|false|False|FALSE|null|Null|NULL|~)$/;
const NUMBER = /^[-+]?(?:\d[\d_]*(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?$|^0x[0-9a-f]+$|^0o[0-7]+$|^[-+]?\.(?:inf|nan)$/i;

/** A scalar as YAML: plain when that reads back as the same string, otherwise double-quoted. */
export function scalar(value: string | number | boolean | null): string {
  if (value === null) {
    return 'null';
  }

  if (typeof value !== 'string') {
    return String(value);
  }

  const plain = value.length > 0
    && !RESERVED.test(value)
    && !NUMBER.test(value)
    && /^[^\s\-?:,[\]{}#&*!|>'"%@`]/.test(value)
    && !/[\s]$/.test(value)
    && !/: |:$| #|[\n\t,[\]{}]/.test(value);
  return plain ? value : JSON.stringify(value);
}

/** A value in flow style (one line): scalars, `[a, b]` and `{ a: 1, b: 2 }`. */
export function flow(value: Value): string {
  if (Array.isArray(value)) {
    return value.length === 0 ? '[]' : '[' + value.map(flow).join(', ') + ']';
  }

  if (value !== null && typeof value === 'object') {
    const entries = Object.entries(value);
    return entries.length === 0 ? '{}' : '{ ' + entries.map(([k, v]) => `${scalar(k)}: ${flow(v)}`).join(', ') + ' }';
  }

  return scalar(value as string | number | boolean | null);
}

/** A mapping as a block sequence item at `indent`: the first key after `- `, the rest below it, nested values in flow style. */
export function blockItem(value: { readonly [key: string]: Value }, indent: string): string {
  const entries = Object.entries(value);
  if (entries.length === 0) {
    return indent + '- {}';
  }

  return entries.map(([k, v], i) => (i === 0 ? indent + '- ' : indent + '  ') + `${scalar(k)}: ${flow(v)}`).join('\n');
}

// ------------------------------------------------------------------ locating

function nodeAt(doc: Document.Parsed, path: Path): ParsedNode | undefined {
  let node: unknown = doc.contents;
  for (const key of path) {
    if (isMap(node)) {
      node = (node.items as Pair[]).find((p) => isScalar(p.key) && String(p.key.value) === String(key))?.value;
    } else if (isSeq(node) && typeof key === 'number') {
      node = node.items[key];
    } else {
      return undefined;
    }
  }

  return (node ?? undefined) as ParsedNode | undefined;
}

function pairAt(doc: Document.Parsed, path: Path): { map: YAMLMap.Parsed; pair: Pair<ParsedNode, ParsedNode | null>; index: number } | undefined {
  const parent = nodeAt(doc, path.slice(0, -1));
  const key = path[path.length - 1];
  if (!isMap(parent)) {
    return undefined;
  }

  const index = parent.items.findIndex((p) => isScalar(p.key) && String(p.key.value) === String(key));
  return index < 0 ? undefined : { map: parent as YAMLMap.Parsed, pair: parent.items[index] as Pair<ParsedNode, ParsedNode | null>, index };
}

const lineStart = (text: string, at: number) => text.lastIndexOf('\n', at - 1) + 1;

function lineEnd(text: string, at: number): number {
  const end = text.indexOf('\n', at);
  return end < 0 ? text.length : end;
}

const column = (text: string, at: number) => at - lineStart(text, at);

/** The end of a node's own text: the end of its value, before any trailing comment or newline. */
function valueEnd(node: ParsedNode): number {
  return node.range[1];
}

/** The end of the line on which a block node's last line ends (its trailing newline excluded). */
function blockEnd(text: string, node: ParsedNode): number {
  let end = node.range[1];
  while (end > node.range[0] && /\s/.test(text[end - 1]!)) {
    end--;
  }

  return lineEnd(text, end);
}

function isFlow(node: ParsedNode): boolean {
  return (isMap(node) || isSeq(node)) && (node as YAMLMap | YAMLSeq).flow === true;
}

function splice(text: string, start: number, end: number, insert: string): string {
  return text.slice(0, start) + insert + text.slice(end);
}

// ------------------------------------------------------------------ edits

/**
 * Sets the value at `path`: replaces an existing scalar (or a flow value) in place, or adds the key to its mapping. A
 * missing parent mapping is created with the whole subtree in flow style. Collections in block style are edited key by
 * key, so their layout stays.
 */
export function setValue(text: string, path: Path, value: Value): string {
  if (path.length === 0) {
    throw new EditError('The document itself cannot be replaced.');
  }

  const doc = editable(text);
  if (!isMap(doc.contents)) {
    throw new EditError('The workflow is not a YAML mapping.');
  }

  const existing = pairAt(doc, path);
  if (existing !== undefined) {
    const current = existing.pair.value;
    if (current === null) {
      // `key:` with no value
      const keyNode = existing.pair.key as ParsedNode;
      const at = text.indexOf(':', keyNode.range[1]) + 1;
      return splice(text, at, at, ' ' + flow(value));
    }

    if (value !== null && typeof value === 'object' && !Array.isArray(value) && isMap(current) && !isFlow(current)) {
      let next = text;
      for (const [k, v] of Object.entries(value)) {
        next = setValue(next, [...path, k], v);
      }

      return next;
    }

    const end = isScalar(current) || isFlow(current) ? valueEnd(current) : blockEnd(text, current);
    if (isScalar(current) || isFlow(current)) {
      // `key:` with nothing after it reads as an empty scalar at the colon: the new value needs its space.
      const empty = current.range[0] === end && text[current.range[0] - 1] === ':';
      return splice(text, current.range[0], end, (empty ? ' ' : '') + flow(value));
    }

    // A block collection replaced by a scalar or a flow value: on the key's line.
    const keyNode = existing.pair.key as ParsedNode;
    const colon = text.indexOf(':', keyNode.range[1]);
    return splice(text, colon + 1, end, ' ' + flow(value));
  }

  // The key is missing: find the deepest existing mapping and add the rest of the path under it.
  for (let depth = path.length - 1; depth >= 0; depth--) {
    const parent = nodeAt(doc, path.slice(0, depth));
    if (parent === undefined) {
      continue;
    }

    if (!isMap(parent)) {
      if (isSeq(parent) && typeof path[depth] === 'number' && path[depth] === parent.items.length && depth === path.length - 1) {
        return insertItem(text, path.slice(0, depth), parent.items.length, value);
      }

      throw new EditError(`'${path.slice(0, depth + 1).join('.')}' is not a mapping.`);
    }

    let nested: Value = value;
    for (let i = path.length - 1; i > depth; i--) {
      const key = path[i]!;
      if (typeof key === 'number') {
        if (key !== 0) {
          throw new EditError(`'${path.slice(0, i).join('.')}' doesn't exist: only item 0 of a new sequence can be set.`);
        }

        nested = [nested];
      } else {
        nested = { [key]: nested };
      }
    }

    return addKey(text, parent as YAMLMap.Parsed, String(path[depth]), nested);
  }

  throw new EditError(`No '${path.join('.')}'.`);
}

function addKey(text: string, map: YAMLMap.Parsed, key: string, value: Value): string {
  if (map.flow) {
    const close = text.lastIndexOf('}', map.range[1]);
    if (map.items.length === 0) {
      const open = text.indexOf('{', map.range[0]);
      return splice(text, open, close + 1, `{ ${scalar(key)}: ${flow(value)} }`);
    }

    const last = map.items[map.items.length - 1]!;
    const end = valueEnd((last.value ?? last.key) as ParsedNode);
    return splice(text, end, end, `, ${scalar(key)}: ${flow(value)}`);
  }

  const first = map.items[0]!;
  const indent = ' '.repeat(column(text, (first.key as ParsedNode).range[0]));
  const last = map.items[map.items.length - 1]!;
  const lastNode = (last.value ?? last.key) as ParsedNode;
  const end = isScalar(lastNode) || isFlow(lastNode) ? lineEnd(text, valueEnd(lastNode)) : blockEnd(text, lastNode);
  return splice(text, end, end, `\n${indent}${scalar(key)}: ${flow(value)}`);
}

/** Removes the key (or sequence item) at `path`, with its lines in block style or its comma in flow style. */
export function remove(text: string, path: Path): string {
  const doc = editable(text);
  const parent = nodeAt(doc, path.slice(0, -1));
  const key = path[path.length - 1];
  if (isSeq(parent) && typeof key === 'number') {
    return removeItem(text, parent as YAMLSeq.Parsed, key);
  }

  const found = pairAt(doc, path);
  if (found === undefined) {
    throw new EditError(`No '${path.join('.')}'.`);
  }

  const { map, pair, index } = found;
  const keyNode = pair.key as ParsedNode;
  const valueNode = (pair.value ?? pair.key) as ParsedNode;
  if (map.flow) {
    if (map.items.length === 1) {
      const open = text.indexOf('{', map.range[0]);
      const close = text.lastIndexOf('}', map.range[1]);
      return splice(text, open, close + 1, '{}');
    }

    if (index < map.items.length - 1) {
      const next = map.items[index + 1]!.key as ParsedNode;
      return splice(text, keyNode.range[0], next.range[0], '');
    }

    const previous = map.items[index - 1]!;
    const previousEnd = valueEnd((previous.value ?? previous.key) as ParsedNode);
    return splice(text, previousEnd, valueEnd(valueNode), '');
  }

  if (map.items.length === 1) {
    throw new EditError(`'${path.join('.')}' is the only key of its mapping: remove the mapping instead.`);
  }

  const start = lineStart(text, keyNode.range[0]);
  const end = isScalar(valueNode) || isFlow(valueNode) ? lineEnd(text, valueEnd(valueNode)) : blockEnd(text, valueNode);
  return splice(text, start, Math.min(text.length, end + 1), '');
}

function removeItem(text: string, seq: YAMLSeq.Parsed, index: number): string {
  const item = seq.items[index] as ParsedNode | undefined;
  if (item === undefined) {
    throw new EditError(`No item ${index}.`);
  }

  if (seq.flow) {
    if (seq.items.length === 1) {
      return splice(text, text.indexOf('[', seq.range[0]), text.lastIndexOf(']', seq.range[1]) + 1, '[]');
    }

    if (index < seq.items.length - 1) {
      return splice(text, item.range[0], (seq.items[index + 1] as ParsedNode).range[0], '');
    }

    return splice(text, valueEnd(seq.items[index - 1] as ParsedNode), valueEnd(item), '');
  }

  const dash = text.lastIndexOf('-', item.range[0]);
  const start = lineStart(text, dash);
  const end = isScalar(item) || isFlow(item) ? lineEnd(text, valueEnd(item)) : blockEnd(text, item);
  return splice(text, start, Math.min(text.length, end + 1), '');
}

/** Inserts `value` into the sequence at `path` before item `index` (at the end when `index` is its length). */
export function insertItem(text: string, path: Path, index: number, value: Value): string {
  const doc = editable(text);
  const seq = nodeAt(doc, path);
  if (!isSeq(seq)) {
    throw new EditError(`'${path.join('.')}' is not a sequence.`);
  }

  const items = seq.items as ParsedNode[];
  if (index < 0 || index > items.length) {
    throw new EditError(`Index ${index} is outside '${path.join('.')}'.`);
  }

  if (seq.flow) {
    if (items.length === 0) {
      return splice(text, text.indexOf('[', seq.range[0]), text.lastIndexOf(']', seq.range[1]) + 1, `[${flow(value)}]`);
    }

    return index < items.length
      ? splice(text, items[index]!.range[0], items[index]!.range[0], `${flow(value)}, `)
      : splice(text, valueEnd(items[items.length - 1]!), valueEnd(items[items.length - 1]!), `, ${flow(value)}`);
  }

  if (items.length === 0) {
    throw new EditError(`'${path.join('.')}' has no items to take the indentation from.`);
  }

  const firstDash = text.lastIndexOf('-', items[0]!.range[0]);
  const indent = ' '.repeat(column(text, firstDash));
  const rendered = value !== null && typeof value === 'object' && !Array.isArray(value) ? blockItem(value as { [key: string]: Value }, indent) : `${indent}- ${flow(value)}`;
  if (index < items.length) {
    const at = lineStart(text, text.lastIndexOf('-', items[index]!.range[0]));
    return splice(text, at, at, rendered + '\n');
  }

  const last = items[items.length - 1]!;
  const end = isScalar(last) || isFlow(last) ? lineEnd(text, valueEnd(last)) : blockEnd(text, last);
  return splice(text, end, end, '\n' + rendered);
}

/** The 1-based line range of each item of the sequence at `path` (for mapping diagnostics to steps). */
export function itemLines(text: string, path: Path): Array<{ index: number; from: number; to: number }> {
  const seq = nodeAt(parse(text), path);
  if (!isSeq(seq)) {
    return [];
  }

  const lineOf = (at: number) => text.slice(0, at).split('\n').length;
  return (seq.items as ParsedNode[]).map((item, index) => {
    const end = isScalar(item) || isFlow(item) ? valueEnd(item) : Math.max(item.range[0], blockEnd(text, item) - 1);
    return { index, from: lineOf(text.lastIndexOf('-', item.range[0])), to: lineOf(end) };
  });
}

export { isPair };
