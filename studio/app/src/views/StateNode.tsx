import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_ENCODINGS } from '../graph/encodings.ts';
import type { StateFlowNode } from '../graph/statechart.ts';

/** A statechart node: its shape and symbol by kind (a pivot is a diamond-marked box), and its lines of detail. */
export function StateNode({ data }: NodeProps<StateFlowNode>) {
  const { node, lines } = data;
  const encoding = NODE_ENCODINGS[node.kind];
  return (
    <div className={`state state-${node.kind} shape-${encoding.shape}${node.compensable ? ' compensable' : ''}${data.active === true ? ' active' : ''}`} data-kind={node.kind} data-active={data.active === true ? 'true' : undefined}>
      <Handle type="target" position={Position.Top} isConnectable={false} />
      {lines.map((line, index) => (
        <div key={index} className={index === 0 ? 'state-title' : 'state-detail'}>
          {line}
        </div>
      ))}
      {node.compensable ? <div className="state-badge" title="Compensated on failure">⟲ undoable</div> : null}
      {data.active === true ? <div className="state-active">▶ simulated here</div> : null}
      {data.problems !== undefined && data.problems > 0 ? (
        <div className="state-problems">✕ {data.problems} problem{data.problems === 1 ? '' : 's'}</div>
      ) : null}
      <Handle type="source" position={Position.Bottom} isConnectable={false} />
    </div>
  );
}

/** The Compensating group: a labelled container for the compensations, in the order they run. */
export function GroupNode({ data }: NodeProps<StateFlowNode>) {
  return (
    <div className="state-group-frame" data-kind={data.node.kind}>
      <Handle type="target" position={Position.Top} isConnectable={false} />
      <div className="state-group-title">⟲ {data.node.label}</div>
      <Handle type="source" position={Position.Bottom} isConnectable={false} />
    </div>
  );
}
