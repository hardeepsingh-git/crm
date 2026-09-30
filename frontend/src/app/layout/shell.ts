import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { ChangePasswordDialog } from '../features/auth/change-password-dialog';

interface NavItem {
  path: string;
  label: string;
  icon: string;
  adminOnly?: boolean;
}

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule, MatMenuModule, MatDividerModule],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav #nav [mode]="isMobile() ? 'over' : 'side'" [opened]="!isMobile()" class="sidenav">
        <div class="brand"><mat-icon>hub</mat-icon> CRM</div>
        <mat-nav-list>
          @for (item of navItems; track item.path) {
            @if (!item.adminOnly || auth.isAdmin()) {
              <a mat-list-item [routerLink]="item.path" routerLinkActive #rla="routerLinkActive" [activated]="rla.isActive"
                (click)="isMobile() && nav.close()">
                <mat-icon matListItemIcon>{{ item.icon }}</mat-icon>
                <span matListItemTitle>{{ item.label }}</span>
              </a>
            }
          }
        </mat-nav-list>
      </mat-sidenav>
      <mat-sidenav-content>
        <mat-toolbar class="toolbar">
          @if (isMobile()) {
            <button mat-icon-button (click)="nav.toggle()" aria-label="Toggle navigation"><mat-icon>menu</mat-icon></button>
          }
          <span class="spacer"></span>
          <button mat-button [matMenuTriggerFor]="userMenu">
            <mat-icon>account_circle</mat-icon> {{ auth.user()?.fullName }}
          </button>
          <mat-menu #userMenu="matMenu">
            <div class="menu-info">
              <div>{{ auth.user()?.email }}</div>
              <div class="muted">{{ auth.user()?.roles?.join(', ') }}</div>
            </div>
            <mat-divider />
            <button mat-menu-item (click)="changePassword()"><mat-icon>key</mat-icon> Change password</button>
            <button mat-menu-item (click)="auth.logout()"><mat-icon>logout</mat-icon> Sign out</button>
          </mat-menu>
        </mat-toolbar>
        <main class="content">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    .shell { height: 100vh; }
    .sidenav { width: 232px; border-right: 1px solid var(--mat-sys-outline-variant); }
    .brand { display: flex; align-items: center; gap: 8px; padding: 20px 16px; font: var(--mat-sys-title-large); color: var(--mat-sys-primary); }
    .toolbar { position: sticky; top: 0; z-index: 2; background: var(--mat-sys-surface); border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .content { padding: 24px; max-width: 1400px; margin: 0 auto; }
    .menu-info { padding: 8px 16px 12px; }
  `,
})
export class Shell {
  readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  readonly isMobile = toSignal(inject(BreakpointObserver).observe('(max-width: 900px)').pipe(map((r) => r.matches)), {
    initialValue: false,
  });

  readonly navItems: NavItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: 'dashboard' },
    { path: '/accounts', label: 'Accounts', icon: 'domain' },
    { path: '/contacts', label: 'Contacts', icon: 'contacts' },
    { path: '/leads', label: 'Leads', icon: 'person_search' },
    { path: '/opportunities', label: 'Opportunities', icon: 'paid' },
    { path: '/pipeline', label: 'Pipeline', icon: 'view_kanban' },
    { path: '/activities', label: 'Activities', icon: 'event_note' },
    { path: '/admin/users', label: 'Users', icon: 'admin_panel_settings', adminOnly: true },
  ];

  changePassword(): void {
    this.dialog.open(ChangePasswordDialog, { width: '440px' });
  }
}
