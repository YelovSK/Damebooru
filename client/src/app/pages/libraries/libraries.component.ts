import { Component, ChangeDetectionStrategy, inject, input, linkedSignal, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { Subject, catchError, of, switchMap } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type Library, LibraryKind } from '@services/api/damebooru/models';
import { ButtonDirective, FieldLabelDirective, InputDirective, TooltipDirective } from '@shared/directives';
import { ToastService } from '@services/toast.service';
import { ConfirmService } from '@services/confirm.service';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { DateTimePipe } from '@shared/pipes/date-time.pipe';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';
import { ModalComponent } from '@shared/components/modal/modal.component';
import { AppPaths } from '@app/app.paths';
import { PageComponent } from '@shared/components/page/page.component';
import { LibraryPathListComponent, type LibraryPathItem } from './library-path-list.component';

@Component({
    selector: 'app-libraries',
    standalone: true,
    imports: [PageComponent, CommonModule, FormsModule, RouterLink, CdkMenu, CdkMenuItem, CdkMenuTrigger, ButtonDirective, FieldLabelDirective, InputDirective, TooltipDirective, FileSizePipe, DateTimePipe, FormCheckboxComponent, AutocompleteComponent, ModalComponent, LibraryPathListComponent],
    templateUrl: './libraries.component.html',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibrariesComponent {
    private readonly damebooru = inject(DamebooruService);
    private readonly toast = inject(ToastService);
    private readonly confirmService = inject(ConfirmService);
    readonly appPaths = AppPaths;
    readonly libraryKind = LibraryKind;

    readonly data = input.required<Library[]>();
    readonly libraries = linkedSignal(() => this.data());

    createDialogOpen = signal(false);
    newLibraryName = signal('');
    newLibraryPath = signal('');
    newLibraryIsInbox = signal(false);
    isCreating = signal(false);
    isLoading = signal(false);
    scanningLibraryId = signal<number | null>(null);
    renamingLibraryId = signal<number | null>(null);
    changingKindLibraryId = signal<number | null>(null);
    editingLibraryId = signal<number | null>(null);
    addingIgnoredPathLibraryId = signal<number | null>(null);
    removingIgnoredPathId = signal<number | null>(null);
    addingAutoTagExcludedPathLibraryId = signal<number | null>(null);
    removingAutoTagExcludedPathId = signal<number | null>(null);
    editingName = signal('');

    private pathQuery = new Subject<string>();
    pathSuggestions = toSignal(
        this.pathQuery.pipe(
            switchMap(path => this.damebooru.getDirectorySuggestions(path).pipe(
                catchError(() => of([] as string[]))
            ))
        ),
        { initialValue: [] as string[] }
    );

    onPathQueryChange(path: string) {
        this.pathQuery.next(path);
    }

    openCreateDialog() {
        this.newLibraryName.set('');
        this.newLibraryPath.set('');
        this.newLibraryIsInbox.set(false);
        this.createDialogOpen.set(true);
    }

    createLibrary() {
        const name = this.newLibraryName().trim();
        const path = this.newLibraryPath().trim();
        if (!name || !path) return;

        this.isCreating.set(true);
        const kind = this.newLibraryIsInbox() ? LibraryKind.Inbox : LibraryKind.Library;
        this.damebooru.createLibrary(name, path, kind).subscribe({
            next: () => {
                this.toast.success(`Library "${name}" created`);
                this.createDialogOpen.set(false);
                this.reload();
                this.isCreating.set(false);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to create library');
                this.isCreating.set(false);
            }
        });
    }

    deleteLibrary(lib: Library) {
        this.confirmService.confirm({
            title: 'Delete Library',
            message: `Delete library "${lib.name}"? This removes the library record, not your files.`,
            confirmText: 'Delete',
            variant: 'danger',
        }).subscribe(confirmed => {
            if (!confirmed) return;

            this.isLoading.set(true);
            this.damebooru.deleteLibrary(lib.id).subscribe({
                next: () => {
                    this.toast.success(`Library "${lib.name}" deleted`);
                    this.reload();
                    this.isLoading.set(false);
                },
                error: (err) => {
                    this.toast.error(err.error?.description || 'Failed to delete library');
                    this.isLoading.set(false);
                }
            });
        });
    }

    scanLibrary(lib: Library) {
        this.scanningLibraryId.set(lib.id);
        this.damebooru.scanLibrary(lib.id).subscribe({
            next: () => {
                this.toast.success(`Scan queued for "${lib.name}"`);
                this.scanningLibraryId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to queue scan');
                this.scanningLibraryId.set(null);
            }
        });
    }

    startRename(lib: Library) {
        this.editingLibraryId.set(lib.id);
        this.editingName.set(lib.name);
    }

    cancelRename() {
        this.editingLibraryId.set(null);
        this.editingName.set('');
    }

    saveRename(lib: Library) {
        const name = this.editingName().trim();
        if (!name) return;

        this.renamingLibraryId.set(lib.id);
        this.damebooru.renameLibrary(lib.id, name).subscribe({
            next: () => {
                this.toast.success(`Renamed to "${name}"`);
                this.cancelRename();
                this.renamingLibraryId.set(null);
                this.reload();
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to rename library');
                this.renamingLibraryId.set(null);
            }
        });
    }

    toggleInbox(lib: Library) {
        const kind = lib.kind === LibraryKind.Inbox ? LibraryKind.Library : LibraryKind.Inbox;
        this.changingKindLibraryId.set(lib.id);
        this.damebooru.setLibraryKind(lib.id, kind).subscribe({
            next: () => {
                this.toast.success(kind === LibraryKind.Inbox
                    ? `"${lib.name}" is now an inbox; updating folder tags`
                    : `"${lib.name}" is now a library; updating folder tags`);
                this.changingKindLibraryId.set(null);
                this.reload();
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to change library kind');
                this.changingKindLibraryId.set(null);
            }
        });
    }

    addIgnoredPath(lib: Library, path: string) {
        this.addingIgnoredPathLibraryId.set(lib.id);
        this.damebooru.addLibraryIgnoredPath(lib.id, path).subscribe({
            next: (result) => {
                const removedText = result.removedPostCount > 0
                    ? ` and removed ${result.removedPostCount} post(s)`
                    : '';
                this.toast.success(`Ignored path "${result.ignoredPath.path}" saved${removedText}`);
                this.reload();
                this.addingIgnoredPathLibraryId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to add ignored path');
                this.addingIgnoredPathLibraryId.set(null);
            }
        });
    }

    removeIgnoredPath(lib: Library, item: LibraryPathItem) {
        this.removingIgnoredPathId.set(item.id);
        this.damebooru.removeLibraryIgnoredPath(lib.id, item.id).subscribe({
            next: () => {
                this.toast.success(`Removed ignored path "${item.path}"`);
                this.reload();
                this.removingIgnoredPathId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to remove ignored path');
                this.removingIgnoredPathId.set(null);
            }
        });
    }

    addAutoTagExcludedPath(lib: Library, path: string) {
        this.addingAutoTagExcludedPathLibraryId.set(lib.id);
        this.damebooru.addLibraryAutoTagExcludedPath(lib.id, path).subscribe({
            next: (result) => {
                this.toast.success(`Auto-tag excluded path "${result.excludedPath.path}" saved`);
                this.reload();
                this.addingAutoTagExcludedPathLibraryId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to add auto-tag excluded path');
                this.addingAutoTagExcludedPathLibraryId.set(null);
            }
        });
    }

    removeAutoTagExcludedPath(lib: Library, item: LibraryPathItem) {
        this.removingAutoTagExcludedPathId.set(item.id);
        this.damebooru.removeLibraryAutoTagExcludedPath(lib.id, item.id).subscribe({
            next: () => {
                this.toast.success(`Removed auto-tag excluded path "${item.path}"`);
                this.reload();
                this.removingAutoTagExcludedPathId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to remove auto-tag excluded path');
                this.removingAutoTagExcludedPathId.set(null);
            }
        });
    }

    private reload() {
        this.damebooru.getLibraries().subscribe({
            next: libraries => this.libraries.set(libraries),
            error: () => this.toast.error('Failed to load libraries'),
        });
    }
}
