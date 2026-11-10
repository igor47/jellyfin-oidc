// Phase 3: same server data, restarted with JELLYFIN_OIDC_LINK_EXISTING_BY_USERNAME=true
// (password login enabled again).
import { test, expect } from '@playwright/test';
import {
  IDP_USERS, api, call, guidForms, listUsers, loadState, me, oidcLogin, passwordLogin, readOidcLinks, waitForIdp, waitForJellyfin,
} from './lib';

test.beforeAll(async () => {
  const rc = await api();
  await waitForIdp(rc);
  await waitForJellyfin(rc);
  expect(loadState().localUsers?.erin, 'phase 1 state (local erin)').toBeTruthy();
});

test('@oidc LINK_EXISTING_BY_USERNAME=true adopts the existing local user', async ({ browser }) => {
  const rc = await api();
  const localErin = loadState().localUsers!.erin;
  const r = await oidcLogin(browser, 'erin', { expectSuccess: true });
  const u = await me(rc, r.token!);
  expect(u.Id, 'OIDC erin is the pre-existing local erin').toBe(localErin);
  const links = (readOidcLinks() ?? '').toLowerCase();
  expect(links).toContain(IDP_USERS.erin.sub);
  expect(guidForms(localErin).some((f) => links.includes(f)), 'link points at local erin').toBe(true);
  await r.page.context().close();

  // Adoption revokes the account's previous sessions and replaces its password.
  const old = await call(rc, 'GET', '/Users/Me', { token: loadState().erinLocalToken });
  expect(old.status(), "erin's pre-adoption session is revoked").toBe(401);
  expect((await passwordLogin(rc, 'erin', 'local-erin-pw')).status(), 'old local password no longer works').not.toBe(200);
});

test('@oidc an existing link still wins over username matching', async ({ browser }) => {
  // alice was created and linked in phase 1; logging in again must land in the same account.
  const rc = await api();
  const r = await oidcLogin(browser, 'alice', { expectSuccess: true });
  expect((await me(rc, r.token!)).Name).toBe('alice');
  await r.page.context().close();
});

test('@oidc a linked identity whose Jellyfin user was deleted is refused, not re-created', async ({ browser }) => {
  const rc = await api();
  const admin = loadState().adminToken!;
  const bob = (await listUsers(rc, admin)).find((u) => u.Name === 'bob');
  expect(bob, 'bob was created by OIDC in phase 1').toBeTruthy();
  expect((await call(rc, 'DELETE', `/Users/${bob!.Id}`, { token: admin })).status()).toBeLessThan(300);

  const r = await oidcLogin(browser, 'bob', { expectSuccess: false });
  expect(r.callbackStatus, 'OIDC callback was reached').not.toBeNull();
  expect(r.token, 'no session for bob').toBeNull();
  expect((await listUsers(rc, admin)).map((u) => u.Name)).not.toContain('bob');
  await r.page.context().close();
});
