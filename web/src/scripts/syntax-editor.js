import { basicSetup, EditorView } from 'codemirror';
import { EditorState } from '@codemirror/state';
import { HighlightStyle, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';
import { oneDark } from '@codemirror/theme-one-dark';

const armaExtensions = new Set(['cfg', 'cpp', 'ext', 'fsm', 'h', 'hpp', 'inc', 'par', 'profile', 'rvmat', 'sqf', 'sqm', 'sqs']);
const armaKeywords = new Set([
  'break', 'breakout', 'case', 'class', 'continue', 'default', 'do', 'else', 'exitwith',
  'for', 'foreach', 'from', 'if', 'in', 'private', 'scopeName', 'switch', 'then', 'to',
  'try', 'catch', 'while', 'with',
].map(word => word.toLowerCase()));
const armaAtoms = new Set(['true', 'false', 'nil', 'null']);

const armaLanguage = StreamLanguage.define({
  startState: () => ({ blockComment: false, expectType: false }),
  token(stream, state) {
    if (state.blockComment) {
      while (!stream.eol()) {
        if (stream.match('*/')) { state.blockComment = false; break; }
        stream.next();
      }
      return 'comment';
    }

    if (stream.eatSpace()) return null;
    if (stream.match('//')) { stream.skipToEnd(); return 'comment'; }
    if (stream.match('/*')) { state.blockComment = true; return 'comment'; }
    if (stream.peek() === '#') { stream.skipToEnd(); return 'meta'; }

    const quote = stream.peek();
    if (quote === '"' || quote === "'") {
      stream.next();
      while (!stream.eol()) {
        const character = stream.next();
        if (character === '\\') stream.next();
        else if (character === quote) {
          if (stream.peek() === quote) stream.next();
          else break;
        }
      }
      return 'string';
    }

    if (stream.match(/^(?:0x[\da-f]+|(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?)/i)) return 'number';
    if (stream.match(/^(?:==|!=|<=|>=|&&|\|\||\+=|-=|\*=|\/=|=>|[=+\-*\/%<>!&|])/)) return 'operator';

    if (stream.match(/^[A-Za-z_$][\w$]*/)) {
      const word = stream.current();
      const lower = word.toLowerCase();
      if (state.expectType) { state.expectType = false; return 'typeName'; }
      if (lower === 'class') { state.expectType = true; return 'keyword'; }
      if (armaKeywords.has(lower)) return 'keyword';
      if (armaAtoms.has(lower)) return 'atom';
      if (/^[A-Z][A-Z\d_]+$/.test(word)) return 'constant';
      const remaining = stream.string.slice(stream.pos);
      if (word.startsWith('_') || /^\s*(?:\[\s*\])?\s*=/.test(remaining)) return 'variableName';
      if (/^\s*\(/.test(remaining)) return 'function';
      return 'propertyName';
    }

    stream.next();
    return null;
  },
});

const armaHighlightStyle = HighlightStyle.define([
  { tag: tags.variableName, color: '#61afef' },
  { tag: tags.propertyName, color: '#56b6c2' },
  { tag: tags.typeName, color: '#e5c07b', fontWeight: '600' },
  { tag: tags.function(tags.variableName), color: '#c678dd' },
  { tag: tags.constant(tags.name), color: '#d19a66' },
  { tag: tags.keyword, color: '#c678dd' },
  { tag: tags.bool, color: '#d19a66' },
  { tag: tags.atom, color: '#d19a66' },
  { tag: tags.number, color: '#e5c07b' },
  { tag: tags.string, color: '#98c379' },
  { tag: tags.operator, color: '#abb2bf' },
  { tag: tags.meta, color: '#e06c75' },
  { tag: tags.comment, color: '#7f8c98', fontStyle: 'italic' },
]);

async function languageFor(filename) {
  const extension = String(filename || '').split('.').pop().toLowerCase();
  if (armaExtensions.has(extension)) return armaLanguage;
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
      extensions: [basicSetup, oneDark, syntaxHighlighting(armaHighlightStyle), panelTheme, EditorView.lineWrapping, language],
    }),
    parent: host,
  });

  return {
    getValue: () => view.state.doc.toString(),
    focus: () => view.focus(),
    destroy: () => view.destroy(),
  };
}
