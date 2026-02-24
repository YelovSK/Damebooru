import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

export type DataTableSortDirection = 'asc' | 'desc';

export type DataTableSortMode = 'client' | 'external';

export interface DataTableColumn<T, K extends string = string> {
  key: K;
  label: string;
  sortable?: boolean;
  align?: 'left' | 'center' | 'right';
  value: (row: T) => string | number | null | undefined;
}

export interface DataTableSort<K extends string = string> {
  key: K;
  direction: DataTableSortDirection;
}

@Component({
  selector: 'app-data-table',
  standalone: true,
  templateUrl: './data-table.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableComponent<T extends object, K extends string = string> {
  columns = input.required<DataTableColumn<T, K>[]>();
  rows = input<T[]>([]);
  emptyText = input('No data');
  rowClickable = input(true);
  initialSort = input<DataTableSort<K> | null>(null);
  sortMode = input<DataTableSortMode>('client');
  trackBy = input<(row: T, index: number) => string | number>((_row, index) => index);

  rowClick = output<T>();
  sortChange = output<DataTableSort<K>>();

  private sortState = signal<DataTableSort<K> | null>(null);

  sortedRows = computed(() => {
    const rows = this.rows();
    const sort = this.sortState() ?? this.initialSort();
    if (!sort || this.sortMode() === 'external') return rows;

    const column = this.columns().find(col => col.key === sort.key);
    if (!column?.sortable) return rows;

    return [...rows].sort((a, b) => {
      const aValue = column.value(a);
      const bValue = column.value(b);
      const aNormalized = typeof aValue === 'string' ? aValue.toLowerCase() : aValue ?? '';
      const bNormalized = typeof bValue === 'string' ? bValue.toLowerCase() : bValue ?? '';

      if (aNormalized < bNormalized) return sort.direction === 'asc' ? -1 : 1;
      if (aNormalized > bNormalized) return sort.direction === 'asc' ? 1 : -1;
      return 0;
    });
  });

  onHeaderClick(column: DataTableColumn<T, K>): void {
    if (!column.sortable) return;

    const current = this.sortState() ?? this.initialSort();
    const next: DataTableSort<K> =
      current?.key === column.key
        ? { key: column.key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
        : { key: column.key, direction: 'asc' };

    this.sortState.set(next);
    this.sortChange.emit(next);
  }

  onRowClick(row: T): void {
    if (!this.rowClickable()) return;
    this.rowClick.emit(row);
  }

  getAlignClass(column: DataTableColumn<T, K>): string {
    if (column.align === 'center') return 'text-center';
    if (column.align === 'right') return 'text-right';
    return 'text-left';
  }

  isSorted(column: DataTableColumn<T, K>): boolean {
    const sort = this.sortState() ?? this.initialSort();
    return sort?.key === column.key;
  }

  sortDirection(column: DataTableColumn<T, K>): DataTableSortDirection | null {
    const sort = this.sortState() ?? this.initialSort();
    return sort?.key === column.key ? sort.direction : null;
  }
}
