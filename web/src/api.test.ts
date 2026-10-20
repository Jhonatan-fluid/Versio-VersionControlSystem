import { describe, expect, it } from 'vitest';
import { readWriteAnswer } from './api';

// A write's answer (docs/web/PLAN.md, C5): only this server's own answer can say a write happened
// or was refused. Anything else keeps the operation key, because GitHub may already have done it.

describe('readWriteAnswer', () => {
  it("takes this server's outcomes on a 2xx", () => {
    expect(readWriteAnswer(200, { outcome: 'done', id: 'IC_1' })).toMatchObject({ outcome: 'done', id: 'IC_1' });
    expect(readWriteAnswer(202, { outcome: 'pending', message: 'Still being posted.' })).toMatchObject({ outcome: 'pending' });
    expect(readWriteAnswer(202, { outcome: 'unknown' })).toMatchObject({ outcome: 'unknown' });
  });

  it("takes a refusal only as this server words one: a 4xx naming its error", () => {
    const refused = readWriteAnswer(422, { error: 'github-refused', message: 'GitHub refused this: Validation Failed' });
    expect(refused.outcome).toBeUndefined();
    expect(refused).toMatchObject({ error: 'github-refused', message: 'GitHub refused this: Validation Failed' });
    expect(readWriteAnswer(409, { error: 'snapshot-stale' }).error).toBe('snapshot-stale');
  });

  // Found by review (2026-10-20): null threw, and {} on a 200 read as a refusal.
  for (const [what, status, body] of [
    ['JSON null', 200, null],
    ['{} on a 200', 200, {}],
    ['an array', 200, []],
    ['a number', 200, 7],
    ['nothing parseable', 504, undefined],
    ['an error on a 5xx', 500, { error: 'internal' }],
    ['an outcome it does not know', 200, { outcome: 'maybe' }],
    ['an outcome on a 5xx', 502, { outcome: 'done' }],
    ['an error that is not text', 422, { error: 42 }],
  ] as const) {
    it(`reads ${what} as unknown, never as done or refused`, () => {
      const answer = readWriteAnswer(status, body);
      expect(answer.outcome).toBe('unknown');
      expect(answer.error).toBeUndefined();
      expect(answer.message).toContain('may or may not');
    });
  }

  it("keeps a body's other fields, so the Markdown preview still has its html", () => {
    expect(readWriteAnswer(200, { html: '<p>x</p>' })).toMatchObject({ html: '<p>x</p>' });
  });
});
