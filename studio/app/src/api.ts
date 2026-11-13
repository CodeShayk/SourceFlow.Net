// The Studio local host's REST API (dotnet sourceflow studio, WP-S.8). Paths are relative to the workspace file.

export interface ServiceDto {
  name: string;
  /** The manifest path or URL as written in sourceflow.domain.yaml. */
  manifest: string;
  /** The manifest JSON text, as the core takes it. */
  manifestJson: string;
  /** The service's workflows folder, when it owns workflows. */
  workflows?: string;
  /** The service's project, when Studio can re-export its manifest. */
  project?: string;
  /** The service's test project folder, where Studio writes the specs of its workflows. */
  tests?: string;
  /** The service's module type, which the specs register. */
  module?: string;
  workflowFiles: string[];
}

/** What to generate specs for: one of the service's workflow files and the paths (none: the minimal cover). */
export interface SpecsRequest {
  workflow: string;
  paths?: readonly string[];
  allPaths?: boolean;
  /** Generate without writing: the preview. */
  dryRun?: boolean;
}

export interface SpecsResult {
  /** The file, relative to the workspace. */
  path: string;
  tests: number;
  bytes: number;
  written: boolean;
  /** The generated C#, for a dry run. */
  text?: string;
}

export interface WorkspaceDto {
  domain: string;
  description?: string;
  file: string;
  readOnly: boolean;
  services: ServiceDto[];
}

/** A request the host refused or failed. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export interface WorkspaceApi {
  workspace(): Promise<WorkspaceDto>;
  readFile(path: string): Promise<string>;
  writeFile(path: string, content: string): Promise<void>;
  exportManifest(service: string): Promise<void>;
  /** Generates (and unless `dryRun`, writes) the specs of a workflow into its owner's test project. */
  specs(service: string, request: SpecsRequest): Promise<SpecsResult>;
}

/** The API of the host that served this page (`base` defaults to the page's origin). */
export function hostApi(base = '', fetcher: typeof fetch = (input, init) => fetch(input, init)): WorkspaceApi {
  async function request(path: string, init?: RequestInit): Promise<Response> {
    const response = await fetcher(base + path, init);
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`;
      try {
        const body = (await response.json()) as { error?: string };
        message = body.error ?? message;
      } catch {
        // not JSON
      }

      throw new ApiError(response.status, message);
    }

    return response;
  }

  const file = (path: string) => '/api/files?path=' + encodeURIComponent(path);
  return {
    async workspace() {
      return (await (await request('/api/workspace')).json()) as WorkspaceDto;
    },
    async readFile(path) {
      return (await request(file(path))).text();
    },
    async writeFile(path, content) {
      await request(file(path), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ content }) });
    },
    async exportManifest(service) {
      await request('/api/services/' + encodeURIComponent(service) + '/manifest/export', { method: 'POST' });
    },
    async specs(service, body) {
      const response = await request('/api/services/' + encodeURIComponent(service) + '/specs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      return (await response.json()) as SpecsResult;
    },
  };
}
