const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const code = fs.readFileSync(require('node:path').join(__dirname, '../wwwroot/app.js'), 'utf8');
const context = vm.createContext({});
vm.runInContext(code.slice(code.indexOf('let monitorSessionId'), code.indexOf('function cycleChart')), context);
const epoch = 1700000000000;
const sample = (seconds, online, session = 'a', delay = 60) => ({
  timestamp: new Date(epoch + seconds * 1000).toISOString(), online, sessionId: session,
  nextCheckAt: new Date(epoch + (seconds + delay) * 1000).toISOString()
});
const periods = (samples, seconds, session = 'a', cut = epoch) => {
  context.session = session;
  vm.runInContext('monitorSessionId = session', context);
  return context.connectionPeriods(samples, cut, epoch + seconds * 1000);
};
const total = values => values.reduce((n, p) => n + p.duration, 0);
// Deliberate hour sleeps join; no synthetic samples are needed.
assert.equal(total(periods([sample(0, true, 'a', 3600), sample(3600, true)], 3600)), 3600000);
// Restart/suspension sessions must never bridge, even before the old due time.
assert.equal(total(periods([sample(0, true, 'a', 3600), sample(1800, true, 'b')], 1800, 'b')), 0);
// Missing a scheduled check creates a gap.
assert.equal(total(periods([sample(0, true), sample(180, true)], 180)), 0);
// Live scheduled sleep extends only for the active process.
assert.equal(total(periods([sample(0, true, 'a', 3600)], 1800)), 1800000);
assert.equal(total(periods([sample(0, true, 'a', 3600)], 1800, 'b')), 0);
// Late live check must not claim continuous coverage.
assert.equal(total(periods([sample(0, true)], 100)), 0);
const cycle = periods([sample(0, true), sample(60, false), sample(120, false), sample(180, true)], 180);
assert.equal(cycle.find(p => !p.online).duration, 120000);
assert.equal(cycle.find(p => !p.online).complete, true);
assert.equal(cycle[0].complete, false); // Initial period is partial.
assert.equal(total(periods([sample(0, true, 'a', 3600), sample(3600, true)], 3600, 'a', epoch + 1800000)), 1800000);
const legacy = s => ({timestamp: new Date(epoch + s * 1000).toISOString(), online: true});
assert.equal(total(periods([legacy(0), legacy(10)], 10)), 10000);
assert.equal(total(periods([legacy(0), legacy(60)], 60)), 0);
console.log('Passed: scheduled sleep, restart/suspension, missing/late checks, live coverage, failure/recovery, clipping, and legacy records.');
