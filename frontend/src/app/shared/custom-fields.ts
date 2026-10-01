import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ParamMap, RouterLink } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { switchMap } from 'rxjs';
import { RecordNameService } from '../core/api';
import { MetadataService } from '../core/metadata.service';
import { CustomFields, FieldDef } from '../core/models';
import { EntityPicker } from './entity-picker';
import { ListState, fromLocalInput, toLocalInput } from './list';

export type CustomFieldsForm = FormGroup<Record<string, FormControl<unknown>>>;

/** Builds a reactive form group whose controls mirror the object's custom field definitions. */
export function customFieldsGroup(fields: FieldDef[], values: CustomFields = {}): CustomFieldsForm {
  const controls: Record<string, FormControl<unknown>> = {};
  for (const f of fields) {
    const validators: ValidatorFn[] = [];
    if (f.isRequired && f.type !== 'Boolean') validators.push(f.type === 'MultiPicklist' ? nonEmptyArray : Validators.required);
    if (f.maxLength) validators.push(Validators.maxLength(f.maxLength));
    if (f.min !== null) validators.push(Validators.min(f.min));
    if (f.max !== null) validators.push(Validators.max(f.max));
    if (f.type === 'Email') validators.push(Validators.email);
    if (f.type === 'Url') validators.push(Validators.pattern(/^https?:\/\/.+/i));

    let value = values[f.key] ?? null;
    if (f.type === 'DateTime') value = toLocalInput((value as string) ?? null);
    if (f.type === 'MultiPicklist') value = (value as string[] | null) ?? [];
    if (f.type === 'Boolean') value = value ?? false;
    controls[f.key] = new FormControl<unknown>(value, validators);
  }
  return new FormGroup(controls);
}

function nonEmptyArray(c: AbstractControl): { required: true } | null {
  return Array.isArray(c.value) && c.value.length > 0 ? null : { required: true };
}

/** Converts the form group back into the API's customFields object (empty values become null to clear them). */
export function customFieldsValue(fields: FieldDef[], group: CustomFieldsForm): CustomFields {
  const result: CustomFields = {};
  for (const f of fields) {
    const raw = group.controls[f.key]?.value;
    if (raw === null || raw === undefined || raw === '' || (Array.isArray(raw) && raw.length === 0)) {
      result[f.key] = null;
      continue;
    }
    result[f.key] =
      f.type === 'Number' || f.type === 'Currency' ? Number(raw)
      : f.type === 'DateTime' ? fromLocalInput(raw as string)
      : raw;
  }
  return result;
}

@Component({
  selector: 'app-custom-fields-editor',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, EntityPicker],
  host: { style: 'display: contents' },
  template: `
    @for (f of fields(); track f.key) {
      @let c = group().controls[f.key];
      @switch (f.type) {
        @case ('Lookup') {
          <app-entity-picker [label]="f.label" [kind]="f.lookupObjectKey!" [formControl]="$any(c)" [required]="f.isRequired" />
        }
        @case ('Boolean') {
          <div class="toggle-field"><mat-slide-toggle [formControl]="$any(c)">{{ f.label }}</mat-slide-toggle></div>
        }
        @case ('Picklist') {
          <mat-form-field appearance="outline">
            <mat-label>{{ f.label }}</mat-label>
            <mat-select [formControl]="$any(c)" [required]="f.isRequired">
              @if (!f.isRequired) { <mat-option [value]="null">None</mat-option> }
              @for (o of f.options; track o) { <mat-option [value]="o">{{ o }}</mat-option> }
            </mat-select>
            @if (f.helpText) { <mat-hint>{{ f.helpText }}</mat-hint> }
            <mat-error>{{ f.label }} is required</mat-error>
          </mat-form-field>
        }
        @case ('MultiPicklist') {
          <mat-form-field appearance="outline">
            <mat-label>{{ f.label }}</mat-label>
            <mat-select [formControl]="$any(c)" multiple [required]="f.isRequired">
              @for (o of f.options; track o) { <mat-option [value]="o">{{ o }}</mat-option> }
            </mat-select>
            @if (f.helpText) { <mat-hint>{{ f.helpText }}</mat-hint> }
          </mat-form-field>
        }
        @default {
          <mat-form-field appearance="outline" [class.span-2]="f.type === 'TextArea'">
            <mat-label>{{ f.label }}</mat-label>
            @if (f.type === 'TextArea') {
              <textarea matInput rows="3" [formControl]="$any(c)" [required]="f.isRequired"></textarea>
            } @else {
              <input matInput [type]="inputType(f)" [formControl]="$any(c)" [required]="f.isRequired"
                [attr.min]="f.min" [attr.max]="f.max" [attr.step]="f.type === 'Currency' ? '0.01' : null" />
            }
            @if (f.helpText) { <mat-hint>{{ f.helpText }}</mat-hint> }
            @if (c.hasError('required')) { <mat-error>{{ f.label }} is required</mat-error> }
            @else if (c.hasError('min') || c.hasError('max')) { <mat-error>Must be between {{ f.min ?? '…' }} and {{ f.max ?? '…' }}</mat-error> }
            @else if (c.invalid) { <mat-error>Invalid value</mat-error> }
          </mat-form-field>
        }
      }
    }
  `,
  styles: `.toggle-field { display: flex; align-items: center; min-height: 56px; margin-bottom: 20px; }`,
})
export class CustomFieldsEditor {
  readonly fields = input.required<FieldDef[]>();
  readonly group = input.required<CustomFieldsForm>();

  inputType(f: FieldDef): string {
    switch (f.type) {
      case 'Number':
      case 'Currency':
        return 'number';
      case 'Date':
        return 'date';
      case 'DateTime':
        return 'datetime-local';
      case 'Email':
        return 'email';
      case 'Url':
        return 'url';
      case 'Phone':
        return 'tel';
      default:
        return 'text';
    }
  }
}

/** Renders a single custom field value; lookups resolve to a link with the referenced record's name. */
@Component({
  selector: 'app-cf-value',
  imports: [CurrencyPipe, DatePipe, DecimalPipe, RouterLink],
  template: `
    @let v = value();
    @if (v === null || v === undefined || v === '') {
      <span class="muted">-</span>
    } @else {
      @switch (field().type) {
        @case ('Currency') { {{ $any(v) | currency: meta.currency() : 'symbol' : '1.0-2' }} }
        @case ('Number') { {{ $any(v) | number }} }
        @case ('Date') { {{ $any(v) | date: 'mediumDate' }} }
        @case ('DateTime') { {{ $any(v) | date: 'medium' }} }
        @case ('Boolean') { {{ v ? 'Yes' : 'No' }} }
        @case ('MultiPicklist') { {{ $any(v).join(', ') }} }
        @case ('Url') { <a [href]="v" target="_blank" rel="noopener noreferrer">{{ v }}</a> }
        @case ('Email') { <a [href]="'mailto:' + v">{{ v }}</a> }
        @case ('Lookup') { <a [routerLink]="meta.route(field().lookupObjectKey!, $any(v))" (click)="$event.stopPropagation()">{{ lookupName() || '…' }}</a> }
        @default { {{ v }} }
      }
    }
  `,
})
export class CfValue {
  readonly field = input.required<FieldDef>();
  readonly value = input<unknown>();
  readonly meta = inject(MetadataService);
  private readonly names = inject(RecordNameService);
  private readonly lookupUrl = computed(() =>
    this.field().type === 'Lookup' && this.value() ? `${this.meta.apiUrl(this.field().lookupObjectKey!)}/${this.value()}` : null,
  );
  readonly lookupName = toSignal(toObservable(this.lookupUrl).pipe(switchMap((url) => this.names.name(url))), { initialValue: '' });
}

/** Definition-list rows (dt/dd) for an object's custom fields, for use inside a <dl class="fields">. */
@Component({
  selector: 'app-custom-fields-view',
  imports: [CfValue],
  host: { style: 'display: contents' },
  template: `
    @for (f of fields(); track f.key) {
      <dt>{{ f.label }}</dt><dd><app-cf-value [field]="f" [value]="values()[f.key]" /></dd>
    }
  `,
})
export class CustomFieldsView {
  readonly fields = input.required<FieldDef[]>();
  readonly values = input.required<CustomFields>();
}

/** Filter controls for the object's custom fields: picklist and yes/no fields shown in lists, plus lookups (also any lookup filter set by a link). */
@Component({
  selector: 'app-cf-filters',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatSelectModule, EntityPicker],
  host: { style: 'display: contents' },
  template: `
    @for (f of filterable(); track f.key) {
      @if (f.type === 'Lookup') {
        <app-entity-picker class="lookup-filter" [label]="f.label" [kind]="f.lookupObjectKey!" [formControl]="lookup(f.key)" [compact]="true" />
      } @else {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>{{ f.label }}</mat-label>
          <mat-select [value]="state().cf()[f.key] ?? ''" (selectionChange)="state().setCf(f.key, $event.value)">
            <mat-option value="">All</mat-option>
            @if (f.type === 'Boolean') {
              <mat-option value="true">Yes</mat-option>
              <mat-option value="false">No</mat-option>
            } @else {
              @for (o of f.options; track o) { <mat-option [value]="o">{{ o }}</mat-option> }
            }
          </mat-select>
        </mat-form-field>
      }
    }
  `,
  styles: `.lookup-filter { display: inline-block; width: 220px; }`,
})
export class CfFilters {
  readonly objectKey = input.required<string>();
  readonly state = input.required<ListState>();
  private readonly meta = inject(MetadataService);
  private readonly lookups = new Map<string, FormControl<string | null>>();

  readonly filterable = computed(() => {
    const active = this.state().cf();
    return this.meta.fields(this.objectKey()).filter((f) =>
      f.type === 'Lookup' ? f.showInList || !!active[f.key] : (f.type === 'Picklist' || f.type === 'Boolean') && f.showInList,
    );
  });

  constructor() {
    // Keeps the pickers in step when filters change elsewhere, e.g. a deep link or switching objects.
    effect(() => {
      const cf = this.state().cf();
      const prefix = `${this.objectKey()}.`;
      for (const [id, control] of this.lookups) {
        if (!id.startsWith(prefix)) continue;
        const value = cf[id.slice(prefix.length)] ?? null;
        if (control.value !== value) control.setValue(value, { emitEvent: false });
      }
    });
  }

  lookup(key: string): FormControl<string | null> {
    const id = `${this.objectKey()}.${key}`;
    let control = this.lookups.get(id);
    if (!control) {
      control = new FormControl<string | null>(this.state().cf()[key] ?? null);
      control.valueChanges.subscribe((value) => this.state().setCf(key, value));
      this.lookups.set(id, control);
    }
    return control;
  }
}

/** Reads ?cf.key=value parameters (e.g. from a related-records link) into a filter map. */
export function cfFromQuery(params: ParamMap): Record<string, string> {
  return Object.fromEntries(params.keys.filter((k) => k.startsWith('cf.') && k.length > 3).map((k) => [k.slice(3), params.get(k)!]));
}
