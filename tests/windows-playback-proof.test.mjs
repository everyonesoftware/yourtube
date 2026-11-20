import test from 'node:test';
import assert from 'node:assert/strict';
import { verifyPlaybackProof } from '../scripts/verify-playback-proof.mjs';
const sha='a'.repeat(40);
const lifecycle=()=>({callbackFailureLatched:true,failureTeardown:true,pendingAtReplacement:true,pendingReadCanceled:true,
 retiredCallbackDrained:true,lateResultIgnored:true,replacementAdvanced:true,replacementTeardown:true});
const valid=()=>({schemaVersion:2,lifecycle:lifecycle(),sourceSha:sha,status:'passed',native:true,adapter:'NativePlaybackSource',authenticated:false,results:['jNQXAC9IVRw','aqz-KE-bpKQ','YE7VzlLtp-4'].map(videoId=>({videoId,status:'passed',videoWidth:640,videoHeight:360,audioTracks:1,videoTracks:1,durationSeconds:19,initialPosition:0,positionBeforeSeek:1.5,seekTarget:6,seekCompleted:true,pausedAtSeekLanding:true,positionAtSeekLanding:6,positionBeforeResume:6.02,positionAfterSeek:7.5,pausedAdvance:0.05,resourceReads:4,resourceBytes:100000,disposed:true}))});
test('proof requires native audio/video playback, source identity, seek/pause and disposal for every public test video',()=>{
 assert.equal(verifyPlaybackProof(valid(),sha),true);
 for(const change of [p=>p.native=false,p=>p.status='failed',p=>p.sourceSha='b'.repeat(40),p=>p.authenticated=true,p=>p.adapter='fake',p=>p.results.pop(),p=>p.results.push(p.results[0]),p=>p.results[1].videoId=p.results[0].videoId,p=>p.results[0].audioTracks=0,p=>p.results[0].videoTracks=0,p=>p.results[0].videoWidth=0,p=>p.results[0].videoHeight=0,p=>p.results[0].positionBeforeSeek=0,p=>p.results[0].seekTarget=0,p=>p.results[0].positionAfterSeek=6,p=>p.results[0].pausedAdvance=1,p=>p.results[0].resourceReads=0,p=>p.results[0].resourceBytes=0,p=>p.results[0].disposed=false,p=>p.results[0].status='failed',p=>p.results[0].durationSeconds=0]){
  const proof=valid();change(proof);assert.throws(()=>verifyPlaybackProof(proof,sha));
 }
 for(const value of [null,{},[],{...valid(),results:null},{...valid(),results:[null]}, {...valid(),results:valid().results.map(r=>({...r,positionAfterSeek:'8'}))}])assert.throws(()=>verifyPlaybackProof(value,sha));
});

test('ordinary advancement cannot substitute for a completed seek and paused landing',()=>{
 for(const [name, fields] of Object.entries({
  'missing seek event': { seekCompleted: undefined },
  'already advanced at open': { initialPosition:1.5 },
  'missing landing': { positionAtSeekLanding: undefined },
  'landing already past target': { positionAtSeekLanding: 7.5 },
  'landing too short': { positionAtSeekLanding: 4.9 },
  'small seek indistinguishable from drift': { positionBeforeSeek: 5.8 },
  'not paused at landing': { pausedAtSeekLanding: false },
  'unstable landing': { positionBeforeResume: 6.8 },
  'position did not advance after resume': { positionAtSeekLanding: 6.55, positionBeforeResume: 6.7, positionAfterSeek: 7.1 },
 })) {
  const proof=valid();
  Object.assign(proof.results[0], fields);
  assert.throws(()=>verifyPlaybackProof(proof,sha), name);
 }
});

test('native proof requires callback failure teardown and successful replacement of a pending callback',()=>{
 assert.throws(()=>verifyPlaybackProof({...valid(),schemaVersion:1},sha),'old proof schema');
 assert.throws(()=>verifyPlaybackProof({...valid(),lifecycle:undefined},sha),'missing lifecycle evidence');
 for(const key of Object.keys(lifecycle()))
  assert.throws(()=>verifyPlaybackProof({...valid(),lifecycle:{...lifecycle(),[key]:false}},sha),key);
});
