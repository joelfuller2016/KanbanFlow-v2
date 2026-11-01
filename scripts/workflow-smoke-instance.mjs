// Preparation and read-only target proof for the workflow mutation harness (#522).
// This module never starts a server or removes a database.
import { randomUUID } from 'node:crypto';
import { lstat, mkdir, readFile, realpath, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../', import.meta.url));
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
const samePath = (a, b) => comparable(path.resolve(a)) === comparable(path.resolve(b));

export function smokeBase(value) {
  let url;
  try { url = new URL(value); } catch { throw new Error('Supply a valid disposable loopback HTTP URL.'); }
  if (!['http:', 'https:'].includes(url.protocol) || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)
      || !url.port || Number(url.port) < 1024 || url.port === '5123' || url.username || url.password
      || url.pathname !== '/' || url.search || url.hash)
    throw new Error('Use a disposable loopback origin with an explicit non-default port, never the normal app on 5123.');
  return url.origin;
}

async function unlinkedPath(target, root) {
  const relative = path.relative(root, target);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Smoke files must remain inside this repository.');
  let current = root;
  for (const part of relative.split(path.sep)) {
    current = path.join(current, part);
    let info;
    try { info = await lstat(current); } catch (error) { if (error.code === 'ENOENT') return; throw error; }
    if (info.isSymbolicLink() || (!info.isDirectory() && (!info.isFile() || info.nlink !== 1)))
      throw new Error('Smoke files must not use linked directories, symbolic links or hard links.');
  }
}

export async function prepareSmokeInstance(base, root = repositoryRoot) {
  const baseUrl = smokeBase(base);
  root = await realpath(root);
  const runId = randomUUID();
  const directory = path.join(root, '_sandbox', 'workflow-smoke', runId);
  await unlinkedPath(directory, root);
  await mkdir(directory, { recursive: true });
  const manifestPath = path.join(directory, 'run.json');
  const manifest = { runId, baseUrl, databasePath: path.join(directory, 'kanbanflow.db'),
    createdAtUtc: new Date().toISOString(), state: 'prepared' };
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2), { flag: 'wx' });
  return { ...manifest, manifestPath };
}

export async function validateSmokeInstance(base, manifestPath, { root = repositoryRoot, fetchImpl = fetch } = {}) {
  const baseUrl = smokeBase(base);
  if (!manifestPath) throw new Error('Prepare a disposable instance and pass --instance <run.json> before running the workflow smoke test.');
  root = await realpath(root);
  manifestPath = path.resolve(manifestPath);
  await unlinkedPath(manifestPath, root);
  const info = await lstat(manifestPath);
  if (!info.isFile() || info.size > 8192) throw new Error('Invalid workflow smoke manifest.');
  let manifest;
  try { manifest = JSON.parse(await readFile(manifestPath, 'utf8')); } catch { throw new Error('Invalid workflow smoke manifest JSON.'); }
  if (!manifest || !uuid.test(manifest.runId) || manifest.state !== 'prepared' || manifest.baseUrl !== baseUrl
      || !Number.isFinite(Date.parse(manifest.createdAtUtc))) throw new Error('The smoke manifest is not a prepared run for this target.');
  const directory = path.join(root, '_sandbox', 'workflow-smoke', manifest.runId);
  const databasePath = path.join(directory, 'kanbanflow.db');
  if (!samePath(manifestPath, path.join(directory, 'run.json')) || typeof manifest.databasePath !== 'string'
      || !samePath(manifest.databasePath, databasePath)) throw new Error('The smoke manifest does not identify its owned disposable database.');
  await unlinkedPath(databasePath, root);
  const dbFile = await lstat(databasePath);
  if (!dbFile.isFile() || dbFile.size === 0 || !samePath(await realpath(databasePath), databasePath))
    throw new Error('Start the disposable app with the manifest database before running its smoke test.');
  try { await lstat(path.join(directory, 'run.lock')); throw new Error('This smoke run was already started; preserve its evidence and prepare a new instance.'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }

  // Existing health opens SQLite read-only and reports the actual DbPathResolver result.
  // A loopback port alone cannot establish which database a process has opened.
  const response = await fetchImpl(`${baseUrl}/api/admin/health`, {
    method: 'GET', redirect: 'error', signal: AbortSignal.timeout(10000), headers: { Accept: 'application/json' }
  });
  if (!response.ok) throw new Error(`Disposable instance identity could not be read (HTTP ${response.status}).`);
  const health = await response.json();
  if (!health || health.app !== 'KanbanFlow' || health.db?.source !== 'env' || health.db?.exists !== true
      || health.db?.existedAtStartup !== false || health.db?.status !== 'ok'
      || typeof health.db?.path !== 'string' || !samePath(health.db.path, databasePath)
      || !Number.isFinite(Date.parse(health.startedAtUtc)) || Date.parse(health.startedAtUtc) < Date.parse(manifest.createdAtUtc))
    throw new Error('Target identity mismatch: expected a fresh app explicitly using this run’s disposable database. No workflow writes were started.');
  return { ...manifest, manifestPath, databasePath, baseUrl, startedAtUtc: health.startedAtUtc };
}

export async function recordSmokeRun(run, state, details = {}) {
  await writeFile(run.manifestPath, JSON.stringify({ ...run, state, ...details, recordedAtUtc: new Date().toISOString() }, null, 2));
}

export async function claimSmokeRun(run) {
  await writeFile(path.join(path.dirname(run.manifestPath), 'run.lock'), JSON.stringify({ pid: process.pid, startedAtUtc: new Date().toISOString() }), { flag: 'wx' });
  await recordSmokeRun(run, 'running');
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  if (process.argv[2] !== 'prepare' || process.argv.length !== 4)
    throw new Error('Usage: node scripts/workflow-smoke-instance.mjs prepare http://127.0.0.1:5126');
  console.log(JSON.stringify(await prepareSmokeInstance(process.argv[3]), null, 2));
}
