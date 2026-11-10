// Shared helpers for the jellyfin-oidc end-to-end tests.
//
// Test titles carry a tag:
//   @base  must pass against stock Jellyfin too (setup, API keys, Quick Connect
//          approved by a pre-existing session, ...)
//   @oidc  exercises the fork; MUST fail against stock Jellyfin (negative control)
import { expect, request as pwRequest, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

export const JF = 'http://localhost:8096';
export const IDP = 'https://idp:8443/realms/e2e';
export const PROVIDER_NAME = 'E2E IdP';
export const ADMIN = { name: 'admin', pw: 'e2e-admin-password' };
export const OPERATOR_DISCLAIMER = 'E2E operator disclaimer';
export const OPERATOR_CSS = '/* e2e operator css */';

// Must match e2e/keycloak/realm-e2e.json.
export const IDP_USERS = {
  alice: { pw: 'alice-password', sub: '00000000-0000-4000-a000-00000000a11c' }, // users + admins
  bob: { pw: 'bob-password', sub: '00000000-0000-4000-a000-000000000b0b' }, // users
  carol: { pw: 'carol-password', sub: '00000000-0000-4000-a000-00000000ca10' }, // no groups
  dave: { pw: 'dave-password', sub: '00000000-0000-4000-a000-00000000da7e' }, // local "dave" exists; link off
  erin: { pw: 'erin-password', sub: '00000000-0000-4000-a000-00000000e214' }, // local "erin" exists; link on
} as const;
export type IdpUser = keyof typeof IDP_USERS;

const RESULTS = process.env.E2E_RESULTS ?? '/results';
const STATE_FILE = path.join(RESULTS, 'state.json');

export type State = {
  adminToken?: string; // session from password login in phase 1
  adminId?: string;
  apiKey?: string;
  localUsers?: Record<string, string>; // name -> id, created by the admin in phase 1
  erinLocalToken?: string; // erin's own password session, from before her account is adopted (phase 3)
};

export function loadState(): State {
  try {
    return JSON.parse(fs.readFileSync(STATE_FILE, 'utf8')) as State;
  } catch {
    return {};
  }
}

export function saveState(patch: Partial<State>): State {
  const s = { ...loadState(), ...patch };
  fs.writeFileSync(STATE_FILE, JSON.stringify(s, null, 2));
  return s;
}

export function authHeader(token?: string, deviceId = 'e2e-runner'): string {
  let h = `MediaBrowser Client="jellyfin-oidc-e2e", Device="e2e", DeviceId="${deviceId}", Version="1.0.0"`;
  if (token) h += `, Token="${token}"`;
  return h;
}

export async function api(): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL: JF, ignoreHTTPSErrors: true });
}

type CallOpts = { token?: string; data?: unknown; deviceId?: string };

export async function call(rc: APIRequestContext, method: string, url: string, o: CallOpts = {}) {
  return rc.fetch(url, {
    method,
    headers: { Authorization: authHeader(o.token, o.deviceId), 'Content-Type': 'application/json' },
    data: o.data === undefined ? undefined : JSON.stringify(o.data),
    failOnStatusCode: false,
    maxRedirects: 0,
  });
}

// Jellyfin first answers from a temporary startup server and then briefly
// refuses connections while the real one starts, so require /health to be
// "Healthy" several times in a row.
export async function waitForJellyfin(rc: APIRequestContext, timeoutMs = 180_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let last = '';
  let streak = 0;
  while (Date.now() < deadline) {
    try {
      const r = await rc.get('/health', { failOnStatusCode: false, timeout: 5000 });
      const body = await r.text();
      if (r.ok() && body.includes('Healthy')) {
        if (++streak >= 3) return;
      } else {
        streak = 0;
        last = `HTTP ${r.status()} ${short(body)}`;
      }
    } catch (e) {
      streak = 0;
      last = String(e);
    }
    await new Promise((res) => setTimeout(res, 1500));
  }
  throw new Error(`jellyfin not ready after ${timeoutMs}ms: ${last}`);
}

export async function waitForIdp(rc: APIRequestContext, timeoutMs = 180_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const r = await rc.get(`${IDP}/.well-known/openid-configuration`, { failOnStatusCode: false, timeout: 5000 });
      if (r.ok()) return;
    } catch {
      /* not up yet */
    }
    await new Promise((res) => setTimeout(res, 2000));
  }
  throw new Error('IdP discovery document not reachable');
}

// Jellyfin revokes a device's previous token when the same DeviceId logs in
// again, so every login gets its own DeviceId unless one is given.
let deviceSeq = 0;
export function newDeviceId(prefix = 'e2e'): string {
  return `${prefix}-${process.pid}-${Date.now()}-${deviceSeq++}`;
}

export async function passwordLogin(rc: APIRequestContext, name: string, pw: string, deviceId = newDeviceId('pw')) {
  return call(rc, 'POST', '/Users/AuthenticateByName', { data: { Username: name, Pw: pw }, deviceId });
}

export function short(s: string, n = 300): string {
  return s.length > n ? `${s.slice(0, n)}...` : s;
}

export async function me(rc: APIRequestContext, token: string) {
  const r = await call(rc, 'GET', '/Users/Me', { token });
  expect(r.status(), `/Users/Me with token: ${short(await r.text())}`).toBe(200);
  return (await r.json()) as { Id: string; Name: string; Policy: { IsAdministrator: boolean } };
}

export async function listUsers(rc: APIRequestContext, adminToken: string) {
  const r = await call(rc, 'GET', '/Users', { token: adminToken });
  expect(r.status()).toBe(200);
  return (await r.json()) as Array<{ Id: string; Name: string; Policy: { IsAdministrator: boolean } }>;
}

// Find a Jellyfin access token the stock web client has stored in localStorage
// (the "jellyfin_credentials" entry, { Servers: [{ AccessToken, UserId }] }).
export async function webClientSession(page: Page): Promise<{ token: string; userId?: string } | null> {
  return page.evaluate(() => {
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i)!;
      try {
        const v = JSON.parse(localStorage.getItem(k) ?? 'null');
        for (const s of v?.Servers ?? []) {
          if (s?.AccessToken) return { token: s.AccessToken as string, userId: s.UserId as string | undefined };
        }
      } catch {
        /* not JSON */
      }
    }
    return null;
  });
}

export async function gotoLoginPage(page: Page): Promise<void> {
  await page.goto(`${JF}/web/`);
  // The login page renders the (augmented) disclaimer once branding is loaded.
  await page.locator('#loginPage').first().waitFor({ state: 'attached', timeout: 60_000 });
  await page.waitForLoadState('networkidle');
}

export function oidcButton(page: Page) {
  return page.locator('.loginDisclaimer').getByText(`Sign in with ${PROVIDER_NAME}`);
}

export type OidcResult = {
  token: string | null; // web client session after the flow, if any
  callbackStatus: number | null;
  finalUrl: string;
};

// Full browser login: stock login page -> button -> IdP form -> callback ->
// web client. Uses a fresh browser context (no cookies, no localStorage).
export async function oidcLogin(browser: Browser, user: IdpUser, opts: { expectSuccess: boolean }): Promise<OidcResult & { page: Page }> {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  let callbackStatus: number | null = null;
  page.on('response', (r) => {
    if (r.url().startsWith(`${JF}/oidc/callback`)) callbackStatus = r.status();
  });

  await gotoLoginPage(page);
  const btn = oidcButton(page);
  await expect(btn, 'OIDC sign-in button on the stock login page').toBeVisible({ timeout: 15_000 });
  await btn.click();

  await page.waitForURL((u) => u.toString().startsWith(IDP), { timeout: 30_000 });
  await page.locator('#username').fill(user);
  await page.locator('#password').fill(IDP_USERS[user].pw);
  await page.locator('#kc-login').click();

  await page.waitForURL((u) => u.toString().startsWith(JF), { timeout: 30_000 });

  let session: { token: string } | null = null;
  if (opts.expectSuccess) {
    // The callback page hands the session to the web client, which then shows
    // the logged-in UI (user button in the header).
    await expect
      .poll(async () => (await webClientSession(page))?.token ?? null, { timeout: 30_000, message: 'web client session token' })
      .not.toBeNull();
    // User menu button: modern layout (default in 12.x) or legacy layout.
    // Both headers exist in the DOM; only the active layout's is visible.
    const userMenu = page
      .locator('button[aria-controls="app-user-menu"], .headerUserButton:not(.hide)')
      .filter({ visible: true })
      .first();
    await expect(userMenu, 'logged-in header (user menu button)').toBeVisible({ timeout: 30_000 });
    expect(page.url(), 'web client left the login page').not.toMatch(/login/i);
    session = await webClientSession(page);
  } else {
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(3000);
    session = await webClientSession(page);
  }
  return { page, token: session?.token ?? null, callbackStatus, finalUrl: page.url() };
}

export async function brandingConfig(rc: APIRequestContext) {
  const r = await call(rc, 'GET', '/Branding/Configuration');
  expect(r.status()).toBe(200);
  return (await r.json()) as { LoginDisclaimer?: string; CustomCss?: string };
}

export async function brandingCss(rc: APIRequestContext) {
  const r = await call(rc, 'GET', '/Branding/Css');
  expect([200, 204]).toContain(r.status());
  return r.text();
}

export function readOidcLinks(): string | null {
  // The runner mounts Jellyfin's /config at /jfconfig; config dir is /config/config.
  const p = '/jfconfig/config/oidc-links.json';
  return fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null;
}

export async function apiKeyWorks(rc: APIRequestContext, key: string) {
  const r = await call(rc, 'GET', '/System/Info', { token: key });
  expect(r.status(), `GET /System/Info with API key: ${short(await r.text())}`).toBe(200);
  const r2 = await call(rc, 'GET', '/Users', { token: key });
  expect(r2.status(), 'GET /Users with API key').toBe(200);
}

// Quick Connect as a TV would do it: initiate unauthenticated, have `approver`
// (an existing session token) authorize the code, then exchange the secret.
export async function quickConnect(rc: APIRequestContext, approverToken: string) {
  const deviceId = newDeviceId('tv');
  const init = await call(rc, 'POST', '/QuickConnect/Initiate', { deviceId });
  expect(init.status(), `QuickConnect/Initiate: ${short(await init.text())}`).toBe(200);
  const { Secret, Code } = (await init.json()) as { Secret: string; Code: string };

  const auth = await call(rc, 'POST', `/QuickConnect/Authorize?code=${encodeURIComponent(Code)}`, { token: approverToken });
  expect(auth.status(), `QuickConnect/Authorize: ${short(await auth.text())}`).toBe(200);

  const state = await call(rc, 'GET', `/QuickConnect/Connect?secret=${encodeURIComponent(Secret)}`, { deviceId });
  expect(state.status()).toBe(200);
  expect(((await state.json()) as { Authenticated: boolean }).Authenticated).toBe(true);

  const ex = await call(rc, 'POST', '/Users/AuthenticateWithQuickConnect', { data: { Secret }, deviceId });
  expect(ex.status(), `AuthenticateWithQuickConnect: ${short(await ex.text())}`).toBe(200);
  const token = ((await ex.json()) as { AccessToken: string }).AccessToken;
  expect(token).toBeTruthy();
  return token;
}

// A Jellyfin user id in the spellings it may appear in on disk.
export function guidForms(id: string): string[] {
  const n = id.replace(/-/g, '').toLowerCase();
  const d = `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
  return [n, d];
}
