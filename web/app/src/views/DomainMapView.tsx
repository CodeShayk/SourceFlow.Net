import { BaseEdge, Controls, EdgeLabelRenderer, getBezierPath, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider, type Edge, type EdgeProps, type EdgeTypes, type Node, type NodeProps, type NodeTypes } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import type { WorkspaceDto } from '../api.ts';
import { chainFrom, domainMap, mapLayoutInput, type MapEdge, type MapNode } from '../graph/domainMap.ts';
import { handlesFor } from '../graph/layout.ts';
import { useServices } from '../services.tsx';
import { serviceRoute } from '../workspace.ts';

type MapFlowNode = Node<{ node: MapNode }>;
type MapFlowEdge = Edge<{ edge: MapEdge; state: 'normal' | 'highlighted' | 'dimmed' }>;

const EDGE_STYLE: Record<MapEdge['kind'], { dash: string; name: string }> = {
  command: { dash: '', name: 'commands' },
  publishes: { dash: '7 4', name: 'publishes' },
  subscribes: { dash: '2 3', name: 'subscribes' },
};

function MapNodeView({ data }: NodeProps<MapFlowNode>) {
  const { node } = data;
  return (
    <div className={`map-node map-${node.kind}`}>
      <Handle type="target" position={Position.Left} isConnectable={false} />
      {node.kind === 'service' ? (
        <div className="map-title">▢ <Link to={serviceRoute(node.label)}>{node.label}</Link></div>
      ) : node.kind === 'topic' ? (
        <div className="map-title">⇄ {node.label}</div>
      ) : (
        <>
          <div className="map-title">▣ {node.label}</div>
          <div className="map-detail">{node.details.length} command{node.details.length === 1 ? '' : 's'}</div>
        </>
      )}
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  );
}

function MapEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, data }: EdgeProps<MapFlowEdge>) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  const { edge, state } = data!;
  return (
    <>
      <BaseEdge id={id} path={path} {...(markerEnd === undefined ? {} : { markerEnd })} className={`map-edge map-edge-${edge.kind} ${state}`}
        style={{ strokeDasharray: EDGE_STYLE[edge.kind].dash || undefined, strokeWidth: state === 'highlighted' ? 3 : 1.5, opacity: state === 'dimmed' ? 0.2 : 1 }} />
      <EdgeLabelRenderer>
        <div className={`edge-label map-label ${state}`} aria-hidden="true" style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
          {edge.label}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}

const nodeTypes: NodeTypes = { map: MapNodeView };
const edgeTypes: EdgeTypes = { map: MapEdgeView };

function describe(edge: MapEdge, nodes: readonly MapNode[]): string {
  const name = (id: string) => nodes.find((n) => n.id === id)?.label ?? id;
  return `${name(edge.source)} ${EDGE_STYLE[edge.kind].name} ${name(edge.target)}: ${edge.messages.join(', ')}`;
}

/**
 * The domain map (doc 15 §6.1, WFD-13): services with their aggregates, commands to their owner, publications to
 * topics, subscriptions. Hovering or focusing a node highlights its choreography chain.
 */
export function DomainMapView({ workspace }: { workspace: WorkspaceDto }) {
  const { layout } = useServices();
  const map = useMemo(() => domainMap(workspace), [workspace]);
  const input = useMemo(() => mapLayoutInput(map), [map]);
  const positions = useQuery({ queryKey: ['layout', 'domain', map], queryFn: () => layout(input.nodes, input.edges, 'RIGHT'), retry: false });
  // Hover follows the pointer; selection (a click, or Enter on a focused node) stays until the pane is clicked.
  const [hovered, setHovered] = useState<string | undefined>();
  const [selected, setSelected] = useState<string | undefined>();
  const focus = hovered ?? selected;
  const chain = useMemo(() => (focus === undefined ? undefined : chainFrom(map, focus)), [map, focus]);

  const flow = useMemo(() => {
    if (positions.data === undefined) {
      return undefined;
    }

    const nodes: MapFlowNode[] = map.nodes.map((node) => {
      const box = positions.data.get(node.id) ?? { x: 0, y: 0, width: 140, height: 50 };
      return {
        id: node.id,
        type: 'map',
        position: { x: box.x, y: box.y },
        width: box.width,
        height: box.height,
        handles: handlesFor(box.width, box.height, 'RIGHT').map((h) => ({ ...h, position: h.position as Position })),
        data: { node },
        ariaLabel: node.kind === 'service' ? `Service ${node.label}` : node.kind === 'topic' ? `Topic ${node.label}: ${node.details.join(', ')}` : `Aggregate ${node.label}: ${node.details.join(', ')}`,
        draggable: false,
        connectable: false,
        ...(node.parent === undefined ? {} : { parentId: node.parent, extent: 'parent' as const }),
        ...(node.kind === 'service' ? { zIndex: -1 } : {}),
      };
    });
    const edges: MapFlowEdge[] = map.edges.map((edge) => ({
      id: edge.id,
      source: edge.source,
      target: edge.target,
      type: 'map',
      data: { edge, state: chain === undefined ? 'normal' : chain.has(edge.id) ? 'highlighted' : 'dimmed' },
      ariaLabel: describe(edge, map.nodes),
      focusable: true,
      markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14 },
    }));
    return { nodes, edges };
  }, [map, positions.data, chain]);

  return (
    <section className="diagram" aria-label={`Domain map of ${workspace.domain}`}>
      <div className="flow flow-map" data-ready={flow !== undefined ? 'true' : 'false'} data-highlighted={chain?.size ?? 0}>
        {positions.isError ? (
          <p role="alert">The layout failed: {(positions.error as Error).message}</p>
        ) : flow === undefined ? (
          <p role="status">Laying out {map.nodes.length} services, aggregates and topics…</p>
        ) : (
          <ReactFlowProvider>
            <ReactFlow
              nodes={flow.nodes}
              edges={flow.edges}
              nodeTypes={nodeTypes}
              edgeTypes={edgeTypes}
              nodesDraggable={false}
              nodesConnectable={false}
              nodesFocusable
              edgesFocusable
              fitView
              minZoom={0.1}
              onNodeMouseEnter={(_, node) => setHovered(node.data.node.parent ?? node.id)}
              onNodeMouseLeave={() => setHovered(undefined)}
              // The nodes are controlled: React Flow reports a selection (Enter on a focused node) as a change.
              onNodesChange={(changes) => {
                const chosen = changes.find((c) => c.type === 'select' && c.selected);
                if (chosen !== undefined && 'id' in chosen) {
                  setSelected(map.nodes.find((n) => n.id === chosen.id)?.parent ?? chosen.id);
                }
              }}
              onNodeClick={(_, node) => setSelected(node.data.node.parent ?? node.id)}
              onPaneClick={() => setSelected(undefined)}
            >
              <Controls showInteractive={false} />
            </ReactFlow>
          </ReactFlowProvider>
        )}
      </div>
      <p className="map-key">
        <span>→ command to its owner</span> · <span>⇢ publishes (dashed)</span> · <span>⇠ subscribes (dotted)</span> · hover or select a service to follow its chain
      </p>
    </section>
  );
}
