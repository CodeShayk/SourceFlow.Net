import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router';
import type { WorkspaceDto } from '../api.ts';
import { useServices } from '../services.tsx';
import { manifestOf } from '../workspace.ts';
import { NotFound } from './NotFound.tsx';

/** A service: its aggregates and the commands they handle, from its manifest; and a manifest re-export. */
export function ServiceView({ workspace }: { workspace: WorkspaceDto }) {
  const { name } = useParams();
  const { api } = useServices();
  const queries = useQueryClient();
  const service = workspace.services.find((s) => s.name === name);
  const exporting = useMutation({
    mutationFn: () => api.exportManifest(service!.name),
    onSuccess: () => queries.invalidateQueries({ queryKey: ['workspace'] }),
  });
  if (service === undefined) {
    return <NotFound what={`service '${name ?? ''}'`} />;
  }

  const manifest = manifestOf(service);
  const disabled = workspace.readOnly || service.project === undefined || exporting.isPending;
  return (
    <article className="view" aria-labelledby="service-title">
      <h1 id="service-title">{service.name}</h1>
      <p className="lead">
        Manifest <code>{service.manifest}</code>
        {service.workflows !== undefined ? <> · workflows in <code>{service.workflows}</code></> : null}
      </p>
      <p>
        <button type="button" className="button" disabled={disabled} onClick={() => exporting.mutate()}
          title={workspace.readOnly ? 'The Studio runs read-only' : service.project === undefined ? "Add the service's 'project' to the workspace file" : undefined}>
          {exporting.isPending ? 'Exporting the manifest…' : 'Export the manifest again'}
        </button>
        {exporting.isError ? <span role="alert"> {(exporting.error as Error).message}</span> : null}
        {exporting.isSuccess ? <span role="status"> The manifest was exported.</span> : null}
      </p>
      <h2>Aggregates</h2>
      {(manifest.aggregates ?? []).length === 0 ? (
        <p>None.</p>
      ) : (
        <ul>
          {(manifest.aggregates ?? []).map((aggregate) => (
            <li key={aggregate.name}>
              <strong>{aggregate.name}</strong>: {(aggregate.commands ?? []).map((c) => (c as { type?: string }).type).join(', ') || 'no commands'}
            </li>
          ))}
        </ul>
      )}
    </article>
  );
}
