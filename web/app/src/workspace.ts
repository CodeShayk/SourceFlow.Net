// Workspace helpers: the explorer tree, routes for workflow files, manifests for the core, and diagnostics order.
import type { Diagnostic } from '@sourceflow/core-client';
import type { ServiceDto, WorkspaceDto } from './api.ts';

export interface ExplorerWorkflow {
  path: string;
  /** The file name without `.sfflow.yaml`. */
  name: string;
  route: string;
}

export interface ExplorerService {
  name: string;
  route: string;
  workflows: ExplorerWorkflow[];
  /** Commands the service's aggregates handle (from its manifest). */
  commands: number;
}

export const WORKFLOW_SUFFIX = '.sfflow.yaml';

/** The route of a workflow file: `/workflows/<path>`. */
export function workflowRoute(path: string): string {
  return '/workflows/' + path.split('/').map(encodeURIComponent).join('/');
}

/** The workflow file of a route splat (the part after `/workflows/`). */
export function workflowPath(splat: string | undefined): string | undefined {
  if (splat === undefined || splat.length === 0) {
    return undefined;
  }

  const path = splat.split('/').map(decodeURIComponent).join('/');
  return path.endsWith(WORKFLOW_SUFFIX) && !path.split('/').includes('..') ? path : undefined;
}

export function serviceRoute(name: string): string {
  return '/services/' + encodeURIComponent(name);
}

interface ManifestShape {
  aggregates?: Array<{ name?: string; commands?: unknown[]; events?: unknown[] }>;
  messages?: unknown[];
}

/** The parsed manifest, or an empty one when the text isn't JSON. */
export function manifestOf(service: ServiceDto): ManifestShape {
  try {
    const parsed = JSON.parse(service.manifestJson) as unknown;
    return typeof parsed === 'object' && parsed !== null ? (parsed as ManifestShape) : {};
  } catch {
    return {};
  }
}

/** The explorer tree: services in workspace order, each with its workflows sorted by name. */
export function explorerTree(workspace: WorkspaceDto): ExplorerService[] {
  return workspace.services.map((service) => ({
    name: service.name,
    route: serviceRoute(service.name),
    commands: (manifestOf(service).aggregates ?? []).reduce((sum, aggregate) => sum + (aggregate.commands?.length ?? 0), 0),
    workflows: [...service.workflowFiles]
      .sort((a, b) => fileName(a).localeCompare(fileName(b)))
      .map((path) => ({ path, name: fileName(path), route: workflowRoute(path) })),
  }));
}

/** The service that owns a workflow file. */
export function ownerOf(workspace: WorkspaceDto, path: string): ServiceDto | undefined {
  return workspace.services.find((s) => s.workflowFiles.includes(path));
}

/** Every manifest in workspace order: what the core validates against. */
export function manifests(workspace: WorkspaceDto): string[] {
  return workspace.services.map((s) => s.manifestJson);
}

function fileName(path: string): string {
  const name = path.slice(path.lastIndexOf('/') + 1);
  return name.endsWith(WORKFLOW_SUFFIX) ? name.slice(0, -WORKFLOW_SUFFIX.length) : name;
}

export interface DiagnosticCounts {
  errors: number;
  warnings: number;
}

export function countDiagnostics(diagnostics: readonly Diagnostic[]): DiagnosticCounts {
  return diagnostics.reduce(
    (counts, d) => (d.severity === 'error' ? { ...counts, errors: counts.errors + 1 } : { ...counts, warnings: counts.warnings + 1 }),
    { errors: 0, warnings: 0 },
  );
}

/** Errors first, then by position: how the panel lists them. */
export function sortDiagnostics(diagnostics: readonly Diagnostic[]): Diagnostic[] {
  return [...diagnostics].sort((a, b) =>
    a.severity !== b.severity ? (a.severity === 'error' ? -1 : 1) : a.line - b.line || a.column - b.column || a.id.localeCompare(b.id));
}
