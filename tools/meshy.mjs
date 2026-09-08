#!/usr/bin/env node
// Meshy text-to-3D client.
//
// One file, no dependencies, Node 18+. Lives outside any single project because a 3D asset
// pipeline is not a property of the game that happened to need one first.
//
// The two-stage shape is Meshy's, not ours: a `preview` task builds an untextured mesh, and a
// `refine` task paints it. Both cost credits and both take minutes, so everything here is built
// around not paying twice — every task id is written to a job file the moment it is issued, and
// re-running a batch picks up whatever already exists rather than starting again.
//
//   meshy.mjs generate --name sinew --prompt "..." --dir ./out [--texture-prompt "..."]
//   meshy.mjs batch manifest.json --dir ./out [--concurrency 3]
//   meshy.mjs status <task-id>
//   meshy.mjs balance
//
// The API key is never passed on the command line — it comes from MESHY_API_KEY, or from
// ~/.claude/secrets/meshy.key. Anything on a command line ends up in shell history and in process
// listings, which is not where a credential belongs.
//
// In a Claude Code cloud session there is no key here at all, and the script works anyway. See
// `apiKey` below: with no key it sends no Authorization header, and the environment's API
// credential is attached by the proxy on the way out. That requires the credential to list
// `*.meshy.ai` and not just `api.meshy.ai` — the API hands back finished models on
// `assets.meshy.ai`, so a credential scoped to the API host alone starts jobs fine and then
// fails every download.
//
// Endpoints are under `/openapi/`. This is easy to get wrong: `api.meshy.ai/v2/text-to-3d`
// looks right, is what the marketing pages imply, and 404s.

import { readFile, writeFile, mkdir, access } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { homedir } from 'node:os';
import path from 'node:path';

const BASE = 'https://api.meshy.ai/openapi/v2/text-to-3d';
const RIG = 'https://api.meshy.ai/openapi/v1/rigging';
const BALANCE = 'https://api.meshy.ai/openapi/v1/balance';

// ---- key ----

// Returns null in a cloud session, and that is a success rather than a failure.
//
// Locally the key is ours to send. In a Claude Code cloud session it deliberately never enters
// the sandbox at all: the key sits on the environment as an API credential and Anthropic's proxy
// attaches the Authorization header after the request has already left the VM. So there is
// nothing here to read, and there is not meant to be.
//
// Sending our own header anyway would be worse than useless — the proxy adds its own, the request
// arrives carrying two Authorization headers, and Meshy rejects it. Returning null means "say
// nothing about auth and let the proxy speak", which is what `api` below does.
async function apiKey() {
  if (process.env.MESHY_API_KEY?.trim()) return process.env.MESHY_API_KEY.trim();

  const file = path.join(homedir(), '.claude', 'secrets', 'meshy.key');
  try {
    const k = (await readFile(file, 'utf8')).trim();
    if (k) return k;
  } catch { /* no local key: either a cloud session, or genuinely unconfigured */ }

  return null;
}

// ---- http ----

async function api(method, url, body, key) {
  const res = await fetch(url, {
    method,
    headers: {
      // Omitted entirely when there is no local key, so the cloud proxy can attach its own.
      ...(key ? { Authorization: `Bearer ${key}` } : {}),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });

  const text = await res.text();

  if (!res.ok) {
    // The status code is the useful part of a Meshy failure: 402 means out of credits and 429
    // means slow down, and those want completely different responses from the caller.
    throw Object.assign(
      new Error(`Meshy ${method} ${res.status}: ${text.slice(0, 400)}`),
      { status: res.status }
    );
  }

  return text ? JSON.parse(text) : {};
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Poll a task to a terminal state.
 *
 * Backs off from 5s to 20s. Generation takes single-digit minutes and hammering a queue endpoint
 * every second earns a 429, which is a slower way to get the same answer.
 */
async function waitFor(id, key, label, onTick, base = BASE) {
  let delay = 5000;

  for (;;) {
    const task = await api('GET', `${base}/${id}`, null, key);

    if (task.status === 'SUCCEEDED') return task;

    if (task.status === 'FAILED' || task.status === 'CANCELED') {
      throw new Error(`${label} ${task.status}: ${task.task_error?.message || '(no message)'}`);
    }

    onTick?.(task);
    await sleep(delay);
    delay = Math.min(delay * 1.3, 20000);
  }
}

async function download(url, dest) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`download ${res.status} for ${dest}`);

  await mkdir(path.dirname(dest), { recursive: true });
  await pipeline(Readable.fromWeb(res.body), createWriteStream(dest));
  return dest;
}

// ---- job records ----
//
// Written beside the output. A generation is minutes of wall clock and real credits, so the ids
// are persisted the instant they exist: a crash, a Ctrl-C or a rate limit should cost you the wait,
// never the money.

async function jobPath(dir, name) {
  return path.join(dir, '.meshy', `${name}.json`);
}

async function loadJob(dir, name) {
  try {
    return JSON.parse(await readFile(await jobPath(dir, name), 'utf8'));
  } catch {
    return {};
  }
}

async function saveJob(dir, name, job) {
  const p = await jobPath(dir, name);
  await mkdir(path.dirname(p), { recursive: true });
  await writeFile(p, JSON.stringify(job, null, 2));
}

async function exists(p) {
  try { await access(p); return true; } catch { return false; }
}

// ---- the pipeline ----

/**
 * Preview, refine, download. Resumable at every step.
 *
 * `opts` mirrors the API's own names so anything Meshy supports can be passed straight through
 * without this file needing to know about it.
 */
async function generate({ name, prompt, dir, key, log = console.error, ...opts }) {
  const out = path.join(dir, `${name}.glb`);
  const job = await loadJob(dir, name);

  // "Already built" has to mean built *to the standard being asked for*.
  //
  // A first pass that produced a mesh and a texture is done as far as it knew, but a later pass
  // asking for a rig is not satisfied by it — and the naive check skipped the whole job, so adding
  // `rig` to a manifest that had already run would silently do nothing at all. The stages are
  // independently resumable, so the guard has to be too.
  const rigWanted = Boolean(opts.rig);
  const rigHave = !rigWanted || (job.walk && job.run);

  if (await exists(out) && job.done && rigHave) {
    log(`  ${name}: already built`);
    return { name, file: out, skipped: true };
  }

  // ---- preview ----
  if (!job.preview) {
    const body = {
      mode: 'preview',
      prompt,
      should_remesh: true,
      topology: opts.topology ?? 'triangle',
      target_polycount: opts.target_polycount ?? 30000,
      target_formats: ['glb'],
      ...(opts.pose_mode ? { pose_mode: opts.pose_mode } : {}),
      ...(opts.ai_model ? { ai_model: opts.ai_model } : {}),
      ...(opts.symmetry_action ? { symmetry_action: opts.symmetry_action } : {}),
    };

    job.preview = (await api('POST', BASE, body, key)).result;
    job.prompt = prompt;
    await saveJob(dir, name, job);
    log(`  ${name}: preview ${job.preview}`);
  }

  await waitFor(job.preview, key, `${name} preview`,
    (t) => log(`  ${name}: preview ${t.progress}%`));

  // ---- refine ----
  if (!job.refine) {
    const body = {
      mode: 'refine',
      preview_task_id: job.preview,
      enable_pbr: opts.enable_pbr ?? true,
      texture_resolution: opts.texture_resolution ?? '2k',
      target_formats: ['glb'],
      ...(opts.texture_prompt ? { texture_prompt: opts.texture_prompt } : {}),
    };

    job.refine = (await api('POST', BASE, body, key)).result;
    await saveJob(dir, name, job);
    log(`  ${name}: refine ${job.refine}`);
  }

  const done = await waitFor(job.refine, key, `${name} refine`,
    (t) => log(`  ${name}: texturing ${t.progress}%`));

  const glb = done.model_urls?.glb;
  if (!glb) throw new Error(`${name}: finished with no GLB url`);

  await download(glb, out);

  // ---- rig ----
  //
  // Only if asked. Rigging is what turns a statue into a character: it returns a skinned model and,
  // usefully, a set of basic locomotion clips with it — so a walk and a run cost five credits
  // total rather than five plus three per clip through the animation endpoint. For a game that
  // wants exactly "walk" and "run", this stage *is* the animation pipeline.
  if (opts.rig) {
    if (!job.rig) {
      job.rig = (await api('POST', RIG, {
        input_task_id: job.refine,
        height_meters: opts.height_meters ?? 1.7,
      }, key)).result;

      await saveJob(dir, name, job);
      log(`  ${name}: rig ${job.rig}`);
    }

    const rigged = await waitFor(job.rig, key, `${name} rig`,
      (t) => log(`  ${name}: rigging ${t.progress}%`), RIG);

    // Under `result`, not at the top level.
    //
    // The rigging response nests its payload — `result.basic_animations` — where the text-to-3D
    // response puts `model_urls` at the top. Reading the wrong level found nothing and, because the
    // lookup was written defensively, said so instead of silently writing zero-byte files. Both
    // shapes are accepted here so this survives the two endpoints converging later.
    const clips = rigged.result?.basic_animations ?? rigged.basic_animations ?? {};

    // Loose matching on the key names, but the plain GLB in preference to the armature-only
    // variant: `walking_armature_glb_url` is the skeleton without the mesh, which is not what a
    // character wants to be drawn as.
    const pick = (want) => {
      const keys = Object.keys(clips)
        .filter((k) => typeof clips[k] === 'string'
                    && new RegExp(want, 'i').test(k)
                    && /glb/i.test(k))
        .sort((a, b) => (a.includes('armature') ? 1 : 0) - (b.includes('armature') ? 1 : 0));

      return keys.length ? clips[keys[0]] : null;
    };

    const walk = pick('walk');
    const run = pick('run');

    if (walk) await download(walk, path.join(dir, `${name}_walk.glb`));
    if (run) await download(run, path.join(dir, `${name}_run.glb`));

    job.walk = Boolean(walk);
    job.run = Boolean(run);

    if (!walk || !run) {
      log(`  ${name}: WARNING rig returned ${Object.keys(clips).join(', ') || 'no clips'}`);
    } else {
      log(`  ${name}: wrote ${name}_walk.glb and ${name}_run.glb`);
    }
  }

  job.done = true;
  job.credits = done.consumed_credits;
  await saveJob(dir, name, job);

  log(`  ${name}: wrote ${out}`);
  return { name, file: out, credits: done.consumed_credits, rigged: Boolean(job.rig) };
}

/**
 * Run a manifest with bounded concurrency.
 *
 * Bounded because Meshy queues per account: firing twenty at once does not make them finish sooner,
 * it just makes the first failure harder to find. Failures are collected rather than thrown, so one
 * bad prompt does not abandon thirteen good ones that have already been paid for.
 */
async function batch(specs, { dir, key, concurrency = 3 }) {
  const queue = [...specs];
  const okResults = [];
  const failures = [];

  async function worker() {
    for (;;) {
      const spec = queue.shift();
      if (!spec) return;

      try {
        okResults.push(await generate({ ...spec, dir, key }));
      } catch (e) {
        console.error(`  ${spec.name}: FAILED — ${e.message}`);
        failures.push({ name: spec.name, error: e.message });
      }
    }
  }

  await Promise.all(Array.from({ length: Math.min(concurrency, specs.length) }, worker));
  return { ok: okResults, failures };
}

// ---- cli ----

function arg(flag, fallback) {
  const i = process.argv.indexOf(flag);
  return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const USAGE =
  'usage:\n' +
  '  meshy.mjs generate --name <n> --prompt "..." [--dir d] [--texture-prompt "..."]\n' +
  '                     [--pose a-pose] [--polycount 30000] [--texture-res 2k|4k]\n' +
  '  meshy.mjs batch <manifest.json> [--dir d] [--concurrency 3]\n' +
  '  meshy.mjs status <task-id>\n' +
  '  meshy.mjs balance\n';

async function main() {
  const cmd = process.argv[2];

  // Usage before credentials. Asking for an API key in order to be told what the arguments are is
  // a bad first impression of a tool, and it made the no-args smoke test look like a key failure.
  if (!cmd || cmd === '--help' || cmd === '-h') { console.error(USAGE); process.exitCode = 2; return; }

  const key = await apiKey();

  if (cmd === 'balance') {
    console.log(JSON.stringify(await api('GET', BALANCE, null, key), null, 2));
    return;
  }

  if (cmd === 'status') {
    const id = process.argv[3];
    if (!id) throw new Error('usage: meshy.mjs status <task-id>');

    const t = await api('GET', `${BASE}/${id}`, null, key);
    console.log(JSON.stringify(
      { id: t.id, status: t.status, progress: t.progress, error: t.task_error?.message }, null, 2));
    return;
  }

  if (cmd === 'generate') {
    const dir = path.resolve(arg('--dir', '.'));
    const res = await generate({
      name: arg('--name'),
      prompt: arg('--prompt'),
      texture_prompt: arg('--texture-prompt'),
      pose_mode: arg('--pose'),
      target_polycount: Number(arg('--polycount', 30000)),
      texture_resolution: arg('--texture-res', '2k'),
      rig: process.argv.includes('--rig'),
      height_meters: Number(arg('--height', 1.7)),
      dir, key,
    });
    console.log(JSON.stringify(res, null, 2));
    return;
  }

  if (cmd === 'batch') {
    const file = process.argv[3];
    if (!file) throw new Error('usage: meshy.mjs batch <manifest.json> [--dir out] [--concurrency 3]');

    const dir = path.resolve(arg('--dir', '.'));
    const specs = JSON.parse(await readFile(file, 'utf8'));

    const { ok, failures } = await batch(specs, {
      dir, key, concurrency: Number(arg('--concurrency', 3)),
    });

    console.log(JSON.stringify({ built: ok.length, failed: failures.length, failures }, null, 2));
    if (failures.length) process.exitCode = 1;
    return;
  }

  console.error(
    'usage:\n' +
    '  meshy.mjs generate --name <n> --prompt "..." [--dir d] [--texture-prompt "..."]\n' +
    '                     [--pose a-pose] [--polycount 30000] [--texture-res 2k|4k]\n' +
    '  meshy.mjs batch <manifest.json> [--dir d] [--concurrency 3]\n' +
    '  meshy.mjs status <task-id>\n' +
    '  meshy.mjs balance\n'
  );
  process.exitCode = 2;
}

main().catch((e) => { console.error(String(e.message || e)); process.exitCode = 1; });
