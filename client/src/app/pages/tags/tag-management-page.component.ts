import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ManagedTag, TagCategoryKind } from '@services/api/damebooru/models';
import { ToastService } from '@services/toast.service';
import { ButtonComponent } from '@shared/components/button/button.component';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { SearchInputComponent } from '@shared/components/search-input/search-input.component';
import { FormDropdownComponent, FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { FormInputComponent } from '@shared/components/form-input/form-input.component';
import { DataTableColumn, DataTableComponent, DataTableSort } from '@shared/components/data-table/data-table.component';
import { ModalComponent } from '@shared/components/modal/modal.component';
import { ConfirmService } from '@services/confirm.service';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';

interface EditTagModel {
  id: number;
  name: string;
  category: TagCategoryKind;
  mergeTargetName: string;
}

type TagSortKey = 'name' | 'category' | 'usages';

const CATEGORY_OPTIONS: FormDropdownOption<TagCategoryKind>[] = [
  { label: 'General', value: TagCategoryKind.General },
  { label: 'Artist', value: TagCategoryKind.Artist },
  { label: 'Character', value: TagCategoryKind.Character },
  { label: 'Copyright', value: TagCategoryKind.Copyright },
  { label: 'Meta', value: TagCategoryKind.Meta },
];

@Component({
  selector: 'app-tag-management-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonComponent,
    PaginatorComponent,
    SearchInputComponent,
    FormDropdownComponent,
    FormInputComponent,
    DataTableComponent,
    ModalComponent,
    AutocompleteComponent,
  ],
  templateUrl: './tag-management-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TagManagementPageComponent {
  private readonly api = inject(DamebooruService);
  private readonly toast = inject(ToastService);
  private readonly confirmService = inject(ConfirmService);

  readonly categoryOptions = CATEGORY_OPTIONS;

  tags = signal<ManagedTag[]>([]);
  tagsQuery = signal('');
  tagsPage = signal(1);
  readonly tagsPageSize = signal(50);
  tagsTotal = signal(0);
  readonly tagsSort = signal<DataTableSort<TagSortKey>>({ key: 'usages', direction: 'desc' });
  tagsTotalPages = computed(() => Math.max(1, Math.ceil(this.tagsTotal() / this.tagsPageSize())));

  tagColumns: DataTableColumn<ManagedTag, TagSortKey>[] = [
    { key: 'name', label: 'Name', sortable: true, value: row => row.name },
    { key: 'category', label: 'Category', sortable: true, value: row => this.getCategoryLabel(row.category) },
    { key: 'usages', label: 'Usages', sortable: true, align: 'right', value: row => row.usages },
  ];

  createTagOpen = signal(false);
  createTagName = signal('');
  createTagCategory = signal(TagCategoryKind.General);

  editTag = signal<EditTagModel | null>(null);
  mergeTagSuggestions = signal<ManagedTag[]>([]);
  mergeTargetId = signal<number | null>(null);

  constructor() {
    this.loadTags();
  }

  trackTagRow = (row: ManagedTag): number => row.id;

  onTagsSearch(query: string): void {
    this.tagsQuery.set(query);
    this.tagsPage.set(1);
    this.loadTags();
  }

  onTagsPageChange(page: number): void {
    this.tagsPage.set(page);
    this.loadTags();
  }

  onTagsSortChange(sort: DataTableSort<TagSortKey>): void {
    this.tagsSort.set(sort);
    this.tagsPage.set(1);
    this.loadTags();
  }

  openCreateTagModal(): void {
    this.createTagName.set('');
    this.createTagCategory.set(TagCategoryKind.General);
    this.createTagOpen.set(true);
  }

  closeCreateTagModal(): void {
    this.createTagOpen.set(false);
  }

  createTag(): void {
    const name = this.createTagName().trim().toLowerCase();
    if (!name) {
      this.toast.warning('Tag name is required');
      return;
    }

    this.api.createManagedTag(name, this.createTagCategory()).subscribe({
      next: () => {
        this.toast.success('Tag created');
        this.createTagOpen.set(false);
        this.loadTags();
      },
      error: err => this.toast.error(err?.error || 'Failed to create tag'),
    });
  }

  openEditTagModal(tag: ManagedTag): void {
    this.editTag.set({
      id: tag.id,
      name: tag.name,
      category: tag.category,
      mergeTargetName: '',
    });
    this.mergeTargetId.set(null);
    this.mergeTagSuggestions.set([]);
  }

  closeEditTagModal(): void {
    this.editTag.set(null);
  }

  updateEditTag(patch: Partial<EditTagModel>): void {
    this.editTag.update(model => (model ? { ...model, ...patch } : null));
  }

  saveTag(): void {
    const model = this.editTag();
    if (!model) return;

    const name = model.name.trim().toLowerCase();
    if (!name) {
      this.toast.warning('Tag name is required');
      return;
    }

    this.api.updateManagedTag(model.id, name, model.category).subscribe({
      next: () => {
        this.toast.success('Tag updated');
        this.editTag.set(null);
        this.loadTags();
      },
      error: err => this.toast.error(err?.error || 'Failed to update tag'),
    });
  }

  onMergeQueryChange(query: string): void {
    if (!query || query.length < 1) {
      this.mergeTagSuggestions.set([]);
      return;
    }

    this.api.getManagedTags(query, 0, 10).subscribe({
      next: result => {
        const model = this.editTag();
        this.mergeTagSuggestions.set(result.results.filter(tag => tag.id !== model?.id));
      },
    });
  }

  onMergeSelection(tag: ManagedTag): void {
    this.mergeTargetId.set(tag.id);
    this.updateEditTag({ mergeTargetName: tag.name });
  }

  mergeTag(): void {
    const model = this.editTag();
    if (!model) return;

    const targetId = this.mergeTargetId();
    if (!targetId) {
      this.toast.warning('Select a target tag from the suggestions');
      return;
    }

    if (targetId === model.id) {
      this.toast.warning('Source and target tags are the same');
      return;
    }

    this.api.mergeTag(model.id, targetId).subscribe({
      next: () => {
        this.toast.success('Tag merged');
        this.editTag.set(null);
        this.mergeTargetId.set(null);
        this.loadTags();
      },
      error: err => this.toast.error(err?.error || 'Failed to merge tag'),
    });
  }

  deleteTagFromEdit(): void {
    const model = this.editTag();
    if (!model) return;

    this.confirmService.confirm({
      title: 'Delete Tag',
      message: `Delete tag "${model.name}"? This removes it from all posts.`,
      confirmText: 'Delete',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.api.deleteManagedTag(model.id).subscribe({
        next: () => {
          this.toast.success('Tag deleted');
          this.editTag.set(null);
          this.loadTags();
        },
        error: err => this.toast.error(err?.error || 'Failed to delete tag'),
      });
    });
  }

  getCategoryLabel(category: TagCategoryKind): string {
    return CATEGORY_OPTIONS.find(option => option.value === category)?.label ?? 'General';
  }

  private loadTags(): void {
    const offset = (this.tagsPage() - 1) * this.tagsPageSize();
    const sort = this.tagsSort();
    this.api.getManagedTags(this.tagsQuery(), offset, this.tagsPageSize(), sort.key, sort.direction).subscribe({
      next: result => {
        this.tags.set(result.results);
        this.tagsTotal.set(result.total);
      },
      error: () => {
        this.toast.error('Failed to load tags');
      },
    });
  }
}
