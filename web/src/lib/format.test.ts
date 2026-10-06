import { date, money, todayInLima } from './format'

describe('format', () => {
  it('formats money with the symbol of the currency', () => {
    expect(money(1234.5)).toBe('S/ 1,234.50')
    expect(money(10, 'USD')).toBe('US$ 10.00')
  })

  it('takes the civil date in Lima, not the one of the browser', () => {
    // 03:00 UTC is 22:00 of the previous day in Lima (UTC-5).
    expect(todayInLima(new Date('2026-10-07T03:00:00Z'))).toBe('2026-10-06')
    expect(todayInLima(new Date('2026-10-07T06:00:00Z'))).toBe('2026-10-07')
  })

  it('formats a civil date without shifting the day', () => {
    expect(date('2026-10-06')).toBe('06/10/2026')
    expect(date(null)).toBe('—')
  })
})
