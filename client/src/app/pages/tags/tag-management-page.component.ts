import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { ManagedTag, ManagedTagCategory } from '@services/api/bakabooru/models';
import { ToastService } from '@services/toast.service';
import { ButtonComponent } from '@shared/components/button/button.component';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { TabsComponent } from '@shared/components/tabs/tabs.component';
import { TabComponent } from '@shared/components/tabs/tab.component';
import { SearchInputComponent } from '@shared/components/search-input/search-input.component';
import { FormDropdownComponent, FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { DataTableColumn, DataTableComponent } from '@shared/components/data-table/data-table.component';
import { ModalComponent } from '@shared/components/modal/modal.component';

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
  ],
  templateUrl: './tag-management-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TagManagementPageComponent implements OnInit {
  private readonly api = inject(BakabooruService);
  private readonly toast = inject(ToastService);

  tags = signal<ManagedTag[]>([]);
  categories = signal<ManagedTagCategory[]>([]);

  tagsLoading = signal(false);
  categoriesLoading = signal(false);

  // Tags tab state
  tagsQuery = signal('');
  tagsPage = signal(1);
  readonly tagsPageSize = signal(50);
  tagsTotal = signal(0);
  tagsTotalPages = computed(() => Math.max(1, Math.ceil(this.tagsTotal() / this.tagsPageSize())));

  // Categories tab state
  categoriesQuery = signal('');
  categoriesPage = signal(1);
  readonly categoriesPageSize = signal(30);
  filteredCategories = computed(() => {
    const query = this.categoriesQuery().toLowerCase();
    if (!query) return this.categories();
    return this.categories().filter(category => category.name.toLowerCase().includes(query));
  });
  pagedCategories = computed(() => {
    const start = (this.categoriesPage() - 1) * this.categoriesPageSize();
    return this.filteredCategories().slice(start, start + this.categoriesPageSize());
  });
  categoriesTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.filteredCategories().length / this.categoriesPageSize())),
  );

  categorySelectOptions = computed<FormDropdownOption<number | null>[]>(() =>
    this.categories().map(category => ({ label: category.name, value: category.id })),
  );

  tagColumns: DataTableColumn<ManagedTag>[] = [
    { key: 'name', label: 'Name', sortable: true, value: row => row.name },
    { key: 'category', label: 'Category', sortable: true, value: row => row.categoryName || 'Uncategorized' },
    { key: 'usages', label: 'Usages', sortable: true, align: 'right', value: row => row.usages },
  ];

  categoryColumns: DataTableColumn<ManagedTagCategory>[] = [
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

  ngOnInit(): void {
    this.loadTags();
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

  mergeTag(): void {
    const model = this.editTag();
    if (!model) return;

    const targetName = model.mergeTargetName.trim().toLowerCase();
    if (!targetName) {
      this.toast.warning('Enter target tag name');
      return;
    }

    this.api.getManagedTags(targetName, 0, 30).subscribe({
      next: result => {
        const target = result.results.find(tag => tag.name.toLowerCase() === targetName);
        if (!target) {
          this.toast.error(`Target tag "${targetName}" not found`);
          return;
        }
        if (target.id === model.id) {
          this.toast.warning('Source and target tags are the same');
          return;
        }

        this.api.mergeTag(model.id, target.id).subscribe({
          next: () => {
            this.toast.success('Tag merged');
            this.editTag.set(null);
            this.loadTags();
            this.loadCategories();
          },
          error: err => this.toast.error(err?.error || 'Failed to merge tag'),
        });
      },
      error: () => this.toast.error('Failed to resolve target tag'),
    });
  }

  deleteTagFromEdit(): void {
    const model = this.editTag();
    if (!model) return;
    if (!confirm(`Delete tag "${model.name}"? This removes it from all posts.`)) return;

    this.api.deleteManagedTag(model.id).subscribe({
      next: () => {
        this.toast.success('Tag deleted');
        this.editTag.set(null);
        this.loadTags();
        this.loadCategories();
      },
      error: err => this.toast.error(err?.error || 'Failed to delete tag'),
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
    if (!confirm(`Delete category "${model.name}"? Tags in it become uncategorized.`)) return;

    this.api.deleteTagCategory(model.id).subscribe({
      next: () => {
        this.toast.success('Category deleted');
        this.editCategory.set(null);
        this.loadCategories();
        this.loadTags();
      },
      error: err => this.toast.error(err?.error || 'Failed to delete category'),
    });
  }

  private loadTags(): void {
    this.tagsLoading.set(true);
    const offset = (this.tagsPage() - 1) * this.tagsPageSize();
    this.api.getManagedTags(this.tagsQuery(), offset, this.tagsPageSize()).subscribe({
      next: result => {
        this.tags.set(result.results);
        this.tagsTotal.set(result.total);
        this.tagsLoading.set(false);
      },
      error: () => {
        this.toast.error('Failed to load tags');
        this.tagsLoading.set(false);
      },
    });
  }

  private loadCategories(): void {
    this.categoriesLoading.set(true);
    this.api.getManagedTagCategories().subscribe({
      next: categories => {
        this.categories.set(categories);
        this.categoriesLoading.set(false);
      },
      error: () => {
        this.toast.error('Failed to load categories');
        this.categoriesLoading.set(false);
      },
    });
  }
}
