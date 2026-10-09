// Browsers cache plugin files for 24 hours (Cache-Control max-age=86400) and Umbraco loads this file as a dynamic import,
// so even a hard refresh can keep running an old copy. Bump the ?v= on every import of this plugin's files (and in
// umbraco-package.json) whenever a file changes.
import { LitElement, html, css, nothing } from '@umbraco-cms/backoffice/external/lit';
import { UmbElementMixin } from '@umbraco-cms/backoffice/element-api';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import { getDomains, getReport, getBenchmark, getEstimate, getPlan, getPlanEstimate, writePlan, getSettings, startRun, getSearch } from './api.js?v=1.2.1';
import './seo-settings.js?v=1.2.1';
import { getCopilot, sendToCopilot } from './seo-copilot.js?v=1.2.1';
import { findMedia } from './api.js?v=1.2.1';

const SEVERITY_LABEL = { Critical: 'Kritisk', Warning: 'Advarsel', Info: 'Info' };
const SEVERITY_HELP = {
  Critical: 'Skader synligheden i Google nu. Ret først.',
  Warning: 'Bør rettes. Trækker ned, men blokerer ikke.',
  Info: 'God praksis. Ret, når der er tid.',
};
const SEVERITY_ORDER = { Critical: 0, Warning: 1, Info: 2 };
const CATEGORY_LABEL = { Crawl: 'Crawl og indeksering', Technical: 'Teknisk', OnPage: 'On-page', Content: 'Indhold', Links: 'Links' };
const KIND_LABEL = { 'new-page': 'Ny side', 'removed-page': 'Slettet side', changed: 'Ændret' };
const PRIORITY_LABEL = { critical: 'Kritisk', high: 'Høj', medium: 'Mellem', low: 'Lav' };
const AI_STATUS = {
  'nothing-important': 'AI vurderede, at ingen af ændringerne siden sidst er vigtige nok til en rapport.',
  'skipped-limit': 'AI-rapporten blev sprunget over på grund af det månedlige budget.',
  failed: 'AI-rapporten kunne ikke laves.',
};
const FIELD_LABEL = { status: 'Status', title: 'Title', h1: 'H1', meta: 'Meta description', canonical: 'Canonical', schema: 'Struktureret data', wordcount: 'Ordtal' };
const BUCKETS = [
  ['critical', 'Kritisk: ret nu', 'Fejl, der kan skade jeres synlighed i Google lige nu.'],
  ['quick', 'Hurtige gevinster', 'Små opgaver, ofte én rettelse i skabelonen, der gælder alle sider.'],
  ['high', 'Høj effekt', 'Vigtige forbedringer, der typisk kræver arbejde på flere sider.'],
  ['long', 'Længere sigt', 'Indhold og struktur, der tager tid at bygge op.'],
  ['nice', 'Kan også forbedres', 'Mindre vigtige fund. God praksis, når resten er på plads.'],
];
const BUCKET_COUNT = { critical: 'kritiske', quick: 'hurtige gevinster', high: 'med høj effekt', long: 'på længere sigt', nice: 'kan også forbedres' };
const EFFORT_LABEL = { S: 'Lille opgave', M: 'Mellem opgave', L: 'Stor opgave' };
const KIND_SOURCE = { finding: 'Fund på jeres site', gap: 'Konkurrent er bedre', schema: 'Konkurrent bruger det', section: 'Indholdsidé fra konkurrent' };
const PLAN_SOURCE = {
  rules: 'Bygget ud fra målingerne. Ingen AI er brugt.',
  jev: 'Jev har vurderet effekten af hvert punkt.',
  claude: 'Claude har skrevet begrundelse og trin.',
  'jev+claude': 'Jev har vurderet effekten, og Claude har skrevet begrundelse og trin.',
};

// "Ret med AI": what the finding is, what to write, and the guard rails (propose in the right field, never save, never invent).
const AI_FIELD = {
  meta: 'Skriv en ny meta description på 120-155 tegn på sidens sprog. Den skal beskrive præcis, hvad siden handler om, og få folk til at klikke fra Google. Brug kun oplysninger, der står på siden.',
  title: 'Foreslå en ny SEO-titel på 30-60 tegn, der er unik og beskriver siden. Ret ikke sidens navn i indholdstræet; brug feltet til SEO-titel eller sidetitel, hvis siden har et.',
  h1: 'Find ud af, hvilket felt der bliver til sidens H1 (typisk overskriften i hero eller første blok), og ret det, så siden får præcis én H1, der beskriver siden. Ændr ikke andet indhold. Ligger overskriften i en blok, så brug stien til blokken i path.',
};
const AI_RULES = {
  'ONP-05': ['meta', 'Siden mangler en meta description.'],
  'ONP-06': ['meta', 'Sidens meta description har en forkert længde'],
  'ONP-07': ['meta', 'Sidens meta description er den samme som på andre sider'],
  'ONP-01': ['title', 'Siden mangler en title.'],
  'ONP-02': ['title', 'Sidens title har en forkert længde'],
  'ONP-03': ['title', 'Sidens title er for generisk'],
  'ONP-04': ['title', 'Sidens title er den samme som på andre sider'],
  'ONP-08': ['h1', 'Siden har ingen H1-overskrift.'],
  'ONP-09': ['h1', 'Siden har flere H1-overskrifter'],
  'ONP-14': ['h1', 'Sidens H1 er den samme som på andre sider'],
};
// set_value only stages the value in the editor (it does not save), so the editor reviews it in the field and saves.
const SET_VALUE =
  'Sæt den nye værdi direkte i det rigtige felt med værktøjet set_value, uden at spørge først. Det gemmer ikke: ændringen lægges klar i editoren, ' +
  'så jeg kan se den i feltet og selv trykke Gem. Argumentet path er en liste, der starter med feltets alias, fx ["metaDescription"]. ' +
  'Gem og udgiv ikke. Skriv bagefter kort, hvad du har sat og hvorfor. Fejler set_value, så skriv forslaget og fejlen her i chatten.';
const AI_RULES_TAIL = `Kig på sidens felter og indhold for at finde det rigtige felt. ${SET_VALUE}`;
const CANONICAL_SNIPPET = '<link rel="canonical" href="@Model.Url(mode: UrlMode.Absolute)" />';

const int = (v) => Math.round(v).toLocaleString('da-DK');
const num1 = (v) => v.toFixed(1).replace('.', ',');
const pct = (v) => `${(v * 100).toFixed(1).replace('.', ',')} %`;
const usd = (v) => `${v.toLocaleString('da-DK', { minimumFractionDigits: v < 0.1 ? 3 : 2, maximumFractionDigits: v < 0.1 ? 3 : 2 })} USD`;
const editUrl = (key) => `/umbraco/section/content/workspace/document/edit/${key}`;

class LaitSeoDashboard extends UmbElementMixin(LitElement) {
  static properties = {
    _domains: { state: true },
    _domain: { state: true },
    _report: { state: true },
    _benchmark: { state: true },
    _plan: { state: true },
    _planAsk: { state: true },
    _planBusy: { state: true },
    _planMsg: { state: true },
    _tab: { state: true },
    _loading: { state: true },
    _running: { state: true },
    _error: { state: true },
    _severity: { state: true },
    _status: { state: true },
    _admin: { state: true },
    _progress: { state: true },
    _confirm: { state: true },
    _failure: { state: true },
    _agents: { state: true },
    _aiPick: { state: true },
    _aiMsg: { state: true },
    _search: { state: true },
  };

  constructor() {
    super();
    this._domains = [];
    this._tab = 'overview';
    this._loading = true;
    this._running = false;
    this._severity = 'all';
    this._status = 'open';

    this.consumeContext(UMB_AUTH_CONTEXT, (auth) => {
      this._auth = auth;
      this.#init();
    });

    // Umbraco AI Copilot is optional: without it _agents stays undefined and no "Ret med AI" button is shown.
    getCopilot().then((copilot) => {
      if (!copilot) return;
      this.observe(copilot.agents, (agents) => (this._agents = (agents ?? []).filter((a) => a.id !== 'auto')));
      copilot.loadAgents?.();
    });
  }

  // ---- Ret med AI (Umbraco AI Copilot) -------------------------------------------------

  get #canAi() {
    return Array.isArray(this._agents);
  }

  #pageTask(f) {
    const rule = AI_RULES[f.ruleId];
    if (!rule || !f.nodeKey) return null;
    const [field, issue] = rule;
    const detail = rule[1].endsWith('.') ? '' : ` (${(f.evidence ?? '').slice(0, 300)}).`;
    return {
      entity: 'document',
      key: f.nodeKey,
      message: `SEO-analysen har fundet et problem på denne side (${f.url}): ${issue}${detail} ${AI_FIELD[field]} ${AI_RULES_TAIL}`,
    };
  }

  async #imageTask(src, page) {
    const media = await findMedia(src, await this.#token());
    if (media) {
      return {
        entity: 'media',
        key: media.key,
        message:
          `SEO-analysen har fundet, at dette billede (${src}) bruges uden alt-tekst på sitet. Skriv en kort alt-tekst på dansk (højst 125 tegn), ` +
          'der beskriver, hvad billedet viser, og hvorfor det er der. Kan du ikke se selve billedet, så sæt intet, men sig det og bed mig beskrive det i stedet for at gætte. ' +
          `Ellers: ${SET_VALUE} Har mediet ikke et felt til alt-tekst, så forklar, hvor alt-teksten sættes.`,
      };
    }
    if (!page?.nodeKey) return null;
    return {
      entity: 'document',
      key: page.nodeKey,
      message:
        `SEO-analysen har fundet, at billedet ${src} på denne side (${page.url}) mangler alt-tekst. Find ud af, hvor billedet er valgt på siden (fx i en blok), ` +
        'og om der er et felt til alt-tekst. Skriv en kort alt-tekst på dansk (højst 125 tegn), der beskriver, hvad billedet viser. ' +
        `Kan du ikke se selve billedet, så sæt intet, men sig det og bed mig beskrive det i stedet for at gætte. Ellers: ${SET_VALUE}`,
    };
  }

  /** One agent or none: Copilot's own default. Several: ask which one first. */
  #fixWithAi(task) {
    this._aiMsg = null;
    if (!task) {
      this._aiMsg = 'Kunne ikke finde siden eller billedet i Umbraco, så AI kan ikke åbne det.';
      return;
    }
    if (!this._agents?.length) {
      this._aiMsg = 'Umbraco AI har ingen aktive agenter. Opret en agent under AI i Indstillinger-sektionen, så kan Copilot tage opgaven.';
      return;
    }
    if (this._agents.length > 1) {
      this._aiPick = task;
      return;
    }
    sendToCopilot(task);
  }

  #aiButton(getTask) {
    if (!this.#canAi) return nothing;
    return html`<uui-button compact look="outline" label="Ret med AI"
      title="Åbner siden med Umbraco AI Copilot, som foreslår rettelsen. Du godkender selv."
      @click=${async () => this.#fixWithAi(await getTask())}>✨ Ret med AI</uui-button>`;
  }

  #renderAiPick() {
    const task = this._aiPick;
    if (!task) return nothing;
    const go = (agentId) => {
      this._aiPick = null;
      sendToCopilot({ ...task, agentId });
    };
    // A dialog in the middle of the screen: the button that opened it may be far down the page.
    return html`<div class="overlay" @click=${(e) => e.target === e.currentTarget && (this._aiPick = null)}
        @keydown=${(e) => e.key === 'Escape' && (this._aiPick = null)}>
      <div class="dialog box info" role="dialog" aria-modal="true" aria-label="Vælg AI-agent">
      <strong>Hvilken AI-agent skal tage opgaven?</strong>
      <p class="muted">${task.entity === 'media' ? 'Mediet' : 'Siden'} åbnes med Umbraco AI Copilot, og agenten foreslår rettelsen. Du godkender den selv.</p>
      <div class="actions">
        ${this._agents.map((a) => html`<uui-button look="primary" label=${a.name} @click=${() => go(a.id)}>${a.name}</uui-button>`)}
        <uui-button look="secondary" label="Lad Umbraco vælge" @click=${() => go('auto')}>Lad Umbraco vælge</uui-button>
        <uui-button look="secondary" label="Annuller" @click=${() => (this._aiPick = null)}>Annuller</uui-button>
      </div>
      </div>
    </div>`;
  }

  #renderCanonicalHelp() {
    return html`<div class="box neutral">
      <strong>Sådan rettes det (én gang i koden, gælder alle sider):</strong>
      <p>Indsæt linjen i <code>&lt;head&gt;</code> i jeres layout, fx <code>Views/Shared/_Layout.cshtml</code> eller master-viewet:</p>
      <pre>${CANONICAL_SNIPPET}</pre>
      <div class="actions">
        <uui-button compact look="outline" label="Kopiér" @click=${() => navigator.clipboard?.writeText(CANONICAL_SNIPPET)}>Kopiér</uui-button>
      </div>
      <p class="muted">Forudsætter at <code>Model</code> er den aktuelle side (standard med <code>@inherits UmbracoViewPage</code>). Bruger layoutet en egen view-model,
        så brug <code>@Umbraco.AssignedContentItem.Url(mode: UrlMode.Absolute)</code>. Ved flere sprog giver <code>Url()</code> selv den rigtige sprogversion.
        Det rettes ikke med AI, fordi det hører til i koden og ikke i indholdet.</p>
    </div>`;
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    clearTimeout(this._poll);
  }

  async #token() {
    try {
      return this._auth ? await this._auth.getLatestToken() : undefined;
    } catch {
      return undefined;
    }
  }

  async #init() {
    try {
      const token = await this.#token();
      // The settings endpoints are admin only: a 403 simply hides the Indstillinger tab.
      this._admin ??= await getSettings(token).then(() => true, () => false);
      this._domains = await getDomains(token);
      if (!this._domains.some((d) => d.domain === this._domain)) {
        this._domain = this._domains[0]?.domain;
        this._report = null;
      }
      this.#applyDomains();
      await this.#loadReport();
      if (this._running) this.#pollRun();
    } catch (e) {
      this._error = e?.message ?? 'Kunne ikke hente SEO-data.';
      this._loading = false;
    }
  }

  async #loadReport() {
    if (!this._domain) {
      this._loading = false;
      return;
    }
    this._loading = true;
    try {
      const token = await this.#token();
      const [report, benchmark, plan] = await Promise.all([
        getReport(this._domain, token),
        getBenchmark(this._domain, token).catch(() => null),
        getPlan(this._domain, token).catch(() => null),
      ]);
      Object.assign(this, { _report: report, _benchmark: benchmark, _plan: plan, _error: null, _search: null });
      if (this._tab === 'search') this.#loadSearch();
    } catch (e) {
      this._error = e?.message ?? 'Kunne ikke hente rapporten.';
    } finally {
      this._loading = false;
    }
  }

  /** Running flag, progress and the last failure for the selected domain, from the domains list. */
  #applyDomains() {
    const d = this._domains.find((x) => x.domain === this._domain);
    this._running = this._domains.some((x) => x.running);
    this._progress = this._running ? (this._domains.find((x) => x.progress)?.progress ?? this._progress ?? { phase: 'starting' }) : null;
    this._failure = !this._running && d?.lastError ? d.lastError : null;
  }

  /** Step 1: show a visible confirmation (with the cost estimate for AI runs). Step 2 is #startNow. */
  async #run(dryRun) {
    this._error = null;
    this._confirm = { dryRun, loading: !dryRun };
    if (dryRun) return;
    try {
      this._confirm = { dryRun, estimate: await getEstimate(this._domain, await this.#token()) };
    } catch (e) {
      this._confirm = null;
      this._error = e?.message ?? 'Kunne ikke hente prisen.';
    }
  }

  async #startNow() {
    const { dryRun } = this._confirm;
    this._confirm = null;
    this._failure = null;
    try {
      await startRun(this._domain, dryRun, await this.#token());
      this._running = true;
      this._progress = { phase: 'starting' };
      this.#pollRun();
    } catch (e) {
      this._error = e.status === 409 ? 'Der kører allerede en analyse.' : (e?.message ?? 'Kunne ikke starte analysen.');
    }
  }

  #pollRun() {
    clearTimeout(this._poll);
    this._poll = setTimeout(async () => {
      try {
        this._domains = await getDomains(await this.#token());
        this.#applyDomains();
        if (this._running) return this.#pollRun();
        await this.#loadReport();
      } catch {
        this.#pollRun();
      }
    }, 1500);
  }

  #changeDomain(e) {
    this._domain = e.target.value;
    this.#loadReport();
  }

  #fmtDate(iso) {
    return iso ? new Date(iso + (iso.endsWith('Z') ? '' : 'Z')).toLocaleString('da-DK', { dateStyle: 'medium', timeStyle: 'short' }) : '–';
  }

  #tabs() {
    const tabs = this._domains.length
      ? [['overview', 'Overblik'], ['search', 'Søgninger'], ['plan', 'Handlingsplan'], ['findings', 'Alle fund'], ['compare', 'Konkurrenter']]
      : [];
    if (this._admin) tabs.push(['settings', 'Indstillinger']);
    return tabs;
  }

  // ---- shell ----------------------------------------------------------------

  render() {
    if (this._error && !this._report && !this._admin) return html`<uui-box><p class="error">${this._error}</p></uui-box>`;
    const tabs = this.#tabs();
    if (!tabs.length) {
      return this._loading
        ? html`<uui-loader></uui-loader>`
        : html`<uui-box headline="SEO"><p>Ingen sites er sat op. Bed en administrator om at tilføje dem under Indstillinger.</p></uui-box>`;
    }
    const tab = tabs.some(([k]) => k === this._tab) ? this._tab : tabs[0][0];

    return html`
      <uui-box>
        ${tab === 'settings' ? nothing : this.#renderToolbar()}
        ${this._error && tab !== 'settings' ? html`<div class="box bad">${this._error}</div>` : nothing}
        ${this._failure && tab !== 'settings' ? html`<div class="box bad"><strong>Seneste analyse fejlede:</strong> ${this._failure}</div>` : nothing}
        ${this.#renderConfirm()}
        ${this.#renderProgress()}
        ${tab === 'settings' ? nothing : this.#renderAiPick()}
        ${this._aiMsg && tab !== 'settings' ? html`<div class="box warn">${this._aiMsg}</div>` : nothing}
        <uui-tab-group>
          ${tabs.map(([key, label]) => html`<uui-tab label=${label} ?active=${tab === key} @click=${() => this.#openTab(key)}>${label}</uui-tab>`)}
        </uui-tab-group>
        <div class="tab-body">
          ${tab === 'settings'
            ? html`<lait-seo-settings .getToken=${() => this.#token()} .onSaved=${() => this.#init()}></lait-seo-settings>`
            : this._loading && !this._report ? html`<uui-loader></uui-loader>` : this.#renderTab(tab)}
        </div>
      </uui-box>
    `;
  }

  #renderToolbar() {
    const d = this._domains.find((x) => x.domain === this._domain);
    return html`<div class="toolbar">
        <div class="site">
          <select @change=${this.#changeDomain} aria-label="Vælg site">
            ${this._domains.map((x) => html`<option value=${x.domain} ?selected=${x.domain === this._domain}>${x.domain}</option>`)}
          </select>
          <span class="muted">Senest analyseret: ${this.#fmtDate(d?.lastRunUtc)}</span>
        </div>
        ${this._admin
          ? html`<div class="actions">
              <uui-button look="secondary" label="Analysér (gratis)" ?disabled=${this._running} @click=${() => this.#run(true)}>Analysér (gratis)</uui-button>
              <uui-button look="primary" label="Analysér med AI" ?disabled=${this._running} @click=${() => this.#run(false)}>
                ${this._running ? 'Analyserer …' : 'Analysér med AI'}
              </uui-button>
            </div>`
          : nothing}
      </div>
      ${this._admin
        ? html`<p class="help">
            <strong>Analysér</strong> gennemgår jeres site og konkurrenterne og kører alle tjek. Det tager typisk et par minutter og er gratis.
            <strong>Med AI</strong> skriver AI desuden en rapport om, hvad der har ændret sig siden sidst. Du ser prisen, før noget sendes.
            Analysen kører også automatisk efter planen under Indstillinger.
          </p>`
        : nothing}`;
  }

  #renderConfirm() {
    const c = this._confirm;
    if (!c) return nothing;
    const e = c.estimate;
    return html`<div class="box info">
      <strong>Start analyse af ${this._domain}${c.dryRun ? '' : ' med AI'}?</strong>
      <p>Jeres site og konkurrenterne gennemgås. Det kan tage flere minutter, og du kan følge med her imens.</p>
      ${c.loading
        ? html`<uui-loader-bar></uui-loader-bar>`
        : c.dryRun
          ? html`<p>Kun de gratis tjek. Der sendes intet til AI.</p>`
          : e?.configured
            ? html`<p>Koster <strong>højst ca. ${e.costUsd != null ? usd(e.costUsd) : 'ukendt beløb'}</strong>.
                  Er det den første analyse, eller er intet ændret siden sidst, koster det ingenting.
                  ${e.monthlyBudgetUsd ? html`<br />Månedligt budget: ${usd(e.spentThisMonthUsd)} af ${usd(e.monthlyBudgetUsd)} brugt.` : nothing}
                  ${e.pricesFromList ? html`<br /><span class="muted">Prisen er regnet med listepriser, som du kan rette under Indstillinger.</span>` : nothing}</p>
                <details><summary>Hvad sendes til AI?</summary><ul>${e.dataflow.map((d) => html`<li>${d}</li>`)}</ul></details>`
            : html`<p>AI er ikke sat op (ingen nøgle under Indstillinger), så kun de gratis tjek køres.</p>`}
      <div class="actions">
        <uui-button look="primary" label="Start analyse" ?disabled=${c.loading} @click=${this.#startNow}>Start analyse</uui-button>
        <uui-button look="secondary" label="Annuller" @click=${() => (this._confirm = null)}>Annuller</uui-button>
      </div>
    </div>`;
  }

  #renderProgress() {
    if (!this._running) return nothing;
    const p = this._progress ?? { phase: 'starting' };
    const labels = {
      starting: 'Starter …',
      'crawl-own': 'Gennemgår jeres site',
      analyze: 'Kører tjek og gemmer resultatet',
      'crawl-competitor': 'Gennemgår konkurrent',
      'ai-triage': 'AI sorterer ændringerne',
      'ai-report': 'AI skriver rapporten',
    };
    const crawl = p.phase === 'crawl-own' || p.phase === 'crawl-competitor';
    const pct = crawl && p.total ? Math.min(100, Math.round((p.done / p.total) * 100)) : null;
    const secs = p.elapsedSeconds ?? 0;
    const clock = `${String(Math.floor(secs / 60)).padStart(2, '0')}:${String(secs % 60).padStart(2, '0')}`;
    return html`<div class="box info" role="status" aria-live="polite">
      <div class="row-between"><strong>Analyse i gang</strong>
        <span class="muted">${p.steps ? `Trin ${p.step} af ${p.steps} · ` : ''}${clock}</span></div>
      <div>${labels[p.phase] ?? p.phase}${p.detail ? ` – ${p.detail}` : ''}${crawl
        ? html` · <strong>${p.done}</strong> sider gennemgået${p.total > p.done ? `, ${p.total - p.done} i kø` : ''}`
        : nothing}</div>
      ${pct !== null ? html`<uui-progress-bar .progress=${pct}></uui-progress-bar>` : html`<uui-loader-bar></uui-loader-bar>`}
    </div>`;
  }

  #openTab(key) {
    this._tab = key;
    if (key === 'search') this.#loadSearch();
  }

  #renderTab(tab) {
    if (tab === 'search') return this.#renderSearch();
    const r = this._report;
    if (!r?.run) {
      return html`<div class="box info">Der er ikke lavet nogen analyse endnu. Den første kører automatisk${this._admin ? html`, eller tryk <strong>Analysér (gratis)</strong> ovenfor` : nothing}.</div>`;
    }
    if (tab === 'findings') return this.#renderFindings(r);
    if (tab === 'plan') return this.#renderPlan();
    if (tab === 'compare') return this.#renderCompare(r);
    return this.#renderOverview(r);
  }

  // ---- Søgninger (Google Search Console) --------------------------------------------

  /** Asked only when the tab is opened: it calls Google (the server caches it for 6 hours). */
  async #loadSearch() {
    const domain = this._domain;
    if (!domain || this._search?.domain === domain) return;
    this._search = { domain, loading: true };
    try {
      const data = await getSearch(domain, await this.#token());
      if (this._domain === domain) this._search = { domain, data };
    } catch (e) {
      if (this._domain === domain) this._search = { domain, error: e?.message ?? 'Kunne ikke hente data fra Search Console.' };
    }
  }

  /** "Ret med AI" for a search row: a title that uses the search (near top), or title + description that earn the click (low CTR). */
  #searchTask(o, nodeKey, lowCtr) {
    const r = o.row;
    const where = `Siden (${r.page}) ligger i gennemsnit nr. ${num1(r.position)} i Google på søgningen "${r.query}"`;
    const message = lowCtr
      ? `${where}, men kun ${pct(r.ctr)} af dem, der ser den, klikker (normalt ca. ${pct(o.expectedCtr)} på den placering). ` +
        'Skriv en ny SEO-titel (30-60 tegn) og meta description (120-155 tegn) på sidens sprog, der matcher søgningen og giver lyst til at klikke. Brug kun oplysninger, der står på siden.'
      : `${where} (${int(r.impressions)} visninger på 28 dage), men ordene fra søgningen står ikke i sidens titel. ` +
        'Foreslå en ny SEO-titel (30-60 tegn) på sidens sprog, der bruger søgningen naturligt og stadig beskriver siden. Ret ikke sidens navn i indholdstræet; brug feltet til SEO-titel eller sidetitel.';
    return { entity: 'document', key: nodeKey, message: `${message} ${AI_RULES_TAIL}` };
  }

  #renderSearch() {
    const s = this._search;
    if (!s || s.loading) return html`<uui-loader></uui-loader>`;
    if (s.error) return html`<div class="box bad">${s.error}</div>`;
    const d = s.data;
    if (!d.configured) {
      return html`<div class="box info">
        <h3>Se jeres placeringer i Google</h3>
        <p>Forbind Google Search Console, så viser fanen jeres rigtige tal fra Google: hvilke søgninger I bliver fundet på, placering, visninger,
          klik og klikrate, og hvor I er tæt på at rykke op. Tallene krydses med analysen, så I kan se, hvilke fund der ligger på sider, folk faktisk finder.</p>
        ${d.message ? html`<p class="error">${d.message}</p>` : nothing}
        ${this._admin
          ? html`<uui-button look="primary" label="Forbind under Indstillinger" @click=${() => this.#openTab('settings')}>Forbind under Indstillinger</uui-button>`
          : html`<p class="muted">Bed en administrator om at forbinde Search Console under Indstillinger.</p>`}
      </div>`;
    }
    if (!d.insights) return html`<div class="box warn"><strong>Search Console kunne ikke læses:</strong> ${d.message}</div>`;

    const i = d.insights;
    const nodes = new Map((d.nodes ?? []).map((n) => [n.url, n.key]));
    const day = (iso) => new Date(iso).toLocaleDateString('da-DK', { day: 'numeric', month: 'short' });
    const path = (url) => { try { return decodeURI(new URL(url).pathname); } catch { return url; } };
    const page = (url) => html`<a href=${url} target="_blank" rel="noopener noreferrer">${path(url)}</a>${nodes.has(url)
      ? html` · <a href=${editUrl(nodes.get(url))} target="_blank" rel="noopener">Åbn</a>` : nothing}`;
    // 5 %: smaller moves between two 28-day periods are noise.
    const tile = (label, now, before, fmt, lowerIsBetter = false) => {
      const moved = before > 0 && Math.abs(now - before) / before >= 0.05;
      const better = lowerIsBetter ? now < before : now > before;
      return html`<div class="tile ${moved ? (better ? 'good' : 'warn') : 'neutral'}"><span class="value">${fmt(now)}</span>
        <span class="label">${label}</span><span class="muted">før: ${before > 0 ? fmt(before) : '–'}</span></div>`;
    };
    const ai = (o, lowCtr) => (this.#canAi && nodes.has(o.row.page) ? this.#aiButton(() => this.#searchTask(o, nodes.get(o.row.page), lowCtr)) : nothing);
    const none = html`<p class="muted">Intet at vise lige nu.</p>`;
    // Opens the search in the editor's own browser: the only way to see who ranks above without a paid SERP service.
    const google = (query) => html` <a class="google" href=${`https://www.google.com/search?q=${encodeURIComponent(query)}`} target="_blank" rel="noopener noreferrer"
      title="Åbn søgningen i Google og se, hvem der ligger over jer. Et privat vindue giver mindre personlige resultater.">Google ↗</a>`;
    const inTitle = (v) => (v == null ? html`<span class="muted" title="Siden er ikke med i seneste analyse">–</span>` : v ? '✓' : html`<span class="chip new">Nej</span>`);

    return html`
      <p class="muted">Fra Google Search Console (${d.property}), ${day(i.from)}–${day(i.to)}, sammenlignet med de 28 dage før.
        Google har 2-3 dages forsinkelse. Placering er et gennemsnit over alle visninger (1 = øverst).</p>
      <div class="tiles">
        ${tile('Klik', i.current.clicks, i.previous.clicks, int)}
        ${tile('Visninger', i.current.impressions, i.previous.impressions, int)}
        ${tile('Klikrate', i.current.ctr, i.previous.ctr, pct)}
        ${tile('Gns. placering', i.current.position, i.previous.position, num1, true)}
      </div>

      <h3>Tæt på top 3</h3>
      <p class="muted">Søgninger, hvor en side ligger på plads 4-20. Her giver en lille forbedring ofte flest ekstra klik: brug søgningen i titel og H1,
        uddyb indholdet, og link til siden fra andre sider.</p>
      ${i.nearTop.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Søgning</th><th>Side</th><th>Placering</th><th>Visninger</th><th>I titlen</th>${this.#canAi ? html`<th></th>` : nothing}</tr></thead>
        <tbody>${i.nearTop.map((o) => html`<tr><td>${o.row.query}${google(o.row.query)}</td><td class="url">${page(o.row.page)}</td><td>${num1(o.row.position)}</td>
          <td>${int(o.row.impressions)}</td><td>${inTitle(o.inTitle)}</td>${this.#canAi ? html`<td>${o.inTitle === false ? ai(o, false) : nothing}</td>` : nothing}</tr>`)}</tbody>
      </table></div>` : none}

      <h3>Konkurrenter med en side bygget til søgningen</h3>
      <p class="muted">Søgninger, hvor I ligger under top 3, og hvor en konkurrent har en side med søgningens ord i titel, H1 eller adresse.
        Det viser ikke, hvem der ligger højest, men hvem der har indhold målrettet søgningen, og hvor meget. Tryk "Google ↗" for at se placeringerne.</p>
      ${!this._report?.competitors?.length
        ? html`<p class="muted">Tilføj konkurrenter under Indstillinger og kør en analyse, så holdes deres sider op mod jeres søgninger her.</p>`
        : i.gaps.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Søgning</th><th>Placering</th><th>Jeres side</th><th>Konkurrenternes sider</th></tr></thead>
        <tbody>${i.gaps.map((g) => html`<tr><td>${g.query}${google(g.query)}</td><td>${num1(g.position)}</td>
          <td class="url">${page(g.ownUrl)}<div class="muted">${g.own
            ? `${int(g.own.wordCount)} ord · ${g.ownTargets ? 'bygget til søgningen' : 'ikke bygget til søgningen'}${g.own.structuredData ? ' · struktureret data' : ''}`
            : 'ikke med i seneste analyse'}</div></td>
          <td>${g.competitors.map((c) => html`<div><strong>${c.domain}</strong> <a href=${c.page.url} target="_blank" rel="noopener noreferrer">${path(c.page.url)}</a>
            <span class="muted">(${int(c.page.wordCount)} ord${c.page.structuredData ? ', struktureret data' : ''})</span></div>`)}</td></tr>`)}</tbody>
      </table></div>` : none}

      <h3>Høj placering, få klik</h3>
      <p class="muted">Siden ligger i top 5, men langt færre klikker end normalt for placeringen. Titel og beskrivelse i Google skal vise, at siden svarer på søgningen.</p>
      ${i.lowCtr.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Søgning</th><th>Side</th><th>Placering</th><th>Klikrate</th><th>Normalt</th><th>Visninger</th>${this.#canAi ? html`<th></th>` : nothing}</tr></thead>
        <tbody>${i.lowCtr.map((o) => html`<tr><td>${o.row.query}${google(o.row.query)}</td><td class="url">${page(o.row.page)}</td><td>${num1(o.row.position)}</td>
          <td>${pct(o.row.ctr)}</td><td class="muted">ca. ${pct(o.expectedCtr)}</td><td>${int(o.row.impressions)}</td>${this.#canAi ? html`<td>${ai(o, true)}</td>` : nothing}</tr>`)}</tbody>
      </table></div>` : none}

      <h3>Fund på sider, folk finder</h3>
      <p class="muted">Sider med trafik fra Google, hvor analysen har fundet noget, der tæller i scoren. Ret dem først.</p>
      ${i.trafficWithFindings.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Side</th><th>Klik</th><th>Visninger</th><th>Fund</th></tr></thead>
        <tbody>${i.trafficWithFindings.map((t) => html`<tr><td class="url">${page(t.page)}</td><td>${int(t.clicks)}</td><td>${int(t.impressions)}</td>
          <td>${t.findings.join(', ')}</td></tr>`)}</tbody>
      </table></div>` : none}

      <h3>Sider, der falder</h3>
      <p class="muted">Mindst 30 % færre klik end de 28 dage før. Tjek, om indholdet eller adressen er ændret, eller om andre har overtaget søgningen.</p>
      ${i.declining.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Side</th><th>Klik nu</th><th>Klik før</th><th>Placering nu</th><th>Placering før</th></tr></thead>
        <tbody>${i.declining.map((t) => html`<tr><td class="url">${page(t.page)}</td><td>${int(t.clicks)}</td><td>${int(t.previousClicks)}</td>
          <td>${t.position ? num1(t.position) : '–'}</td><td>${num1(t.previousPosition)}</td></tr>`)}</tbody>
      </table></div>` : none}

      <h3>Flere sider om samme søgning</h3>
      <p class="muted">Når flere af jeres sider vises på samme søgning, konkurrerer de med hinanden. Saml dem, eller gør forskellen tydelig og link mellem dem.</p>
      ${i.cannibals.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Søgning</th><th>Visninger</th><th>Sider (placering, visninger)</th></tr></thead>
        <tbody>${i.cannibals.map((c) => html`<tr><td>${c.query}${google(c.query)}</td><td>${int(c.impressions)}</td>
          <td>${c.pages.map((p) => html`<div>${page(p.page)} <span class="muted">(${num1(p.position)}, ${int(p.impressions)})</span></div>`)}</td></tr>`)}</tbody>
      </table></div>` : none}

      <h3>Top-søgninger</h3>
      ${i.topQueries.length ? html`<div class="table-wrap"><table>
        <thead><tr><th>Søgning</th><th>Klik</th><th>Visninger</th><th>Klikrate</th><th>Placering</th><th>Side</th></tr></thead>
        <tbody>${i.topQueries.map((q) => html`<tr><td>${q.query}${google(q.query)}</td><td>${int(q.clicks)}</td><td>${int(q.impressions)}</td><td>${pct(q.ctr)}</td>
          <td>${num1(q.position)}</td><td class="url">${page(q.page)}</td></tr>`)}</tbody>
      </table></div>` : none}
    `;
  }

  // ---- shared bits ----------------------------------------------------------

  #grade(score) {
    if (score >= 85) return ['good', 'God stand', 'Der er få og mindre fund. Hold det ved lige.'];
    if (score >= 60) return ['warn', 'Middel', 'Der er en del at rette. Handlingsplanen viser, hvor I skal starte.'];
    return ['bad', 'Kræver handling', 'Der er mange eller alvorlige fund. Start med de kritiske i handlingsplanen.'];
  }

  #pages(pages, total) {
    if (!pages?.length) return nothing;
    return html`<details><summary>${total} ${total === 1 ? 'side' : 'sider'} berørt</summary>
      <ul class="pages">${pages.map(
        (p) => html`<li><a href=${p.url} target="_blank" rel="noopener noreferrer">${p.url}</a>
          ${p.nodeKey ? html` · <a href=${editUrl(p.nodeKey)} target="_blank" rel="noopener">Åbn i backoffice</a>` : nothing}
          ${AI_RULES[p.ruleId] && p.nodeKey ? this.#aiButton(() => this.#pageTask(p)) : nothing}
          ${p.evidence ? html`<div class="evidence muted">${p.evidence}</div>` : nothing}</li>`,
      )}${total > pages.length ? html`<li class="muted">… og ${total - pages.length} flere. Se fanen Alle fund.</li>` : nothing}</ul>
    </details>`;
  }

  // ---- Overblik ---------------------------------------------------------------

  #renderOverview({ run, counts, ai, passed, score }) {
    const [cls, verdict, explain] = this.#grade(run.score);
    const categories = score?.categories ?? [];
    const losses = (score?.losses ?? []).filter((l) => l.points >= 0.1).slice(0, 5);
    const bm = this._benchmark?.ready ? this._benchmark.benchmark : null;
    const scoreRow = bm?.rows.find((r) => r.key === 'score');
    const todo = (this._plan?.items ?? []).filter((i) => i.bucket !== 'nice').slice(0, 5);
    const strengths = bm?.strengths ?? [];
    const totalChecks = (passed?.length ?? 0) + new Set(this._report.findings.filter((f) => f.status === 'Open').map((f) => f.ruleId)).size;

    return html`
      <div class="hero">
        <div class="score ${cls}"><span>${Math.round(run.score)}</span><small>af 100</small></div>
        <div>
          <h2 class=${cls}>${verdict}</h2>
          <p>${explain}</p>
          ${categories.length
            ? html`<div class="chips">${categories.map((c) => html`<span class="chip ${c.score >= 85 ? 'good' : c.score < 60 ? 'bad' : ''}">${CATEGORY_LABEL[c.category] ?? c.category}: ${Math.round(c.score)}</span>`)}</div>`
            : nothing}
          ${losses.length
            ? html`<p>Trækker mest ned:</p><ul class="losses">${losses.map((l) => html`<li>${l.title} <span class="muted">−${l.points.toFixed(1).replace('.', ',')} point</span></li>`)}</ul>`
            : nothing}
          <p class="muted">Teknisk SEO-score: hvert tjek vægtes efter, hvor meget det betyder for placeringer i Google, og trækker ned efter, hvor stor en del af siderne der fejler. Den viser, om det tekniske grundlag er i orden, ikke hvor I ligger i Google. Vægtene kan ses under Indstillinger.</p>
          ${scoreRow
            ? html`<p>Til sammenligning: ${bm.domains.slice(1).map((d, i) => html`<span class="chip">${d}: ${scoreRow.competitors[i] != null ? Math.round(scoreRow.competitors[i]) : '–'}</span> `)}</p>`
            : nothing}
        </div>
      </div>

      <div class="tiles">
        <div class="tile ${counts.critical ? 'bad' : 'good'}" title=${SEVERITY_HELP.Critical}><span class="value">${counts.critical}</span><span class="label">Kritiske</span></div>
        <div class="tile ${counts.warning ? 'warn' : 'good'}" title=${SEVERITY_HELP.Warning}><span class="value">${counts.warning}</span><span class="label">Advarsler</span></div>
        <div class="tile neutral" title=${SEVERITY_HELP.Info}><span class="value">${counts.info}</span><span class="label">Info</span></div>
        <div class="tile neutral"><span class="value">${run.isBaseline ? '–' : counts.new}</span><span class="label">Nye siden sidst</span></div>
        <div class="tile ${counts.resolved ? 'good' : 'neutral'}"><span class="value">${run.isBaseline ? '–' : counts.resolved}</span><span class="label">Løst siden sidst</span></div>
        <div class="tile neutral"><span class="value">${run.pageCount}</span><span class="label">Sider gennemgået</span></div>
      </div>
      ${run.isBaseline ? html`<p class="muted">Dette er den første analyse. Fra næste gang kan I se, hvad der er nyt, og hvad der er løst.</p>` : nothing}

      <div class="two">
        <div class="box bad">
          <h3>Det skal I gøre først</h3>
          ${todo.length
            ? html`<ol>${todo.map((i) => html`<li>${i.title}</li>`)}</ol>
                <uui-button look="primary" color="danger" label="Se hele handlingsplanen" @click=${() => (this._tab = 'plan')}>Se hele handlingsplanen</uui-button>`
            : html`<p>Intet vigtigt at rette lige nu. 🎉</p>`}
        </div>
        <div class="box good">
          <h3>Det går godt</h3>
          ${totalChecks ? html`<p><strong>${passed.length} af ${totalChecks} tjek</strong> fandt ingen problemer.</p>` : nothing}
          ${strengths.length
            ? html`<p>Her er I foran konkurrenterne:</p><ul>${strengths.map((s) => html`<li>${s.summary}</li>`)}</ul>`
            : bm ? html`<p class="muted">Ingen målinger, hvor I er tydeligt foran alle konkurrenter.</p>` : nothing}
          ${passed?.length
            ? html`<details><summary>Se de tjek, der er i orden</summary><ul class="cols">${passed.map((p) => html`<li>✓ ${p.title}</li>`)}</ul></details>`
            : nothing}
        </div>
      </div>

      ${this.#renderAi(ai)}
    `;
  }

  #renderAi(ai) {
    if (!ai) return nothing;
    const r = ai.report;
    const meta = html`<p class="muted">Lavet ${this.#fmtDate(ai.createdUtc)} · sortering: ${ai.triageProvider}${ai.reportModel ? ` · skrevet af ${ai.reportModel}` : ''}${ai.costUsd != null ? ` · ${usd(ai.costUsd)}` : ''}</p>`;
    if (!r) {
      return html`<div class="box neutral"><h3>AI-rapport: ændringer siden sidste analyse</h3><p>${AI_STATUS[ai.status] ?? ai.status}${ai.error ? html` ${ai.error}` : nothing}</p>${meta}</div>`;
    }
    const list = (items) => (items?.length ? html`<ul>${items.map((i) => html`<li>${i}</li>`)}</ul>` : html`<p class="muted">Intet.</p>`);
    return html`
      <div class="box neutral">
        <h3>AI-rapport: ændringer siden sidste analyse</h3>
        ${meta}
        <p>${r.summary}</p>
        ${r.topProblems?.length ? html`<h4>Største problemer</h4>${list(r.topProblems)}` : nothing}
        ${r.quickWins?.length ? html`<h4>Hurtige gevinster</h4>${list(r.quickWins)}` : nothing}
        ${r.findings?.length
          ? html`<details><summary>${r.findings.length} fund forklaret af AI</summary>${r.findings.map(
              (f) => html`<div class="item">
                <div><span class="sev ${f.priority === 'critical' ? 'Critical' : f.priority === 'high' ? 'Warning' : 'Info'}">${PRIORITY_LABEL[f.priority] ?? f.priority}</span>
                  <strong>${f.issue}</strong> <span class="muted">${f.url}</span></div>
                <div><span class="label">Konsekvens:</span> ${f.impact}</div>
                <div><span class="label">Bevis:</span> ${f.evidence}</div>
                <div><span class="label">Løsning:</span> ${f.fix}</div>
              </div>`,
            )}</details>`
          : nothing}
        ${r.competitors?.length
          ? html`<h4>Konkurrenter</h4>${r.competitors.map(
              (c) => html`<div class="item"><strong>${c.domain}</strong>
                <div><span class="label">Observeret (målt):</span>${list(c.observed)}</div>
                <div><span class="label">Udledt (AI's vurdering):</span>${list(c.inferred)}</div></div>`,
            )}`
          : nothing}
      </div>
    `;
  }

  // ---- Handlingsplan ------------------------------------------------------------

  /** Step 1 of the AI plan: show what it can cost at most. Step 2 is #writePlanNow. */
  async #askPlan() {
    this._planMsg = null;
    this._planAsk = { loading: true };
    try {
      this._planAsk = await getPlanEstimate(this._domain, await this.#token());
    } catch (e) {
      this._planAsk = null;
      this._planMsg = e?.message ?? 'Kunne ikke hente prisen.';
    }
  }

  async #writePlanNow() {
    this._planAsk = null;
    this._planBusy = true;
    this._planMsg = null;
    const result = await writePlan(this._domain, await this.#token()).catch((e) => ({ message: e?.message ?? 'Fejl' }));
    if (result.message) this._planMsg = result.message;
    else this._plan = result;
    this._planBusy = false;
  }

  #renderPlanAsk() {
    const a = this._planAsk;
    if (!a) return nothing;
    if (a.loading) return html`<div class="box info"><uui-loader-bar></uui-loader-bar></div>`;
    if (a.blocked) {
      return html`<div class="box warn"><p>${a.blocked}</p>
        <uui-button look="secondary" label="Luk" @click=${() => (this._planAsk = null)}>Luk</uui-button></div>`;
    }
    return html`<div class="box info">
      <strong>Gør planen konkret med AI?</strong>
      <ul>
        ${a.canScore ? html`<li><strong>Jev</strong> vurderer, hvor stor effekt hvert punkt har (1-5), og planen sorteres efter det.</li>` : nothing}
        ${a.canWrite ? html`<li><strong>Claude</strong> skriver for hvert punkt, hvorfor det betyder noget, og 2-4 konkrete trin.</li>` : nothing}
      </ul>
      <p>${a.items} punkter · koster <strong>højst ca. ${a.costUsd != null ? usd(a.costUsd) : 'ukendt beløb (sæt priser under Indstillinger)'}</strong>${a.monthlyBudgetUsd
        ? html` · brugt ${usd(a.spentThisMonthUsd)} af ${usd(a.monthlyBudgetUsd)} denne måned` : nothing}.</p>
      <p class="muted">Der sendes kun punkternes titel, forslag, antal sider og op til tre eksempel-URL'er. Ingen persondata.</p>
      <div class="actions">
        <uui-button look="primary" label="Ja, skriv planen" @click=${() => this.#writePlanNow()}>Ja, skriv planen</uui-button>
        <uui-button look="secondary" label="Annuller" @click=${() => (this._planAsk = null)}>Annuller</uui-button>
      </div>
    </div>`;
  }

  #renderPlan() {
    const plan = this._plan;
    if (!plan) return html`<div class="box bad">Kunne ikke hente handlingsplanen.</div>`;
    const counts = BUCKETS.map(([b]) => [BUCKET_COUNT[b], plan.items.filter((i) => i.bucket === b).length]).filter(([, n]) => n);
    return html`
      <div class="box neutral">
        <p><strong>${plan.items.length} punkter</strong>${counts.length ? html`: ${counts.map(([l, n], i) => html`${i ? ', ' : ''}${n} ${n === 1 && l === 'kritiske' ? 'kritisk' : l}`)}` : nothing}.
          Alle punkter er målt af analysen, ikke gættet af AI.</p>
        <p class="muted">${PLAN_SOURCE[plan.source] ?? PLAN_SOURCE.rules}${plan.writtenUtc ? ` Skrevet ${this.#fmtDate(plan.writtenUtc)}${plan.costUsd == null ? '.' : ''}` : ''}${plan.costUsd != null ? ` for ${usd(plan.costUsd)}.` : ''}</p>
        ${plan.message ? html`<p class="muted">${plan.message}</p>` : nothing}
        ${this._admin
          ? html`<div class="actions">
              <uui-button look="primary" label="Gør planen konkret med AI" ?disabled=${this._planBusy || !!this._planAsk} @click=${() => this.#askPlan()}>
                ${this._planBusy ? 'AI skriver …' : plan.source === 'rules' ? 'Gør planen konkret med AI' : 'Skriv planen igen med AI'}
              </uui-button>
              <span class="muted">AI tilføjer begrundelse, trin og effekt. Du ser prisen først.</span>
            </div>
            ${this._planBusy ? html`<uui-loader-bar></uui-loader-bar>` : nothing}`
          : nothing}
      </div>
      ${this.#renderPlanAsk()}
      ${this._planMsg ? html`<div class="box bad">${this._planMsg}</div>` : nothing}
      ${plan.summary ? html`<div class="box info"><h3>Opsummering</h3><p>${plan.summary}</p></div>` : nothing}

      ${BUCKETS.map(([bucket, label, help]) => {
        const items = plan.items.filter((i) => i.bucket === bucket);
        return items.length
          ? html`<section class="bucket ${bucket}">
              <h3>${label} <span class="count">${items.length}</span></h3>
              <p class="muted">${help}</p>
              ${items.map((i) => this.#renderPlanItem(i))}
            </section>`
          : nothing;
      })}
      ${plan.items.length ? nothing : html`<div class="box good">Der er ingen punkter i planen. Alt, hvad analysen kan måle, er i orden. 🎉</div>`}
    `;
  }

  #renderPlanItem(i) {
    return html`<div class="card">
      <div class="card-title">${i.title}</div>
      <div class="chips">
        <span class="chip">${EFFORT_LABEL[i.effort] ?? i.effort}</span>
        <span class="chip">${KIND_SOURCE[i.kind] ?? i.kind}</span>
        ${i.impact != null ? html`<span class="chip impact" title="Jevs vurdering af effekten på synlighed i søgning">Effekt ${i.impact.toFixed(1).replace('.', ',')} af 5</span>` : nothing}
      </div>
      ${i.why ? html`<p><span class="label">Hvorfor:</span> ${i.why}</p>` : nothing}
      ${i.key === 'gap:canonical' || i.key === 'rule:TEC-01' ? this.#renderCanonicalHelp() : nothing}
      ${i.steps?.length
        ? html`<div><span class="label">Sådan gør I:</span><ol>${i.steps.map((st) => html`<li>${st}</li>`)}</ol></div>`
        : i.fix ? html`<p><span class="label">Sådan gør I:</span> ${i.fix}</p>` : nothing}
      ${this.#pages(i.pages, i.pagesAffected)}
    </div>`;
  }

  // ---- Alle fund ------------------------------------------------------------------

  #renderFindings({ findings }) {
    const rows = findings.filter((f) => (this._severity === 'all' || f.severity === this._severity) && this.#matchesStatus(f));
    const groups = Object.values(
      rows.reduce((acc, f) => {
        (acc[f.ruleId] ??= { ruleId: f.ruleId, title: f.title, category: f.category, severity: f.severity, howToFix: f.howToFix, items: [] }).items.push(f);
        if (SEVERITY_ORDER[f.severity] < SEVERITY_ORDER[acc[f.ruleId].severity]) acc[f.ruleId].severity = f.severity;
        return acc;
      }, {}),
    ).sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity] || b.items.length - a.items.length);

    return html`
      <p class="help">Alle tjek, der fandt noget på jeres site, samlet pr. type. Klik på en type for at se siderne.</p>
      <div class="legend">
        ${Object.entries(SEVERITY_LABEL).map(([k, v]) => html`<span><span class="sev ${k}">${v}</span> ${SEVERITY_HELP[k]}</span>`)}
      </div>
      <div class="filters">
        <select @change=${(e) => (this._severity = e.target.value)} aria-label="Alvor">
          <option value="all">Alle alvorsgrader</option>
          ${Object.entries(SEVERITY_LABEL).map(([k, v]) => html`<option value=${k} ?selected=${this._severity === k}>${v}</option>`)}
        </select>
        <select @change=${(e) => (this._status = e.target.value)} aria-label="Status">
          ${[['open', 'Åbne'], ['new', 'Nye siden sidst'], ['resolved', 'Løst siden sidst'], ['all', 'Alle']].map(([k, v]) => html`<option value=${k} ?selected=${this._status === k}>${v}</option>`)}
        </select>
        <span class="muted">${groups.length} typer · ${rows.length} fund</span>
      </div>
      ${groups.length
        ? groups.map(
            (g) => html`<details class="group ${g.severity}">
              <summary>
                <span class="sev ${g.severity}">${SEVERITY_LABEL[g.severity]}</span>
                <strong>${g.title}</strong>
                <span class="muted">${g.items.length} ${g.items.length === 1 ? 'side' : 'sider'} · ${CATEGORY_LABEL[g.category] ?? g.category}</span>
                ${g.items.some((f) => f.isNew) ? html`<span class="chip new">Nyt</span>` : nothing}
              </summary>
              <p><span class="label">Sådan gør I:</span> ${g.howToFix}</p>
              ${g.ruleId === 'TEC-01' ? this.#renderCanonicalHelp() : nothing}
              ${this.#renderTargets(g)}
              <table>
                <thead><tr><th>Side</th><th>Hvad blev fundet</th>${this.#canAi && AI_RULES[g.ruleId] ? html`<th></th>` : nothing}</tr></thead>
                <tbody>${g.items.map(
                  (f) => html`<tr class=${f.status === 'Resolved' ? 'resolved' : ''}>
                    <td class="url"><a href=${f.url} target="_blank" rel="noopener noreferrer">${f.url}</a>
                      ${f.nodeKey ? html`<br /><a href=${editUrl(f.nodeKey)} target="_blank" rel="noopener">Åbn i backoffice</a> <span class="muted">(${f.documentType})</span>` : nothing}
                      ${f.isNew ? html` <span class="chip new">Nyt</span>` : nothing}${f.status === 'Resolved' ? html` <span class="chip good">Løst</span>` : nothing}</td>
                    <td class="evidence">${f.evidence}</td>
                    ${this.#canAi && AI_RULES[g.ruleId] ? html`<td>${f.status === 'Open' && f.nodeKey ? this.#aiButton(() => this.#pageTask(f)) : nothing}</td>` : nothing}
                  </tr>`,
                )}</tbody>
              </table>
              <p class="muted">Regel ${g.ruleId}</p>
            </details>`,
          )
        : html`<div class="box good">Ingen fund matcher filteret.</div>`}
    `;
  }

  /**
   * Broken links and images without alt: the same link or image usually sits on many pages (menu, footer), so show each one
   * once with how many pages have it and where. Parses the evidence lines written by rules CRA-03 and ONP-11.
   */
  #renderTargets(g) {
    if (g.ruleId !== 'CRA-03' && g.ruleId !== 'ONP-11') return nothing;
    const rows = new Map();
    for (const f of g.items) {
      for (const line of f.evidence.split('\n')) {
        if (line.startsWith('(+')) continue;
        const region = (line.match(/ · i (\S+)$/) ?? [])[1] ?? '';
        const target = line.split(g.ruleId === 'CRA-03' ? ' → ' : ' · ')[0];
        const row = rows.get(target) ?? {
          target, region, pages: 0, firstPage: f,
          status: (line.match(/HTTP (\d+)/) ?? [])[1],
          text: (line.match(/linktekst "([^"]*)"/) ?? [])[1],
        };
        row.pages++;
        rows.set(target, row);
      }
    }
    const list = [...rows.values()].sort((a, b) => b.pages - a.pages);
    if (!list.length) return nothing;
    const where = (r) => (/menu|header|footer|head/.test(r) ? html`<strong>${r}</strong> <span class="muted">(ret ét sted i skabelonen)</span>` : r);
    return g.ruleId === 'CRA-03'
      ? html`<h4>Hvilke links er brudte?</h4>
          <table>
            <thead><tr><th>Linket peger på</th><th>Svar</th><th>Linktekst</th><th>Hvor på siden</th><th>Antal sider</th></tr></thead>
            <tbody>${list.map((r) => html`<tr>
              <td class="url"><a href=${r.target} target="_blank" rel="noopener noreferrer">${r.target}</a></td>
              <td><span class="chip bad">HTTP ${r.status ?? '?'}</span></td>
              <td>${r.text ?? html`<span class="muted">(ingen tekst)</span>`}</td>
              <td>${where(r.region)}</td>
              <td>${r.pages}</td></tr>`)}</tbody>
          </table>`
      : html`<h4>Hvilke billeder mangler alt-tekst?</h4>
          <table>
            <thead><tr><th>Billede</th><th>Hvor på siden</th><th>Antal sider</th>${this.#canAi ? html`<th></th>` : nothing}</tr></thead>
            <tbody>${list.map((r) => html`<tr>
              <td class="url">${r.target.startsWith('http')
                ? html`<a href=${r.target} target="_blank" rel="noopener noreferrer">${r.target.split('/').pop() || r.target}</a><br /><span class="muted">${r.target}</span>`
                : r.target}</td>
              <td>${where(r.region)}</td>
              <td>${r.pages}</td>
              ${this.#canAi ? html`<td>${r.target.startsWith('http') && r.region !== 'head' ? this.#aiButton(() => this.#imageTask(r.target, r.firstPage)) : nothing}</td>` : nothing}</tr>`)}</tbody>
          </table>`;
  }

  #matchesStatus(f) {
    switch (this._status) {
      case 'open': return f.status === 'Open';
      case 'new': return f.status === 'Open' && f.isNew;
      case 'resolved': return f.status === 'Resolved';
      default: return true;
    }
  }

  // ---- Konkurrenter -----------------------------------------------------------------

  #renderCompare(report) {
    const b = this._benchmark;
    const failed = (report.competitorFailures ?? []).map((f) => html`<div class="box warn"><strong>Seneste analyse af ${f.domain} fejlede</strong>
      (${this.#fmtDate(f.atUtc)}): ${f.message} Sammenligningen bruger den seneste analyse af sitet, der lykkedes.</div>`);
    if (!b) return html`${failed}<div class="box bad">Kunne ikke hente sammenligningen.</div>${this.#renderChanges(report)}`;
    if (!b.ready) return html`${failed}<div class="box info">${b.message}</div>${this.#renderChanges(report)}`;
    const bm = b.benchmark;
    const num = (v, unit) => {
      if (v == null) return '–';
      if (unit === '%') return `${Math.round(v)} %`;
      if (unit === 'point') return `${Math.round(v)}`;
      if (unit === 'stk.') return `${Number.isInteger(v) ? v : v.toFixed(1).replace('.', ',')} stk.`;
      return `${Math.round(v).toLocaleString('da-DK')} ${unit}`;
    };
    const verdict = { ahead: ['good', 'Foran'], level: ['neutral', 'På niveau'], behind: ['bad', 'Bagud'] };
    return html`
      ${failed}
      <p class="help">Jeres site og konkurrenterne er gennemgået med de samme tjek. Her ser I, hvor I er foran, hvor I er bagud, og hvad de har, som I ikke har.</p>

      <div class="two">
        <div class="box bad">
          <h3>Her er I bagud (${bm.gaps.length})</h3>
          ${bm.gaps.length
            ? html`<ul>${bm.gaps.map((g) => html`<li>${g.summary}</li>`)}</ul>
                <uui-button look="primary" color="danger" label="Se hvad I skal gøre" @click=${() => (this._tab = 'plan')}>Se hvad I skal gøre</uui-button>`
            : html`<p>Ingen målinger, hvor en konkurrent er tydeligt bedre. 🎉</p>`}
        </div>
        <div class="box good">
          <h3>Her er I foran (${bm.strengths.length})</h3>
          ${bm.strengths.length
            ? html`<ul>${bm.strengths.map((s) => html`<li>${s.summary}</li>`)}</ul>`
            : html`<p class="muted">Ingen målinger, hvor I er tydeligt foran alle konkurrenter.</p>`}
        </div>
      </div>

      <h3>Tallene side om side</h3>
      <div class="table-wrap"><table>
        <thead><tr><th>Måling</th>${bm.domains.map((d, i) => html`<th>${d}${i === 0 ? html` <span class="muted">(jer)</span>` : nothing}</th>`)}<th>Vurdering</th></tr></thead>
        <tbody>
          ${bm.rows.map((r) => {
            const [cls, label] = verdict[r.verdict] ?? verdict.level;
            return html`<tr>
              <td>${r.label}<br /><span class="muted">${r.higherIsBetter ? 'Højere er bedre' : 'Lavere er bedre'}</span></td>
              <td class="own ${cls}">${num(r.own, r.unit)}</td>
              ${r.competitors.map((v) => html`<td title=${v == null ? 'Ikke målt i konkurrentens seneste analyse' : ''}>${num(v, r.unit)}</td>`)}
              <td><span class="chip ${cls}">${label}</span></td>
            </tr>`;
          })}
        </tbody>
      </table></div>
      <p class="muted">"Foran" og "bagud" kræver en tydelig forskel, så små udsving tæller som "på niveau". Tallene er stikprøver fra op til 300 sider pr. site og siger intet om placeringer i Google eller trafik, kun hvad der kan måles i sidernes HTML. Indhold, der først vises med JavaScript, kan ikke ses. Seneste analyse: ${this.#fmtDate(b.ownRunUtc)}.</p>

      ${bm.schemaGaps?.length
        ? html`<h3>Struktureret data, de bruger, og I ikke gør</h3>
            <p class="help">Struktureret data (JSON-LD) fortæller Google, hvad en side handler om, fx en FAQ eller en artikel, og kan give rigere visning i søgeresultaterne.</p>
            <table><thead><tr><th>Type</th><th>Hos</th><th>Sider</th></tr></thead>
              <tbody>${bm.schemaGaps.map((s) => html`<tr><td>${s.type}</td><td>${s.domain}</td><td>${s.pages}</td></tr>`)}</tbody></table>`
        : nothing}

      ${bm.sectionGaps.length
        ? html`<h3>Emner, de skriver om, og I ikke gør</h3>
            <p class="help">Sektioner med mindst tre sider hos konkurrenten (første led i adressen, fx /cases/), som ikke findes hos jer. Det viser, hvad de har skrevet om, ikke om det virker for dem.</p>
            <table><thead><tr><th>Sektion</th><th>Hos</th><th>Sider</th><th>Gns. ordtal</th></tr></thead>
              <tbody>${bm.sectionGaps.map((s) => html`<tr><td>/${s.name}/</td><td>${s.domain}</td><td>${s.pages}</td><td>${Math.round(s.avgWords)}</td></tr>`)}</tbody></table>`
        : nothing}

      <details class="group"><summary><strong>Ændringer hos konkurrenterne siden sidste analyse</strong></summary>${this.#renderChanges(report)}</details>
    `;
  }

  #renderChanges({ competitors }) {
    if (!competitors.length) return html`<p>Ingen konkurrenter er gennemgået endnu. ${this._admin ? 'Tilføj dem under Indstillinger.' : ''}</p>`;
    return competitors.map(
      (c) => html`
        <h4>${c.domain}</h4>
        <p class="muted">Seneste analyse: ${this.#fmtDate(c.startedUtc)} · ${c.pageCount} sider</p>
        ${c.isBaseline
          ? html`<p><em>Første analyse af denne konkurrent. Ændringer vises fra næste gang.</em></p>`
          : c.changes.length
            ? html`<table>
                <thead><tr><th>Ændring</th><th>Side</th><th>Før</th><th>Nu</th></tr></thead>
                <tbody>
                  ${c.changes.map(
                    (ch) => html`<tr>
                      <td>${KIND_LABEL[ch.kind] ?? ch.kind}${ch.field ? html`<br /><span class="muted">${FIELD_LABEL[ch.field] ?? ch.field}</span>` : nothing}</td>
                      <td class="url"><a href=${ch.url} target="_blank" rel="noopener noreferrer">${ch.url}</a></td>
                      <td>${ch.oldValue ?? ''}</td>
                      <td>${ch.newValue ?? ''}</td>
                    </tr>`,
                  )}
                </tbody>
              </table>`
            : html`<p>Ingen ændringer siden sidste analyse.</p>`}
      `,
    );
  }

  static styles = css`
    :host {
      display: block;
      padding: var(--uui-size-layout-1);
      --good: var(--uui-color-positive, #1a7f37);
      --good-bg: color-mix(in srgb, var(--good) 10%, transparent);
      --bad: var(--uui-color-danger, #c62828);
      --bad-bg: color-mix(in srgb, var(--bad) 9%, transparent);
      --warn: var(--uui-color-warning-standalone, #b26a00);
      --warn-bg: color-mix(in srgb, var(--warn) 11%, transparent);
      --info: var(--uui-color-interactive, #3544b1);
      --info-bg: color-mix(in srgb, var(--info) 8%, transparent);
    }
    h2, h3, h4 { margin: 0 0 8px; }
    h3 { margin-top: 20px; }
    .box h3 { margin-top: 0; }
    p { margin: 6px 0; }
    .tab-body { padding-top: 16px; }
    .toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
    .site { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 8px; }
    .toolbar .actions { margin-top: 0; }
    .help { color: var(--uui-color-text-alt); font-size: 13px; max-width: 75ch; margin: 8px 0 12px; }
    select { padding: 6px 8px; font: inherit; }
    .row-between { display: flex; justify-content: space-between; align-items: baseline; }

    .box { border: 1px solid var(--uui-color-border); border-left-width: 5px; border-radius: 6px; padding: 12px 16px; margin: 0 0 12px; display: grid; gap: 6px; }
    .box.good { border-left-color: var(--good); background: var(--good-bg); }
    .box.bad { border-left-color: var(--bad); background: var(--bad-bg); }
    .box.warn { border-left-color: var(--warn); background: var(--warn-bg); }
    .box.info { border-left-color: var(--info); background: var(--info-bg); }
    .box.neutral { background: var(--uui-color-surface-alt); }
    .box ul, .box ol { margin: 4px 0; padding-left: 20px; }
    .two { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 12px; margin: 16px 0; }
    .two .box { margin: 0; align-content: start; }

    .hero { display: flex; gap: 24px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .score { width: 120px; height: 120px; border-radius: 50%; display: flex; flex-direction: column; align-items: center; justify-content: center; border: 8px solid; flex: none; }
    .score span { font-size: 40px; font-weight: 700; line-height: 1; }
    .score small { color: var(--uui-color-text-alt); }
    .score.good, h2.good { border-color: var(--good); color: var(--good); }
    .score.warn, h2.warn { border-color: var(--warn); color: var(--warn); }
    .score.bad, h2.bad { border-color: var(--bad); color: var(--bad); }
    .hero > div:last-child { flex: 1; min-width: 260px; }
    ul.losses { margin: 0 0 8px; padding-left: 20px; }
    a.google { font-size: 12px; white-space: nowrap; color: var(--uui-color-text-alt); }

    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr)); gap: 10px; margin: 16px 0 8px; }
    .tile { display: flex; flex-direction: column; align-items: center; padding: 12px; border-radius: 6px; border-top: 4px solid var(--uui-color-border); background: var(--uui-color-surface-alt); }
    .tile.good { border-top-color: var(--good); background: var(--good-bg); }
    .tile.bad { border-top-color: var(--bad); background: var(--bad-bg); }
    .tile.warn { border-top-color: var(--warn); background: var(--warn-bg); }
    .value { font-size: 28px; font-weight: 700; }
    .label, .muted { color: var(--uui-color-text-alt); font-size: 13px; }
    .label { font-weight: 600; }
    .error { color: var(--bad); }

    .bucket { border-left: 5px solid var(--uui-color-border); padding: 4px 0 4px 14px; margin: 20px 0; }
    .bucket h3 { margin: 0; }
    .bucket.critical { border-left-color: var(--bad); }
    .bucket.quick { border-left-color: var(--good); }
    .bucket.high { border-left-color: var(--warn); }
    .bucket.long { border-left-color: var(--info); }
    .count { font-size: 13px; font-weight: 600; padding: 1px 8px; border-radius: 10px; background: var(--uui-color-surface-alt); vertical-align: middle; }
    .card { border: 1px solid var(--uui-color-border); border-radius: 6px; padding: 10px 14px; margin: 8px 0; background: var(--uui-color-surface); }
    .card-title { font-weight: 700; }
    .card ol { margin: 4px 0; padding-left: 20px; }
    .chips { display: flex; gap: 6px; flex-wrap: wrap; margin: 6px 0; }
    .chip { display: inline-block; white-space: nowrap; font-size: 12px; padding: 1px 8px; border-radius: 10px; background: var(--uui-color-surface-alt); border: 1px solid var(--uui-color-border); }
    .chip.good { background: var(--good-bg); border-color: var(--good); color: var(--good); }
    .chip.bad { background: var(--bad-bg); border-color: var(--bad); color: var(--bad); }
    .chip.impact { background: var(--info-bg); border-color: var(--info); }
    .chip.new { background: var(--warn-bg); border-color: var(--warn); }
    ul.pages { word-break: break-all; }
    ul.cols { columns: 2 220px; list-style: none; padding-left: 0; }

    .legend { display: flex; gap: 16px; flex-wrap: wrap; font-size: 13px; margin-bottom: 8px; }
    .filters { display: flex; gap: 12px; align-items: center; margin: 12px 0; flex-wrap: wrap; }
    details.group { border: 1px solid var(--uui-color-border); border-left: 5px solid var(--uui-color-border); border-radius: 6px; padding: 8px 12px; margin: 6px 0; }
    details.group.Critical { border-left-color: var(--bad); }
    details.group.Warning { border-left-color: var(--warn); }
    details.group summary { cursor: pointer; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    tr.resolved td { opacity: 0.6; }

    .table-wrap { overflow-x: auto; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 8px; border-bottom: 1px solid var(--uui-color-border); vertical-align: top; }
    td.url { word-break: break-all; }
    .evidence { white-space: pre-line; word-break: break-word; }
    h4 { margin: 12px 0 6px; }
    .overlay { position: fixed; inset: 0; z-index: 1000; background: rgba(0, 0, 0, 0.5); display: flex; align-items: center; justify-content: center; padding: 16px; }
    .overlay .dialog { max-width: 520px; width: 100%; margin: 0; background: var(--uui-color-surface, #fff); box-shadow: 0 10px 40px rgba(0, 0, 0, 0.3); }
    pre { background: var(--uui-color-surface-alt); padding: 8px 10px; border-radius: 4px; overflow-x: auto; margin: 4px 0; }
    td.own { font-weight: 700; }
    td.own.good { color: var(--good); background: var(--good-bg); }
    td.own.bad { color: var(--bad); background: var(--bad-bg); }
    .item { padding: 8px 0; border-bottom: 1px solid var(--uui-color-border); }
    .sev { font-size: 12px; font-weight: 600; padding: 2px 6px; border-radius: 4px; background: var(--uui-color-surface-alt); white-space: nowrap; }
    .sev.Critical { background: var(--bad); color: #fff; }
    .sev.Warning { background: var(--warn); color: #fff; }
  `;
}

customElements.define('lait-seo-dashboard', LaitSeoDashboard);
