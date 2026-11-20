import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const playbackProofVideos = Object.freeze(['jNQXAC9IVRw','aqz-KE-bpKQ','YE7VzlLtp-4']);

export function verifyPlaybackProof(proof, sourceSha) {
  const require = (condition) => { if (!condition) throw new Error('Native playback acceptance is incomplete or invalid.'); };
  require(typeof sourceSha === 'string' && /^[a-f0-9]{40}$/.test(sourceSha));
  require(proof && proof.schemaVersion === 2 && proof.sourceSha === sourceSha && proof.status === 'passed');
  require(proof.native === true && proof.authenticated === false && proof.adapter === 'NativePlaybackSource');
  for (const check of ['callbackFailureLatched','failureTeardown','pendingAtReplacement','pendingReadCanceled',
    'retiredCallbackDrained','lateResultIgnored','replacementAdvanced','replacementTeardown'])
    require(proof.lifecycle?.[check] === true);
  require(Array.isArray(proof.results) && proof.results.length === playbackProofVideos.length);
  require(new Set(proof.results.map(r => r?.videoId)).size === playbackProofVideos.length);
  for (const id of playbackProofVideos) {
    const row=proof.results.find(r => r?.videoId === id);
    require(row && row.status === 'passed' && row.disposed === true);
    for (const key of ['videoWidth','videoHeight','audioTracks','videoTracks','durationSeconds','positionBeforeSeek','seekTarget','positionAfterSeek','resourceReads','resourceBytes'])
      require(typeof row[key] === 'number' && Number.isFinite(row[key]) && row[key] > 0);
    require(Number.isFinite(row.initialPosition) && row.initialPosition >= 0 && row.positionBeforeSeek >= row.initialPosition + 1);
    require(row.durationSeconds > row.seekTarget && row.seekTarget >= row.positionBeforeSeek + 2);
    require(row.seekCompleted === true && row.pausedAtSeekLanding === true);
    for (const landing of [row.positionAtSeekLanding, row.positionBeforeResume])
      require(Number.isFinite(landing) && Math.abs(landing - row.seekTarget) <= 0.75);
    require(Math.abs(row.positionBeforeResume - row.positionAtSeekLanding) <= 0.35);
    require(row.positionAfterSeek >= Math.max(row.seekTarget, row.positionBeforeResume) + 1 && row.positionAfterSeek <= row.durationSeconds + 0.5);
    require(typeof row.pausedAdvance === 'number' && Number.isFinite(row.pausedAdvance) && row.pausedAdvance >= 0 && row.pausedAdvance <= 0.35);
  }
  return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv.length !== 4) throw new Error('Expected proof path and exact source SHA.');
    verifyPlaybackProof(JSON.parse(readFileSync(process.argv[2],'utf8')),process.argv[3]);
    console.log('PASS: native audio/video and paused seek for all 3 public test videos; callback failure and pending replacement teardown.');
  } catch {
    console.error('FAIL: native playback acceptance is incomplete or invalid.');
    process.exitCode=1;
  }
}
