import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, forwardRef, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ControlValueAccessor, FormControl, NG_VALUE_ACCESSOR, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { debounceTime, distinctUntilChanged, filter, map, switchMap } from 'rxjs';
import { toParams } from '../core/api';
import { PagedResult } from '../core/models';

export type PickerKind = 'accounts' | 'contacts' | 'leads' | 'opportunities';

interface Option {
  id: string;
  name: string;
}

interface Named {
  id: string;
  name?: string;
  fullName?: string;
}

const toOption = (x: Named): Option => ({ id: x.id, name: x.name ?? x.fullName ?? '' });

/** Server-side search autocomplete that binds to a record id via reactive forms. */
@Component({
  selector: 'app-entity-picker',
  imports: [ReactiveFormsModule, MatAutocompleteModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => EntityPicker), multi: true }],
  template: `
    <mat-form-field appearance="outline" class="full">
      <mat-label>{{ label() }}</mat-label>
      <input matInput [formControl]="text" [matAutocomplete]="auto" [required]="required()" (blur)="onTouched()" />
      @if (selectedId() && !disabled()) {
        <button matSuffix mat-icon-button type="button" (click)="clear()" aria-label="Clear selection">
          <mat-icon>close</mat-icon>
        </button>
      }
      <mat-autocomplete #auto="matAutocomplete" [displayWith]="display" (optionSelected)="select($event.option.value)">
        @for (o of options(); track o.id) {
          <mat-option [value]="o">{{ o.name }}</mat-option>
        } @empty {
          @if (isSearching()) {
            <mat-option disabled>No matches</mat-option>
          }
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
})
export class EntityPicker implements ControlValueAccessor {
  private readonly http = inject(HttpClient);
  readonly label = input.required<string>();
  readonly kind = input.required<PickerKind>();
  readonly required = input(false);
  readonly extraParams = input<Record<string, string | null>>({});

  readonly text = new FormControl<string | Option>('', { nonNullable: true });
  readonly options = signal<Option[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly disabled = signal(false);

  private onChange: (id: string | null) => void = () => undefined;
  onTouched: () => void = () => undefined;

  constructor() {
    this.text.valueChanges
      .pipe(
        filter((v): v is string => typeof v === 'string'),
        debounceTime(250),
        distinctUntilChanged(),
        switchMap((search) =>
          this.http.get<PagedResult<Named>>(`/api/${this.kind()}`, {
            params: toParams({ search, pageSize: 10, ...this.extraParams() }),
          }),
        ),
        map((r) => r.items.map(toOption)),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((opts) => this.options.set(opts));
  }

  display = (o: Option | string | null): string => (typeof o === 'string' ? o : (o?.name ?? ''));

  isSearching(): boolean {
    return typeof this.text.value === 'string' && this.text.value.length > 0;
  }

  select(o: Option): void {
    this.selectedId.set(o.id);
    this.onChange(o.id);
  }

  clear(): void {
    this.text.setValue('');
    this.selectedId.set(null);
    this.onChange(null);
  }

  writeValue(id: string | null): void {
    this.selectedId.set(id);
    if (!id) {
      this.text.setValue('', { emitEvent: false });
      return;
    }
    this.http.get<Named>(`/api/${this.kind()}/${id}`).subscribe({
      next: (item) => this.text.setValue(toOption(item), { emitEvent: false }),
      error: () => this.text.setValue('', { emitEvent: false }),
    });
  }

  registerOnChange(fn: (id: string | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
    if (disabled) this.text.disable({ emitEvent: false });
    else this.text.enable({ emitEvent: false });
  }
}
