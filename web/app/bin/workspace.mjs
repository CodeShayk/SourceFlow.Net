// Reads sourceflow.domain.yaml for the static build, with the rules of SourceFlow.Studio's DomainWorkspace (doc 15 §4):
// paths relative to the workspace file, unknown keys are errors, remote manifests are refused (the build is offline),
// and every service's *.sfflow.yaml files are listed recursively, sorted, relative to the workspace with '/'.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { LineCounter, parseDocument, isMap, isScalar, isSeq } from 'yaml';

export const FILE_NAME = 'sourceflow.domain.yaml';
export const WORKFLOW_SUFFIX = '.sfflow.yaml';
export const LAYOUT_SUFFIX = '.sfflow.layout.json';

const WORKSPACE_KEYS = ['domain', 'description', 'services'];
const SERVICE_KEYS = ['name', 'manifest', 'workflows', 'project', 'tests', 'module'];

export class WorkspaceError extends Error {
  constructor(message) {
    super(message);
    this.name = 'WorkspaceError';
  }
}

function at(file, lines, node) {
  const offset = node?.range?.[0] ?? 0;
  const { line, col } = lines.linePos(offset);
  return `${file}(${line},${col})`;
}

function scalar(file, lines, node) {
  if (!isScalar(node)) {
    throw new WorkspaceError(`${at(file, lines, node)}: expected a single value.`);
  }

  return node.value === null ? undefined : String(node.value);
}

function listWorkflows(root, folder) {
  if (!existsSync(folder) || !statSync(folder).isDirectory()) {
    return [];
  }

  const found = [];
  const visit = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        visit(full);
      } else if (entry.isFile() && entry.name.endsWith(WORKFLOW_SUFFIX)) {
        found.push(relative(root, full).split(sep).join('/'));
      }
    }
  };
  visit(folder);
  return found.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/**
 * Loads a workspace (the file, or a folder holding sourceflow.domain.yaml) into the Studio API's workspace shape, with
 * each service's manifest JSON, and the text of every workflow file and the layout file beside it.
 */
export function loadWorkspace(path) {
  const file = resolve(existsSync(path) && statSync(path).isDirectory() ? join(path, FILE_NAME) : path);
  if (!existsSync(file)) {
    throw new WorkspaceError(`Workspace file '${file}' not found.`);
  }

  const root = dirname(file);
  const lines = new LineCounter();
  const doc = parseDocument(readFileSync(file, 'utf8'), { lineCounter: lines, uniqueKeys: true });
  if (doc.errors.length > 0) {
    const error = doc.errors[0];
    const { line, col } = error.linePos?.[0] ?? { line: 1, col: 1 };
    throw new WorkspaceError(`${file}(${line},${col}): ${error.message.split('\n')[0]}`);
  }

  if (!isMap(doc.contents)) {
    throw new WorkspaceError(`${file}: the workspace file must be a mapping with 'domain' and 'services'.`);
  }

  const workspace = { domain: undefined, description: undefined, services: undefined };
  for (const pair of doc.contents.items) {
    const key = scalar(file, lines, pair.key);
    if (!WORKSPACE_KEYS.includes(key)) {
      throw new WorkspaceError(`${at(file, lines, pair.key)}: unknown key '${key}' (expected domain, description or services).`);
    }

    if (key === 'services') {
      if (!isSeq(pair.value)) {
        throw new WorkspaceError(`${at(file, lines, pair.value)}: expected a list.`);
      }

      workspace.services = pair.value.items.map((item) => {
        if (!isMap(item)) {
          throw new WorkspaceError(`${at(file, lines, item)}: a service is a mapping with 'name', 'manifest' and optionally 'workflows', 'project', 'tests' and 'module'.`);
        }

        const service = {};
        for (const entry of item.items) {
          const name = scalar(file, lines, entry.key);
          if (!SERVICE_KEYS.includes(name)) {
            throw new WorkspaceError(`${at(file, lines, entry.key)}: unknown key '${name}' (expected name, manifest, workflows, project, tests or module).`);
          }

          service[name] = scalar(file, lines, entry.value);
        }

        return service;
      });
    } else {
      workspace[key] = scalar(file, lines, pair.value);
    }
  }

  if (!workspace.domain) {
    throw new WorkspaceError(`${file}: 'domain' is required.`);
  }

  if (!workspace.services || workspace.services.length === 0) {
    throw new WorkspaceError(`${file}: 'services' lists no service.`);
  }

  const names = new Set();
  const files = {};
  const services = workspace.services.map((service) => {
    if (!service.name || !service.manifest) {
      throw new WorkspaceError(`${file}: every service needs a 'name' and a 'manifest'.`);
    }

    if (names.has(service.name)) {
      throw new WorkspaceError(`${file}: service '${service.name}' is listed twice.`);
    }

    names.add(service.name);
    if (/^https?:\/\//i.test(service.manifest)) {
      throw new WorkspaceError(`Service '${service.name}': the manifest '${service.manifest}' is remote; the static build reads local files only.`);
    }

    const manifestPath = isAbsolute(service.manifest) ? service.manifest : resolve(root, service.manifest);
    if (!existsSync(manifestPath)) {
      throw new WorkspaceError(`Service '${service.name}': manifest '${service.manifest}' not found (${manifestPath}). Run 'dotnet sourceflow manifest export --project <service>' to create it.`);
    }

    const manifestJson = readFileSync(manifestPath, 'utf8');
    let declared;
    try {
      declared = JSON.parse(manifestJson).service;
    } catch (error) {
      throw new WorkspaceError(`Service '${service.name}': manifest '${service.manifest}' is invalid: ${error.message}`);
    }

    if (declared !== undefined && declared !== service.name) {
      throw new WorkspaceError(`Service '${service.name}': '${service.manifest}' is the manifest of service '${declared}'.`);
    }

    const workflowFiles = service.workflows === undefined ? [] : listWorkflows(root, resolve(root, service.workflows));
    for (const workflow of workflowFiles) {
      files[workflow] = readFileSync(resolve(root, workflow), 'utf8');
      const layout = workflow.slice(0, -WORKFLOW_SUFFIX.length) + LAYOUT_SUFFIX;
      if (existsSync(resolve(root, layout))) {
        files[layout] = readFileSync(resolve(root, layout), 'utf8');
      }
    }

    const dto = { name: service.name, manifest: service.manifest, manifestJson, workflowFiles };
    for (const optional of ['workflows', 'project', 'tests', 'module']) {
      if (service[optional] !== undefined) {
        dto[optional] = service[optional];
      }
    }

    return dto;
  });

  return {
    workspace: { domain: workspace.domain, ...(workspace.description === undefined ? {} : { description: workspace.description }), file: basename(file), readOnly: true, staticSite: true, services },
    files,
  };
}
