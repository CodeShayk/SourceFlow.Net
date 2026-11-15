// Monaco with monaco-yaml beside the canvas (doc 15 §6.2): completion and hover from schemas/workflow-1.json, and the
// core's diagnostics as markers. Loaded on demand (it is the app's largest part); workers come from this origin only.
// The editor API, its features and YAML only: the package's main entry registers every language and its workers.
import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/features/register.all.js';
import 'monaco-editor/languages/definitions/yaml/register.js';
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';
import { configureMonacoYaml } from 'monaco-yaml';
import YamlWorker from 'monaco-yaml/yaml.worker?worker';
import { useEffect, useRef } from 'react';
import schema from '../../../../schemas/workflow-1.json';
import type { CodeEditorProps } from './CodeEditor.tsx';

(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker: (_: string, label: string) => (label === 'yaml' ? new YamlWorker() : new EditorWorker()),
};

configureMonacoYaml(monaco, {
  enableSchemaRequest: false,
  validate: false, // the core validates (WF-S*, WF-V*); the schema drives completion and hover
  schemas: [{ uri: 'https://sourceflow.dev/schemas/workflow-1.json', fileMatch: ['**/*.sfflow.yaml'], schema: schema as never }],
});

function theme(): string {
  return document.documentElement.dataset.theme === 'dark' ? 'vs-dark' : 'vs';
}

export default function MonacoEditor({ path, value, onChange, diagnostics, readOnly, label, reveal }: CodeEditorProps) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const changing = useRef(false);
  const handler = useRef(onChange);
  handler.current = onChange;

  useEffect(() => {
    const uri = monaco.Uri.parse('file:///' + path);
    const model = monaco.editor.getModel(uri) ?? monaco.editor.createModel(value, 'yaml', uri);
    const instance = monaco.editor.create(host.current!, {
      model,
      readOnly,
      ariaLabel: label,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 13,
      tabSize: 2,
      insertSpaces: true,
      theme: theme(),
    });
    editor.current = instance;
    const content = model.onDidChangeContent(() => {
      if (!changing.current) {
        handler.current(model.getValue());
      }
    });
    const cursor = instance.onDidChangeCursorPosition((event) => {
      host.current?.setAttribute('data-cursor-line', String(event.position.lineNumber));
    });
    const observer = new MutationObserver(() => monaco.editor.setTheme(theme()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => {
      content.dispose();
      cursor.dispose();
      observer.disconnect();
      instance.dispose();
      model.dispose();
      editor.current = null;
    };
    // The editor is created once per file; value and options follow below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path]);

  // Text from outside (a visual edit, undo): replace it as one edit, so Monaco's own undo still works.
  useEffect(() => {
    const instance = editor.current;
    const model = instance?.getModel();
    if (model === undefined || model === null || model.getValue() === value) {
      return;
    }

    changing.current = true;
    model.pushEditOperations([], [{ range: model.getFullModelRange(), text: value }], () => null);
    changing.current = false;
  }, [value]);

  useEffect(() => {
    editor.current?.updateOptions({ readOnly });
  }, [readOnly]);

  useEffect(() => {
    const model = editor.current?.getModel();
    if (model === undefined || model === null) {
      return;
    }

    monaco.editor.setModelMarkers(model, 'sourceflow', diagnostics.map((d) => ({
      severity: d.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
      message: `${d.id}: ${d.message}`,
      startLineNumber: d.line,
      startColumn: d.column,
      endLineNumber: d.line,
      endColumn: model.getLineMaxColumn(Math.min(d.line, model.getLineCount())),
    })));
  }, [diagnostics, value]);

  useEffect(() => {
    if (reveal === undefined || editor.current === null) {
      return;
    }

    editor.current.revealLineInCenter(reveal.line);
    editor.current.setPosition({ lineNumber: reveal.line, column: 1 });
    editor.current.focus();
  }, [reveal]);

  return <div ref={host} className="code-editor monaco" data-testid="monaco" />;
}
