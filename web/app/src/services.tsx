// What the app talks to: the local host's API, the WebAssembly core and the ELK layout engine. Injected so tests can
// replace them.
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { StudioCoreClient, ValidateResult } from '@sourceflow/core-client';
import { createContext, useContext, type ReactNode } from 'react';
import type { WorkspaceApi, WorkspaceDto } from './api.ts';
import { TextareaEditor, type CodeEditorComponent } from './edit/CodeEditor.tsx';
import type { LayoutEngine } from './graph/layout.ts';
import { manifests } from './workspace.ts';

/** The part of the core client the app uses. */
export type StudioCore = Pick<StudioCoreClient, 'validate' | 'graph' | 'enumeratePaths' | 'sequence' | 'simulateStep' | 'toMermaid' | 'toBpmn'>;

export interface StudioServices {
  api: WorkspaceApi;
  core: StudioCore;
  layout: LayoutEngine;
  /** The YAML editor: Monaco in the app; a textarea by default (tests, fallback). */
  codeEditor?: CodeEditorComponent;
}

const ServicesContext = createContext<StudioServices | undefined>(undefined);

export function ServicesProvider({ services, children }: { services: StudioServices; children: ReactNode }) {
  return <ServicesContext.Provider value={services}>{children}</ServicesContext.Provider>;
}

export function useServices(): StudioServices & { codeEditor: CodeEditorComponent } {
  const services = useContext(ServicesContext);
  if (services === undefined) {
    throw new Error('useServices needs a <ServicesProvider>.');
  }

  return { ...services, codeEditor: services.codeEditor ?? TextareaEditor };
}

export function useWorkspace() {
  const { api } = useServices();
  return useQuery({ queryKey: ['workspace'], queryFn: () => api.workspace() });
}

export function useWorkflowText(path: string | undefined) {
  const { api } = useServices();
  return useQuery({ queryKey: ['file', path], queryFn: () => api.readFile(path!), enabled: path !== undefined });
}

/** The workflow's diagnostics from the core, against every manifest of the workspace. */
export function useValidation(text: string | undefined, workspace: WorkspaceDto | undefined) {
  const { core } = useServices();
  const domain = workspace === undefined ? [] : manifests(workspace);
  return useQuery<ValidateResult>({
    queryKey: ['validate', text, domain],
    queryFn: () => core.validate({ yaml: text!, manifests: domain }),
    enabled: text !== undefined && workspace !== undefined,
  });
}

/** The workflow's statechart from the core. */
export function useGraph(text: string | undefined) {
  const { core } = useServices();
  return useQuery({ queryKey: ['graph', text], queryFn: () => core.graph({ yaml: text! }), enabled: text !== undefined, retry: false });
}

export function usePaths(text: string | undefined) {
  const { core } = useServices();
  // While an edit is re-enumerated, the previous paths stay (as the statechart keeps its layout).
  return useQuery({ queryKey: ['paths', text], queryFn: () => core.enumeratePaths({ yaml: text! }), enabled: text !== undefined, retry: false, placeholderData: keepPreviousData });
}

export function useSequence(text: string | undefined, workspace: WorkspaceDto | undefined, path: string | undefined) {
  const { core } = useServices();
  const domain = workspace === undefined ? [] : manifests(workspace);
  return useQuery({
    queryKey: ['sequence', text, domain, path],
    queryFn: () => core.sequence({ yaml: text!, manifests: domain, path: path! }),
    enabled: text !== undefined && workspace !== undefined && path !== undefined,
    retry: false,
  });
}
