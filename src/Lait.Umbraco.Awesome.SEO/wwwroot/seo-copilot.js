// Bridge to Umbraco AI Copilot (Umbraco.AI.Agent.Copilot), without depending on it: when Copilot is not installed its
// context simply never shows up and the dashboard hides the "Ret med AI" buttons.
//
// Flow: the dashboard calls sendToCopilot(task) → the task is parked in sessionStorage and the backoffice navigates to the
// page or media item → this entry point waits until that workspace is open, then opens Copilot, selects the agent and
// sends the task. Copilot collects the page/media context itself and proposes the change; the editor approves it.
import { UmbContextConsumerController, UmbContextToken } from '@umbraco-cms/backoffice/context-api';

// Same alias and type check as Copilot's own UAI_COPILOT_CONTEXT, so we never import its module.
const COPILOT = new UmbContextToken('UaiCopilotContext', undefined, (c) => !!c?.IS_COPILOT_CONTEXT);
const KEY = 'lait-seo-copilot-task';
const SETTLE_MS = 1500;    // let the workspace load before Copilot collects the page context
const GIVE_UP_MS = 60000;

let copilot;
let waiting = [];

/** Resolves to the Copilot context, or undefined when it is not there within the timeout (= not installed). */
export function getCopilot(timeoutMs = 2000) {
  if (copilot) return Promise.resolve(copilot);
  return new Promise((resolve) => {
    waiting.push(resolve);
    setTimeout(() => resolve(copilot), timeoutMs);
  });
}

export function editPath(entity, key) {
  return entity === 'media'
    ? `/umbraco/section/media/workspace/media/edit/${key}`
    : `/umbraco/section/content/workspace/document/edit/${key}`;
}

/** task: { entity: 'document' | 'media', key, message, agentId? } */
export function sendToCopilot(task) {
  const path = editPath(task.entity, task.key);
  try {
    sessionStorage.setItem(KEY, JSON.stringify({ ...task, path, at: Date.now() }));
  } catch {
    return false;
  }
  if (location.pathname !== path) {
    window.history.pushState({}, '', path);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }
  return true;
}

function pending() {
  try {
    const t = JSON.parse(sessionStorage.getItem(KEY) ?? 'null');
    if (t && Date.now() - t.at > GIVE_UP_MS) {
      sessionStorage.removeItem(KEY);
      return null;
    }
    return t;
  } catch {
    return null;
  }
}

let onPathSince = 0;
async function tick() {
  const task = pending();
  if (!task || !copilot) return;
  if (location.pathname !== task.path) {
    onPathSince = 0;
    return;
  }
  onPathSince ||= Date.now();
  if (Date.now() - onPathSince < SETTLE_MS) return;

  sessionStorage.removeItem(KEY);
  onPathSince = 0;
  try {
    if (task.agentId) {
      await copilot.loadAgents?.();
      copilot.selectAgent(task.agentId);
    }
    copilot.open();
    await copilot.sendUserMessage(task.message);
  } catch (e) {
    console.warn('[Lait SEO] Could not hand the task to Copilot', e);
  }
}

export const onInit = (host) => {
  new UmbContextConsumerController(host, COPILOT, (ctx) => {
    copilot = ctx;
    waiting.forEach((r) => r(ctx));
    waiting = [];
  });
  setInterval(tick, 500);
};
