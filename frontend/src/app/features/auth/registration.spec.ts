import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Login } from './login';
import { PHONE_PATTERN, Register, Verify } from './registration';
import { PlatformSettingsPage } from '../platform/platform-settings';

describe('Self-registration pages', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(component);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture;
  }

  /** httpResource publishes responses asynchronously; call once no request is left open. */
  async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function flushOptions(available: boolean, email = false, sms = false, tenant = 'acme'): void {
    const req = backend.expectOne((r) => r.url === '/api/auth/registration-options');
    expect(req.request.params.get('tenant')).toBe(tenant);
    req.flush({ available, tenantName: available ? 'Acme Pty Ltd' : null, emailVerification: email, smsVerification: sms });
  }

  it('explains when an organisation does not accept registrations', async () => {
    const fixture = render(Register, { tenantCode: 'acme' });
    flushOptions(false);
    await settle(fixture);
    expect(fixture.nativeElement.textContent).toContain("Self-registration isn't available");
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('registers, requires the mobile when SMS verification is on, and continues to verification', async () => {
    const fixture = render(Register, { tenantCode: 'acme' });
    flushOptions(true, true, true);
    await settle(fixture);
    expect(fixture.nativeElement.textContent).toContain('Acme Pty Ltd');

    const page = fixture.componentInstance;
    page.form.setValue({ firstName: ' Nina ', lastName: 'New', email: 'nina@acme.test', phoneNumber: '', password: 'Str0ng#Password', confirm: 'Str0ng#Password' });
    page.submit();
    backend.expectNone('/api/auth/register');
    expect(page.form.controls.phoneNumber.hasError('required')).toBe(true);

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    page.form.controls.phoneNumber.setValue('+61 412 345 678');
    page.submit();
    const req = backend.expectOne('/api/auth/register');
    expect(req.request.body).toEqual({
      tenantCode: 'acme', email: 'nina@acme.test', firstName: 'Nina', lastName: 'New', phoneNumber: '+61 412 345 678', password: 'Str0ng#Password',
    });
    req.flush({ verification: ['Email', 'Sms'], message: 'codes sent' });
    expect(navigate).toHaveBeenCalledWith(['/verify'], { queryParams: { email: 'nina@acme.test', channels: 'Email,Sms' } });
  });

  it('confirms submission when no verification is needed', async () => {
    const fixture = render(Register, { tenantCode: 'acme' });
    flushOptions(true);
    await settle(fixture);
    const page = fixture.componentInstance;
    page.form.setValue({ firstName: 'A', lastName: 'B', email: 'a@acme.test', phoneNumber: '', password: 'Str0ng#Password', confirm: 'Different#1234' });
    page.submit();
    backend.expectNone('/api/auth/register');

    page.form.controls.confirm.setValue('Str0ng#Password');
    page.submit();
    const req = backend.expectOne('/api/auth/register');
    expect(req.request.body.phoneNumber).toBeNull();
    req.flush({ verification: [], message: 'An administrator will review your request.' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('An administrator will review your request.');
  });

  it('verifies each channel and then shows that approval is pending', async () => {
    const fixture = render(Verify, { email: 'nina@acme.test', channels: 'Email,Sms' });
    const page = fixture.componentInstance;
    expect(page.remaining()).toEqual(['Email', 'Sms']);

    page.codes.Email.setValue('123456');
    page.verify('Email');
    const email = backend.expectOne('/api/auth/verify');
    expect(email.request.body).toEqual({ email: 'nina@acme.test', channel: 'Email', code: '123456' });
    email.flush({ status: 'PendingVerification', remaining: ['Sms'] });
    expect(page.remaining()).toEqual(['Sms']);

    page.resend('Sms');
    backend.expectOne('/api/auth/resend-code').flush(null, { status: 202, statusText: 'Accepted' });

    page.codes.Sms.setValue('654321');
    page.verify('Sms');
    backend.expectOne('/api/auth/verify').flush({ status: 'PendingApproval', remaining: [] });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('An administrator will review your registration');
  });

  it('offers registration on the sign-in page only when the platform allows it', async () => {
    const hidden = render(Login);
    backend.expectOne('/api/auth/registration-options').flush({ available: false, tenantName: null, emailVerification: false, smsVerification: false });
    await settle(hidden);
    expect(hidden.nativeElement.textContent).not.toContain('Create an account');

    const shown = render(Login);
    backend.expectOne('/api/auth/registration-options').flush({ available: true, tenantName: null, emailVerification: false, smsVerification: false });
    await settle(shown);
    expect(shown.nativeElement.textContent).toContain('Create an account');
  });

  it('handles Enter in the code forms in-app instead of submitting the page', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const submit = (fixture: ComponentFixture<unknown>, value: string): Event => {
      const input: HTMLInputElement = fixture.nativeElement.querySelector('form input');
      input.value = value;
      input.dispatchEvent(new Event('input'));
      const event = new Event('submit', { cancelable: true });
      fixture.nativeElement.querySelector('form').dispatchEvent(event);
      return event;
    };

    const register = render(Register);
    expect(submit(register, ' ACME ').defaultPrevented).toBe(true);
    expect(navigate).toHaveBeenCalledWith(['/register', 'acme']);

    const verify = render(Verify, { email: 'nina@acme.test', channels: 'Email' });
    expect(submit(verify, '123456').defaultPrevented).toBe(true);
    backend.expectOne('/api/auth/verify').flush({ status: 'PendingApproval', remaining: [] });
  });

  it('platform settings: loads flags, warns about log-only delivery and saves a clean payload', async () => {
    const settings = {
      selfRegistrationEnabled: true, emailVerificationEnabled: true, smsVerificationEnabled: false,
      codeLength: 6, codeTtlMinutes: 10, maxAttempts: 5, resendCooldownSeconds: 60,
      emailProvider: 'Log', smtpHost: null, smtpPort: 587, smtpUsername: null, smtpUseStartTls: true, emailFromAddress: null, emailFromName: 'CRM',
      smsProvider: 'Log', smsGatewayUrl: null, smsSenderId: null, smsAuthHeader: 'Authorization', smsAuthScheme: 'Bearer',
    };
    const fixture = render(PlatformSettingsPage);
    backend.expectOne('/api/platform/settings').flush({ settings, smtpPasswordConfigured: true, smsApiKeyConfigured: false });
    await settle(fixture);
    const text = () => fixture.nativeElement.textContent as string;
    expect(text()).toContain('codes are only written to the API log');

    const page = fixture.componentInstance;
    page.form.patchValue({ emailProvider: 'Smtp', smtpHost: ' smtp.example.com ', emailFromAddress: 'crm@example.com', codeLength: '8' as unknown as number });
    fixture.detectChanges();
    expect(text()).not.toContain('codes are only written to the API log');
    expect(text()).toContain('SMTP password is set');

    page.save();
    const req = backend.expectOne((r) => r.method === 'PUT' && r.url === '/api/platform/settings');
    expect(req.request.body).toMatchObject({ emailProvider: 'Smtp', smtpHost: 'smtp.example.com', smtpUsername: null, smsGatewayUrl: null, codeLength: 8 });
    req.flush({ settings: { ...settings, emailProvider: 'Smtp' }, smtpPasswordConfigured: true, smsApiKeyConfigured: false });
  });

  it('accepts common mobile formats client-side', () => {
    expect(PHONE_PATTERN.test('+61 412 345 678')).toBe(true);
    expect(PHONE_PATTERN.test('(02) 9000-0000')).toBe(true);
    expect(PHONE_PATTERN.test('call me')).toBe(false);
  });
});
