import { UserInfo } from './models';

/** Test fixture: a signed-in user with no permissions unless overridden. */
export const testUser = (overrides: Partial<UserInfo> = {}): UserInfo => ({
  id: 'u1',
  email: 'a@b.c',
  firstName: 'Ada',
  lastName: 'Lovelace',
  fullName: 'Ada Lovelace',
  roles: ['Standard User'],
  tenant: { id: 't1', name: 'Acme', slug: 'acme', currency: 'AUD', template: 'sales' },
  isAdministrator: false,
  isPlatformAdmin: false,
  permissions: {},
  teamUserIds: ['u1'],
  ...overrides,
});
