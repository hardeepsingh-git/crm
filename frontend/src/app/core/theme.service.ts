import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, linkedSignal, signal } from '@angular/core';
import { CrmApi } from './api';
import { AuthService } from './auth.service';
import { ThemePreference } from './models';

const THEME_KEY = 'crm.theme';
export const THEMES: ThemePreference[] = ['System', 'Light', 'Dark'];

export function storedTheme(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): ThemePreference {
  const value = storage?.getItem(THEME_KEY);
  return THEMES.includes(value as ThemePreference) ? (value as ThemePreference) : 'System';
}

export function resolveTheme(preference: ThemePreference, systemDark: boolean): 'light' | 'dark' {
  return preference === 'Dark' || (preference === 'System' && systemDark) ? 'dark' : 'light';
}

/**
 * Light, dark or follow-the-system. Signed-in users' choice is saved on their profile so it follows them to other
 * browsers; it's also kept locally so public pages and the sign-in screen open in the right theme.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)');
  private readonly systemDark = signal(this.media?.matches ?? false);

  readonly preference = linkedSignal<ThemePreference | undefined, ThemePreference>({
    source: () => this.auth.user()?.theme,
    computation: (server, previous) => server ?? previous?.value ?? storedTheme(),
  });
  readonly resolved = computed(() => resolveTheme(this.preference(), this.systemDark()));

  constructor() {
    this.media?.addEventListener?.('change', (e) => this.systemDark.set(e.matches));
    effect(() => {
      const root = this.document.documentElement;
      root.classList.toggle('dark-theme', this.resolved() === 'dark');
      root.style.colorScheme = this.resolved();
      globalThis.localStorage?.setItem(THEME_KEY, this.preference());
    });
  }

  set(preference: ThemePreference): void {
    this.preference.set(preference);
    if (!this.auth.isAuthenticated()) return;
    this.auth.user.update((u) => (u ? { ...u, theme: preference } : u));
    this.api.savePreferences({ theme: preference }).subscribe({ error: () => undefined });
  }
}
