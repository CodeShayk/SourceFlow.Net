// The palette (doc 15 §6.2): what the workspace's manifests make valid at the selected step. A command is offered when
// a service of the domain owns it (WF-V02); the events and rejection codes offered are the ones that command can
// produce (WF-V03, WF-V08).
import type { WorkspaceDto } from '../api.ts';

export interface PaletteCommand {
  /** The reference, `name.vN`. */
  ref: string;
  service: string;
  aggregate: string;
  emits: string[];
  rejects: string[];
  /** The command's fields (from its message schema), for the map. */
  fields: string[];
}

export interface Palette {
  commands: PaletteCommand[];
  /** The services that own commands, in workspace order. */
  services: string[];
  /** Every event a service of the domain publishes or emits. */
  events: string[];
}

interface ManifestJson {
  service?: string;
  messages?: Array<{ type: string; version: number; kind: string; fields?: Array<{ name: string }> }>;
  aggregates?: Array<{ name: string; commands?: Array<{ type: string; version: number; emits?: string[]; rejects?: string[] }> }>;
  publishes?: Array<{ as: string }>;
}

function parse(json: string): ManifestJson {
  try {
    const value = JSON.parse(json) as unknown;
    return typeof value === 'object' && value !== null ? (value as ManifestJson) : {};
  } catch {
    return {};
  }
}

const ref = (type: string, version: number) => `${type}.v${version}`;

export function palette(workspace: WorkspaceDto): Palette {
  const commands = new Map<string, PaletteCommand>();
  const events = new Set<string>();
  for (const service of workspace.services) {
    const manifest = parse(service.manifestJson);
    const fields = new Map((manifest.messages ?? []).map((m) => [ref(m.type, m.version), (m.fields ?? []).map((f) => f.name)]));
    for (const aggregate of manifest.aggregates ?? []) {
      for (const command of aggregate.commands ?? []) {
        const key = ref(command.type, command.version);
        if (!commands.has(key)) {
          commands.set(key, { ref: key, service: service.name, aggregate: aggregate.name, emits: [...(command.emits ?? [])], rejects: [...(command.rejects ?? [])], fields: fields.get(key) ?? [] });
        }

        (command.emits ?? []).forEach((e) => events.add(e));
      }
    }

    (manifest.publishes ?? []).forEach((p) => events.add(p.as));
  }

  const sorted = [...commands.values()].sort((a, b) => a.ref.localeCompare(b.ref));
  return { commands: sorted, services: workspace.services.map((s) => s.name).filter((s) => sorted.some((c) => c.service === s)), events: [...events].sort() };
}

export interface StepChoices {
  commands: PaletteCommand[];
  /** Events the step can wait for: what its command emits, or any domain event for a step that only waits. */
  successEvents: string[];
  /** Rejection codes the step's command can reply with that the step doesn't handle yet. */
  rejections: string[];
  /** Other events of the command, as failure events. */
  failureEvents: string[];
  /** The command's fields not mapped yet. */
  unmappedFields: string[];
}

/** What is valid at a step that sends `sends` and already handles `handled` codes and maps `mapped` fields. */
export function choicesFor(p: Palette, sends: string | undefined, handled: readonly string[] = [], mapped: readonly string[] = [], awaits?: string): StepChoices {
  const command = sends === undefined ? undefined : p.commands.find((c) => c.ref === sends);
  const success = command === undefined ? p.events : command.emits;
  return {
    commands: p.commands,
    successEvents: success,
    rejections: (command?.rejects ?? []).filter((r) => !handled.includes(r)),
    failureEvents: (command?.emits ?? []).filter((e) => e !== awaits),
    unmappedFields: (command?.fields ?? []).filter((f) => !mapped.includes(f)),
  };
}

/** Whether a command has an owner in the workspace (WF-V02 at edit time). */
export function isOwned(p: Palette, command: string | undefined): boolean {
  return command === undefined || p.commands.some((c) => c.ref === command);
}
