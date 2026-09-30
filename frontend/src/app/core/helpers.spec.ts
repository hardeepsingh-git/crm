import { HttpErrorResponse } from '@angular/common/http';
import { toParams } from './api';
import { errorMessage } from './notify.service';
import { HumanizePipe } from '../shared/list';
import { strongPassword } from '../features/auth/change-password-dialog';
import { FormControl } from '@angular/forms';

describe('helpers', () => {
  it('toParams drops empty values', () => {
    const params = toParams({ search: '', mine: false, page: 1, stage: null, owner: undefined, open: true });
    expect(params.keys()).toEqual(['page', 'open']);
    expect(params.get('open')).toBe('true');
  });

  it('humanize splits PascalCase enum values', () => {
    const pipe = new HumanizePipe();
    expect(pipe.transform('ClosedWon')).toBe('Closed Won');
    expect(pipe.transform('ColdCall')).toBe('Cold Call');
    expect(pipe.transform(null)).toBe('');
  });

  it('errorMessage flattens validation problem details', () => {
    const err = new HttpErrorResponse({
      status: 400,
      error: { title: 'Validation', errors: { name: ['Name is required.'], email: ['Invalid email.'] } },
    });
    expect(errorMessage(err)).toBe('Name is required. Invalid email.');
  });

  it('errorMessage maps common statuses', () => {
    expect(errorMessage(new HttpErrorResponse({ status: 0 }))).toContain('Cannot reach');
    expect(errorMessage(new HttpErrorResponse({ status: 403 }))).toContain('permission');
    expect(errorMessage(new HttpErrorResponse({ status: 403, error: { detail: 'Your organisation\'s account is suspended.' } }))).toContain('suspended');
    expect(errorMessage(new HttpErrorResponse({ status: 409, error: 'Account has opportunities.' }))).toBe('Account has opportunities.');
  });

  it('strongPassword mirrors the server password policy', () => {
    expect(strongPassword(new FormControl('weak'))).toEqual({ weakPassword: true });
    expect(strongPassword(new FormControl('alllowercase1!'))).toEqual({ weakPassword: true });
    expect(strongPassword(new FormControl('Str0ng#Password'))).toBeNull();
  });
});
