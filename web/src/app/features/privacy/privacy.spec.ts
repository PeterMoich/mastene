import { PrivacyTenant } from '../../core/api.models';
import { erasureText } from './privacy';

const t = (o: Partial<PrivacyTenant>): PrivacyTenant => ({
  id: 't', name: 'Johan van der Merwe', email: 'j@example.com', isDeleted: false, anonymisedAt: null,
  lastLeaseEndedOn: null, erasableFrom: null, allowedActions: ['export'], ...o,
});

describe('erasureText', () => {
  it('says plainly whether and when details may be erased', () => {
    expect(erasureText(t({ allowedActions: ['export', 'anonymise'] }))).toBe('Can be erased');
    expect(erasureText(t({ anonymisedAt: '2026-11-04T10:00:00Z' }))).toBe('Details erased');
    expect(erasureText(t({ lastLeaseEndedOn: '2026-01-31', erasableFrom: '2031-01-31' }), '2026-11-04')).toBe('Keep until 2031-01-31');
    expect(erasureText(t({}))).toBe('Lease running, or money owed');
  });
});
