import { basicSetup, EditorView } from 'codemirror';
import { EditorState } from '@codemirror/state';
import { oneDark } from '@codemirror/theme-one-dark';

const armaExtensions = new Set(['cfg', 'cpp', 'ext', 'hpp', 'sqf', 'sqm']);

async function languageFor(filename) {
  const extension = String(filename || '').split('.').pop().toLowerCase();
  if (armaExtensions.has(extension)) return (await import('@codemirror/lang-cpp')).cpp();
  if (extension === 'json') return (await import('@codemirror/lang-json')).json();
  if (['html', 'htm', 'xml'].includes(extension)) return (await import('@codemirror/lang-html')).html();
  if (['js', 'mjs', 'cjs'].includes(extension)) return (await import('@codemirror/lang-javascript')).javascript();
  return [];
}

const panelTheme = EditorView.theme({
  '&': {
    backgroundColor: '#09090b',
    border: '1px solid #303038',
    borderRadius: '6px',
    fontSize: '13px',
    minHeight: '420px',
  },
  '&.cm-focused': { outline: '1px solid #8b5cf6' },
  '.cm-scroller': {
    fontFamily: "'Cascadia Code', 'Courier New', monospace",
    minHeight: '420px',
    overflow: 'auto',
  },
  '.cm-content': { caretColor: '#c4b5fd', minHeight: '420px' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: '#c4b5fd' },
  '.cm-gutters': {
    backgroundColor: '#111114',
    borderRight: '1px solid #303038',
    color: '#71717a',
  },
  '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: '#1c1827' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: '#4c3577 !important' },
});

export async function mountSyntaxEditor(host, value, filename) {
  host.replaceChildren();
  const language = await languageFor(filename);
  const view = new EditorView({
    state: EditorState.create({
      doc: value || '',
      extensions: [basicSetup, oneDark, panelTheme, EditorView.lineWrapping, language],
    }),
    parent: host,
  });

  return {
    getValue: () => view.state.doc.toString(),
    focus: () => view.focus(),
    destroy: () => view.destroy(),
  };
}
