import { forwardRef } from 'react';
import { NavLink } from 'react-router';
import type { WorkspaceDto } from '../api.ts';
import { explorerTree } from '../workspace.ts';

/** The workspace explorer: the domain's services and the workflows each one owns (doc 15 §4). */
export const Explorer = forwardRef<HTMLElement, { workspace: WorkspaceDto }>(function Explorer({ workspace }, ref) {
  const tree = explorerTree(workspace);
  return (
    <nav ref={ref} className="explorer" aria-label="Workspace" tabIndex={-1}>
      <NavLink to="/" end className="explorer-domain">
        {workspace.domain}
      </NavLink>
      <ul className="explorer-services">
        {tree.map((service) => (
          <li key={service.name}>
            <NavLink to={service.route} className="explorer-service" aria-label={`Service ${service.name}`}>
              <span aria-hidden="true">▣ </span>
              {service.name}
            </NavLink>
            {service.workflows.length > 0 ? (
              <ul aria-label={`Workflows of ${service.name}`}>
                {service.workflows.map((workflow) => (
                  <li key={workflow.path}>
                    <NavLink to={workflow.route} className="explorer-workflow" title={workflow.path} aria-label={`Workflow ${workflow.name}`}>
                      <span aria-hidden="true">⇢ </span>
                      {workflow.name}
                    </NavLink>
                  </li>
                ))}
              </ul>
            ) : null}
          </li>
        ))}
      </ul>
    </nav>
  );
});
