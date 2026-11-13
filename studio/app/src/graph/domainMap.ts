// The domain map (doc 15 §6.1, WFD-13): services as containers with their aggregates, commands to their owner, domain
// events published to topics, and subscriptions, aggregated from the workspace's manifests.
import type { ServiceDto, WorkspaceDto } from '../api.ts';
import { sizeFor, type LayoutEdge, type LayoutNode } from './layout.ts';

export type MapNodeKind = 'service' | 'aggregate' | 'topic';
export type MapEdgeKind = 'command' | 'publishes' | 'subscribes';

export interface MapNode {
  id: string;
  kind: MapNodeKind;
  label: string;
  parent?: string;
  /** Aggregates: the commands they handle; topics: the event types published to them. */
  details: string[];
}

export interface MapEdge {
  id: string;
  source: string;
  target: string;
  kind: MapEdgeKind;
  /** The message types the edge carries. */
  messages: string[];
  label: string;
}

export interface DomainMap {
  nodes: MapNode[];
  edges: MapEdge[];
}

interface ManifestJson {
  service?: string;
  aggregates?: Array<{ name: string; commands?: Array<{ type: string; version: number }> }>;
  publishes?: Array<{ domainEvent: string; as: string; topic: string }>;
  subscribes?: Array<{ topic: string; only?: string[] }>;
  commandRoutes?: Array<{ command: string; to: string }>;
  processManagers?: Array<{ sends?: string[] }>;
}

export const serviceId = (name: string) => 'service:' + name;
export const aggregateId = (service: string, aggregate: string) => 'aggregate:' + service + '/' + aggregate;
export const topicId = (topic: string) => 'topic:' + topic;

function parse(service: ServiceDto): ManifestJson {
  try {
    const value = JSON.parse(service.manifestJson) as unknown;
    return typeof value === 'object' && value !== null ? (value as ManifestJson) : {};
  } catch {
    return {};
  }
}

/** Builds the map. A command goes to the aggregate that handles it; commands a service handles itself draw no edge. */
export function domainMap(workspace: WorkspaceDto): DomainMap {
  const manifests = workspace.services.map((s) => ({ service: s.name, manifest: parse(s) }));
  const nodes: MapNode[] = [];
  const owners = new Map<string, { service: string; aggregate: string }>();
  for (const { service, manifest } of manifests) {
    nodes.push({ id: serviceId(service), kind: 'service', label: service, details: [] });
    for (const aggregate of manifest.aggregates ?? []) {
      const commands = (aggregate.commands ?? []).map((c) => `${c.type}.v${c.version}`);
      nodes.push({ id: aggregateId(service, aggregate.name), kind: 'aggregate', label: aggregate.name, parent: serviceId(service), details: commands });
      for (const command of commands) {
        if (!owners.has(command)) {
          owners.set(command, { service, aggregate: aggregate.name });
        }
      }
    }
  }

  const edges = new Map<string, MapEdge>();
  const add = (kind: MapEdgeKind, source: string, target: string, message: string) => {
    const id = `${kind}:${source}->${target}`;
    const edge = edges.get(id) ?? { id, source, target, kind, messages: [], label: '' };
    if (!edge.messages.includes(message)) {
      edge.messages.push(message);
    }

    edges.set(id, edge);
  };

  const topics = new Map<string, string[]>();
  for (const { service, manifest } of manifests) {
    const sent = new Set([...(manifest.commandRoutes ?? []).map((r) => r.command), ...(manifest.processManagers ?? []).flatMap((p) => p.sends ?? [])]);
    for (const command of [...sent].sort()) {
      const owner = owners.get(command);
      if (owner !== undefined && owner.service !== service) {
        add('command', serviceId(service), aggregateId(owner.service, owner.aggregate), command);
      }
    }

    for (const publication of manifest.publishes ?? []) {
      topics.set(publication.topic, [...(topics.get(publication.topic) ?? []), publication.as]);
      add('publishes', serviceId(service), topicId(publication.topic), publication.as);
    }
  }

  for (const { service, manifest } of manifests) {
    for (const subscription of manifest.subscribes ?? []) {
      if (!topics.has(subscription.topic)) {
        topics.set(subscription.topic, []);
      }

      const only = subscription.only ?? [];
      add('subscribes', topicId(subscription.topic), serviceId(service), only.length === 0 ? 'all events' : only.join(', '));
    }
  }

  for (const [topic, published] of [...topics].sort(([a], [b]) => a.localeCompare(b))) {
    nodes.push({ id: topicId(topic), kind: 'topic', label: topic, details: published });
  }

  const symbols: Record<MapEdgeKind, string> = { command: '→', publishes: '⇢', subscribes: '⇠' };
  return {
    nodes,
    edges: [...edges.values()].map((e) => ({ ...e, label: `${symbols[e.kind]} ${e.messages.length === 1 ? e.messages[0] : `${e.messages.length} ${e.kind === 'command' ? 'commands' : 'events'}`}` })),
  };
}

/**
 * The choreography chain from a node (highlighted on hover): every edge reachable downstream, following commands to
 * aggregates' services, publications to topics, and subscriptions to the services that react.
 */
export function chainFrom(map: DomainMap, start: string): Set<string> {
  const parentOf = new Map(map.nodes.filter((n) => n.parent !== undefined).map((n) => [n.id, n.parent!] as const));
  const chain = new Set<string>();
  const seen = new Set<string>([start]);
  const queue = [start];
  while (queue.length > 0) {
    const node = queue.shift()!;
    for (const edge of map.edges) {
      const from = edge.source === node || parentOf.get(edge.source) === node || edge.source === parentOf.get(node);
      if (!from || chain.has(edge.id)) {
        continue;
      }

      chain.add(edge.id);
      const next = parentOf.get(edge.target) ?? edge.target;
      if (!seen.has(next)) {
        seen.add(next);
        queue.push(next);
      }
    }
  }

  return chain;
}

/** What ELK lays out (left to right): aggregates inside their service. */
export function mapLayoutInput(map: DomainMap): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  return {
    nodes: map.nodes.map((n) => ({
      id: n.id,
      ...sizeFor(n.kind === 'service' ? ['▢ ' + n.label] : n.kind === 'topic' ? ['⇄ ' + n.label] : ['▣ ' + n.label, `${n.details.length} command${n.details.length === 1 ? '' : 's'}`], 120),
      ...(n.parent === undefined ? {} : { parent: n.parent }),
    })),
    edges: map.edges.map((e) => ({ id: e.id, source: e.source, target: e.target })),
  };
}
