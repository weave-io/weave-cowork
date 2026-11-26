import { describe, expect, test } from 'vitest'
import { highlight, plain, hasFilter, toggleFilter, resultPath } from './search'

describe('search helpers', () => {
  test('marks become hits and the rest stays text', () => {
    expect(highlight('roundTax \u0002rounds\u0003 each line, so a \u0002refund\u0003 drifts…')).toEqual([
      { text: 'roundTax ', hit: false }, { text: 'rounds', hit: true }, { text: ' each line, so a ', hit: false }, { text: 'refund', hit: true }, { text: ' drifts…', hit: false },
    ])
    expect(highlight('\u0002Rounding\u0003')).toEqual([{ text: 'Rounding', hit: true }])
    expect(highlight('<b>not html</b>')).toEqual([{ text: '<b>not html</b>', hit: false }])
  })
  test("a snippet's markdown reads as text", () => {
    expect(plain('…but **Vatbook does not support partial \u0002refunds\u0003 at all**—so')).toBe('…but Vatbook does not support partial \u0002refunds\u0003 at all—so')
    expect(plain('Rounds VAT *per line* before summing 2. **\u0002Refund\u0003** (`refundTotal` in \u0002refunds\u0003.ts')).toBe('Rounds VAT per line before summing 2. \u0002Refund\u0003 (refundTotal in \u0002refunds\u0003.ts')
    expect(plain('### \u0002Refund\u0003 rounding')).toBe('\u0002Refund\u0003 rounding')
    expect(plain('2 * 3 and snake_case_name stay')).toBe('2 * 3 and snake_case_name stay')
    expect(highlight('**\u0002Refund\u0003**')).toEqual([{ text: 'Refund', hit: true }])
  })
  test('chips add a filter and take it out again', () => {
    expect(toggleFilter('refund rounding', 'is:thread')).toBe('refund rounding is:thread ')
    expect(toggleFilter('', 'from:@reviewer')).toBe('from:@reviewer ')
    expect(toggleFilter('refund from:@Reviewer rounding', 'from:@reviewer')).toBe('refund rounding')
    expect(hasFilter('refund IN:#checkout-app', 'in:#checkout-app')).toBe(true)
    expect(hasFilter('refund in:#checkout-apps', 'in:#checkout-app')).toBe(false)
  })
  test('a result opens the channel at the post, or the thread with the reply', () => {
    expect(resultPath({ channelId: 2, threadId: null, postId: 7 })).toBe('/c/2?post=7')
    expect(resultPath({ channelId: 2, threadId: 5, postId: 9 })).toBe('/c/2/t/5?post=9')
  })
})
