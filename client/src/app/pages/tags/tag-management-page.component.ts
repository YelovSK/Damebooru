import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ManagedTag, ManagedTagCategory } from '@services/api/damebooru/models';
import { ToastService } from '@services/toast.service';
import { ButtonComponent } from '@shared/components/button/button.component';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { TabsComponent } from '@shared/components/tabs/tabs.component';
import { TabComponent } from '@shared/components/tabs/tab.component';
import { SearchInputComponent } from '@shared/components/search-input/search-input.component';
import { FormDropdownComponent, FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { DataTableColumn, DataTableComponent, DataTableSort, DataTableSortDirection } from '@shared/components/data-table/data-table.component';
import { ModalComponent } from '@shared/components/modal/modal.component';
import { ConfirmService } from '@services/confirm.service';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';

type EditTagModel = {
  id: number;
  name: string;
  categoryId: number | null;
  mergeTargetName: string;
};

type EditCategoryModel = {
  id: number;
  name: string;
  color: string;
  order: number;
};

type TagSortKey = 'name' | 'category' | 'usages';
type CategorySortKey = 'name' | 'color' | 'order' | 'tagCount';

@Component({
  selector: 'app-tag-management-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonComponent,
    PaginatorComponent,
    TabsComponent,
    TabComponent,
    SearchInputComponent,
    FormDropdownComponent,
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

  tags = signal<ManagedTag[]>([]);
  categories = signal<ManagedTagCategory[]>([]);

  // Tags tab state
  tagsQuery = signal('');
  tagsPage = signal(1);
  readonly tagsPageSize = signal(50);
  tagsTotal = signal(0);
  readonly tagsSort = signal<DataTableSort<TagSortKey>>({ key: 'usages', direction: 'desc' });
  tagsTotalPages = computed(() => Math.max(1, Math.ceil(this.tagsTotal() / this.tagsPageSize())));

  // Categories tab state
  categoriesQuery = signal('');
  categoriesPage = signal(1);
  readonly categoriesPageSize = signal(30);
  readonly categoriesSort = signal<DataTableSort<CategorySortKey>>({ key: 'name', direction: 'asc' });
  filteredCategories = computed(() => {
    const query = this.categoriesQuery().toLowerCase();
    if (!query) return this.categories();
    return this.categories().filter(category => category.name.toLowerCase().includes(query));
  });
  sortedFilteredCategories = computed(() => {
    const sort = this.categoriesSort();
    const rows = [...this.filteredCategories()];

    return rows.sort((a, b) => {
      const direction = sort.direction === 'asc' ? 1 : -1;

      const aValue = this.getCategorySortValue(a, sort.key);
      const bValue = this.getCategorySortValue(b, sort.key);

      if (aValue < bValue) return -1 * direction;
      if (aValue > bValue) return 1 * direction;
      return 0;
    });
  });
  pagedCategories = computed(() => {
    const start = (this.categoriesPage() - 1) * this.categoriesPageSize();
    return this.sortedFilteredCategories().slice(start, start + this.categoriesPageSize());
  });
  categoriesTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.sortedFilteredCategories().length / this.categoriesPageSize())),
  );

  categorySelectOptions = computed<FormDropdownOption<number | null>[]>(() =>
    this.categories().map(category => ({ label: category.name, value: category.id })),
  );

  tagColumns: DataTableColumn<ManagedTag, TagSortKey>[] = [
    { key: 'name', label: 'Name', sortable: true, value: row => row.name },
    { key: 'category', label: 'Category', sortable: true, value: row => row.categoryName || 'Uncategorized' },
    { key: 'usages', label: 'Usages', sortable: true, align: 'right', value: row => row.usages },
  ];

  categoryColumns: DataTableColumn<ManagedTagCategory, CategorySortKey>[] = [
    { key: 'name', label: 'Name', sortable: true, value: row => row.name },
    { key: 'color', label: 'Color', sortable: true, value: row => row.color },
    { key: 'order', label: 'Order', sortable: true, align: 'right', value: row => row.order },
    { key: 'tagCount', label: 'Tag Count', sortable: true, align: 'right', value: row => row.tagCount },
  ];

  // Create modals
  createTagOpen = signal(false);
  createTagName = signal('');
  createTagCategoryId = signal<number | null>(null);

  createCategoryOpen = signal(false);
  createCategoryName = signal('');
  createCategoryColor = signal('#888888');
  createCategoryOrder = signal(0);

  // Edit modals
  editTag = signal<EditTagModel | null>(null);
  editCategory = signal<EditCategoryModel | null>(null);

  // Merge tag autocomplete
  mergeTagSuggestions = signal<ManagedTag[]>([]);
  mergeTargetId = signal<number | null>(null);
  private tagsTabInitialized = false;
  private categoriesTabInitialized = false;

  onTagsTabInit(): void {
    if (this.tagsTabInitialized) {
      return;
    }

    this.tagsTabInitialized = true;
    this.loadTags();
  }

  onCategoriesTabInit(): void {
    if (this.categoriesTabInitialized) {
      return;
    }

    this.categoriesTabInitialized = true;
    this.loadCategories();
  }

  onTagsSearch(query: string): void {
    this.tagsQuery.set(query);
    this.tagsPage.set(1);
    this.loadTags();
  }

  onCategoriesSearch(query: string): void {
    this.categoriesQuery.set(query);
    this.categoriesPage.set(1);
  }

  onTagsPageChange(page: number): void {
    this.tagsPage.set(page);
    this.loadTags();
  }

  onCategoriesPageChange(page: number): void {
    this.categoriesPage.set(page);
  }

  onTagsSortChange(sort: DataTableSort<TagSortKey>): void {
    this.tagsSort.set(sort);
    this.tagsPage.set(1);
    this.loadTags();
  }

  onCategoriesSortChange(sort: DataTableSort<CategorySortKey>): void {
    this.categoriesSort.set(sort);
    this.categoriesPage.set(1);
  }

  trackTagRow = (row: ManagedTag): number => row.id;
  trackCategoryRow = (row: ManagedTagCategory): number => row.id;

  // Tag CRUD
  openCreateTagModal(): void {
    this.createTagName.set('');
    this.createTagCategoryId.set(null);
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

    this.api.createManagedTag(name, this.createTagCategoryId()).subscribe({
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
      categoryId: tag.categoryId,
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

    this.api.updateManagedTag(model.id, name, model.categoryId).subscribe({
      next: () => {
        this.toast.success('Tag updated');
        this.editTag.set(null);
        this.loadTags();
        this.loadCategories();
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
        this.mergeTagSuggestions.set(
          result.results.filter(tag => tag.id !== model?.id)
        );
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
        this.loadCategories();
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
          this.loadCategories();
        },
        error: err => this.toast.error(err?.error || 'Failed to delete tag'),
      });
    });
  }

  // Category CRUD
  openCreateCategoryModal(): void {
    this.createCategoryName.set('');
    this.createCategoryColor.set('#888888');
    this.createCategoryOrder.set(0);
    this.createCategoryOpen.set(true);
  }

  closeCreateCategoryModal(): void {
    this.createCategoryOpen.set(false);
  }

  createCategory(): void {
    const name = this.createCategoryName().trim();
    if (!name) {
      this.toast.warning('Category name is required');
      return;
    }

    this.api.createTagCategory(name, this.createCategoryColor(), this.createCategoryOrder()).subscribe({
      next: () => {
        this.toast.success('Category created');
        this.createCategoryOpen.set(false);
        this.loadCategories();
      },
      error: err => this.toast.error(err?.error || 'Failed to create category'),
    });
  }

  openEditCategoryModal(category: ManagedTagCategory): void {
    this.editCategory.set({
      id: category.id,
      name: category.name,
      color: category.color,
      order: category.order,
    });
  }

  closeEditCategoryModal(): void {
    this.editCategory.set(null);
  }

  updateEditCategory(patch: Partial<EditCategoryModel>): void {
    this.editCategory.update(model => (model ? { ...model, ...patch } : null));
  }

  saveCategory(): void {
    const model = this.editCategory();
    if (!model) return;

    const name = model.name.trim();
    if (!name) {
      this.toast.warning('Category name is required');
      return;
    }

    this.api.updateTagCategory(model.id, name, model.color, model.order).subscribe({
      next: () => {
        this.toast.success('Category updated');
        this.editCategory.set(null);
        this.loadCategories();
        this.loadTags();
      },
      error: err => this.toast.error(err?.error || 'Failed to update category'),
    });
  }

  deleteCategoryFromEdit(): void {
    const model = this.editCategory();
    if (!model) return;

    this.confirmService.confirm({
      title: 'Delete Category',
      message: `Delete category "${model.name}"? Tags in it become uncategorized.`,
      confirmText: 'Delete',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.api.deleteTagCategory(model.id).subscribe({
        next: () => {
          this.toast.success('Category deleted');
          this.editCategory.set(null);
          this.loadCategories();
          this.loadTags();
        },
        error: err => this.toast.error(err?.error || 'Failed to delete category'),
      });
    });
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

  private loadCategories(): void {
    this.api.getManagedTagCategories().subscribe({
      next: categories => {
        this.categories.set(categories);
      },
      error: () => {
        this.toast.error('Failed to load categories');
      },
    });
  }

  private getCategorySortValue(category: ManagedTagCategory, key: CategorySortKey): string | number {
    switch (key) {
      case 'name':
        return category.name.toLowerCase();
      case 'color':
        return category.color.toLowerCase();
      case 'order':
        return category.order;
      case 'tagCount':
        return category.tagCount;
    }
  }
}
