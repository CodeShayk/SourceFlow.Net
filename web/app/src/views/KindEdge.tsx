import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type EdgeProps } from '@xyflow/react';
import { EDGE_ENCODINGS } from '../graph/encodings.ts';
import type { KindFlowEdge } from '../graph/statechart.ts';

/** The path of a self loop (a retry): out of the node's right side and back, further out for each further loop. */
export function loopPath(sourceX: number, sourceY: number, targetX: number, targetY: number, loop: number): { path: string; labelX: number; labelY: number } {
  const reach = 90 + loop * 40;
  const path = `M ${sourceX} ${sourceY} C ${sourceX + reach} ${sourceY + 40}, ${targetX + reach} ${targetY - 40}, ${targetX} ${targetY}`;
  return { path, labelX: (sourceX + targetX) / 2 + reach * 0.75, labelY: (sourceY + targetY) / 2 };
}

/** A statechart edge drawn with its kind's line style, arrow and symbol label (never colour alone). */
export function KindEdge({ id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, data, selected }: EdgeProps<KindFlowEdge>) {
  const edge = data!.edge;
  const encoding = EDGE_ENCODINGS[edge.kind];
  const [path, labelX, labelY] = source === target
    ? (({ path: p, labelX: x, labelY: y }) => [p, x, y] as const)(loopPath(sourceX, sourceY, targetX, targetY, data!.loop))
    : getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 12 });
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        {...(markerEnd === undefined ? {} : { markerEnd })}
        style={{ stroke: encoding.color, strokeWidth: selected ? encoding.width + 1.5 : encoding.width, strokeDasharray: encoding.dash || undefined }}
      />
      <EdgeLabelRenderer>
        <div className={`edge-label kind-${edge.kind}${selected ? ' selected' : ''}`} aria-hidden="true"
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
          {edge.label}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
