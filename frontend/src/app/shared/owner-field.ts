import { Component, inject, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { UserLookupService } from '../core/api';
import { AuthService } from '../core/auth.service';

/** Owner selector; hidden unless the user's update scope (Group/All) lets them assign records to others. */
@Component({
  selector: 'app-owner-field',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatSelectModule],
  template: `
    @if (auth.canAssign(resource())) {
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
  /** Permission resource (object key) the record belongs to. */
  readonly resource = input.required<string>();
  readonly auth = inject(AuthService);
  readonly lookup = inject(UserLookupService);
}
