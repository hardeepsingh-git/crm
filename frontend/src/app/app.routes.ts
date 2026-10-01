import { Routes } from '@angular/router';
import { adminAreaGuard, authGuard, metadataGuard, objectGuard, permissionGuard, platformGuard } from './core/http';

const customize = permissionGuard('customization', 'manage');

export const routes: Routes = [
  { path: 'login', title: 'Sign in | CRM', loadComponent: () => import('./features/auth/login').then((m) => m.Login) },
  { path: 'register', title: 'Register | CRM', loadComponent: () => import('./features/auth/registration').then((m) => m.Register) },
  { path: 'register/:tenantCode', title: 'Register | CRM', loadComponent: () => import('./features/auth/registration').then((m) => m.Register) },
  { path: 'verify', title: 'Verify | CRM', loadComponent: () => import('./features/auth/registration').then((m) => m.Verify) },
  { path: 'forgot-password', title: 'Forgot password | CRM', loadComponent: () => import('./features/auth/password-reset').then((m) => m.ForgotPassword) },
  { path: 'reset-password', title: 'Reset password | CRM', loadComponent: () => import('./features/auth/password-reset').then((m) => m.ResetPassword) },
  { path: 'careers/:slug', title: 'Careers', loadComponent: () => import('./features/careers/careers').then((m) => m.CareersPageView) },
  { path: 'careers/:slug/:jobId', title: 'Careers', loadComponent: () => import('./features/careers/careers').then((m) => m.CareerJobView) },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      {
        // Runs after authGuard so the metadata request carries a token.
        path: '',
        canActivate: [metadataGuard],
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
          { path: 'dashboard', title: 'Dashboard | CRM', loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard) },

          { path: 'accounts', title: 'Accounts | CRM', canActivate: [objectGuard('account')], loadComponent: () => import('./features/accounts/account-list').then((m) => m.AccountList) },
          { path: 'accounts/:id', title: 'Account | CRM', canActivate: [objectGuard('account')], loadComponent: () => import('./features/accounts/account-detail').then((m) => m.AccountDetailPage) },
          { path: 'contacts', title: 'Contacts | CRM', canActivate: [objectGuard('contact')], loadComponent: () => import('./features/contacts/contact-list').then((m) => m.ContactList) },
          { path: 'contacts/:id', title: 'Contact | CRM', canActivate: [objectGuard('contact')], loadComponent: () => import('./features/contacts/contact-detail').then((m) => m.ContactDetailPage) },
          { path: 'leads', title: 'Leads | CRM', canActivate: [objectGuard('lead')], loadComponent: () => import('./features/leads/lead-list').then((m) => m.LeadList) },
          { path: 'leads/:id', title: 'Lead | CRM', canActivate: [objectGuard('lead')], loadComponent: () => import('./features/leads/lead-detail').then((m) => m.LeadDetailPage) },
          { path: 'opportunities', title: 'Opportunities | CRM', canActivate: [objectGuard('opportunity')], loadComponent: () => import('./features/opportunities/opportunity-list').then((m) => m.OpportunityList) },
          { path: 'pipeline', title: 'Pipeline | CRM', canActivate: [objectGuard('opportunity')], loadComponent: () => import('./features/opportunities/pipeline-board').then((m) => m.PipelineBoard) },
          { path: 'opportunities/:id', title: 'Opportunity | CRM', canActivate: [objectGuard('opportunity')], loadComponent: () => import('./features/opportunities/opportunity-detail').then((m) => m.OpportunityDetailPage) },
          { path: 'activities', title: 'Activities | CRM', canActivate: [objectGuard('activity')], loadComponent: () => import('./features/activities/activity-list').then((m) => m.ActivityList) },

          { path: 'o/:objectKey', title: 'Records | CRM', canActivate: [objectGuard()], loadComponent: () => import('./features/records/record-list').then((m) => m.RecordList) },
          { path: 'o/:objectKey/board', title: 'Board | CRM', canActivate: [objectGuard()], loadComponent: () => import('./features/records/record-board').then((m) => m.RecordBoard) },
          { path: 'o/:objectKey/:id', title: 'Record | CRM', canActivate: [objectGuard()], loadComponent: () => import('./features/records/record-detail').then((m) => m.RecordDetailPage) },

          {
            path: 'settings',
            canActivate: [adminAreaGuard],
            children: [
              { path: '', title: 'Settings | CRM', loadComponent: () => import('./features/admin/settings-home').then((m) => m.SettingsHome) },
              { path: 'general', title: 'Organisation | CRM', canActivate: [permissionGuard('settings', 'manage')], loadComponent: () => import('./features/admin/general-settings').then((m) => m.GeneralSettings) },
              { path: 'users', title: 'Users | CRM', canActivate: [permissionGuard('users', 'manage')], loadComponent: () => import('./features/admin/user-list').then((m) => m.UserList) },
              { path: 'roles', title: 'Roles | CRM', canActivate: [permissionGuard('roles', 'manage')], loadComponent: () => import('./features/admin/role-list').then((m) => m.RoleList) },
              { path: 'groups', title: 'Groups | CRM', canActivate: [permissionGuard('groups', 'manage')], loadComponent: () => import('./features/admin/group-list').then((m) => m.GroupList) },
              { path: 'objects', title: 'Objects | CRM', canActivate: [customize], loadComponent: () => import('./features/admin/object-list').then((m) => m.ObjectList) },
              { path: 'objects/:key', title: 'Object | CRM', canActivate: [customize], loadComponent: () => import('./features/admin/object-editor').then((m) => m.ObjectEditor) },
              { path: 'picklists', title: 'Picklists | CRM', canActivate: [customize], loadComponent: () => import('./features/admin/picklists').then((m) => m.Picklists) },
              { path: 'automation', title: 'Automation | CRM', canActivate: [customize], loadComponent: () => import('./features/admin/automation-list').then((m) => m.AutomationList) },
              { path: 'email-templates', title: 'Email templates | CRM', canActivate: [customize], loadComponent: () => import('./features/admin/email-templates').then((m) => m.EmailTemplates) },
              { path: 'emails', title: 'Email log | CRM', canActivate: [permissionGuard('settings', 'manage')], loadComponent: () => import('./features/admin/email-log').then((m) => m.EmailLog) },
              { path: 'audit', title: 'Audit log | CRM', canActivate: [permissionGuard('audit', 'read')], loadComponent: () => import('./features/admin/audit-log').then((m) => m.AuditLog) },
            ],
          },
          { path: 'admin/users', redirectTo: 'settings/users' },
          { path: 'platform/tenants', title: 'Tenants | CRM', canActivate: [platformGuard], loadComponent: () => import('./features/platform/tenant-list').then((m) => m.TenantList) },
          { path: 'platform/settings', title: 'Platform settings | CRM', canActivate: [platformGuard], loadComponent: () => import('./features/platform/platform-settings').then((m) => m.PlatformSettingsPage) },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
