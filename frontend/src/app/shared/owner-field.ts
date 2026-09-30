import { Component, inject, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { UserLookupService } from '../core/api';
import { AuthService } from '../core/auth.service';

/** Owner selector; only managers/admins may assign records to other users, so it is hidden for reps. */
@Component({
  selector: 'app-owner-field',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatSelectModule],
  template: `
    @if (auth.isManager()) {
      <mat-form-field appearance="outline" class="full">
        <mat-label>Owner</mat-label>
        <mat-select [formControl]="control()">
          <mat-option [value]="null">Me</mat-option>
          @for (u of lookup.users.value(); track u.id) {
            <mat-option [value]="u.id">{{ u.fullName }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    }
  `,
})
export class OwnerField {
  readonly control = input.required<FormControl<string | null>>();
  readonly auth = inject(AuthService);
  readonly lookup = inject(UserLookupService);
}
