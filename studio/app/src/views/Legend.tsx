import { EDGE_ENCODINGS, EDGE_KINDS, NODE_ENCODINGS } from '../graph/encodings.ts';
import type { NodeKind } from '@sourceflow/core-client';

const NODE_KINDS: NodeKind[] = ['step', 'pivot', 'compensation', 'manual', 'terminal'];

/** The key to the statechart: each edge kind's line, symbol and name; each node kind's symbol and name. */
export function Legend({ counts }: { counts?: ReadonlyMap<string, number> }) {
  return (
    <figure className="legend" aria-label="Legend">
      <ul>
        {EDGE_KINDS.map((kind) => {
          const encoding = EDGE_ENCODINGS[kind];
          return (
            <li key={kind} data-kind={kind}>
              <svg width="36" height="10" aria-hidden="true">
                <line x1="2" y1="5" x2="34" y2="5" style={{ stroke: encoding.color, strokeWidth: encoding.width, strokeDasharray: encoding.dash || undefined }} />
              </svg>
              <span aria-hidden="true">{encoding.symbol}</span> {encoding.name}
              {counts?.has(kind) ? <span className="legend-count"> ({counts.get(kind)})</span> : null}
            </li>
          );
        })}
        {NODE_KINDS.map((kind) => (
          <li key={'node-' + kind}>
            <span aria-hidden="true">{NODE_ENCODINGS[kind].symbol}</span> {NODE_ENCODINGS[kind].name}
          </li>
        ))}
      </ul>
    </figure>
  );
}
