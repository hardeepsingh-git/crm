import { Routes } from '@angular/router';
import { authGuard, roleGuard } from './core/http';

export const routes: Routes = [
  { path: 'login', title: 'Sign in | CRM', loadComponent: () => import('./features/auth/login').then((m) => m.Login) },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', title: 'Dashboard | CRM', loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard) },
      { path: 'accounts', title: 'Accounts | CRM', loadComponent: () => import('./features/accounts/account-list').then((m) => m.AccountList) },
      { path: 'accounts/:id', title: 'Account | CRM', loadComponent: () => import('./features/accounts/account-detail').then((m) => m.AccountDetailPage) },
      { path: 'contacts', title: 'Contacts | CRM', loadComponent: () => import('./features/contacts/contact-list').then((m) => m.ContactList) },
      { path: 'contacts/:id', title: 'Contact | CRM', loadComponent: () => import('./features/contacts/contact-detail').then((m) => m.ContactDetailPage) },
      { path: 'leads', title: 'Leads | CRM', loadComponent: () => import('./features/leads/lead-list').then((m) => m.LeadList) },
      { path: 'leads/:id', title: 'Lead | CRM', loadComponent: () => import('./features/leads/lead-detail').then((m) => m.LeadDetailPage) },
      { path: 'opportunities', title: 'Opportunities | CRM', loadComponent: () => import('./features/opportunities/opportunity-list').then((m) => m.OpportunityList) },
      { path: 'pipeline', title: 'Pipeline | CRM', loadComponent: () => import('./features/opportunities/pipeline-board').then((m) => m.PipelineBoard) },
      { path: 'opportunities/:id', title: 'Opportunity | CRM', loadComponent: () => import('./features/opportunities/opportunity-detail').then((m) => m.OpportunityDetailPage) },
      { path: 'activities', title: 'Activities | CRM', loadComponent: () => import('./features/activities/activity-list').then((m) => m.ActivityList) },
      {
        path: 'admin/users',
        title: 'Users | CRM',
        canActivate: [roleGuard('Admin')],
        loadComponent: () => import('./features/admin/user-list').then((m) => m.UserList),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
