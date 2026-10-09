// Browsers cache plugin files for 24 hours (Cache-Control max-age=86400) and Umbraco loads this file as a dynamic import,
// so even a hard refresh can keep running an old copy. Bump the ?v= on every import of this plugin's files (and in
// umbraco-package.json) whenever a file changes.
import { LitElement, html, css, nothing } from '@umbraco-cms/backoffice/external/lit';
import { getSettings, saveSettings, saveAiSettings, testAiConnection } from './api.js?v=1.1.2';

const CATEGORY_LABEL = { Crawl: 'Crawl og indeksering', Technical: 'Teknik', OnPage: 'On-page', Content: 'Indhold', Links: 'Links' };
const INTERVALS = [[1, 'Dagligt'], [7, 'Ugentligt'], [14, 'Hver 2. uge'], [30, 'Månedligt'], [60, 'Hver 2. måned']];

/**
 * Indstillinger tab (admin only). Edits a copy of the saved sites and saves the whole list at once.
 * Properties: getToken (async function returning a bearer token), onSaved (called after a successful save).
 */
class LaitSeoSettings extends LitElement {
  static properties = {
    getToken: { attribute: false },
    onSaved: { attribute: false },
    _data: { state: true },
    _sites: { state: true },
    _errors: { state: true },
    _status: { state: true },
    _busy: { state: true },
    _pick: { state: true },
    _ai: { state: true },
    _aiErrors: { state: true },
    _aiStatus: { state: true },
    _tests: { state: true },
    _testing: { state: true },
  };

  constructor() {
    super();
    this._errors = [];
    this._busy = false;
    this._pick = '';
    this._aiErrors = [];
    this._tests = {};
  }

  connectedCallback() {
    super.connectedCallback();
    this.#load();
  }

  async #load() {
    try {
      this._data = await getSettings(await this.getToken());
      this._sites = structuredClone(this._data.sites);
      this._ai = this.#aiForm(this._data.ai);
    } catch (e) {
      this._errors = [e?.message ?? 'Kunne ikke hente indstillingerne.'];
    }
  }

  async #save() {
    this._busy = true;
    this._errors = [];
    this._status = null;
    try {
      const result = await saveSettings(this._sites, await this.getToken());
      if (result.errors) {
        this._errors = result.errors;
      } else {
        await this.#load();
        this._status = 'Indstillingerne er gemt.';
        this.onSaved?.();
      }
    } catch (e) {
      this._errors = [e?.message ?? 'Kunne ikke gemme.'];
    } finally {
      this._busy = false;
    }
  }

  /** Form state from the effective settings. Keys are never sent to the browser, so those fields start empty. */
  #aiForm(ai) {
    const str = (v) => (v == null ? '' : String(v));
    return {
      claudeKey: '',
      clearClaude: false,
      claudeBase: str(ai.claude.baseUrl),
      triageModel: ai.claude.triageModel,
      reportModel: ai.claude.reportModel,
      jevKey: '',
      clearJev: false,
      jevModel: ai.jev.model,
      budget: str(ai.monthlyBudgetUsd),
      prices: {
        reportInput: str(ai.prices.reportInput),
        reportOutput: str(ai.prices.reportOutput),
        triageInput: str(ai.prices.triageInput),
        triageOutput: str(ai.prices.triageOutput),
        jevInput: str(ai.prices.jevInput),
      },
    };
  }

  #setAi(patch) {
    this._ai = { ...this._ai, ...patch };
    this._aiStatus = null;
  }

  async #saveAi() {
    const f = this._ai;
    const num = (v) => (String(v).trim() === '' ? null : Number(v));
    this._aiErrors = [];
    this._aiStatus = null;
    this._busy = true;
    try {
      const result = await saveAiSettings(
        {
          claude: { apiKey: f.claudeKey || null, clearKey: f.clearClaude, baseUrl: f.claudeBase, triageModel: f.triageModel, reportModel: f.reportModel },
          jev: { apiKey: f.jevKey || null, clearKey: f.clearJev, model: f.jevModel },
          monthlyBudgetUsd: num(f.budget),
          prices: {
            reportInput: num(f.prices.reportInput),
            reportOutput: num(f.prices.reportOutput),
            triageInput: num(f.prices.triageInput),
            triageOutput: num(f.prices.triageOutput),
            jevInput: num(f.prices.jevInput),
          },
        },
        await this.getToken(),
      );
      if (result.errors) {
        this._aiErrors = result.errors;
      } else {
        this._data = { ...this._data, ai: result.ai };
        this._ai = this.#aiForm(result.ai);
        this._aiStatus = 'AI-indstillingerne er gemt.';
      }
    } catch (e) {
      this._aiErrors = [e?.message ?? 'Kunne ikke gemme.'];
    } finally {
      this._busy = false;
    }
  }

  async #test(provider) {
    this._testing = provider;
    this._tests = { ...this._tests, [provider]: null };
    try {
      const f = this._ai;
      const form = provider === 'claude'
        ? { apiKey: f.claudeKey, baseUrl: f.claudeBase, model: f.triageModel }
        : { apiKey: f.jevKey, model: f.jevModel };
      this._tests = { ...this._tests, [provider]: await testAiConnection(provider, form, await this.getToken()) };
    } catch (e) {
      this._tests = { ...this._tests, [provider]: { ok: false, message: e?.message ?? 'Test mislykkedes.' } };
    } finally {
      this._testing = null;
    }
  }

  #update(i, patch) {
    this._sites = this._sites.map((s, idx) => (idx === i ? { ...s, ...patch } : s));
    this._status = null;
  }

  #addSite(url = '') {
    this._sites = [...this._sites, { url, enabled: true, intervalDays: 7, maxPages: 300, competitors: [], disabledRules: [] }];
    this._pick = '';
    this._status = null;
  }

  #removeSite(i) {
    if (!confirm(`Fjern ${this._sites[i].url || 'dette site'}? Tidligere rapporter beholdes, men der crawles ikke mere.`)) return;
    this._sites = this._sites.filter((_, idx) => idx !== i);
  }

  #toggleRule(i, ruleId, active) {
    const set = new Set(this._sites[i].disabledRules);
    if (active) set.delete(ruleId);
    else set.add(ruleId);
    this.#update(i, { disabledRules: [...set].sort() });
  }

  render() {
    if (!this._data) return this._errors.length ? this.#renderErrors() : html`<uui-loader></uui-loader>`;
    const taken = new Set(this._sites.map((s) => s.url.toLowerCase()));
    const available = this._data.umbracoDomains.filter((d) => !taken.has(d.toLowerCase()));
    return html`
      <h3>1. Sites og konkurrenter</h3>
      <p class="help">
        Vælg, hvilke af jeres sites der skal analyseres, hvor ofte, og hvilke konkurrenter de skal sammenlignes med.
        Husk at trykke <strong>Gem indstillinger</strong> nederst i afsnittet.
        <span class="muted">(Gemmes i databasen. <code>LaitSeo:Sites</code> i appsettings bruges kun som startværdier første gang.)</span>
      </p>
      ${this._sites.map((s, i) => this.#renderSite(s, i))}
      <div class="add">
        ${available.length
          ? html`<select aria-label="Domæner fra Umbraco" .value=${this._pick} @change=${(e) => (this._pick = e.target.value)}>
                <option value="">Domæner fra Umbraco …</option>
                ${available.map((d) => html`<option value=${d}>${d}</option>`)}
              </select>
              <uui-button look="secondary" label="Tilføj" ?disabled=${!this._pick} @click=${() => this.#addSite(this._pick)}>Tilføj</uui-button>`
          : nothing}
        <uui-button look="secondary" label="Tilføj anden adresse" @click=${() => this.#addSite()}>Tilføj anden adresse</uui-button>
      </div>
      ${this.#renderErrors()}
      ${this._status ? html`<p class="ok">${this._status}</p>` : nothing}
      <uui-button look="primary" color="positive" label="Gem" ?disabled=${this._busy} @click=${this.#save}>Gem indstillinger</uui-button>
      ${this.#renderAi(this._data.ai)}
    `;
  }

  #renderErrors() {
    return this._errors.length ? html`<ul class="error">${this._errors.map((e) => html`<li>${e}</li>`)}</ul>` : nothing;
  }

  #renderSite(s, i) {
    const grouped = Object.entries(CATEGORY_LABEL).map(([cat, label]) => [label, this._data.rules.filter((r) => r.category === cat)]).filter(([, r]) => r.length);
    return html`
      <div class="site">
        <div class="row">
          <label class="grow">Adresse
            <input type="url" .value=${s.url} placeholder="https://www.example.dk/" @input=${(e) => this.#update(i, { url: e.target.value })} />
          </label>
          <label class="check" title="Slået fra: sitet analyseres ikke og vises ikke i dashboardet"><input type="checkbox" .checked=${s.enabled} @change=${(e) => this.#update(i, { enabled: e.target.checked })} /> Aktiv</label>
          <uui-button look="secondary" color="danger" label="Fjern" @click=${() => this.#removeSite(i)}>Fjern</uui-button>
        </div>
        <div class="row">
          <label>Analysér automatisk hver … dag <span class="muted">(fx 7 = ugentligt)</span>
            <input type="number" min="1" max="365" list="intervals" .value=${String(s.intervalDays)} @input=${(e) => this.#update(i, { intervalDays: Number(e.target.value) })} />
          </label>
          <label>Højst antal sider pr. site <span class="muted">(gælder også konkurrenter; flere = mere præcist, men tager længere)</span>
            <input type="number" min="1" max="2000" .value=${String(s.maxPages)} @input=${(e) => this.#update(i, { maxPages: Number(e.target.value) })} />
          </label>
        </div>
        <label>Konkurrenter <span class="muted">(én adresse pr. linje, fx https://www.konkurrent.dk/, højst 5. Hver konkurrent gør analysen længere)</span>
          <textarea rows="3" .value=${s.competitors.join('\n')}
            @input=${(e) => this.#update(i, { competitors: e.target.value.split(/\r?\n/) })}></textarea>
        </label>
        <details>
          <summary>Tjek (${s.disabledRules.length ? `${s.disabledRules.length} slået fra` : 'alle er slået til'})</summary>
          <p class="muted">Slå et tjek fra, hvis det ikke giver mening for jeres site. Fund fra et slået-fra tjek vises ikke og tæller ikke i scoren.</p>
          ${grouped.map(
            ([label, rules]) => html`<div class="group"><strong>${label}</strong>
              ${rules.map(
                (r) => html`<label class="check">
                  <input type="checkbox" .checked=${!s.disabledRules.includes(r.id)} @change=${(e) => this.#toggleRule(i, r.id, e.target.checked)} />
                  ${r.id} – ${r.title}
                </label>`,
              )}</div>`,
          )}
        </details>
      </div>
      <datalist id="intervals">${INTERVALS.map(([d, l]) => html`<option value=${d}>${l}</option>`)}</datalist>
    `;
  }

  #keyStatus(k, unreadable) {
    if (k.keySource === 'database') return html`<span class="ok">Gemt i databasen (krypteret) ${k.keyHint ?? ''}</span>`;
    if (k.keySource === 'config') return html`Sat i appsettings/secrets ${k.keyHint ?? ''} – en nøgle gemt her går forud for den.`;
    return html`<span class="error">Ingen nøgle${unreadable ? ' (en gemt nøgle kunne ikke læses, indtast den igen)' : ''}</span>`;
  }

  #renderTest(provider) {
    const t = this._tests[provider];
    return html`<uui-button look="secondary" label="Test forbindelse" ?disabled=${this._testing === provider} @click=${() => this.#test(provider)}>
        ${this._testing === provider ? 'Tester …' : 'Test forbindelse'}</uui-button>
      ${t ? html`<span class=${t.ok ? 'ok' : 'error'}>${t.message}</span>` : nothing}`;
  }

  #renderAi(ai) {
    const usd = (v) => `${v.toLocaleString('da-DK', { minimumFractionDigits: v < 0.1 ? 3 : 2, maximumFractionDigits: v < 0.1 ? 3 : 2 })} USD`;
    const f = this._ai;
    const price = (key, label) => html`<label>${label}
      <input type="number" min="0" step="any" .value=${f.prices[key]} @input=${(e) => this.#setAi({ prices: { ...f.prices, [key]: e.target.value } })} /></label>`;
    return html`
      <h3>2. AI (valgfrit)</h3>
      <p class="help">
        Alle tjek, sammenligningen og handlingsplanen virker uden AI og er gratis. AI gør rapporten mere forståelig:
      </p>
      <ul class="help">
        <li><strong>Claude</strong> skriver tekst: en rapport om, hvad der har ændret sig, og begrundelse og trin i handlingsplanen.</li>
        <li><strong>Jev</strong> skriver ikke tekst, men vurderer: hvilke ændringer der er vigtige, og hvor stor effekt hvert punkt i planen har.</li>
        <li>I kan bruge den ene, den anden eller begge. Med begge vurderer Jev, og Claude skriver.</li>
      </ul>
      <p class="muted">Nøgler krypteres og vises aldrig igen, når de er gemt. Værdier her går forud for appsettings.</p>

      <div class="site">
        <strong>Claude (Anthropic)</strong>
        <div>${this.#keyStatus(ai.claude, ai.storedKeyUnreadable)}</div>
        <div class="row">
          <label class="grow">Ny API-nøgle <span class="muted">(tom = behold nuværende)</span>
            <input type="password" autocomplete="new-password" name="lait-seo-api-key" spellcheck="false" placeholder="sk-ant-…" .value=${f.claudeKey} @input=${(e) => this.#setAi({ claudeKey: e.target.value, clearClaude: false })} />
          </label>
          ${ai.claude.keySource === 'database'
            ? html`<label class="check"><input type="checkbox" .checked=${f.clearClaude} @change=${(e) => this.#setAi({ clearClaude: e.target.checked, claudeKey: '' })} /> Fjern gemt nøgle</label>`
            : nothing}
        </div>
        <div class="row">
          <label>Sorterings-model <span class="muted">(billig, bruges når Jev ikke er sat op)</span> <input type="text" .value=${f.triageModel} @input=${(e) => this.#setAi({ triageModel: e.target.value })} /></label>
          <label>Skrive-model <span class="muted">(skriver rapport og plan)</span> <input type="text" .value=${f.reportModel} @input=${(e) => this.#setAi({ reportModel: e.target.value })} /></label>
        </div>
        <label>Egen adresse <span class="muted">(valgfri, fx en EU-gateway)</span>
          <input type="url" .value=${f.claudeBase} placeholder="https://…" @input=${(e) => this.#setAi({ claudeBase: e.target.value })} />
        </label>
        <div class="row">${this.#renderTest('claude')}</div>
      </div>

      <div class="site">
        <strong>Jev <span class="muted">(TypeSafe. Nøgle fra console.typesafe.ai/keys. Amerikansk tjeneste)</span></strong>
        <div>${this.#keyStatus(ai.jev, false)}</div>
        <div class="row">
          <label class="grow">Ny API-nøgle <span class="muted">(tom = behold nuværende)</span>
            <input type="password" autocomplete="new-password" name="lait-seo-api-key" spellcheck="false" placeholder="apikey_…" .value=${f.jevKey} @input=${(e) => this.#setAi({ jevKey: e.target.value, clearJev: false })} />
          </label>
          ${ai.jev.keySource === 'database'
            ? html`<label class="check"><input type="checkbox" .checked=${f.clearJev} @change=${(e) => this.#setAi({ clearJev: e.target.checked, jevKey: '' })} /> Fjern gemt nøgle</label>`
            : nothing}
        </div>
        <label>Model <input type="text" .value=${f.jevModel} @input=${(e) => this.#setAi({ jevModel: e.target.value })} /></label>
        <div class="row">${this.#renderTest('jev')}</div>
      </div>

      <div class="site">
        <strong>Månedligt loft for AI-forbrug</strong>
        <label>Loft i USD pr. måned <span class="muted">(tomt eller 0 = intet loft. Brugt i denne måned: ${usd(ai.spentThisMonthUsd)})</span>
          <input type="number" min="0" step="0.01" .value=${f.budget} @input=${(e) => this.#setAi({ budget: e.target.value })} />
        </label>
        <p class="muted">Med et loft starter AI ikke, hvis det kan sende måneden over loftet. Der regnes med det dyreste udfald, så I aldrig ender over.</p>
        <details open>
          <summary>Priser (USD pr. million tokens)</summary>
          <div class="row">${price('reportInput', 'Rapport input')}${price('reportOutput', 'Rapport output')}${price('triageInput', 'Triage input')}${price('triageOutput', 'Triage output')}${price('jevInput', 'Jev input')}</div>
          <p class="muted">${ai.pricesFromList
            ? `Felter, du ikke selv har sat, bruger listepriser pr. ${ai.listPricesAsOf}. Priser ændrer sig, så tjek dem og ret dem her.`
            : 'Priserne er sat af dig. Tjek dem, hvis udbyderne ændrer priser.'} Jev-output er gratis.</p>
        </details>
      </div>
      ${ai.warnings.length ? html`<ul class="error">${ai.warnings.map((w) => html`<li>${w}</li>`)}</ul>` : nothing}

      ${this._aiErrors.length ? html`<ul class="error">${this._aiErrors.map((e) => html`<li>${e}</li>`)}</ul>` : nothing}
      ${this._aiStatus ? html`<p class="ok">${this._aiStatus}</p>` : nothing}
      <uui-button look="primary" color="positive" label="Gem AI-indstillinger" ?disabled=${this._busy} @click=${this.#saveAi}>Gem AI-indstillinger</uui-button>
    `;
  }

  static styles = css`
    :host { display: block; }
    .muted { color: var(--uui-color-text-alt); font-size: 13px; }
    .help { color: var(--uui-color-text-alt); font-size: 13px; max-width: 75ch; margin: 4px 0 10px; }
    ul.help { padding-left: 20px; }
    h3 { margin: 20px 0 4px; }
    .site { border: 1px solid var(--uui-color-border); border-radius: 6px; padding: 12px; margin-bottom: 12px; display: grid; gap: 10px; }
    .row { display: flex; gap: 12px; align-items: flex-end; flex-wrap: wrap; }
    .grow { flex: 1; min-width: 240px; }
    label { display: flex; flex-direction: column; gap: 4px; font-size: 13px; }
    label.check { flex-direction: row; align-items: center; gap: 6px; }
    input[type='url'], input[type='number'], input[type='password'], input[type='text'], textarea, select { font: inherit; padding: 6px 8px; box-sizing: border-box; }
    input[type='url'], input[type='password'], textarea { width: 100%; }
    .group { margin: 8px 0; display: grid; gap: 2px; }
    .add { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; flex-wrap: wrap; }
    .error { color: var(--uui-color-danger); }
    .ok { color: var(--uui-color-positive); }
    summary { cursor: pointer; }
  `;
}

customElements.define('lait-seo-settings', LaitSeoSettings);
