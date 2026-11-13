// What the app talks to: the local host's API and the WebAssembly core. Injected so tests can replace both.
import { useQuery } from '@tanstack/react-query';
import type { StudioCoreClient, ValidateResult } from '@sourceflow/core-client';
import { createContext, useContext, type ReactNode } from 'react';
import type { WorkspaceApi, WorkspaceDto } from './api.ts';
import { manifests } from './workspace.ts';

/** The part of the core client the shell uses (the views of WP-S.9 onwards use more). */
export type StudioCore = Pick<StudioCoreClient, 'validate'>;

export interface StudioServices {
  api: WorkspaceApi;
  core: StudioCore;
}

const ServicesContext = createContext<StudioServices | undefined>(undefined);

export function ServicesProvider({ services, children }: { services: StudioServices; children: ReactNode }) {
  return <ServicesContext.Provider value={services}>{children}</ServicesContext.Provider>;
}

export function useServices(): StudioServices {
  const services = useContext(ServicesContext);
  if (services === undefined) {
    throw new Error('useServices needs a <ServicesProvider>.');
  }

  return services;
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
