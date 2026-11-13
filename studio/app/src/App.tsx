import { useEffect, useRef, useState } from 'react';
import { Route, Routes, useLocation, useNavigate } from 'react-router';
import { DiagnosticsPanel } from './components/DiagnosticsPanel.tsx';
import { Explorer } from './components/Explorer.tsx';
import { KeyboardMap } from './components/KeyboardMap.tsx';
import { ThemeSwitch } from './components/ThemeSwitch.tsx';
import { commandFor } from './keys.ts';
import { NotFound } from './routes/NotFound.tsx';
import { Overview } from './routes/Overview.tsx';
import { ServiceView } from './routes/ServiceView.tsx';
import { WorkflowView } from './routes/WorkflowView.tsx';
import { useValidation, useWorkflowText, useWorkspace } from './services.tsx';
import { useStudio } from './store.ts';
import { applyTheme, prefersDark, resolveTheme } from './theme.ts';
import { ownerOf, sortDiagnostics, workflowPath } from './workspace.ts';

/** The Studio shell (WP-S.8): header, workspace explorer, the routed view, and the diagnostics panel. */
export function App() {
  const workspace = useWorkspace();
  const location = useLocation();
  const navigate = useNavigate();
  const path = location.pathname.startsWith('/workflows/') ? workflowPath(location.pathname.slice('/workflows/'.length)) : undefined;
  const text = useWorkflowText(path);
  const validation = useValidation(text.data, workspace.data);

  const theme = useStudio((s) => s.theme);
  const [systemDark, setSystemDark] = useState(prefersDark);
  const resolved = resolveTheme(theme, systemDark);
  useEffect(() => applyTheme(resolved), [resolved]);
  useEffect(() => {
    const query = typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : undefined;
    const listener = (event: MediaQueryListEvent) => setSystemDark(event.matches);
    query?.addEventListener('change', listener);
    return () => query?.removeEventListener('change', listener);
  }, []);

  const explorer = useRef<HTMLElement>(null);
  const editor = useRef<HTMLElement>(null);
  const diagnosticsPanel = useRef<HTMLElement>(null);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const command = commandFor(event);
      if (command === undefined) {
        return;
      }

      event.preventDefault();
      const state = useStudio.getState();
      switch (command) {
        case 'keyboardMap': state.setKeyboardMapOpen(true); break;
        case 'focusExplorer': explorer.current?.focus(); break;
        case 'focusEditor': editor.current?.focus(); break;
        case 'focusDiagnostics': diagnosticsPanel.current?.focus(); break;
        case 'nextDiagnostic': state.moveDiagnostic(1); break;
        case 'previousDiagnostic': state.moveDiagnostic(-1); break;
        case 'toggleTheme': state.toggleTheme(resolved); break;
        case 'overview': void navigate('/'); break;
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [navigate, resolved]);

  const selectedIndex = useStudio((s) => s.selectedDiagnostic);
  const sorted = validation.data === undefined ? undefined : sortDiagnostics(validation.data.diagnostics);

  if (workspace.isError) {
    return (
      <main className="fatal">
        <h1>SourceFlow Studio</h1>
        <p role="alert">The workspace could not be loaded: {(workspace.error as Error).message}</p>
      </main>
    );
  }

  if (workspace.data === undefined) {
    return <main className="fatal"><p role="status">Loading the workspace…</p></main>;
  }

  const data = workspace.data;
  const status = path === undefined ? 'Open a workflow to see its diagnostics.'
    : text.isError ? 'The workflow could not be read.'
    : validation.isError ? 'The Studio core failed: ' + (validation.error as Error).message
    : validation.data === undefined ? 'Validating…' : undefined;
  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">SourceFlow Studio</span>
        <span className="domain">{data.domain}</span>
        {data.readOnly ? <span className="badge" role="note">Read-only</span> : null}
        <span className="spacer" />
        <ThemeSwitch />
        <button type="button" className="button" onClick={() => useStudio.getState().setKeyboardMapOpen(true)} aria-keyshortcuts="Shift+?">
          Keyboard map
        </button>
      </header>
      <aside className="sidebar">
        <Explorer ref={explorer} workspace={data} />
      </aside>
      <main className="main">
        <Routes>
          <Route path="/" element={<Overview workspace={data} />} />
          <Route path="/services/:name" element={<ServiceView workspace={data} />} />
          <Route
            path="/workflows/*"
            element={path === undefined || !data.services.some((s) => s.workflowFiles.includes(path)) ? (
              <NotFound what="workflow" />
            ) : (
              <WorkflowView ref={editor} path={path} owner={ownerOf(data, path)} text={text.data} selected={sorted?.[selectedIndex]}
                {...(text.isError ? { error: (text.error as Error).message } : {})} />
            )}
          />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
      <footer className="panel">
        <DiagnosticsPanel ref={diagnosticsPanel} diagnostics={sorted} {...(status === undefined ? {} : { status })} />
      </footer>
      <KeyboardMap />
    </div>
  );
}
