// Tiny client for the Lait SEO Management API (requires an authenticated backoffice user).
const BASE = '/umbraco/lait-seo/api/v1';

async function request(url, token, method = 'GET') {
  const response = await fetch(url, {
    method,
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  });
  if (!response.ok) {
    const err = new Error(`SEO API request failed (${response.status})`);
    err.status = response.status;
    throw err;
  }
  return response.status === 202 ? null : response.json();
}

export const getDomains = (token) => request(`${BASE}/domains`, token);
export const getReport = (domain, token) => request(`${BASE}/report?domain=${encodeURIComponent(domain)}`, token);
export const getEstimate = (domain, token) => request(`${BASE}/estimate?domain=${encodeURIComponent(domain)}`, token);
export const startRun = (domain, dryRun, token) =>
  request(`${BASE}/run?domain=${encodeURIComponent(domain)}&dryRun=${dryRun ? 'true' : 'false'}`, token, 'POST');

export const getSettings = (token) => request(`${BASE}/settings`, token);

/** Resolves { errors: string[] } on a 400 so the UI can list what is wrong; rejects on other failures. */
export async function saveSettings(sites, token) {
  const response = await fetch(`${BASE}/settings`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify({ sites }),
  });
  if (response.status === 400) return { errors: await response.json() };
  if (response.status === 409) return { errors: [await response.text()] };
  if (!response.ok) throw new Error(`Kunne ikke gemme (${response.status})`);
  return {};
}

/** Resolves { errors } on 400, otherwise { ai } with the refreshed (key-free) AI settings. */
export async function saveAiSettings(body, token) {
  const response = await fetch(`${BASE}/settings/ai`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify(body),
  });
  if (response.status === 400) return { errors: await response.json() };
  if (!response.ok) throw new Error(`Kunne ikke gemme (${response.status})`);
  return { ai: await response.json() };
}

/** Resolves { ok, message }. What is typed in the form (key, address, model) is tested without being saved. */
export async function testAiConnection(provider, { apiKey, baseUrl, model }, token) {
  const response = await fetch(`${BASE}/settings/ai/test`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify({ provider, apiKey: apiKey || null, baseUrl: baseUrl || null, model: model || null }),
  });
  if (!response.ok) throw new Error(`Test mislykkedes (${response.status})`);
  return response.json();
}

export const getBenchmark = (domain, token) => request(`${BASE}/benchmark?domain=${encodeURIComponent(domain)}`, token);

export const getPlan = (domain, token) => request(`${BASE}/plan?domain=${encodeURIComponent(domain)}`, token);
export const getPlanEstimate = (domain, token) => request(`${BASE}/plan/estimate?domain=${encodeURIComponent(domain)}`, token);

/** Resolves the new plan, or { message } when the server refuses (budget, no crawl, provider error). */
export async function writePlan(domain, token) {
  const response = await fetch(`${BASE}/plan?domain=${encodeURIComponent(domain)}`, {
    method: 'POST',
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  });
  const body = await response.json().catch(() => ({}));
  return response.ok ? body : { message: body.message ?? `Fejl (${response.status})` };
}

/** The media item behind an image URL, or null when it is not in the media library. */
export const findMedia = (src, token) => request(`${BASE}/media?src=${encodeURIComponent(src)}`, token).catch(() => null);

/** Google Search Console for the site: { configured, message, property, serviceAccount, insights, nodes }. */
export const getSearch = (domain, token) => request(`${BASE}/search?domain=${encodeURIComponent(domain)}`, token);

/** body: { serviceAccountJson } to save, or { clear: true }. Resolves { errors } on 400, otherwise { search }. */
export async function saveSearchSettings(body, token) {
  const response = await fetch(`${BASE}/settings/search`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify(body),
  });
  if (response.status === 400) return { errors: await response.json() };
  if (!response.ok) throw new Error(`Kunne ikke gemme (${response.status})`);
  return { search: await response.json() };
}

/** Resolves { ok, message, properties, sites }. A typed key is tested without being saved; empty = the saved key. */
export async function testSearchConnection(serviceAccountJson, token) {
  const response = await fetch(`${BASE}/settings/search/test`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify({ serviceAccountJson: serviceAccountJson || null }),
  });
  if (!response.ok) throw new Error(`Test mislykkedes (${response.status})`);
  return response.json();
}
