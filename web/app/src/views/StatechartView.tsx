import { applyNodeChanges, Controls, MarkerType, ReactFlow, ReactFlowProvider, type EdgeTypes, type NodeChange, type NodeTypes } from '@xyflow/react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { GraphResult } from '@sourceflow/core-client';
import { StudioCoreError } from '@sourceflow/core-client';
import { useDeferredValue, useEffect, useMemo, useState } from 'react';
import { applyLayout, type LayoutFile } from '../edit/layoutFile.ts';
import { edgeCounts, statechartPositions, toFlow, type StateFlowNode } from '../graph/statechart.ts';
import { useServices } from '../services.tsx';
import { KindEdge } from './KindEdge.tsx';
import { Legend } from './Legend.tsx';
import { GroupNode, StateNode } from './StateNode.tsx';

const nodeTypes: NodeTypes = { state: StateNode, group: GroupNode };
const edgeTypes: EdgeTypes = { kind: KindEdge };

/** How the statechart takes part in editing (WP-S.10): selecting steps, moving nodes, marking steps with problems. */
export interface StatechartEditing {
  editable: boolean;
  selected?: string;
  onSelect?: (step: string | undefined) => void;
  layout?: LayoutFile;
  onMove?: (node: string, x: number, y: number) => void;
  /** Problems per step id. */
  problems?: ReadonlyMap<string, number>;
  /** The node the simulated instance is at. */
  active?: string;
}

/** The statechart of a workflow (doc 15 §6.1, WFD-30): the core's graph, laid out by ELK, drawn by React Flow. */
export function StatechartView({ text, name, editing }: { text: string; name: string; editing?: StatechartEditing }) {
  const { core } = useServices();
  const deferred = useDeferredValue(text);
  // While the text is being typed and doesn't read, the last good statechart stays.
  const graph = useQuery({ queryKey: ['graph', deferred], queryFn: () => core.graph({ yaml: deferred }), retry: false, placeholderData: keepPreviousData });
  const failure = graph.isError ? graph.error : undefined;
  if (graph.data === undefined) {
    return failure !== undefined ? (
      <div role="alert" className="view-message">
        The statechart needs a readable workflow: {failure instanceof StudioCoreError && failure.diagnostics.length > 0 ? `${failure.diagnostics.length} error(s) in the file` : (failure as Error).message}.
      </div>
    ) : (
      <p role="status">Drawing the statechart…</p>
    );
  }

  return (
    <>
      {failure !== undefined ? <p role="status" className="stale">The file doesn't read right now: the statechart shows the last version that did.</p> : null}
      <LaidOut graph={graph.data} name={name} editing={editing} />
    </>
  );
}

function LaidOut({ graph, name, editing }: { graph: GraphResult; name: string; editing: StatechartEditing | undefined }) {
  const { layout } = useServices();
  const positions = useQuery({ queryKey: ['layout', 'statechart', graph], queryFn: () => statechartPositions(graph, layout), retry: false, placeholderData: keepPreviousData });
  const flow = useMemo(() => {
    if (positions.data === undefined) {
      return undefined;
    }

    const placed = editing?.layout === undefined ? positions.data : applyLayout(positions.data, editing.layout);
    const { nodes, edges } = toFlow(graph, placed);
    return {
      nodes: nodes.map((n): StateFlowNode => {
        const problems = editing?.problems?.get(n.id) ?? 0;
        const step = n.data.node.kind === 'step' || n.data.node.kind === 'pivot';
        return {
          ...n,
          data: { ...n.data, problems, active: editing?.active === n.id },
          selected: editing?.selected === n.id,
          draggable: editing?.editable === true && n.data.node.kind !== 'compensating',
          ariaLabel: n.ariaLabel + (problems > 0 ? `, ${problems} problem${problems === 1 ? '' : 's'}` : '') + (editing?.active === n.id ? ', simulated here' : '') + (step && editing?.editable ? ', press Enter to edit' : ''),
        };
      }),
      edges: edges.map((e) => ({ ...e, markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16 } })),
    };
  }, [graph, positions.data, editing?.layout, editing?.problems, editing?.selected, editing?.editable, editing?.active]);
  const counts = useMemo(() => edgeCounts(graph), [graph]);

  // Controlled nodes: drags move them here; the drop is reported to the layout sidecar.
  const [nodes, setNodes] = useState<StateFlowNode[]>([]);
  useEffect(() => setNodes(flow?.nodes ?? []), [flow]);
  const onNodesChange = (changes: NodeChange<StateFlowNode>[]) => {
    setNodes((current) => applyNodeChanges(changes, current));
    for (const change of changes) {
      if (change.type === 'select' && change.selected) {
        const node = flow?.nodes.find((n) => n.id === change.id);
        if (node !== undefined && (node.data.node.kind === 'step' || node.data.node.kind === 'pivot')) {
          editing?.onSelect?.(node.id);
        }
      }
    }
  };

  if (positions.isError) {
    return <p role="alert">The layout failed: {(positions.error as Error).message}</p>;
  }

  return (
    <section className="diagram" aria-label={`Statechart of ${name}`}>
      <div className="flow" data-ready={flow !== undefined ? 'true' : 'false'} data-nodes={graph.nodes.length} data-edges={graph.edges.length}>
        {flow === undefined ? (
          <p role="status">Laying out {graph.nodes.length} states…</p>
        ) : (
          <ReactFlowProvider>
            <ReactFlow
              nodes={nodes}
              edges={flow.edges}
              nodeTypes={nodeTypes}
              edgeTypes={edgeTypes}
              onNodesChange={onNodesChange}
              onNodeClick={(_, node) => {
                if (node.data.node.kind === 'step' || node.data.node.kind === 'pivot') {
                  editing?.onSelect?.(node.id);
                }
              }}
              onNodeDragStop={(_, node) => editing?.onMove?.(node.id, node.position.x, node.position.y)}
              onPaneClick={() => editing?.onSelect?.(undefined)}
              nodesDraggable={editing?.editable === true}
              nodesConnectable={false}
              nodesFocusable
              edgesFocusable
              elementsSelectable
              fitView
              minZoom={0.1}
            >
              <Controls showInteractive={false} />
            </ReactFlow>
          </ReactFlowProvider>
        )}
      </div>
      <Legend counts={counts} />
    </section>
  );
}
