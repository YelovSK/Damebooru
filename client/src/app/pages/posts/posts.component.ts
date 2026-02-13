import { Component, inject, input, signal, computed, ChangeDetectionStrategy, DestroyRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { toSignal, toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink, Router } from '@angular/router';
import { Subject, switchMap, of, map, catchError, combineLatest, startWith, scan } from 'rxjs';
import { HotkeysService } from '@services/hotkeys.service';
import { environment } from '@env/environment';
import { BakabooruPostListDto, BakabooruTagDto } from '@models';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';
import { FormDropdownComponent, FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { escapeTagName } from '@shared/utils/utils';
import { StorageService, STORAGE_KEYS } from '@services/storage.service';
import { AppLinks } from '@app/app.paths';

interface PostsState {
    data: BakabooruPostListDto | null;
    isLoading: boolean;
    error: unknown;
}

// Type for the loading state that retains previous data
interface LoadingState extends PostsState {
    isLoading: true;
    data: BakabooruPostListDto; // Required to retain previous data
}

@Component({
    selector: 'app-posts',
    imports: [CommonModule, RouterLink, AutocompleteComponent, FormDropdownComponent, PaginatorComponent],
    templateUrl: './posts.component.html',
    styleUrl: './posts.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush
})
export class PostsComponent {
    private readonly bakabooru = inject(BakabooruService);
    private readonly router = inject(Router);
    private readonly storage = inject(StorageService);
    private readonly hotkeys = inject(HotkeysService);
    private readonly destroyRef = inject(DestroyRef);

    readonly appLinks = AppLinks;

    query = input<string | null>('');
    offset = input<string | null>('0');
    limit = input<string | null>(null);

    environment = environment;

    pageSize = signal(this.storage.getNumber(STORAGE_KEYS.POSTS_PAGE_SIZE) ?? 50);
    pageSizeOptions: FormDropdownOption<number>[] = [10, 25, 50, 75, 100].map(size => ({
        label: `${size} per page`,
        value: size
    }));

    gridSizes = [100, 150, 200, 250, 300, 400];
    gridSizeIndex = signal(this.storage.getNumber(STORAGE_KEYS.POSTS_GRID_SIZE_INDEX) ?? 1);
    gridSize = signal(this.gridSizes[this.gridSizeIndex()]);

    currentPage = computed(() => {
        const off = Number(this.offset() ?? '0') || 0;
        return Math.floor(off / this.pageSize()) + 1;
    });

    currentSearchValue = signal('');

    private tagQuery$ = new Subject<string>();
    tagSuggestions = toSignal(
        this.tagQuery$.pipe(
            switchMap(word => {
                if (word.length < 1) return of([]);
                return this.bakabooru.getTags(`*${word}* sort:usages`, 0, 15).pipe(
                    map(res => res.results),
                    catchError(() => of([]))
                );
            })
        ),
        { initialValue: [] as BakabooruTagDto[] }
    );

    private postsState$ = combineLatest([
        toObservable(this.query),
        toObservable(this.offset),
        toObservable(this.limit)
    ]).pipe(
        switchMap(([q, off, l]) => {
            const offsetNum = Number(off ?? '0') || 0;
            const limitNum = l ? Number(l) : this.pageSize();
            return this.bakabooru.getPosts(q ?? '', offsetNum, limitNum).pipe(
                map(data => ({ data, isLoading: false, error: null } as PostsState)),
                startWith({ isLoading: true, data: null, error: null } as PostsState),
                catchError(error => of({ data: null, isLoading: false, error } as PostsState))
            );
        }),
        scan((acc: PostsState, curr: PostsState): PostsState => {
            // If loading and previous data exists, retain it
            if (curr.isLoading && acc.data) {
                return { ...curr, data: acc.data };
            }
            // If loading and no previous data, use null
            if (curr.isLoading) {
                return { ...curr, data: null };
            }
            return curr;
        }, { data: null, isLoading: true, error: null } as PostsState)
    );

    postsState = toSignal(this.postsState$, {
        initialValue: { data: null, isLoading: true, error: null } as PostsState
    });

    totalItems = signal(0);
    totalPages = computed(() => Math.ceil(this.totalItems() / this.pageSize()));

    constructor() {
        toObservable(this.query)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(q => this.currentSearchValue.set(q ?? ''));

        // Sync URL limit with pageSize signal
        toObservable(this.limit)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(l => {
                if (l) {
                    const val = Number(l);
                    if (!isNaN(val) && val > 0 && val !== this.pageSize()) {
                        this.pageSize.set(val);
                    }
                }
            });

        // Explicitly update totalItems only when we have real data to avoid jumps
        toObservable(this.postsState)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(state => {
                if (state.data) {
                    this.totalItems.set(state.data.totalCount);
                }
            });

        this.setupHotkeys();
    }

    private setupHotkeys() {
        this.hotkeys.on('ArrowLeft')
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(() => this.onPageChange(this.currentPage() - 1));

        this.hotkeys.on('ArrowRight')
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(() => this.onPageChange(this.currentPage() + 1));
    }

    onQueryChange(word: string) {
        // Strip the leading dash if it exists before querying tags
        const cleanWord = word.startsWith('-') ? word.substring(1) : word;
        this.tagQuery$.next(escapeTagName(cleanWord));
    }

    onSelection(tag: BakabooruTagDto) {
        const value = this.currentSearchValue();
        const parts = value.split(/\s+/);
        // Detect if the user was typing an exclusion
        const lastPart = parts[parts.length - 1] || '';
        const prefix = lastPart.startsWith('-') ? '-' : '';

        parts[parts.length - 1] = prefix + escapeTagName(tag.name);
        const newValue = parts.join(' ').trim() + ' ';

        this.currentSearchValue.set(newValue);
        this.tagQuery$.next('');
    }

    onSearch(q: string) {
        this.router.navigate([], {
            queryParams: { query: q, offset: 0 },
            queryParamsHandling: 'merge',
            replaceUrl: true
        });
    }

    onPageChange(page: number) {
        if (page < 1 || page > this.totalPages()) return;
        this.updateOffset((page - 1) * this.pageSize());
    }

    onGridSizeChange(event: Event) {
        const input = event.target as HTMLInputElement;
        const newIndex = Number(input.value);
        this.gridSize.set(this.gridSizes[newIndex]);
        this.storage.setItem(STORAGE_KEYS.POSTS_GRID_SIZE_INDEX, newIndex.toString());
    }

    onPageSizeChange(newSize: number) {
        this.pageSize.set(newSize);
        this.storage.setItem(STORAGE_KEYS.POSTS_PAGE_SIZE, newSize.toString());
        // Reset to first page when changing page size
        this.router.navigate([], {
            queryParams: { limit: newSize, offset: 0 },
            queryParamsHandling: 'merge',
            replaceUrl: true
        });
    }

    private updateOffset(off: number) {
        this.router.navigate([], {
            queryParams: { offset: off },
            queryParamsHandling: 'merge',
            replaceUrl: true
        });
    }

    getMediaType(contentType: string): 'image' | 'animation' | 'video' {
        if (contentType.startsWith('video/')) return 'video';
        if (contentType === 'image/gif') return 'animation';
        return 'image';
    }
}
