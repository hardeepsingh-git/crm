import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Observable } from 'rxjs';

export interface ConfirmData {
  title: string;
  message: string;
  confirmText?: string;
  danger?: boolean;
  /** When set, the dialog asks for a value and returns it instead of `true`. */
  input?: { label: string; type?: 'text' | 'password'; required?: boolean; hint?: string };
}

@Component({
  selector: 'app-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p>{{ data.message }}</p>
      @if (data.input; as field) {
        <mat-form-field appearance="outline" class="full">
          <mat-label>{{ field.label }}</mat-label>
          <input matInput [type]="field.type ?? 'text'" [value]="value()" (input)="value.set($any($event.target).value)" cdkFocusInitial />
          @if (field.hint) {
            <mat-hint>{{ field.hint }}</mat-hint>
          }
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="undefined">Cancel</button>
      <button mat-flat-button [class.danger]="data.danger" [disabled]="data.input?.required && !value().trim()" (click)="confirm()">
        {{ data.confirmText ?? 'Confirm' }}
      </button>
    </mat-dialog-actions>
  `,
})
export class ConfirmDialog {
  readonly data = inject<ConfirmData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ConfirmDialog, string | true>);
  readonly value = signal('');

  confirm(): void {
    this.ref.close(this.data.input ? this.value() : true);
  }
}

export function confirm(dialog: MatDialog, data: ConfirmData): Observable<string | true | undefined> {
  return dialog.open<ConfirmDialog, ConfirmData, string | true>(ConfirmDialog, { data, width: '440px' }).afterClosed();
}
