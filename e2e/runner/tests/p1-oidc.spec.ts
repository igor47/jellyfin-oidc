// Phase 1: OIDC enabled, password login still enabled,
// LINK_EXISTING_BY_USERNAME=false, allowed groups + admin groups set.
import { test, expect } from '@playwright/test';
import { createHash, randomBytes } from 'node:crypto';
import {
  ADMIN, IDP, IDP_USERS, OPERATOR_CSS, OPERATOR_DISCLAIMER,
  api, apiKeyWorks, brandingConfig, brandingCss, call, gotoLoginPage, listUsers, loadState, me,
  guidForms, oidcButton, oidcLogin, passwordLogin, readOidcLinks, saveState, short, waitForIdp, waitForJellyfin,
} from './lib';

test.beforeAll(async () => {
  const rc = await api();
  await waitForIdp(rc);
  await waitForJellyfin(rc);

  // First-run wizard, via the API (idempotent: skipped when already done).
  const info = await (await call(rc, 'GET', '/System/Info/Public')).json();
  // This endpoint answers in camelCase.
  if (!(info.StartupWizardCompleted ?? info.startupWizardCompleted)) {
    expect((await call(rc, 'POST', '/Startup/Configuration', {
      data: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' },
    })).status()).toBeLessThan(300);
    expect((await call(rc, 'GET', '/Startup/User')).status()).toBe(200);
    const u = await call(rc, 'POST', '/Startup/User', { data: { Name: ADMIN.name, Password: ADMIN.pw } });
    expect(u.status(), short(await u.text())).toBeLessThan(300);
    expect((await call(rc, 'POST', '/Startup/RemoteAccess', { data: { EnableRemoteAccess: true } })).status()).toBeLessThan(300);
    expect((await call(rc, 'POST', '/Startup/Complete')).status()).toBeLessThan(300);
  }

  let s = loadState();
  if (!s.adminToken) {
    const r = await passwordLogin(rc, ADMIN.name, ADMIN.pw, 'e2e-admin-session');
    expect(r.status(), `admin password login during setup: ${short(await r.text())}`).toBe(200);
    const j = await r.json();
    s = saveState({ adminToken: j.AccessToken, adminId: j.User.Id });
  }

  // Local (non-OIDC) users that share a username with IdP users.
  if (!s.localUsers) {
    const localUsers: Record<string, string> = {};
    for (const name of ['dave', 'erin']) {
      const r = await call(rc, 'POST', '/Users/New', { token: s.adminToken, data: { Name: name, Password: `local-${name}-pw` } });
      expect(r.status(), short(await r.text())).toBe(200);
      localUsers[name] = (await r.json()).Id;
    }
    s = saveState({ localUsers });
  }

  // Operator branding, which the fork must preserve and must not rewrite.
  const b = await call(rc, 'POST', '/System/Configuration/branding', {
    token: s.adminToken,
    data: { LoginDisclaimer: OPERATOR_DISCLAIMER, CustomCss: OPERATOR_CSS, SplashscreenEnabled: false },
  });
  expect(b.status(), short(await b.text())).toBeLessThan(300);

  if (!s.apiKey) {
    expect((await call(rc, 'POST', '/Auth/Keys?app=e2e-phase1', { token: s.adminToken })).status()).toBeLessThan(300);
    const keys = await (await call(rc, 'GET', '/Auth/Keys', { token: s.adminToken })).json();
    const k = keys.Items.find((i: { AppName: string }) => i.AppName === 'e2e-phase1');
    expect(k, 'created API key').toBeTruthy();
    saveState({ apiKey: k.AccessToken });
  }
});

// Harness self-test, independent of the server: the IdP fixture behaves as the
// OIDC assertions below assume (PKCE and client secret enforced, groups claim,
// fixed subjects, email_verified=false like Authentik).
// Harness self-test, independent of the server: the IdP fixture behaves as the
// OIDC assertions below assume (PKCE and client secret enforced, groups claim,
// fixed subjects, email_verified=false like Authentik). Uses a second redirect
// URI on a closed port, so the codes never reach Jellyfin.
test('@base IdP fixture: PKCE + confidential client enforced, groups claim present', async ({ browser }) => {
  const redirect = 'http://localhost:9/fixture-callback';
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  let callback: URL | null = null;
  page.on('request', (r) => {
    if (r.url().startsWith(redirect)) callback = new URL(r.url());
  });
  const param = (k: string) => (callback ? callback.searchParams.get(k) : null);
  const authUrl = (extra: Record<string, string>) =>
    `${IDP}/protocol/openid-connect/auth?${new URLSearchParams({
      client_id: 'jellyfin', redirect_uri: redirect, response_type: 'code',
      scope: 'openid profile email', state: 'st', nonce: 'nc', ...extra,
    })}`;
  // Navigations that end on the closed port fail; only the URL matters.
  const go = async (url: string) => { await page.goto(url).catch(() => undefined); };

  // Without PKCE the IdP refuses (error back to the redirect URI).
  await go(authUrl({}));
  await expect.poll(() => param('error'), { message: 'IdP rejects a request without PKCE' }).toBe('invalid_request');

  const verifier = randomBytes(32).toString('base64url');
  const challenge = createHash('sha256').update(verifier).digest('base64url');
  callback = null;
  await go(authUrl({ code_challenge: challenge, code_challenge_method: 'S256' }));
  await page.locator('#username').fill('alice');
  await page.locator('#password').fill(IDP_USERS.alice.pw);
  await page.locator('#kc-login').click();
  await expect.poll(() => param('code')).not.toBeNull();
  const code = param('code')!;

  const rc = await api();
  const tok = await rc.post(`${IDP}/protocol/openid-connect/token`, {
    form: {
      grant_type: 'authorization_code', code, redirect_uri: redirect, code_verifier: verifier,
      client_id: 'jellyfin', client_secret: 'e2e-client-secret',
    },
    failOnStatusCode: false,
  });
  expect(tok.status(), short(await tok.text())).toBe(200);
  const idToken = (await tok.json()).id_token as string;
  const part = (i: number) => JSON.parse(Buffer.from(idToken.split('.')[i], 'base64url').toString());
  const claims = part(1);
  expect(claims.iss).toBe(IDP);
  expect(claims.aud).toBe('jellyfin');
  expect(claims.sub).toBe(IDP_USERS.alice.sub);
  expect(claims.preferred_username).toBe('alice');
  expect(claims.nonce).toBe('nc');
  expect(claims.groups).toEqual(expect.arrayContaining(['jellyfin-users', 'jellyfin-admins']));
  expect(claims.email_verified).toBe(false);
  expect(part(0).alg).toMatch(/^RS/);

  // A second code (SSO session, no form) cannot be redeemed without the secret.
  callback = null;
  await go(authUrl({ code_challenge: challenge, code_challenge_method: 'S256' }));
  await expect.poll(() => param('code')).not.toBeNull();
  const noSecret = await rc.post(`${IDP}/protocol/openid-connect/token`, {
    form: {
      grant_type: 'authorization_code', code: param('code')!, redirect_uri: redirect,
      code_verifier: verifier, client_id: 'jellyfin',
    },
    failOnStatusCode: false,
  });
  expect(noSecret.status(), 'token request without client secret').toBeGreaterThanOrEqual(400);
  await ctx.close();
});

test('@base password login works while password login is enabled', async () => {
  const rc = await api();
  expect((await passwordLogin(rc, ADMIN.name, ADMIN.pw)).status()).toBe(200);
});

test('@base API key works', async () => {
  await apiKeyWorks(await api(), loadState().apiKey!);
});

test('@oidc branding API is augmented; stored branding is untouched', async () => {
  const rc = await api();
  const cfg = await brandingConfig(rc);
  expect(cfg.LoginDisclaimer ?? '').toContain('/oidc/login');
  expect(cfg.LoginDisclaimer ?? '').toContain(OPERATOR_DISCLAIMER);
  expect(await brandingCss(rc)).toContain(OPERATOR_CSS);

  const stored = await (await call(rc, 'GET', '/System/Configuration/branding', { token: loadState().adminToken })).json();
  expect(stored.LoginDisclaimer).toBe(OPERATOR_DISCLAIMER);
  expect(stored.CustomCss).toBe(OPERATOR_CSS);
});

test('@oidc login page shows the sign-in button and (password enabled) the password form', async ({ page }) => {
  await gotoLoginPage(page);
  await expect(oidcButton(page)).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('#txtManualName')).toBeVisible();
});

test('@oidc full OIDC login (admin group) lands in a working session and promotes to admin', async ({ browser }) => {
  const rc = await api();
  const r = await oidcLogin(browser, 'alice', { expectSuccess: true });
  const u = await me(rc, r.token!);
  expect(u.Name).toBe('alice');
  expect(u.Policy.IsAdministrator, 'alice is in jellyfin-admins').toBe(true);
  // The session works for ordinary API calls.
  expect((await call(rc, 'GET', `/Users/${u.Id}/Views`, { token: r.token! })).status()).toBe(200);
  expect((await call(rc, 'GET', '/Sessions', { token: r.token! })).status()).toBe(200);
  // The link is stored in <config dir>/oidc-links.json, keyed by sub.
  expect(readOidcLinks() ?? '', 'oidc-links.json mentions alice\'s sub').toContain(IDP_USERS.alice.sub);
  await r.page.context().close();
});

test('@oidc non-admin group member logs in without admin rights; admin status re-synced on login', async ({ browser }) => {
  const rc = await api();
  const r1 = await oidcLogin(browser, 'bob', { expectSuccess: true });
  const u = await me(rc, r1.token!);
  expect(u.Name).toBe('bob');
  expect(u.Policy.IsAdministrator).toBe(false);
  await r1.page.context().close();

  // An admin promotes bob by hand; the next OIDC login must demote him again.
  const admin = loadState().adminToken!;
  const full = await (await call(rc, 'GET', `/Users/${u.Id}`, { token: admin })).json();
  const p = await call(rc, 'POST', `/Users/${u.Id}/Policy`, { token: admin, data: { ...full.Policy, IsAdministrator: true } });
  expect(p.status(), short(await p.text())).toBeLessThan(300);

  const r2 = await oidcLogin(browser, 'bob', { expectSuccess: true });
  expect((await me(rc, r2.token!)).Policy.IsAdministrator, 'admin status follows group membership').toBe(false);
  await r2.page.context().close();
});

test('@oidc user outside the allowed groups is denied and not created', async ({ browser }) => {
  const rc = await api();
  // Precondition (proves the flow reached the server): the button exists.
  const r = await oidcLogin(browser, 'carol', { expectSuccess: false });
  expect(r.token, 'no web session for carol').toBeNull();
  expect(r.callbackStatus, 'OIDC callback was reached').not.toBeNull();
  const names = (await listUsers(rc, loadState().adminToken!)).map((u) => u.Name.toLowerCase());
  expect(names).not.toContain('carol');
  await r.page.context().close();
});

test('@oidc LINK_EXISTING_BY_USERNAME=false does not adopt an existing local user', async ({ browser }) => {
  const rc = await api();
  const localDave = loadState().localUsers!.dave;
  const r = await oidcLogin(browser, 'dave', { expectSuccess: false });
  if (r.token) {
    const u = await me(rc, r.token);
    expect(u.Id, 'OIDC dave must not be the pre-existing local dave').not.toBe(localDave);
  }
  // The local user is untouched and still has its password.
  const users = await listUsers(rc, loadState().adminToken!);
  expect(users.find((u) => u.Id === localDave)?.Name).toBe('dave');
  expect((await passwordLogin(rc, 'dave', 'local-dave-pw')).status()).toBe(200);
  const links = (readOidcLinks() ?? '').toLowerCase();
  for (const form of guidForms(localDave)) expect(links, 'local dave must not be linked').not.toContain(form);
  await r.page.context().close();
  // Must not have produced a usable session for the local account either way;
  // and the flow must actually have run (button + callback reached).
  expect(r.callbackStatus, 'OIDC callback was reached').not.toBeNull();
});

test('@oidc users created by OIDC cannot log in with a password', async () => {
  const rc = await api();
  // Precondition: alice exists (created by the OIDC test above).
  const names = (await listUsers(rc, loadState().adminToken!)).map((u) => u.Name);
  expect(names).toContain('alice');
  for (const pw of ['', IDP_USERS.alice.pw]) {
    const r = await passwordLogin(rc, 'alice', pw);
    expect(r.status(), `password login for OIDC user alice with pw='${pw}'`).not.toBe(200);
  }
});
