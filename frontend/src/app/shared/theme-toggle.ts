import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ThemePreference } from '../core/models';
import { THEMES, ThemeService } from '../core/theme.service';

const ICONS: Record<ThemePreference, string> = { System: 'brightness_auto', Light: 'light_mode', Dark: 'dark_mode' };

/** Toolbar button to switch between light, dark and the system theme. */
@Component({
  selector: 'app-theme-toggle',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="Appearance" matTooltip="Appearance">
      <mat-icon>{{ icon() }}</mat-icon>
    </button>
    <mat-menu #menu="matMenu" xPosition="before">
      @for (t of themes; track t) {
        <button mat-menu-item (click)="theme.set(t)" [attr.aria-checked]="theme.preference() === t" role="menuitemradio">
          <mat-icon>{{ icons[t] }}</mat-icon>
          <span>{{ t === 'System' ? 'Match device' : t }}</span>
          @if (theme.preference() === t) { <mat-icon class="check">check</mat-icon> }
        </button>
      }
    </mat-menu>
  `,
  styles: `.check { margin: 0 0 0 16px; }`,
})
export class ThemeToggle {
  readonly theme = inject(ThemeService);
  readonly themes = THEMES;
  readonly icons = ICONS;
  readonly icon = computed(() => ICONS[this.theme.preference()]);
}
