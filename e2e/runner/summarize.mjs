// Summarise the per-phase Playwright JSON reports and decide the verdict.
//   node summarize.mjs <results-dir> <normal|negative>
// normal:   every test passed (and there was at least one per phase).
// negative: (stock image) every @base test passed and every @oidc test failed,
//           i.e. the suite demonstrably detects a server without the patch.
import fs from 'node:fs';
import path from 'node:path';

const [dir, mode = 'normal'] = process.argv.slice(2);
const phases = ['p1', 'p2', 'p3'];
const rows = [];
let problems = [];

function walk(suite, file) {
  for (const s of suite.suites ?? []) walk(s, s.file ?? file);
  for (const spec of suite.specs ?? []) {
    for (const t of spec.tests ?? []) {
      const r = t.results?.at(-1);
      const passed = t.status === 'expected';
      rows.push({ file, title: spec.title, passed, status: r?.status ?? t.status, error: r?.error?.message?.split('\n')[0] ?? '' });
    }
  }
}

for (const p of phases) {
  const f = path.join(dir, `${p}.json`);
  if (!fs.existsSync(f)) { problems.push(`${p}: no report (phase did not run)`); continue; }
  const rep = JSON.parse(fs.readFileSync(f, 'utf8'));
  const before = rows.length;
  for (const s of rep.suites ?? []) walk(s, s.file);
  if (rows.length === before) problems.push(`${p}: report has no tests`);
  for (const e of rep.errors ?? []) problems.push(`${p}: ${e.message?.split('\n')[0]}`);
}

const tls = path.join(dir, 'tls.txt');
const tlsResult = fs.existsSync(tls) ? fs.readFileSync(tls, 'utf8').trim() : 'not run';
rows.push({ file: 'run.sh', title: '@base jellyfin container trusts the IdP certificate', passed: tlsResult === 'pass', status: tlsResult, error: '' });

const mis = path.join(dir, 'misconfig.txt');
const misResult = fs.existsSync(mis) ? fs.readFileSync(mis, 'utf8').trim() : 'not run';
rows.push({ file: 'run.sh', title: '@oidc ENABLED=true without issuer/client is a startup error', passed: misResult.startsWith('pass'), status: misResult, error: '' });

const strip = (s) => s.replace(/\x1b\[[0-9;]*m/g, '');
for (const r of rows) {
  console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.file}  ${r.title}${r.passed ? '' : `  -- ${strip(r.error || r.status)}`}`);
}

if (mode === 'negative') {
  for (const r of rows) {
    if (r.title.includes('@base') && !r.passed) problems.push(`@base test failed on the control image: ${r.title}`);
    if (r.title.includes('@oidc') && r.passed) problems.push(`@oidc test PASSED on the control image (test cannot fail?): ${r.title}`);
  }
} else {
  for (const r of rows) if (!r.passed) problems.push(`failed: ${r.title}`);
}

const nOidc = rows.filter((r) => r.title.includes('@oidc')).length;
const nBase = rows.filter((r) => r.title.includes('@base')).length;
console.log(`\n${rows.length} checks (${nBase} @base, ${nOidc} @oidc), mode=${mode}`);
if (problems.length) {
  console.log('VERDICT: FAIL');
  for (const p of problems) console.log(`  - ${p}`);
  process.exit(1);
}
console.log(mode === 'negative' ? 'VERDICT: PASS (control image fails every @oidc check, passes every @base check)' : 'VERDICT: PASS');
