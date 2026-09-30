import { BreakpointObserver } from '@angular/cdk/layout';
import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map, timer } from 'rxjs';
import { CrmApi } from '../core/api';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { NotificationItem, NotificationList } from '../core/models';
import { ChangePasswordDialog } from '../features/auth/change-password-dialog';

interface NavItem {
  path: string;
  label: string;
  icon: string;
}

const NOTIFICATION_POLL_MS = 60_000;

@Component({
  selector: 'app-shell',
  imports: [DatePipe, RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule, MatMenuModule, MatDividerModule, MatBadgeModule, MatTooltipModule],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav #nav [mode]="isMobile() ? 'over' : 'side'" [opened]="!isMobile()" class="sidenav">
        <div class="brand"><mat-icon>hub</mat-icon> <span class="brand-name">{{ meta.tenantName() || 'CRM' }}</span></div>
        <mat-nav-list>
          @for (item of navItems(); track item.path) {
            <a mat-list-item [routerLink]="item.path" routerLinkActive #rla="routerLinkActive" [activated]="rla.isActive"
              (click)="isMobile() && nav.close()">
              <mat-icon matListItemIcon>{{ item.icon }}</mat-icon>
              <span matListItemTitle>{{ item.label }}</span>
            </a>
          }
          @if (auth.hasAdminAccess() || auth.isPlatformAdmin()) {
            <mat-divider />
            @if (auth.hasAdminAccess()) {
              <a mat-list-item routerLink="/settings" routerLinkActive #rs="routerLinkActive" [activated]="rs.isActive" (click)="isMobile() && nav.close()">
                <mat-icon matListItemIcon>settings</mat-icon><span matListItemTitle>Settings</span>
              </a>
            }
            @if (auth.isPlatformAdmin()) {
              <a mat-list-item routerLink="/platform/tenants" routerLinkActive #rp="routerLinkActive" [activated]="rp.isActive" (click)="isMobile() && nav.close()">
                <mat-icon matListItemIcon>apartment</mat-icon><span matListItemTitle>Tenants</span>
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
          <button mat-icon-button [matMenuTriggerFor]="notificationMenu" aria-label="Notifications" matTooltip="Notifications">
            <mat-icon [matBadge]="unread() || null" matBadgeColor="warn" matBadgeSize="small" aria-hidden="false">notifications</mat-icon>
          </button>
          <mat-menu #notificationMenu="matMenu" class="notification-menu" xPosition="before">
            <div class="menu-header" (click)="$event.stopPropagation()" (keydown)="$event.stopPropagation()" tabindex="-1">
              <strong>Notifications</strong>
              <span class="spacer"></span>
              @if (unread() > 0) { <button mat-button (click)="markAllRead()">Mark all read</button> }
            </div>
            <mat-divider />
            @for (n of notificationItems(); track n.id) {
              <button mat-menu-item class="notification" [class.unread]="!n.readAt" (click)="openNotification(n)">
                <div class="n-title">{{ n.title }}</div>
                <div class="n-message muted">{{ n.message }}</div>
                <div class="n-time muted">{{ n.createdAt | date: 'd MMM, h:mm a' }}</div>
              </button>
            } @empty {
              <div class="menu-empty muted">You're all caught up.</div>
            }
          </mat-menu>
          <button mat-button [matMenuTriggerFor]="userMenu">
            <mat-icon>account_circle</mat-icon> {{ auth.user()?.fullName }}
          </button>
          <mat-menu #userMenu="matMenu">
            <div class="menu-info">
              <div>{{ auth.user()?.email }}</div>
              <div class="muted">{{ auth.user()?.tenant?.name }}</div>
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
    .brand-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .toolbar { position: sticky; top: 0; z-index: 2; background: var(--mat-sys-surface); border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .content { padding: 24px; max-width: 1400px; margin: 0 auto; }
    .menu-info { padding: 8px 16px 12px; }
    .menu-header { display: flex; align-items: center; padding: 4px 8px 4px 16px; min-width: 320px; }
    .menu-empty { padding: 16px; }
    .notification { height: auto !important; line-height: 1.3; padding-top: 8px; padding-bottom: 8px; white-space: normal; }
    .notification.unread .n-title { font-weight: 600; }
    .n-message { font-size: 13px; }
    .n-time { font-size: 11px; }
  `,
})
export class Shell {
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly isMobile = toSignal(inject(BreakpointObserver).observe('(max-width: 900px)').pipe(map((r) => r.matches)), {
    initialValue: false,
  });

  /** Navigation follows the tenant's active objects (in their configured order) and the user's read permissions. */
  readonly navItems = computed<NavItem[]>(() => {
    const items: NavItem[] = [{ path: '/dashboard', label: 'Dashboard', icon: 'dashboard' }];
    for (const o of this.meta.activeObjects()) {
      if (!this.auth.can(o.key, 'read')) continue;
      items.push({ path: this.meta.route(o.key)[0], label: o.pluralLabel, icon: o.icon });
      if (o.key === 'opportunity') items.push({ path: '/pipeline', label: 'Pipeline', icon: 'view_kanban' });
    }
    return items;
  });

  private readonly tick = toSignal(timer(NOTIFICATION_POLL_MS, NOTIFICATION_POLL_MS), { initialValue: 0 });
  readonly notifications = httpResource<NotificationList>(() => {
    this.tick();
    return '/api/notifications';
  });
  readonly unread = computed(() => (this.notifications.hasValue() ? this.notifications.value().unreadCount : 0));
  readonly notificationItems = computed(() => (this.notifications.hasValue() ? this.notifications.value().items : []));

  openNotification(n: NotificationItem): void {
    const go = () => n.link && this.router.navigateByUrl(n.link);
    if (n.readAt) {
      go();
      return;
    }
    this.api.markNotificationRead(n.id).subscribe(() => {
      this.notifications.reload();
      go();
    });
  }

  markAllRead(): void {
    this.api.markAllNotificationsRead().subscribe(() => this.notifications.reload());
  }

  changePassword(): void {
    this.dialog.open(ChangePasswordDialog, { width: '440px' });
  }
}
