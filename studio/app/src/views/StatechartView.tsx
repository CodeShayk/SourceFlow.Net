import { Controls, MarkerType, ReactFlow, ReactFlowProvider, type EdgeTypes, type NodeTypes } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import type { GraphResult } from '@sourceflow/core-client';
import { StudioCoreError } from '@sourceflow/core-client';
import { useMemo } from 'react';
import { edgeCounts, statechartPositions, toFlow } from '../graph/statechart.ts';
import { useGraph, useServices } from '../services.tsx';
import { KindEdge } from './KindEdge.tsx';
import { Legend } from './Legend.tsx';
import { GroupNode, StateNode } from './StateNode.tsx';

const nodeTypes: NodeTypes = { state: StateNode, group: GroupNode };
const edgeTypes: EdgeTypes = { kind: KindEdge };

/** The statechart of a workflow (doc 15 §6.1, WFD-30): the core's graph, laid out by ELK, drawn by React Flow. */
export function StatechartView({ text, name }: { text: string; name: string }) {
  const graph = useGraph(text);
  if (graph.isError) {
    const error = graph.error;
    return (
      <div role="alert" className="view-message">
        The statechart needs a readable workflow: {error instanceof StudioCoreError && error.diagnostics.length > 0 ? `${error.diagnostics.length} error(s) in the file` : (error as Error).message}.
      </div>
    );
  }

  return graph.data === undefined ? <p role="status">Drawing the statechart…</p> : <LaidOut graph={graph.data} name={name} />;
}

function LaidOut({ graph, name }: { graph: GraphResult; name: string }) {
  const { layout } = useServices();
  const positions = useQuery({ queryKey: ['layout', 'statechart', graph], queryFn: () => statechartPositions(graph, layout), retry: false });
  const flow = useMemo(() => {
    if (positions.data === undefined) {
      return undefined;
    }

    const { nodes, edges } = toFlow(graph, positions.data);
    return { nodes, edges: edges.map((e) => ({ ...e, markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16 } })) };
  }, [graph, positions.data]);
  const counts = useMemo(() => edgeCounts(graph), [graph]);

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
              nodes={flow.nodes}
              edges={flow.edges}
              nodeTypes={nodeTypes}
              edgeTypes={edgeTypes}
              nodesDraggable={false}
              nodesConnectable={false}
              nodesFocusable
              edgesFocusable
              elementsSelectable
              fitView
              minZoom={0.1}
              ariaLabelConfig={{ 'node.a11yDescription.default': 'Use Tab to move between states and transitions.' }}
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
