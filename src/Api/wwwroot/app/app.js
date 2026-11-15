// The Overens console. Plain modules, no build step. Everything shown is built with
// textContent, so nothing from an agreement or a model is ever run as markup.

const app = document.getElementById("app");
const nav = document.getElementById("nav");
const sessionBox = document.getElementById("session");

function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (name === "class") node.className = value;
    else if (name.startsWith("on")) node.addEventListener(name.slice(2), value);
    else if (value !== false && value != null) node.setAttribute(name, value === true ? "" : value);
  }
  for (const child of children.flat()) {
    if (child != null && child !== false) node.append(child);
  }
  return node;
}

// ------------------------------------------------------------------ sign-in

const config = await (await fetch("config.json")).json();
const redirect = `${location.origin}/app/`;
let token = null;
let claims = null;

const base64url = (bytes) =>
  btoa(String.fromCharCode(...new Uint8Array(bytes))).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");

async function signIn() {
  // Authorization code flow with PKCE: the password is typed at the identity provider, never here.
  const verifier = base64url(crypto.getRandomValues(new Uint8Array(32)));
  const state = base64url(crypto.getRandomValues(new Uint8Array(16)));
  sessionStorage.setItem("pkce", JSON.stringify({ verifier, state }));
  const challenge = base64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier)));
  const query = new URLSearchParams({
    client_id: config.clientId,
    redirect_uri: redirect,
    response_type: "code",
    scope: "openid",
    state,
    code_challenge: challenge,
    code_challenge_method: "S256",
  });
  location.assign(`${config.authority}/protocol/openid-connect/auth?${query}`);
}

async function finishSignIn() {
  const query = new URLSearchParams(location.search);
  const code = query.get("code");
  if (!code) return;
  const saved = JSON.parse(sessionStorage.getItem("pkce") ?? "null");
  sessionStorage.removeItem("pkce");
  history.replaceState(null, "", redirect);
  if (!saved || saved.state !== query.get("state")) throw new Error("The sign-in did not start here. Try again.");
  const response = await fetch(`${config.authority}/protocol/openid-connect/token`, {
    method: "POST",
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: config.clientId,
      redirect_uri: redirect,
      code,
      code_verifier: saved.verifier,
    }),
  });
  if (!response.ok) throw new Error("Sign-in failed.");
  token = (await response.json()).access_token;
  claims = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(token.split(".")[1].replaceAll("-", "+").replaceAll("_", "/")), (c) => c.charCodeAt(0))));
}

function signOut() {
  token = null;
  location.assign(`${config.authority}/protocol/openid-connect/logout?${new URLSearchParams({ client_id: config.clientId, post_logout_redirect_uri: redirect })}`);
}

async function api(method, path, body, type = "application/json") {
  const response = await fetch(path, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "content-type": type }) },
    body: body === undefined ? undefined : type === "application/json" ? JSON.stringify(body) : body,
  });
  const payload = response.headers.get("content-type")?.includes("json") ? await response.json() : null;
  if (!response.ok) throw new Error(payload?.detail ?? `The request failed (${response.status}).`);
  return payload;
}

// ------------------------------------------------------------------ shared pieces

const STATUS = {
  started: ["Reading", "plain"],
  awaiting_review: ["Waiting for review", "warn"],
  published: ["Published", "good"],
  rejected: ["Rejected", "bad"],
  failed: ["Failed", "bad"],
};

const pill = (status) => el("span", { class: `pill ${STATUS[status]?.[1] ?? "plain"}` }, STATUS[status]?.[0] ?? status);
const failure = (error) => el("p", { class: "error" }, error.message);
const kr = (value) => `${Number(value).toLocaleString("da-DK", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} kr.`;
const pct = (value) => `${Number(value).toLocaleString("da-DK", { minimumFractionDigits: 1 })} %`;
const hours = (value) => `${Number(value).toLocaleString("da-DK")} h`;
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

const KINDS = {
  minimum_wage: "Minimum wage",
  overtime: "Overtime",
  inconvenient_hours: "Inconvenient hours",
  pension: "Pension",
  holiday_allowance: "Holiday allowance",
  special_savings: "Special savings",
};

function days(list) {
  if (list.length === 7) return "Every day";
  const ordered = DAYS.filter((day) => list.includes(day));
  if (ordered.join() === DAYS.slice(0, 5).join()) return "Monday to Friday";
  return ordered.join(", ");
}

/** A rule's values as rows of label and value, in words a payroll specialist uses. */
function facts(rule, withPeriod = true) {
  const rows = withPeriod ? [["In force", `${rule.inForce.from} ${rule.inForce.until ? `until ${rule.inForce.until}` : "onwards"}`]] : [];
  switch (rule.kind) {
    case "minimum_wage":
      for (const band of rule.bands) rows.push([band.fromMonths === 0 ? "From hiring" : `After ${band.fromMonths} months`, `${kr(band.hourlyRate)} an hour`]);
      break;
    case "overtime":
      rows.push(["Daily norm", hours(rule.dailyNormHours)]);
      for (const tier of rule.tiers) rows.push([tier.hours == null ? "Further overtime" : `First ${hours(tier.hours)}`, `+ ${pct(tier.percent)}`]);
      rows.push(["Sundays", `+ ${pct(rule.sundayPercent)}`]);
      break;
    case "inconvenient_hours":
      for (const window of rule.windows) rows.push([`${days(window.days)} ${window.from.slice(0, 5)}–${window.to.slice(0, 5)}`, `${kr(window.amountPerHour)} an hour`]);
      break;
    case "pension":
      rows.push(["Employer", pct(rule.employerPercent)], ["Employee", pct(rule.employeePercent)], ["Applies after", `${rule.qualifyingMonths} months`]);
      break;
    default:
      rows.push(["Of pay", pct(rule.percent)]);
  }
  return el("table", {}, rows.map(([label, value]) => el("tr", {}, el("th", {}, label), el("td", {}, value))));
}

/** A clause's text, with the quoted sentence marked where it stands. */
function clauseText(clause, quote) {
  const at = quote ? clause.text.indexOf(quote) : -1;
  const pages = clause.firstPage === clause.lastPage ? `page ${clause.firstPage}` : `pages ${clause.firstPage}–${clause.lastPage}`;
  return el(
    "div",
    { class: "clause" },
    el("span", { class: "where" }, `${clause.id} · ${pages}`),
    at < 0 ? clause.text : [clause.text.slice(0, at), el("mark", {}, quote), clause.text.slice(at + quote.length)],
  );
}

// ------------------------------------------------------------------ specialist: agreements

async function agreementsView() {
  const list = el("div", {}, el("p", { class: "muted" }, "Loading…"));
  const message = el("p", { class: "error" });
  const id = el("input", { type: "text", placeholder: "lager", "aria-label": "Agreement id" });
  const file = el("input", { type: "file", accept: "application/pdf", "aria-label": "Agreement PDF" });

  async function load() {
    const runs = await api("GET", "/runs");
    list.replaceChildren(
      runs.length === 0
        ? el("p", { class: "muted" }, "No agreements have been uploaded yet.")
        : el(
            "table",
            {},
            el("tr", {}, el("th", {}, "Agreement"), el("th", {}, "Title"), el("th", {}, "Uploaded"), el("th", {}, "Status")),
            runs.map((run) =>
              el(
                "tr",
                { class: "link", onclick: () => show(() => runView(run.id)) },
                el("td", {}, run.agreementId),
                el("td", {}, run.title ?? "…"),
                el("td", {}, new Date(run.createdAt).toISOString().slice(0, 16).replace("T", " ")),
                el("td", {}, pill(run.status)),
              ),
            ),
          ),
    );
    return runs;
  }

  async function upload() {
    message.textContent = "";
    if (!file.files[0]) {
      message.textContent = "Choose a PDF first.";
      return;
    }
    try {
      const run = await api("POST", `/agreements/${encodeURIComponent(id.value.trim())}/runs`, file.files[0], "application/pdf");
      show(() => runView(run.id));
    } catch (error) {
      message.textContent = error.message;
    }
  }

  load().catch((error) => list.replaceChildren(failure(error)));
  return [
    el(
      "section",
      {},
      el("h1", {}, "Upload an agreement"),
      el("p", { class: "muted" }, "The pipeline reads the PDF and proposes rules. Nothing is published until you have reviewed and approved them."),
      el("label", {}, "Agreement id"),
      id,
      el("label", {}, "PDF"),
      file,
      el("p", {}, ""),
      el("div", { class: "row" }, el("button", { class: "primary", onclick: upload }, "Upload"), message),
    ),
    el("section", {}, el("h2", {}, "Agreements"), list),
  ];
}

// ------------------------------------------------------------------ specialist: one run

async function runView(runId) {
  const body = el("div", {}, el("p", { class: "muted" }, "Loading…"));
  let timer = null;

  async function act(work) {
    try {
      await work();
    } catch (error) {
      body.prepend(el("div", { class: "notice bad" }, error.message));
      return;
    }
    await load();
  }

  async function load() {
    clearTimeout(timer);
    const run = await api("GET", `/runs/${runId}`);
    const open = run.status === "awaiting_review";
    const undecided = run.rules.filter((rule) => rule.decision === "pending").length;
    const approved = run.rules.filter((rule) => rule.decision === "approved").length;

    const rules = run.rules.map((rule) =>
      el(
        "div",
        { class: "rule", "data-rule": rule.id },
        el(
          "div",
          { class: "spread" },
          el("div", { class: "row" }, el("h3", {}, KINDS[rule.rule.kind] ?? rule.rule.kind), el("span", { class: "muted" }, rule.id)),
          el(
            "div",
            { class: "row" },
            rule.decision === "approved" && el("span", { class: "pill good" }, "Approved"),
            rule.decision === "rejected" && el("span", { class: "pill bad" }, "Rejected"),
            open &&
              el(
                "button",
                {
                  disabled: rule.problems.length > 0 || rule.decision === "approved",
                  onclick: () => act(() => api("POST", `/runs/${runId}/rules/${rule.id}/decision`, { decision: "approved" })),
                },
                "Approve",
              ),
            open &&
              el(
                "button",
                {
                  class: "danger",
                  disabled: rule.decision === "rejected",
                  onclick: () => {
                    // The reason is kept: a rejected rule, with why, is a case for the evals.
                    const reason = window.prompt("Why is this rule wrong? (optional)") ?? undefined;
                    return act(() => api("POST", `/runs/${runId}/rules/${rule.id}/decision`, { decision: "rejected", reason }));
                  },
                },
                "Reject",
              ),
          ),
        ),
        rule.problems.map((problem) => el("div", { class: "notice bad" }, `The checks found a problem, so this rule cannot be approved: ${problem.message}`)),
        el("div", { class: "sides" }, facts(rule.rule), el("div", {}, rule.clauses.map((clause) => clauseText(clause, clause.id === rule.rule.source.clauseId ? rule.quote : null)))),
      ),
    );

    const parts = [
      el(
        "section",
        {},
        el("div", { class: "spread" }, el("h1", {}, run.title ?? run.agreementId), pill(run.status)),
        el("p", { class: "muted" }, `Agreement ${run.agreementId}${run.model ? ` · read by ${run.model}, prompt ${run.promptVersion}` : ""}`),
        run.failure && el("div", { class: "notice bad" }, run.failure),
        (run.gaps ?? []).map((gap) => el("div", { class: "notice" }, `${KINDS[gap.ruleId] ?? gap.ruleId}: ${gap.message}`)),
        open &&
          el(
            "div",
            { class: "spread" },
            el("span", { class: "muted" }, undecided > 0 ? `${undecided} of ${run.rules.length} rules still to decide.` : `${approved} approved, ${run.rules.length - approved} rejected.`),
            el(
              "div",
              { class: "row" },
              el("button", { class: "danger", onclick: () => act(() => api("POST", `/runs/${runId}/rejection`)) }, "Reject the run"),
              el("button", { class: "primary", disabled: undecided > 0 || approved === 0, onclick: () => act(() => api("POST", `/runs/${runId}/approval`)) }, "Approve and publish"),
            ),
          ),
      ),
      open &&
        run.changes &&
        el(
          "section",
          {},
          el("h2", {}, "What publishing would change"),
          run.changes.length === 0
            ? el("p", { class: "muted" }, "Nothing. The rules not rejected say what the published rules already say.")
            : el(
                "table",
                {},
                el("tr", {}, el("th", {}, "Rule"), el("th", {}, "Days"), el("th", {}, "Now"), el("th", {}, "After publishing")),
                run.changes.map((change) =>
                  el(
                    "tr",
                    {},
                    el("td", {}, KINDS[change.kind] ?? change.kind),
                    el("td", {}, `${change.from} ${change.until ? `until ${change.until}` : "onwards"}`),
                    el("td", {}, change.before ? facts(change.before, false) : el("span", { class: "muted" }, "No rule")),
                    el("td", {}, change.after ? facts(change.after, false) : el("span", { class: "pill bad" }, "No rule")),
                  ),
                ),
              ),
        ),
      run.rules.length > 0 && el("section", {}, el("h2", {}, "Proposed rules"), rules),
    ];
    // Unlike el(), replaceChildren would print a false as the word "false".
    body.replaceChildren(...parts.filter(Boolean));

    // A run that is being read, or published, moves on its own.
    if (run.status === "started" || (open && undecided === 0 && approved > 0)) timer = setTimeout(() => load().catch(() => {}), 1500);
  }

  load().catch((error) => body.replaceChildren(failure(error)));
  return [body];
}

// ------------------------------------------------------------------ pay checks

async function payView() {
  const result = el("div");
  const agreement = el("input", { type: "text", placeholder: "lager", "aria-label": "Agreement id" });
  const input = el("textarea", { "aria-label": "Timesheet and payslip", placeholder: '{ "timesheet": { … }, "payslip": { … } }' });
  const file = el("input", { type: "file", accept: "application/json", "aria-label": "Pay file" });
  file.addEventListener("change", async () => {
    if (file.files[0]) input.value = await file.files[0].text();
  });

  async function check() {
    result.replaceChildren(el("p", { class: "muted" }, "Checking…"));
    try {
      const answer = await api("POST", `/agreements/${encodeURIComponent(agreement.value.trim())}/pay-checks`, JSON.parse(input.value));
      result.replaceChildren(
        el(
          "section",
          {},
          el("div", { class: "spread" }, el("h2", {}, "Result"), el("span", { class: `pill ${answer.findings.length ? "bad" : "good"}` }, answer.findings.length ? `${answer.findings.length} to correct` : "Nothing to correct")),
          el("p", { class: "muted" }, `Checked against version ${answer.version} of the rules for ${answer.agreementId}.`),
          answer.findings.length === 0 && el("p", {}, "Every line on the payslip is what the agreement requires."),
          answer.findings.map((finding) =>
            el(
              "div",
              { class: "finding" },
              el("p", {}, finding.explanation),
              el(
                "table",
                {},
                el("tr", {}, el("th", {}, "Paid"), el("td", {}, kr(finding.actual))),
                el("tr", {}, el("th", {}, "Should be"), el("td", {}, kr(finding.expected))),
                el("tr", {}, el("th", {}, "Difference"), el("td", {}, kr(finding.difference))),
                finding.clauses.map((clause) => el("tr", {}, el("th", {}, clause.id), el("td", {}, clause.quote))),
              ),
            ),
          ),
        ),
      );
    } catch (error) {
      result.replaceChildren(el("section", {}, failure(error instanceof SyntaxError ? new Error("That is not JSON.") : error)));
    }
  }

  return [
    el(
      "section",
      {},
      el("h1", {}, "Check a payslip"),
      el("p", { class: "muted" }, "The payslip is checked by arithmetic against the published rules. Nothing about the employee is stored."),
      el("label", {}, "Agreement id"),
      agreement,
      el("label", {}, "Timesheet and payslip, as JSON"),
      input,
      el("div", { class: "row" }, file),
      el("p", {}, ""),
      el("button", { class: "primary", onclick: check }, "Check"),
    ),
    result,
  ];
}

// ------------------------------------------------------------------ questions

async function askView() {
  const result = el("div");
  const agreement = el("input", { type: "text", placeholder: "lager", "aria-label": "Agreement id" });
  const question = el("textarea", { "aria-label": "Question", placeholder: "Hvad er mindstebetalingen for en nyansat?" });

  async function ask() {
    result.replaceChildren(el("p", { class: "muted" }, "Reading the agreement…"));
    try {
      const answer = await api("POST", `/agreements/${encodeURIComponent(agreement.value.trim())}/questions`, { question: question.value });
      result.replaceChildren(
        el(
          "section",
          {},
          el(
            "div",
            { class: "spread" },
            el("h2", {}, "Answer"),
            el("span", { class: `pill ${answer.outcome === "Answered" ? "good" : "warn"}` }, answer.outcome === "Answered" ? "Checked against the agreement" : answer.outcome === "NotInAgreement" ? "Not in the agreement" : "No answer given"),
          ),
          el("p", {}, answer.text),
          answer.citations.map((citation) => el("div", { class: "clause" }, el("span", { class: "where" }, citation.clauseId), citation.quote)),
          el("p", { class: "muted" }, `What the agent did: ${answer.steps.join(" → ")}`),
        ),
      );
    } catch (error) {
      result.replaceChildren(el("section", {}, failure(error)));
    }
  }

  return [
    el(
      "section",
      {},
      el("h1", {}, "Ask about an agreement"),
      el("p", { class: "muted" }, "An answer is shown only if it quotes the agreement word for word and every number in it comes from the agreement or the pay calculator."),
      el("label", {}, "Agreement id"),
      agreement,
      el("label", {}, "Question"),
      question,
      el("p", {}, ""),
      el("button", { class: "primary", onclick: ask }, "Ask"),
    ),
    result,
  ];
}

// ------------------------------------------------------------------ frame

async function show(view) {
  app.replaceChildren(...(await view()));
}

function tabs(items) {
  const buttons = items.map(([label, view], index) =>
    el(
      "button",
      {
        class: `tab${index === 0 ? " current" : ""}`,
        onclick: (event) => {
          for (const button of buttons) button.classList.toggle("current", button === event.currentTarget);
          show(view);
        },
      },
      label,
    ),
  );
  nav.replaceChildren(...buttons);
  show(items[0][1]);
}

try {
  await finishSignIn();
  if (!token) {
    app.replaceChildren(
      el("section", {}, el("h1", {}, "Overens"), el("p", {}, "Pay rules read from collective agreements, reviewed by a person, and used to check payslips."), el("button", { class: "primary", onclick: signIn }, "Sign in")),
    );
  } else {
    const roles = claims.roles ?? [];
    sessionBox.replaceChildren(el("span", {}, `${claims.preferred_username} · ${claims.tenant ?? "no company"}`), el("button", { onclick: signOut }, "Sign out"));
    if (roles.includes("specialist")) tabs([["Agreements", agreementsView], ["Check a payslip", payView], ["Ask", askView]]);
    else if (roles.includes("employer")) tabs([["Check a payslip", payView], ["Ask", askView]]);
    else app.replaceChildren(el("section", {}, el("h1", {}, "No access"), el("p", {}, "Your account has no role in Overens. Ask an administrator.")));
  }
} catch (error) {
  app.replaceChildren(el("section", {}, failure(error)));
}
