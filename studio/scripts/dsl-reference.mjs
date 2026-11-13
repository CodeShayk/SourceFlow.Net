// Renders the workflow DSL reference (docs/v3.0.0/guides/workflow-dsl-reference.md) from the normative schema
// (schemas/workflow-1.json, plan 10 WP-S.13): every key of every element, its type, whether it is required, and its
// description. `npm run docs:dsl` in studio/ writes the file; a test fails while the committed file differs.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const schemaPath = join(root, 'schemas', 'workflow-1.json');
export const referencePath = join(root, 'docs', 'v3.0.0', 'guides', 'workflow-dsl-reference.md');

// The element sections, in the order a workflow file reads, then the value types.
const ELEMENTS = ['start', 'dataField', 'step', 'send', 'mapValue', 'await', 'success', 'failure', 'timeout', 'retry', 'then', 'compensate', 'on'];
const TYPES = ['kebab', 'identifier', 'messageType', 'code', 'duration', 'guard'];
const TITLES = {
  start: '`start`', dataField: '`data` fields', step: 'Step', send: '`send`', mapValue: '`map` values', await: '`await`', success: '`success`',
  failure: '`failure`', timeout: '`timeout`', retry: '`retry`', then: '`then`', compensate: '`compensate`', on: '`on`',
};

const anchor = (name) => name.toLowerCase();
const code = (value) => '`' + String(value).replaceAll('|', '\\|') + '`';
const cell = (text) => (text ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ');

/** A node's type, in words: references link to their section. */
export function typeOf(node) {
  if (node === undefined) {
    return '';
  }

  if (node.$ref) {
    const name = node.$ref.split('/').pop();
    return `[${name}](#${anchor(name)})`;
  }

  if (node.allOf?.length === 1 || (node.allOf && node.allOf.every((a) => a.$ref || a.not))) {
    return node.allOf.filter((a) => a.$ref).map(typeOf).join(', ');
  }

  if ('const' in node) {
    return code(JSON.stringify(node.const).replaceAll('"', ''));
  }

  if (node.enum) {
    return node.enum.map((v) => code(v)).join(' or ');
  }

  if (node.oneOf) {
    return node.oneOf.map(typeOf).filter((t) => t !== '').join(', or ');
  }

  if (node.type === 'array') {
    return `list of ${typeOf(node.items)}`;
  }

  if (node.type === 'object' && node.additionalProperties && typeof node.additionalProperties === 'object' && !node.properties) {
    return `map of ${typeOf(node.propertyNames) || 'name'} to ${typeOf(node.additionalProperties)}`;
  }

  if (node.type === 'object' && node.properties) {
    return 'object: ' + Object.keys(node.properties).map(code).join(', ');
  }

  if (Array.isArray(node.type)) {
    return node.type.join(' or ');
  }

  if (node.type === 'string' && node.pattern) {
    return `string matching ${code(node.pattern)}`;
  }

  return node.type ?? '';
}

// One row per key; the keys of an inline object follow it as parent.key.
function rows(properties, required = [], prefix = '') {
  return Object.entries(properties).flatMap(([key, node]) => [
    `| ${code(prefix + key)} | ${cell(typeOf(node))} | ${required.includes(key) ? 'yes' : ''} | ${cell(node.description)} |`,
    ...(node.type === 'object' && node.properties ? rows(node.properties, node.required, prefix + key + '.') : []),
  ]);
}

function table(properties, required = []) {
  return ['| Key | Type | Required | Description |', '|-----|------|----------|-------------|', ...rows(properties, required)].join('\n');
}

function section(name, node) {
  const lines = [`## ${TITLES[name] ?? code(name)}`, '', `<a id="${anchor(name)}"></a>`, ''];
  if (node.description) {
    lines.push(node.description, '');
  }

  if (node.properties) {
    lines.push(table(node.properties, node.required), '');
  }

  // Forms with keys of their own are listed; constraint-only forms (which keys go together) are said in a sentence.
  const forms = node.oneOf?.filter((form) => form.properties || form.type || form.$ref || form.enum || 'const' in form) ?? [];
  if (forms.length > 0) {
    lines.push('One of:', '');
    for (const form of forms) {
      const words = form.properties ? `an object with ${Object.keys(form.properties).map(code).join(', ')}` : typeOf(form);
      lines.push(`- ${words}${form.description ? ': ' + form.description : ''}`);
      if (form.properties) {
        lines.push('', table(form.properties, form.required).split('\n').map((l) => '  ' + l).join('\n'), '');
      }
    }

    lines.push('');
  }

  const choices = node.oneOf?.filter((o) => o.required && !o.properties) ?? [];
  if (choices.length > 0) {
    lines.push(`Give exactly one of ${choices.map((c) => c.required.map(code).join(' and ')).join(' or ')}.`, '');
  }

  return lines.join('\n');
}

/** The reference, as Markdown. */
export function render(schema) {
  const lines = [
    '# Workflow DSL reference (`sourceflow.workflow: 1`)',
    '',
    '<!-- Generated from schemas/workflow-1.json by `npm run docs:dsl` in studio/ (studio/scripts/dsl-reference.mjs). Edit the schema, not this file. -->',
    '',
    schema.description,
    '',
    'This reference lists every key the schema allows. The [workflows guide](workflows.md) explains how to use them, and design doc 15 §5.3 and §5.4 give the execution semantics and the validation rules.',
    '',
    '## The file',
    '',
    `<a id="root"></a>`,
    '',
    table(schema.properties, schema.required),
    '',
  ];
  for (const name of ELEMENTS) {
    lines.push(section(name, schema.$defs[name]));
  }

  lines.push('## Value types', '', '| Type | Form | Description |', '|------|------|-------------|');
  for (const name of TYPES) {
    const node = schema.$defs[name];
    lines.push(`| <a id="${anchor(name)}"></a>${code(name)} | ${cell(node.pattern ? `string matching ${code(node.pattern)}` : node.type)} | ${cell(node.description)} |`);
  }

  return lines.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  writeFileSync(referencePath, render(JSON.parse(readFileSync(schemaPath, 'utf8'))), 'utf8');
  console.log(`Wrote ${referencePath}`);
}
