// Phase 2: same server data, restarted with JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN=true.
import { test, expect } from '@playwright/test';
import {
  ADMIN, OPERATOR_CSS, api, apiKeyWorks, brandingCss, call, gotoLoginPage, loadState, me,
  oidcButton, oidcLogin, passwordLogin, quickConnect, short, waitForIdp, waitForJellyfin,
} from './lib';

test.beforeAll(async () => {
  const rc = await api();
  await waitForIdp(rc);
  await waitForJellyfin(rc);
  expect(loadState().adminToken, 'phase 1 state (admin session)').toBeTruthy();
});

test('@base API key still works', async () => {
  await apiKeyWorks(await api(), loadState().apiKey!);
});

test('@base existing sessions keep working', async () => {
  const u = await me(await api(), loadState().adminToken!);
  expect(u.Name).toBe(ADMIN.name);
});

test('@base Quick Connect approved from an existing session works', async () => {
  const rc = await api();
  const t = await quickConnect(rc, loadState().adminToken!);
  expect((await me(rc, t)).Name).toBe(ADMIN.name);
});

test('@oidc POST /Users/AuthenticateByName is rejected, also for administrators', async () => {
  const rc = await api();
  for (const [name, pw] of [[ADMIN.name, ADMIN.pw], ['dave', 'local-dave-pw']]) {
    const r = await passwordLogin(rc, name, pw);
    expect(r.status(), `password login for ${name}: ${short(await r.text())}`).toBeGreaterThanOrEqual(400);
  }
});

test('@oidc legacy POST /Users/{id}/Authenticate is rejected', async () => {
  const rc = await api();
  const r = await call(rc, 'POST', `/Users/${loadState().adminId}/Authenticate?pw=${encodeURIComponent(ADMIN.pw)}`);
  expect(r.status(), short(await r.text())).toBeGreaterThanOrEqual(400);
});

test('@oidc forgot-password endpoints are rejected', async () => {
  const rc = await api();
  const r1 = await call(rc, 'POST', '/Users/ForgotPassword', { data: { EnteredUsername: ADMIN.name } });
  expect(r1.status(), `ForgotPassword: ${short(await r1.text())}`).toBeGreaterThanOrEqual(400);
  const r2 = await call(rc, 'POST', '/Users/ForgotPassword/Pin', { data: { Pin: '1234' } });
  expect(r2.status(), `ForgotPassword/Pin: ${short(await r2.text())}`).toBeGreaterThanOrEqual(400);
});

test('@oidc password change endpoints are rejected', async () => {
  const rc = await api();
  const s = loadState();
  // NewPw == CurrentPw so that a stock server accepting it changes nothing.
  const body = { CurrentPw: ADMIN.pw, NewPw: ADMIN.pw };
  // Precondition: the session is valid, so a 4xx below is a real rejection.
  await me(rc, s.adminToken!);
  const r1 = await call(rc, 'POST', `/Users/Password?userId=${s.adminId}`, { token: s.adminToken, data: body });
  expect(r1.status(), `POST /Users/Password: ${short(await r1.text())}`).toBeGreaterThanOrEqual(400);
  expect(r1.status(), 'rejected as forbidden, not as unauthenticated').not.toBe(401);
  const r2 = await call(rc, 'POST', `/Users/${s.adminId}/Password`, { token: s.adminToken, data: body });
  expect(r2.status(), `POST /Users/{id}/Password: ${short(await r2.text())}`).toBeGreaterThanOrEqual(400);
  expect(r2.status()).not.toBe(401);
});

test('@oidc login page: button visible, password form and forgot-password hidden', async ({ page }) => {
  await gotoLoginPage(page);
  await expect(oidcButton(page)).toBeVisible({ timeout: 15_000 });
  // All users are hidden, so the stock page shows the manual form; the fork's
  // CSS must hide it.
  await expect(page.locator('#txtManualName')).toBeHidden();
  await expect(page.locator('#txtManualPassword')).toBeHidden();
  await expect(page.locator('.manualLoginForm .button-submit')).toBeHidden();
  await expect(page.locator('.btnForgotPassword')).toBeHidden();
  await expect(page.locator('.btnManual')).toBeHidden();
  expect(await brandingCss(await api())).toContain(OPERATOR_CSS);
});

test('@oidc OIDC login works and Quick Connect approved from the OIDC session works', async ({ browser }) => {
  const rc = await api();
  const r = await oidcLogin(browser, 'alice', { expectSuccess: true });
  expect((await me(rc, r.token!)).Name).toBe('alice');
  // A TV starts Quick Connect unauthenticated; alice approves from her
  // OIDC browser session; the TV exchanges the secret for its own token.
  const tvToken = await quickConnect(rc, r.token!);
  const tv = await me(rc, tvToken);
  expect(tv.Name).toBe('alice');
  expect(tvToken).not.toBe(r.token);
  // The new API key path also works from an OIDC admin session.
  expect((await call(rc, 'POST', '/Auth/Keys?app=e2e-phase2', { token: r.token! })).status()).toBeLessThan(300);
  await r.page.context().close();
});
