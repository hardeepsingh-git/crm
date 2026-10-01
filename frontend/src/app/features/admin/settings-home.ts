import { Component, computed, inject } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';

interface SettingsArea {
  path: string;
  title: string;
  description: string;
  icon: string;
  resource: string;
  action: string;
  /** Shown only when this object is active. */
  requires?: string;
}

const AREAS: SettingsArea[] = [
  { path: 'general', title: 'Organisation', description: 'Name, currency and self-registration.', icon: 'business', resource: 'settings', action: 'manage' },
  { path: 'users', title: 'Users', description: 'Invite people, reset passwords, assign roles and groups.', icon: 'group', resource: 'users', action: 'manage' },
  { path: 'groups', title: 'Groups', description: 'Teams that share records (Group scope) and inherit roles.', icon: 'workspaces', resource: 'groups', action: 'manage' },
  { path: 'roles', title: 'Roles & permissions', description: 'What each role can read, create, update and delete, and at which scope.', icon: 'shield_person', resource: 'roles', action: 'manage' },
  { path: 'objects', title: 'Objects & fields', description: 'Rename objects, add custom fields, create new objects and pipelines.', icon: 'schema', resource: 'customization', action: 'manage' },
  { path: 'picklists', title: 'Picklists', description: 'Options for industries, lead sources and other shared lists.', icon: 'list', resource: 'customization', action: 'manage' },
  { path: 'automation', title: 'Automation', description: 'Rules that create tasks, notify people, set fields or send emails, when records change or dates come up.', icon: 'bolt', resource: 'customization', action: 'manage' },
  { path: 'email-templates', title: 'Email templates', description: 'Reusable emails for records and automation, with placeholders.', icon: 'drafts', resource: 'customization', action: 'manage' },
  { path: 'emails', title: 'Email log', description: 'Every email sent, its delivery status, and retries.', icon: 'outgoing_mail', resource: 'settings', action: 'manage' },
  { path: 'privacy', title: 'Privacy', description: 'How long candidates are kept, and their data requests.', icon: 'privacy_tip', resource: 'settings', action: 'manage', requires: 'candidate' },
  { path: 'audit', title: 'Audit log', description: 'Who changed what, and when.', icon: 'history', resource: 'audit', action: 'read' },
];

@Component({
  selector: 'app-settings-home',
  imports: [RouterLink, MatCardModule, MatIconModule],
  template: `
    <div class="page-header"><h1>Settings</h1></div>
    <div class="areas">
      @for (a of areas(); track a.path) {
        <a class="area" [routerLink]="a.path">
          <mat-card appearance="outlined">
            <mat-card-header>
              <mat-icon mat-card-avatar>{{ a.icon }}</mat-icon>
              <mat-card-title>{{ a.title }}</mat-card-title>
            </mat-card-header>
            <mat-card-content><p class="muted">{{ a.description }}</p></mat-card-content>
          </mat-card>
        </a>
      }
    </div>
  `,
  styles: `
    .areas { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 16px; }
    .area { text-decoration: none; color: inherit; }
    .area mat-card { height: 100%; }
    .area:hover mat-card { border-color: var(--mat-sys-primary); }
    mat-icon[mat-card-avatar] { color: var(--mat-sys-primary); font-size: 32px; width: 40px; height: 40px; }
  `,
})
export class SettingsHome {
  private readonly auth = inject(AuthService);
  private readonly meta = inject(MetadataService);
  readonly areas = computed(() => AREAS.filter((a) => this.auth.can(a.resource, a.action) && (!a.requires || this.meta.isActive(a.requires))));
}

/** Back link used at the top of every settings page. */
@Component({
  selector: 'app-settings-back',
  imports: [RouterLink, MatIconModule],
  template: `<a routerLink="/settings" class="back"><mat-icon inline>arrow_back</mat-icon> Settings</a>`,
})
export class SettingsBack {}
