import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { Library } from '@services/api/damebooru/models';
import { ButtonComponent } from '@shared/components/button/button.component';
import { ToastService } from '@services/toast.service';
import { ConfirmService } from '@services/confirm.service';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { AppPaths } from '@app/app.paths';

@Component({
    selector: 'app-libraries',
    standalone: true,
    imports: [CommonModule, FormsModule, ButtonComponent, FileSizePipe],
    templateUrl: './libraries.component.html',
    styleUrl: './libraries.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibrariesComponent {
    private readonly damebooru = inject(DamebooruService);
    private readonly toast = inject(ToastService);
    private readonly confirmService = inject(ConfirmService);
    readonly appPaths = AppPaths;

    newLibraryName = signal('');
    newLibraryPath = signal('');
    isCreating = signal(false);
    isLoading = signal(false);
    scanningLibraryId = signal<number | null>(null);
    renamingLibraryId = signal<number | null>(null);
    editingLibraryId = signal<number | null>(null);
    addingIgnoredPathLibraryId = signal<number | null>(null);
    removingIgnoredPathId = signal<number | null>(null);
    ignoredPathDrafts = signal<Record<number, string>>({});
    editingName = signal('');
    private refreshTrigger = signal(0);

    libraries = toSignal(
        toObservable(this.refreshTrigger).pipe(
            switchMap(() => this.damebooru.getLibraries()),
            catchError(err => {
                console.error(err);
                this.toast.error('Failed to load libraries');
                return of([]);
            })
        ),
        { initialValue: [] as Library[] }
    );

    createLibrary() {
        const name = this.newLibraryName().trim();
        const path = this.newLibraryPath().trim();
        if (!name || !path) return;

        this.isCreating.set(true);
        this.damebooru.createLibrary(name, path).subscribe({
            next: () => {
                this.toast.success(`Library "${name}" created`);
                this.newLibraryName.set('');
                this.newLibraryPath.set('');
                this.refreshTrigger.update(v => v + 1);
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
                    this.refreshTrigger.update(v => v + 1);
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
                this.refreshTrigger.update(v => v + 1);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to rename library');
                this.renamingLibraryId.set(null);
            }
        });
    }

    getIgnoredPathDraft(libraryId: number): string {
        return this.ignoredPathDrafts()[libraryId] || '';
    }

    setIgnoredPathDraft(libraryId: number, value: string) {
        this.ignoredPathDrafts.update(drafts => ({ ...drafts, [libraryId]: value }));
    }

    addIgnoredPath(lib: Library) {
        const path = this.getIgnoredPathDraft(lib.id).trim();
        if (!path) return;

        this.addingIgnoredPathLibraryId.set(lib.id);
        this.damebooru.addLibraryIgnoredPath(lib.id, path).subscribe({
            next: (result) => {
                const removedText = result.removedPostCount > 0
                    ? ` and removed ${result.removedPostCount} post(s)`
                    : '';
                this.toast.success(`Ignored path "${result.ignoredPath.path}" saved${removedText}`);
                this.setIgnoredPathDraft(lib.id, '');
                this.refreshTrigger.update(v => v + 1);
                this.addingIgnoredPathLibraryId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to add ignored path');
                this.addingIgnoredPathLibraryId.set(null);
            }
        });
    }

    removeIgnoredPath(lib: Library, ignoredPathId: number, path: string) {
        this.removingIgnoredPathId.set(ignoredPathId);
        this.damebooru.removeLibraryIgnoredPath(lib.id, ignoredPathId).subscribe({
            next: () => {
                this.toast.success(`Removed ignored path "${path}"`);
                this.refreshTrigger.update(v => v + 1);
                this.removingIgnoredPathId.set(null);
            },
            error: (err) => {
                this.toast.error(err.error?.description || 'Failed to remove ignored path');
                this.removingIgnoredPathId.set(null);
            }
        });
    }
}
