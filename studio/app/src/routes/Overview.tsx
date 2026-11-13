import { Link } from 'react-router';
import type { WorkspaceDto } from '../api.ts';
import { explorerTree } from '../workspace.ts';

/** The domain overview: its services, what they own, and where the domain map goes (WP-S.9). */
export function Overview({ workspace }: { workspace: WorkspaceDto }) {
  const services = explorerTree(workspace);
  return (
    <article className="view" aria-labelledby="overview-title">
      <h1 id="overview-title">{workspace.domain}</h1>
      {workspace.description !== undefined ? <p className="lead">{workspace.description}</p> : null}
      <table className="services">
        <caption>Services in {workspace.file}</caption>
        <thead>
          <tr>
            <th scope="col">Service</th>
            <th scope="col">Commands</th>
            <th scope="col">Workflows</th>
          </tr>
        </thead>
        <tbody>
          {services.map((service) => (
            <tr key={service.name}>
              <th scope="row">
                <Link to={service.route}>{service.name}</Link>
              </th>
              <td>{service.commands}</td>
              <td>{service.workflows.length === 0 ? '—' : service.workflows.map((w) => <Link key={w.path} to={w.route} className="chip">{w.name}</Link>)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </article>
  );
}
