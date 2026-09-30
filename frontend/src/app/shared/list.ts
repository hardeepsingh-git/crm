import { Pipe, PipeTransform, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { PageEvent } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';
import { debounceTime } from 'rxjs';
import { QueryParams, toParams } from '../core/api';

/** Search/paging/sorting state shared by all list pages. Must be created in an injection context. */
export class ListState {
  readonly search = signal('');
  readonly mine = signal(false);
  readonly page = signal(0);
  readonly pageSize = signal(20);
  readonly sort = signal<Sort>({ active: '', direction: '' });
  /** Custom-field filters, sent as ?cf.key=value. */
  readonly cf = signal<Record<string, string>>({});
  private readonly debouncedSearch = toSignal(toObservable(this.search).pipe(debounceTime(300)), { initialValue: '' });

  params(extra: QueryParams = {}) {
    const sort = this.sort();
    const cf = Object.fromEntries(Object.entries(this.cf()).map(([k, v]) => [`cf.${k}`, v]));
    return toParams({
      search: this.debouncedSearch(),
      mine: this.mine(),
      page: this.page() + 1,
      pageSize: this.pageSize(),
      sortBy: sort.direction ? sort.active : null,
      sortDir: sort.direction || null,
      ...cf,
      ...extra,
    });
  }

  setCf(key: string, value: string | null): void {
    this.cf.update((current) => {
      const next = { ...current };
      if (value) next[key] = value;
      else delete next[key];
      return next;
    });
    this.page.set(0);
  }

  setSearch(value: string): void {
    this.search.set(value);
    this.page.set(0);
  }

  setMine(value: boolean): void {
    this.mine.set(value);
    this.page.set(0);
  }

  onPage(e: PageEvent): void {
    this.page.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  onSort(s: Sort): void {
    this.sort.set(s);
    this.page.set(0);
  }

  resetPage(): void {
    this.page.set(0);
  }
}

/** "ClosedWon" -> "Closed Won", "ColdCall" -> "Cold Call". */
@Pipe({ name: 'humanize' })
export class HumanizePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? value.replace(/([a-z])([A-Z])/g, '$1 $2') : '';
  }
}

export function inputValue(event: Event): string {
  return (event.target as HTMLInputElement).value;
}

/** Formats an ISO timestamp for an <input type="datetime-local">, in the browser's timezone. */
export function toLocalInput(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function fromLocalInput(value: string | null): string | null {
  return value ? new Date(value).toISOString() : null;
}

export function todayPlus(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() + days);
  return d.toISOString().slice(0, 10);
}

/** Picklist options plus the record's current value, so values removed from the picklist stay selectable. */
export function withCurrent(options: string[], current: string | null | undefined): string[] {
  return current && !options.includes(current) ? [...options, current] : options;
}
