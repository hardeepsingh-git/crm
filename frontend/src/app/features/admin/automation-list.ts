import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { CrmApi, UserLookupService } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import {
  AutomationAction,
  AutomationActionType,
  AutomationCondition,
  AutomationField,
  AutomationTrigger,
  ConditionOperator,
  PRIORITIES,
  RuleItem,
} from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { SettingsBack } from './settings-home';

const TRIGGERS: { value: AutomationTrigger; label: string }[] = [
  { value: 'Created', label: 'is created' },
  { value: 'Updated', label: 'is updated' },
  { value: 'StageChanged', label: 'changes stage' },
];

const OPERATORS: { value: ConditionOperator; label: string; needsValue: boolean }[] = [
  { value: 'Equals', label: 'equals', needsValue: true },
  { value: 'NotEquals', label: 'does not equal', needsValue: true },
  { value: 'GreaterThan', label: 'is greater than', needsValue: true },
  { value: 'LessThan', label: 'is less than', needsValue: true },
  { value: 'Contains', label: 'contains', needsValue: true },
  { value: 'IsEmpty', label: 'is empty', needsValue: false },
  { value: 'IsNotEmpty', label: 'is not empty', needsValue: false },
  { value: 'Changed', label: 'changed', needsValue: false },
];

const ACTION_TYPES: { value: AutomationActionType; label: string }[] = [
  { value: 'CreateTask', label: 'Create a task' },
  { value: 'Notify', label: 'Send a notification' },
  { value: 'SetField', label: 'Set a field' },
];

@Component({
  selector: 'app-rule-editor-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ rule ? 'Edit rule' : 'New automation rule' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Rule name</mat-label>
          <input matInput [(ngModel)]="name" maxlength="200" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>When a</mat-label>
          <mat-select [(ngModel)]="objectKey" (selectionChange)="loadFields()" [disabled]="!!rule">
            @for (o of meta.objects(); track o.key) { <mat-option [value]="o.key">{{ o.label }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Trigger</mat-label>
          <mat-select [(ngModel)]="trigger">
            @for (t of triggers; track t.value) {
              <mat-option [value]="t.value" [disabled]="t.value === 'StageChanged' && !meta.object(objectKey)?.hasPipeline">{{ t.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <h3>Conditions <span class="muted small">(all must match; none = always)</span></h3>
      @for (c of conditions; track $index; let i = $index) {
        <div class="row">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Field</mat-label>
            <mat-select [(ngModel)]="c.field">
              @for (f of fields(); track f.key) { <mat-option [value]="f.key">{{ f.label }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Operator</mat-label>
            <mat-select [(ngModel)]="c.operator">
              @for (o of operators; track o.value) { <mat-option [value]="o.value">{{ o.label }}</mat-option> }
            </mat-select>
          </mat-form-field>
          @if (needsValue(c.operator)) {
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Value</mat-label>
              <input matInput [(ngModel)]="c.value" />
            </mat-form-field>
          }
          <button mat-icon-button (click)="conditions.splice(i, 1)" aria-label="Remove condition"><mat-icon>close</mat-icon></button>
        </div>
      }
      <button mat-stroked-button (click)="addCondition()"><mat-icon>add</mat-icon> Add condition</button>

      <h3>Actions</h3>
      <p class="muted small">Text can include placeholders such as <code>{{ '{{name}}' }}</code>, <code>{{ '{{stage}}' }}</code> or <code>{{ '{{cf.priority}}' }}</code>.</p>
      @for (a of actions; track $index; let i = $index) {
        <div class="action">
          <div class="row">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Action</mat-label>
              <mat-select [(ngModel)]="a.type">
                @for (t of actionTypes; track t.value) { <mat-option [value]="t.value">{{ t.label }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (a.type !== 'SetField') {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>{{ a.type === 'CreateTask' ? 'Assign to' : 'Notify' }}</mat-label>
                <mat-select [(ngModel)]="a.assignTo">
                  <mat-option value="owner">Record owner</mat-option>
                  @for (u of lookup.users.value(); track u.id) { <mat-option [value]="u.id">{{ u.fullName }}</mat-option> }
                </mat-select>
              </mat-form-field>
            }
            <span class="spacer"></span>
            <button mat-icon-button (click)="actions.splice(i, 1)" aria-label="Remove action"><mat-icon>close</mat-icon></button>
          </div>
          @switch (a.type) {
            @case ('CreateTask') {
              <div class="row">
                <mat-form-field appearance="outline" subscriptSizing="dynamic" class="grow">
                  <mat-label>Task subject</mat-label>
                  <input matInput [(ngModel)]="a.subject" maxlength="200" />
                </mat-form-field>
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Due in (days)</mat-label>
                  <input matInput type="number" min="0" max="365" [(ngModel)]="a.dueInDays" />
                </mat-form-field>
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Priority</mat-label>
                  <mat-select [(ngModel)]="a.priority">
                    @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
                  </mat-select>
                </mat-form-field>
              </div>
            }
            @case ('Notify') {
              <mat-form-field appearance="outline" subscriptSizing="dynamic" class="full">
                <mat-label>Message</mat-label>
                <input matInput [(ngModel)]="a.message" maxlength="1000" />
              </mat-form-field>
            }
            @case ('SetField') {
              <div class="row">
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Field</mat-label>
                  <mat-select [(ngModel)]="a.field">
                    @for (f of settableFields(); track f.key) { <mat-option [value]="f.key">{{ f.label }}</mat-option> }
                  </mat-select>
                </mat-form-field>
                <mat-form-field appearance="outline" subscriptSizing="dynamic" class="grow">
                  <mat-label>New value</mat-label>
                  <input matInput [(ngModel)]="a.value" />
                </mat-form-field>
              </div>
            }
          }
        </div>
      }
      <button mat-stroked-button (click)="addAction()"><mat-icon>add</mat-icon> Add action</button>
      <div class="active"><mat-checkbox [(ngModel)]="isActive">Rule is active</mat-checkbox></div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="saving() || !name.trim() || !actions.length">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .row mat-form-field { min-width: 180px; }
    .grow { flex: 1; }
    .full { width: 100%; }
    .action { border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; padding: 12px; margin-bottom: 8px; }
    .active { margin-top: 16px; }
    .small { font-size: 13px; font-weight: normal; }
  `,
})
export class RuleEditorDialog {
  readonly rule = inject<RuleItem | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<RuleEditorDialog, RuleItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly lookup = inject(UserLookupService);
  readonly triggers = TRIGGERS;
  readonly operators = OPERATORS;
  readonly actionTypes = ACTION_TYPES;
  readonly priorities = PRIORITIES;
  readonly saving = signal(false);
  readonly fields = signal<AutomationField[]>([]);
  readonly settableFields = signal<AutomationField[]>([]);

  name = this.rule?.name ?? '';
  objectKey = this.rule?.objectKey ?? this.meta.activeObjects()[0]?.key ?? 'account';
  trigger: AutomationTrigger = this.rule?.trigger ?? 'Created';
  isActive = this.rule?.isActive ?? true;
  conditions: AutomationCondition[] = (this.rule?.conditions ?? []).map((c) => ({ ...c }));
  actions: AutomationAction[] = (this.rule?.actions ?? []).map((a) => ({ ...a, assignTo: a.assignTo ?? 'owner' }));

  constructor() {
    this.loadFields();
    if (!this.rule) this.addAction();
  }

  loadFields(): void {
    const key = this.objectKey;
    this.api.automationFields(key).subscribe((f) => {
      if (key !== this.objectKey) return; // a newer object was selected meanwhile
      this.fields.set(f);
      this.settableFields.set(f.filter((x) => x.settable));
    });
  }

  needsValue(op: ConditionOperator): boolean {
    return OPERATORS.find((o) => o.value === op)?.needsValue ?? true;
  }

  addCondition(): void {
    this.conditions.push({ field: this.fields()[0]?.key ?? 'name', operator: 'Equals', value: '' });
  }

  addAction(): void {
    this.actions.push({ type: 'CreateTask', subject: 'Follow up on {{name}}', dueInDays: 1, priority: 'Normal', assignTo: 'owner' });
  }

  save(): void {
    const actions = this.actions.map((a) => ({
      type: a.type,
      subject: a.type === 'CreateTask' ? a.subject || null : null,
      message: a.type === 'Notify' ? a.message || null : null,
      dueInDays: a.type === 'CreateTask' && a.dueInDays !== null && a.dueInDays !== undefined ? Number(a.dueInDays) : null,
      priority: a.type === 'CreateTask' ? a.priority ?? 'Normal' : null,
      assignTo: a.type === 'SetField' ? null : a.assignTo ?? 'owner',
      field: a.type === 'SetField' ? a.field ?? null : null,
      value: a.type === 'SetField' ? a.value ?? null : null,
    }));
    const conditions = this.conditions.map((c) => ({ ...c, value: this.needsValue(c.operator) ? c.value ?? '' : null }));
    this.saving.set(true);
    this.api
      .saveRule(this.rule?.id ?? null, { name: this.name.trim(), objectKey: this.objectKey, trigger: this.trigger, conditions, actions, isActive: this.isActive })
      .subscribe({
        next: (saved) => {
          this.notify.success('Rule saved');
          this.ref.close(saved);
        },
        error: (err) => {
          this.saving.set(false);
          this.notify.formError(err);
        },
      });
  }
}

@Component({
  selector: 'app-automation-list',
  imports: [DatePipe, MatTableModule, MatButtonModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Automation</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="open(null)"><mat-icon>add</mat-icon> New rule</button>
    </div>
    <p class="muted">Rules run immediately after a record is saved. They can create follow-up tasks, notify people, or update fields.</p>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="data.value() ?? []">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>Rule</th>
          <td mat-cell *matCellDef="let r"><strong>{{ r.name }}</strong>
            <div class="muted">When {{ withArticle(meta.label(r.objectKey)) }} {{ triggerLabel(r.trigger) }}
              @if (r.conditions.length) { and {{ r.conditions.length }} condition(s) match }
              &rarr; {{ describeActions(r) }}</div></td>
        </ng-container>
        <ng-container matColumnDef="runs">
          <th mat-header-cell *matHeaderCellDef>Runs</th>
          <td mat-cell *matCellDef="let r">{{ r.runCount }} @if (r.lastRunAt) { <div class="muted">last {{ r.lastRunAt | date: 'short' }}</div> }</td>
        </ng-container>
        <ng-container matColumnDef="active">
          <th mat-header-cell *matHeaderCellDef>Active</th>
          <td mat-cell *matCellDef="let r"><mat-slide-toggle [checked]="r.isActive" (change)="toggle(r, $event.checked)" [attr.aria-label]="'Toggle ' + r.name" /></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let r" class="actions">
            <button mat-icon-button (click)="open(r)" aria-label="Edit"><mat-icon>edit</mat-icon></button>
            <button mat-icon-button (click)="remove(r)" aria-label="Delete"><mat-icon>delete</mat-icon></button>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="4">No rules yet.</td></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class AutomationList {
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly columns = ['name', 'runs', 'active', 'actions'];
  readonly data = httpResource<RuleItem[]>(() => '/api/admin/automation');

  triggerLabel(t: AutomationTrigger): string {
    return TRIGGERS.find((x) => x.value === t)?.label ?? t;
  }

  withArticle(label: string): string {
    const lower = label.toLowerCase();
    return `${/^[aeiou]/.test(lower) ? 'an' : 'a'} ${lower}`;
  }

  describeActions(r: RuleItem): string {
    return r.actions.map((a) => ACTION_TYPES.find((t) => t.value === a.type)?.label.toLowerCase() ?? a.type).join(', ');
  }

  open(rule: RuleItem | null): void {
    this.dialog.open(RuleEditorDialog, { data: rule, width: '860px', maxHeight: '90vh' }).afterClosed()
      .subscribe((saved) => saved && this.data.reload());
  }

  toggle(r: RuleItem, isActive: boolean): void {
    const { name, objectKey, trigger, conditions, actions } = r;
    this.api.saveRule(r.id, { name, objectKey, trigger, conditions, actions, isActive }).subscribe({
      next: () => this.data.reload(),
      error: (err) => {
        this.notify.formError(err);
        this.data.reload();
      },
    });
  }

  remove(r: RuleItem): void {
    confirm(this.dialog, { title: 'Delete rule?', message: `"${r.name}" will be deleted.`, confirmText: 'Delete', danger: true }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteRule(r.id).subscribe(() => {
        this.notify.success('Rule deleted');
        this.data.reload();
      });
    });
  }
}
