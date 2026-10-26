// Helm Bridge: connects this VS Code window to Helm (Workspace Controller) over a named pipe that only this Windows
// user can open. Helm asks it to switch to a Claude Code session, open a new one, or focus Claude's input; Helm then
// types the prompt itself. The bridge opens no port and runs nothing but those Claude Code commands.
'use strict';
const vscode = require('vscode');
const net = require('net');
const path = require('path');
const crypto = require('crypto');

const PROTOCOL = 1;
const RETRY_MS = 5000;
const CLAUDE_OPEN = 'claude-vscode.primaryEditor.open';
const CLAUDE_FOCUS = 'claude-vscode.focus';
// The tab `open` just showed is the active editor; focusing its group puts the keyboard in that Claude tab's input.
const FOCUS_EDITOR = 'workbench.action.focusActiveEditorGroup';

let socket = null;
let timer = null;
let stopped = false;
let buffer = '';

// Same name as Helm's WorkspaceEndpoint.PipeName: one pipe per Helm data folder.
function pipeName() {
  if (process.env.HELM_BRIDGE_PIPE) return process.env.HELM_BRIDGE_PIPE;
  const root = path.resolve(process.env.HELM_DATA_DIR || path.join(process.env.LOCALAPPDATA || '', 'Helm')).replace(/[\\/]+$/, '').toLowerCase();
  const hash = crypto.createHash('sha256').update(root, 'utf8').digest('hex').slice(0, 16).toUpperCase();
  return '\\\\.\\pipe\\Helm.Workspace.' + hash;
}

function isClaude(tab) {
  const viewType = tab.input && tab.input.viewType;
  return typeof viewType === 'string' && viewType.endsWith('claudeVSCodePanel');
}

function allClaudeTabs() {
  const tabs = [];
  for (const group of vscode.window.tabGroups.all) {
    for (const tab of group.tabs) if (isClaude(tab)) tabs.push({ tab, group });
  }
  return tabs;
}

// Claude names its tab after the conversation (cut to 25 characters); Helm matches the names to its sessions.
function tabList() {
  const active = vscode.window.tabGroups.activeTabGroup;
  return allClaudeTabs().map(({ tab, group }) => ({ label: tab.label, active: tab.isActive && group === active }));
}

// The tab in front of the active group: VS Code puts its name at the start of the title of the window showing it, so
// Helm can tell this workspace's windows apart (an editor moved into a new window has the workspace name too).
function activeEditor() {
  const group = vscode.window.tabGroups.activeTabGroup;
  return group && group.activeTab ? group.activeTab.label : null;
}

function state(type) {
  return {
    type,
    protocol: PROTOCOL,
    pid: process.pid,
    name: vscode.workspace.name || '',
    folders: (vscode.workspace.workspaceFolders || []).map(f => f.uri.fsPath),
    sendKey: vscode.workspace.getConfiguration('claudeCode').get('useCtrlEnterToSend') ? 'ctrl+enter' : 'enter',
    claudeTabs: allClaudeTabs().length,
    tabs: tabList(),
    focused: vscode.window.state.focused,
  };
}

function send(message) {
  if (socket && !socket.destroyed) socket.write(JSON.stringify(message) + '\n');
}

async function handle(request) {
  const reply = (ok, error) => send({ type: 'reply', id: request.id, ok, error: error || null, editor: activeEditor() });
  try {
    switch (request.op) {
      case 'ping':
        return reply(true);
      case 'open':
        // Switches to that session's tab (opens it if closed). A prompt is only pre-filled in a new tab.
        await vscode.commands.executeCommand(CLAUDE_OPEN, request.session || undefined, request.prompt || undefined);
        return reply(true);
      case 'new':
        await vscode.commands.executeCommand(CLAUDE_OPEN, undefined, undefined);
        return reply(true);
      case 'focus':
        await vscode.commands.executeCommand(CLAUDE_FOCUS);
        return reply(true);
      case 'focusEditor':
        await vscode.commands.executeCommand(FOCUS_EDITOR);
        return reply(true);
      case 'close': {
        // Closes the one Claude tab with this name; two tabs with the same name are not guessed between.
        const matches = allClaudeTabs().filter(t => t.tab.label === request.label);
        if (matches.length === 0) return reply(false, 'That Claude tab is not open in VS Code.');
        if (matches.length > 1) return reply(false, 'Two Claude tabs have that name; close it in VS Code.');
        await vscode.window.tabGroups.close(matches[0].tab, true);
        return reply(true);
      }
      default:
        return reply(false, 'Unknown request: ' + request.op);
    }
  } catch (e) {
    reply(false, String((e && e.message) || e));
  }
}

function onData(chunk) {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    let message;
    try { message = JSON.parse(line); } catch { continue; }
    if (message && message.type === 'request') handle(message);
  }
  if (buffer.length > 1024 * 1024) buffer = '';
}

function connect() {
  timer = null;
  if (stopped) return;
  buffer = '';
  const s = net.connect(pipeName());
  socket = s;
  s.setEncoding('utf8');
  s.on('connect', () => send(state('hello')));
  s.on('data', onData);
  s.on('error', () => {});
  s.on('close', () => {
    if (socket === s) socket = null;
    if (!stopped && !timer) timer = setTimeout(connect, RETRY_MS);
  });
}

function activate(context) {
  const changed = () => send(state('state'));
  context.subscriptions.push(
    vscode.workspace.onDidChangeWorkspaceFolders(changed),
    vscode.window.tabGroups.onDidChangeTabs(changed),
    vscode.window.onDidChangeWindowState(changed),
    vscode.workspace.onDidChangeConfiguration(e => { if (e.affectsConfiguration('claudeCode.useCtrlEnterToSend')) changed(); }),
  );
  connect();
}

function deactivate() {
  stopped = true;
  if (timer) clearTimeout(timer);
  if (socket) socket.destroy();
}

module.exports = { activate, deactivate };
