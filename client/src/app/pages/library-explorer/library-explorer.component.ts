import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { Router, ActivatedRoute, RouterLink } from "@angular/router";
import { combineLatest } from "rxjs";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";

import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { LibraryBrowseResponse } from "@services/api/damebooru/models";
import { AppPaths } from "@app/app.paths";
import { ToastService } from "@services/toast.service";
import { PaginatorComponent } from "@shared/components/paginator/paginator.component";
import { PostTileComponent } from "@shared/components/post-tile/post-tile.component";

@Component({
  selector: "app-library-explorer",
  standalone: true,
  imports: [CommonModule, RouterLink, PaginatorComponent, PostTileComponent],
  templateUrl: "./library-explorer.component.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryExplorerComponent {
  private static readonly PAGE_SIZE = 80;

  private readonly damebooru = inject(DamebooruService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly appPaths = AppPaths;

  readonly libraryId = signal<number | null>(null);
  readonly currentPath = signal("");
  readonly page = signal(1);

  readonly browse = signal<LibraryBrowseResponse | null>(null);
  readonly loading = signal(false);

  readonly totalPages = computed(() => {
    const response = this.browse();
    if (!response || response.totalCount <= 0) {
      return 1;
    }

    return Math.max(1, Math.ceil(response.totalCount / response.pageSize));
  });

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        const rawId = params.get("id");
        const nextId = rawId ? Number(rawId) : Number.NaN;
        const parsedId = Number.isInteger(nextId) && nextId > 0 ? nextId : null;

        const nextPath = this.normalizeFolderPath(query.get("path"));
        const nextPage = this.parsePage(query.get("page"));

        this.libraryId.set(parsedId);
        this.currentPath.set(nextPath);
        this.page.set(nextPage);

        if (parsedId === null) {
          this.browse.set(null);
          return;
        }

        this.loadBrowse(parsedId, nextPath, nextPage);
      });
  }

  onSelectFolder(path: string): void {
    this.navigateWithState({ path, page: 1 });
  }

  onGoUp(): void {
    const path = this.currentPath();
    if (!path) {
      return;
    }

    const separatorIndex = path.lastIndexOf("/");
    const parentPath = separatorIndex < 0 ? "" : path.slice(0, separatorIndex);
    this.navigateWithState({ path: parentPath, page: 1 });
  }

  onPageChange(page: number): void {
    this.navigateWithState({ page });
  }

  private loadBrowse(
    libraryId: number,
    path: string,
    page: number,
  ): void {
    this.loading.set(true);

    this.damebooru
      .getLibraryBrowse(libraryId, {
        path,
        page,
        pageSize: LibraryExplorerComponent.PAGE_SIZE,
      })
      .subscribe({
        next: (response) => {
          this.browse.set(response);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.browse.set(null);
          this.toast.error("Failed to load library explorer.");
        },
      });
  }

  private navigateWithState(state: {
    path?: string;
    page?: number;
  }): void {
    const path = state.path ?? this.currentPath();
    const page = state.page ?? this.page();

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        path: path.length > 0 ? path : null,
        page: page > 1 ? page : null,
      },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
  }

  private parsePage(value: string | null): number {
    if (!value) {
      return 1;
    }

    const parsed = Number(value);
    if (!Number.isInteger(parsed) || parsed < 1) {
      return 1;
    }

    return parsed;
  }

  private normalizeFolderPath(value: string | null): string {
    if (!value) {
      return "";
    }

    const parts = value
      .replace(/\\/g, "/")
      .split("/")
      .map((part) => part.trim())
      .filter((part) => part.length > 0 && part !== "." && part !== "..");

    return parts.join("/");
  }
}
