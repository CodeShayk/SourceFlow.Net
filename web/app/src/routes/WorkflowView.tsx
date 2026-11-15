import type { Diagnostic } from '@sourceflow/core-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { forwardRef, Suspense, useEffect, useMemo, useState } from 'react';
import type { ServiceDto, WorkspaceDto } from '../api.ts';
import { Tabs } from '../components/Tabs.tsx';
import { isDirty, textOf, useEditor } from '../edit/editorStore.ts';
import { canRedo, canUndo } from '../edit/history.ts';
import { download, EXPORTS, exportWorkflow, type ExportFormat } from '../export.ts';
import { useStudio } from '../store.ts';
import { prefersDark, resolveTheme } from '../theme.ts';
import { layoutPath, pruneLayout, writeLayout } from '../edit/layoutFile.ts';
import { palette as paletteOf } from '../edit/palette.ts';
import { PalettePanel } from '../edit/PalettePanel.tsx';
import { StepProperties } from '../edit/StepProperties.tsx';
import { stepAtLine, stepLocator, stepsOf } from '../edit/steps.ts';
import { PathExplorer } from '../simulate/PathExplorer.tsx';
import { activeNode } from '../simulate/simulation.ts';
import { SimulationPanel } from '../simulate/SimulationPanel.tsx';
import { useSimulation } from '../simulate/simulationStore.ts';
import { useServices } from '../services.tsx';
import { StatechartView } from '../views/StatechartView.tsx';
import { SwimlaneView } from '../views/SwimlaneView.tsx';

interface Props {
  path: string;
  owner: ServiceDto | undefined;
  workspace: WorkspaceDto;
  /** The file as stored, once read. */
  stored: string | undefined;
  diagnostics: readonly Diagnostic[] | undefined;
  /** The selected diagnostic: the YAML tab opens on its line. */
  selected: Diagnostic | undefined;
  error?: string;
}

/**
 * An open workflow (doc 15 §6.2): the statechart or swimlane beside the step's properties or the YAML. The text is the
 * model: visual edits are minimal text edits, typing in the editor is the same text, and undo covers both.
 */
export const WorkflowView = forwardRef<HTMLElement, Props>(function WorkflowView({ path, owner, workspace, stored, diagnostics, selected, error }, ref) {
  const { api, core, layout: engine, codeEditor: CodeEditor } = useServices();
  const themePreference = useStudio((s) => s.theme);
  const simulation = useSimulation();
  const queries = useQueryClient();
  const editor = useEditor();
  const layoutFile = useQuery({
    queryKey: ['file', layoutPath(path)],
    queryFn: () => api.readFile(layoutPath(path)).catch(() => ''),
    enabled: stored !== undefined,
  });

  // Open the file in the editor when it is read (or re-read while nothing is unsaved).
  useEffect(() => {
    if (stored === undefined || layoutFile.data === undefined) {
      return;
    }

    const state = useEditor.getState();
    if (state.path !== path || (!isDirty(state) && state.saved !== stored)) {
      state.open(path, stored, layoutFile.data === '' ? undefined : layoutFile.data);
    }
  }, [path, stored, layoutFile.data]);

  const open = editor.path === path;
  const text = open ? textOf(editor) : stored;
  const readOnly = workspace.readOnly;
  const steps = useMemo(() => (text === undefined ? [] : stepsOf(text)), [text]);
  const domain = useMemo(() => paletteOf(workspace), [workspace]);
  const problems = useMemo(() => {
    const counts = new Map<string, number>();
    const stepAt = text === undefined || (diagnostics ?? []).length === 0 ? undefined : stepLocator(text);
    for (const d of diagnostics ?? []) {
      const index = stepAt?.(d.line);
      const id = index === undefined ? undefined : steps[index]?.id;
      if (id !== undefined) {
        counts.set(id, (counts.get(id) ?? 0) + 1);
      }
    }

    return counts;
  }, [diagnostics, text, steps]);

  const [left, setLeft] = useState('statechart');
  const [right, setRight] = useState('properties');
  useEffect(() => {
    if (selected !== undefined && open) {
      setRight('yaml');
      editor.revealLine(selected.line);
      const index = text === undefined ? undefined : stepAtLine(text, selected.line);
      if (index !== undefined) {
        editor.select(steps[index]?.id);
      }
    }
    // Only a new selection moves the view.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected]);

  const dirty = open && isDirty(editor);
  // A save's outcome describes the text it saved: an edit after it (undo too) is unsaved again.
  const [saving, setSaving] = useState<{ message: string; text: string } | undefined>();
  const save = async () => {
    const state = useEditor.getState();
    if (readOnly || state.path !== path || !isDirty(state)) {
      return;
    }

    const current = textOf(state);
    const layout = writeLayout(pruneLayout(state.layout, stepsOf(current).map((s) => s.id).concat(['start', 'Completed', 'Compensating', 'Compensated', 'ManualIntervention'])));
    const report = (message: string) => setSaving({ message, text: current });
    report('Saving…');
    try {
      if (current !== state.saved) {
        await api.writeFile(path, current);
      }

      if (layout !== state.savedLayout && Object.keys(state.layout.nodes).length > 0) {
        await api.writeFile(layoutPath(path), layout);
      }

      state.saved_(current, layout);
      queries.setQueryData(['file', path], current);
      report('Saved.');
    } catch (failure) {
      report('Not saved: ' + (failure as Error).message);
    }
  };

  useEffect(() => {
    const onSave = () => void save();
    window.addEventListener('sourceflow:save', onSave);
    return () => window.removeEventListener('sourceflow:save', onSave);
  });

  const name = path.slice(path.lastIndexOf('/') + 1);
  const [exporting, setExporting] = useState<{ busy?: ExportFormat; error?: string; done?: string }>({});
  const exportAs = async (format: ExportFormat) => {
    if (text === undefined) {
      return;
    }

    setExporting({ busy: format });
    try {
      const file = await exportWorkflow(format, text, name, { core, layout: engine }, open ? editor.layout : { version: 1, nodes: {} }, resolveTheme(themePreference, prefersDark()));
      download(file.name, file.content);
      setExporting({ done: `Exported ${file.name}.` });
    } catch (failure) {
      setExporting({ error: `The export needs a readable workflow: ${(failure as Error).message}` });
    }
  };
  const simulated = simulation.file === path && simulation.yaml === text ? activeNode(simulation.moves.at(-1)?.result) : undefined;
  const selectedStep = steps.find((s) => s.id === editor.selected);
  return (
    <article ref={ref} className="view workflow" aria-labelledby="workflow-title" tabIndex={-1}>
      <header className="workflow-header">
        <div>
          <h1 id="workflow-title">{name}</h1>
          <p className="lead">
            {owner !== undefined ? <>Owned by <strong>{owner.name}</strong> · </> : null}
            <code>{path}</code>
          </p>
        </div>
        <div className="toolbar" role="toolbar" aria-label="Edit">
          <button type="button" className="button" disabled={readOnly || !dirty} onClick={() => void save()} aria-keyshortcuts="Control+S">Save</button>
          <button type="button" className="button" disabled={!open || !canUndo(editor.history)} onClick={() => editor.undo()} aria-keyshortcuts="Control+Z">Undo</button>
          <button type="button" className="button" disabled={!open || !canRedo(editor.history)} onClick={() => editor.redo()} aria-keyshortcuts="Control+Y">Redo</button>
          <span className="toolbar-divider" aria-hidden="true" />
          <span className="toolbar-group" role="group" aria-label="Export">
            {EXPORTS.map((e) => (
              <button key={e.format} type="button" className="button" disabled={text === undefined || exporting.busy !== undefined} onClick={() => void exportAs(e.format)}
                aria-label={`Export ${e.label}`} title={`Download the ${e.format === 'svg' || e.format === 'png' ? 'statechart' : 'workflow'} as ${e.label}`}>
                {e.label}
              </button>
            ))}
          </span>
          <span role="status" className="save-state">{readOnly ? 'Read-only' : saving !== undefined && saving.text === text ? saving.message : dirty ? 'Unsaved changes' : 'No changes'}</span>
        </div>
      </header>
      {exporting.error !== undefined ? <p role="alert" className="edit-error">{exporting.error}</p> : null}
      {exporting.done !== undefined ? <p role="status" className="sr-only" aria-label="Export">{exporting.done}</p> : null}
      {editor.error !== undefined && open ? (
        <p role="alert" className="edit-error">
          {editor.error} <button type="button" className="button" onClick={() => editor.clearError()}>Dismiss</button>
        </p>
      ) : null}
      {error !== undefined ? (
        <p role="alert">{error}</p>
      ) : text === undefined ? (
        <p role="status">Loading…</p>
      ) : (
        <div className="split">
          <div className="pane">
            <Tabs label={`Views of ${name}`} selected={left} onSelect={setLeft} tabs={[
              {
                id: 'statechart', label: 'Statechart', content: () => (
                  <StatechartView text={text} name={name} editing={{
                    editable: !readOnly && open,
                    ...(editor.selected !== undefined ? { selected: editor.selected } : {}),
                    onSelect: (id) => { editor.select(id); if (id !== undefined) { setRight('properties'); } },
                    layout: editor.layout,
                    onMove: (id, x, y) => editor.move(id, x, y),
                    problems,
                    ...(simulated !== undefined ? { active: simulated } : {}),
                  }} />
                ),
              },
              { id: 'swimlane', label: 'Swimlane', content: () => <SwimlaneView text={text} workspace={workspace} name={name} /> },
              {
                id: 'paths', label: 'Paths', content: () => (
                  <PathExplorer path={path} text={text} name={name} owner={owner} readOnly={readOnly} dirty={dirty} staticSite={workspace.staticSite === true}
                    selected={simulation.file === path ? simulation.selectedPaths : undefined}
                    onSelect={(ids) => { simulation.open(path, text); simulation.setSelectedPaths(ids); }}
                    onSimulate={(p) => {
                      simulation.open(path, text);
                      setRight('simulate');
                      void simulation.replay(core, text, p, steps.map((s) => s.id));
                    }} />
                ),
              },
            ]} />
          </div>
          <div className="pane side">
            <Tabs label={`Edit ${name}`} selected={right} onSelect={setRight} tabs={[
              {
                id: 'properties', label: 'Properties', content: () => (
                  <>
                    {selectedStep !== undefined ? (
                      <StepProperties key={selectedStep.id} step={selectedStep} steps={steps} palette={domain} edit={editor.edit} readOnly={readOnly || !open} onRenamed={(id) => editor.select(id)} />
                    ) : (
                      <p className="hint">Select a step in the statechart to edit it.</p>
                    )}
                    <PalettePanel palette={domain} steps={steps} selected={selectedStep?.index} edit={editor.edit} readOnly={readOnly || !open} onAdded={(id) => editor.select(id)} />
                  </>
                ),
              },
              {
                id: 'yaml', label: 'YAML', content: () => (
                  <Suspense fallback={<p role="status">Loading the editor…</p>}>
                    <CodeEditor path={path} value={text} onChange={(value) => editor.type(value)} diagnostics={diagnostics ?? []} readOnly={readOnly || !open}
                      label={`YAML of ${name}`} reveal={editor.reveal} />
                  </Suspense>
                ),
              },
              { id: 'simulate', label: 'Simulate', content: () => <SimulationPanel path={path} text={text} steps={steps} /> },
            ]} />
          </div>
        </div>
      )}
    </article>
  );
});
